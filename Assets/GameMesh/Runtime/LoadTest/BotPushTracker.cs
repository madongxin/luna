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
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        const long AoiLogIntervalMs = 30000;
        int _unknownMove;
        int _duplicateLeave;
        long _nextAoiLogMs;

        public ulong MapInstanceId { get; private set; }
        public ulong OwnerEpoch { get; private set; }
        public ulong LastAppliedServerSeq { get; private set; }
        public bool HasGap { get; private set; }
        public bool Healthy { get; private set; }
        public int VisibleCount => _visible.Count;
        public int UnknownMoveCount => _unknownMove;
        public int DuplicateLeaveCount => _duplicateLeave;

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
            if (envelope == null || !envelope.Reliable || envelope.ServerSeq == 0)
                return false;
            NoteAoiClock(Clock.ElapsedMilliseconds);
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
                HasGap = true;
                Healthy = false;
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

            if (inner != null && inner.AoiDelta != null)
                ApplyDelta(inner.AoiDelta);
            LastAppliedServerSeq = envelope.ServerSeq;
            HasGap = false;
            return true;
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
            if (snap.AoiEntities != null)
            {
                for (var i = 0; i < snap.AoiEntities.Count; i++)
                {
                    var id = snap.AoiEntities[i] != null ? snap.AoiEntities[i].PlayerId : 0;
                    if (id != 0)
                        _visible.Add(id);
                }
            }

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
                    _visible.Add(id);
                else if (ev.Op == (int)AoiOp.Move)
                {
                    if (!_visible.Contains(id))
                        _unknownMove++;
                }
                else if (ev.Op == (int)AoiOp.Leave)
                {
                    if (!_visible.Remove(id))
                        _duplicateLeave++;
                }
            }
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
                             " duplicate_leave=" + _duplicateLeave);
            return true;
        }

        void ResetForOtherInstance(ulong mapInstanceId, ulong ownerEpoch)
        {
            _visible.Clear();
            _unknownMove = 0;
            _duplicateLeave = 0;
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
