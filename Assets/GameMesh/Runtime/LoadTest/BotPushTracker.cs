using System.Collections.Generic;
using System.Diagnostics;
using GameMesh.Aoi;
using GameMesh.Network;
using GameMesh.Protocol;

namespace GameMesh.LoadTest
{
    /// <summary>
    /// Minimal reliable-push book for one bot. GameResponse.Seq is never a push seq.
    /// A seq is stored only after the event is accepted as the next contiguous server_seq.
    /// </summary>
    public sealed class BotPushTracker
    {
        readonly HashSet<ulong> _visible = new HashSet<ulong>();
        readonly HashSet<ulong> _recentlyLeft = new HashSet<ulong>();
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        const long AoiLogIntervalMs = 30000;
        const int RecentlyLeftCap = 256;
        int _unknownMove;
        int _duplicateLeave;
        int _aoiEnterEvents;
        int _aoiLeaveEvents;
        int _aoiMoveEvents;
        int _leaveThenMove;
        int _moveWithStartMs;
        int _visiblePeak;
        int _gapSkipped;
        int _bareFrames;
        long _nextAoiLogMs;

        public ulong MapInstanceId { get; private set; }
        public ulong OwnerEpoch { get; private set; }
        public ulong LastAppliedServerSeq { get; private set; }
        public bool HasGap { get; private set; }
        public bool Healthy { get; private set; }
        public int VisibleCount => _visible.Count;
        public int UnknownMoveCount => _unknownMove;
        public int DuplicateLeaveCount => _duplicateLeave;
        public int AoiEnterEvents => _aoiEnterEvents;
        public int AoiLeaveEvents => _aoiLeaveEvents;
        public int AoiMoveEvents => _aoiMoveEvents;
        public int LeaveThenMoveCount => _leaveThenMove;
        public int MoveWithStartMsCount => _moveWithStartMs;
        public int VisiblePeak => _visiblePeak;
        public int AoiGapSkipped => _gapSkipped;
        public int AoiBareFrames => _bareFrames;

        public void BindInstance(ulong mapInstanceId, ulong ownerEpoch)
        {
            if (mapInstanceId != 0 && MapInstanceId != 0 && mapInstanceId != MapInstanceId)
                ResetForOtherInstance(mapInstanceId, ownerEpoch);
            else
            {
                if (mapInstanceId != 0)
                    MapInstanceId = mapInstanceId;
                if (ownerEpoch != 0)
                    OwnerEpoch = ownerEpoch;
            }
        }

        public bool ObserveServerPush(ServerPushEnvelope envelope, GameResponse inner)
        {
            if (envelope == null || envelope.ServerSeq == 0)
                return false;
            NoteAoiClock(Clock.ElapsedMilliseconds);
            if (!envelope.Reliable)
            {
                // Stats only: unreliable AOI still counts for export; reliability cursor unchanged.
                if (inner != null && inner.AoiDelta != null)
                    ApplyDelta(inner.AoiDelta);
                return false;
            }

            if (inner != null && inner.AoiDelta != null &&
                inner.AoiDelta.MapInstanceId != 0 &&
                MapInstanceId != 0 &&
                inner.AoiDelta.MapInstanceId != MapInstanceId)
            {
                ResetForOtherInstance(inner.AoiDelta.MapInstanceId, 0);
                return false;
            }

            if (LastAppliedServerSeq != 0 && envelope.ServerSeq <= LastAppliedServerSeq)
                return false;
            if (LastAppliedServerSeq != 0 && envelope.ServerSeq > LastAppliedServerSeq + 1)
            {
                // F5: gaps used to drop all counters. Keep counting for export without advancing
                // the reliable cursor (move loop behavior unchanged).
                HasGap = true;
                Healthy = false;
                _gapSkipped++;
                if (inner != null && inner.AoiDelta != null)
                    ApplyDelta(inner.AoiDelta);
                else if (inner != null && inner.FullSnapshot != null)
                    ApplySnapshotForStats(inner.FullSnapshot);
                return false;
            }

            if (inner == null)
            {
                Healthy = false;
                return false;
            }

            if (inner.FullSnapshot != null)
            {
                if (!ApplySnapshot(inner.FullSnapshot))
                    return false;
                if (envelope.ServerSeq > LastAppliedServerSeq)
                    LastAppliedServerSeq = envelope.ServerSeq;
                return true;
            }

            if (inner.AoiDelta != null)
                ApplyDelta(inner.AoiDelta);
            LastAppliedServerSeq = envelope.ServerSeq;
            HasGap = false;
            return true;
        }

        /// <summary>
        /// Bare AoiDelta frames (not wrapped in ServerPush). Stats only — no server_seq cursor.
        /// </summary>
        public void ObserveBareAoiDelta(AoiDelta delta)
        {
            if (delta == null)
                return;
            _bareFrames++;
            if (delta.MapInstanceId != 0 && MapInstanceId != 0 && delta.MapInstanceId != MapInstanceId)
                ResetForOtherInstance(delta.MapInstanceId, 0);
            ApplyDelta(delta);
        }

        public bool ApplySnapshot(FullStateSnapshotRsp snap)
        {
            if (snap == null || !snap.Ok)
            {
                Healthy = false;
                return false;
            }

            if (!WorldSnapshotApplier.AcceptsEpoch(OwnerEpoch, snap.OwnerEpoch))
            {
                Healthy = false;
                return false;
            }

            if (snap.MapInstanceId != 0 && MapInstanceId != 0 && snap.MapInstanceId != MapInstanceId)
            {
                ResetForOtherInstance(snap.MapInstanceId, snap.OwnerEpoch);
            }

            var wire = snap.AoiEntities != null ? snap.AoiEntities.Count : 0;
            if (!WorldSnapshotApplier.ShouldReplaceEntities(VisibleCount, wire, true, snap.RecoveryReason))
            {
                Healthy = false;
                return false;
            }

            _visible.Clear();
            _recentlyLeft.Clear();
            if (snap.AoiEntities != null)
            {
                for (var i = 0; i < snap.AoiEntities.Count; i++)
                {
                    var id = snap.AoiEntities[i] != null ? snap.AoiEntities[i].PlayerId : 0;
                    if (id != 0)
                        _visible.Add(id);
                }
            }

            NoteVisiblePeak();
            if (snap.MapInstanceId != 0)
                MapInstanceId = snap.MapInstanceId;
            if (snap.OwnerEpoch != 0)
                OwnerEpoch = snap.OwnerEpoch;
            if (snap.BaselineServerSeq > LastAppliedServerSeq)
                LastAppliedServerSeq = snap.BaselineServerSeq;
            HasGap = false;
            Healthy = true;
            return true;
        }

        void ApplySnapshotForStats(FullStateSnapshotRsp snap)
        {
            if (snap == null || snap.AoiEntities == null)
                return;
            for (var i = 0; i < snap.AoiEntities.Count; i++)
            {
                var id = snap.AoiEntities[i] != null ? snap.AoiEntities[i].PlayerId : 0;
                if (id == 0)
                    continue;
                _visible.Add(id);
                _aoiEnterEvents++;
            }

            NoteVisiblePeak();
        }

        void ApplyDelta(AoiDelta delta)
        {
            if (delta == null)
                return;
            for (var i = 0; i < delta.Events.Count; i++)
            {
                var ev = delta.Events[i];
                var id = ev != null && ev.Entity != null ? ev.Entity.PlayerId : 0;
                if (id == 0)
                    continue;
                if (ev.Op == (int)AoiOp.Enter)
                {
                    _aoiEnterEvents++;
                    _visible.Add(id);
                    _recentlyLeft.Remove(id);
                    NoteVisiblePeak();
                }
                else if (ev.Op == (int)AoiOp.Move)
                {
                    _aoiMoveEvents++;
                    if (ev.Entity != null && ev.Entity.MoveStartMs != 0)
                        _moveWithStartMs++;
                    if (!_visible.Contains(id))
                    {
                        _unknownMove++;
                        if (_recentlyLeft.Contains(id))
                            _leaveThenMove++;
                    }
                }
                else if (ev.Op == (int)AoiOp.Leave)
                {
                    _aoiLeaveEvents++;
                    if (!_visible.Remove(id))
                        _duplicateLeave++;
                    else
                        RememberLeft(id);
                }
            }
        }

        void RememberLeft(ulong id)
        {
            if (_recentlyLeft.Count >= RecentlyLeftCap)
                _recentlyLeft.Clear();
            _recentlyLeft.Add(id);
        }

        void NoteVisiblePeak()
        {
            if (_visible.Count > _visiblePeak)
                _visiblePeak = _visible.Count;
        }

        public void CopyVisibleIds(List<ulong> dest)
        {
            if (dest == null)
                return;
            dest.Clear();
            foreach (var id in _visible)
                dest.Add(id);
            dest.Sort();
        }

        public void CopyInto(BotMovementMetrics metrics)
        {
            if (metrics == null)
                return;
            metrics.AoiEnterEvents = _aoiEnterEvents;
            metrics.AoiLeaveEvents = _aoiLeaveEvents;
            metrics.AoiMoveEvents = _aoiMoveEvents;
            metrics.AoiUnknownMove = _unknownMove;
            metrics.AoiLeaveThenMove = _leaveThenMove;
            metrics.AoiMoveWithStartMs = _moveWithStartMs;
            metrics.AoiVisiblePeak = _visiblePeak;
            metrics.AoiVisibleLast = _visible.Count;
            metrics.AoiGapSkipped = _gapSkipped;
            metrics.AoiBareFrames = _bareFrames;
        }

        internal bool NoteAoiClock(long nowMs)
        {
            if (_nextAoiLogMs == 0)
            {
                _nextAoiLogMs = nowMs + AoiLogIntervalMs;
                return false;
            }

            if (nowMs < _nextAoiLogMs)
                return false;
            _nextAoiLogMs = nowMs + AoiLogIntervalMs;
            GameMeshLog.Info("aoi visible=" + _visible.Count +
                             " unknown_move=" + _unknownMove +
                             " leave_then_move=" + _leaveThenMove +
                             " duplicate_leave=" + _duplicateLeave);
            return true;
        }

        void ResetForOtherInstance(ulong mapInstanceId, ulong ownerEpoch)
        {
            _visible.Clear();
            _recentlyLeft.Clear();
            _unknownMove = 0;
            _duplicateLeave = 0;
            _aoiEnterEvents = 0;
            _aoiLeaveEvents = 0;
            _aoiMoveEvents = 0;
            _leaveThenMove = 0;
            _moveWithStartMs = 0;
            _visiblePeak = 0;
            _gapSkipped = 0;
            _bareFrames = 0;
            _nextAoiLogMs = 0;
            MapInstanceId = mapInstanceId;
            if (ownerEpoch != 0)
                OwnerEpoch = ownerEpoch;
            LastAppliedServerSeq = 0;
            HasGap = false;
            Healthy = false;
        }
    }
}
