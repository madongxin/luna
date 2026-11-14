using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GameMesh.LoadTest
{
    public sealed class BotMovementMetrics
    {
        public int Planned;
        public int Sent;
        public int QueueAccepted;
        public int WriteCompleted;
        public int RspRawReceived;
        public int RspMatched;
        public int LateRsp;
        public int TimeoutBeforeWrite;
        public int ExpiredBeforeWrite;
        public int UnknownOutcome;
        public int Cancelled;
        public int AckOk;
        public int Rejected;
        public int RejectTooFast;
        public int RejectBounds;
        public int RejectUnwalkable;
        public int RejectOther;
        public int Timeout;
        public int Overloaded;
        public int PoseRefresh;
        public int SkippedBusy;
        public int SkippedNotReady;
        public int SkippedPoseUncertain;
        public int SkippedSessionNotReady;
        public int SkippedRecoveryHold;
        public int Paused;
        public int PathRejected;
        public int Corrections;
        public float CorrectionDistanceSum;
        public int SchedulerLagMs;
        public long SampleStartedMs;
        public long SampleEndedMs;
        public int Turns;
        public long MovingMs;
        public long DwellingMs;

        // E1 run profile (filled once when the move loop starts).
        public string LoopKind = "无数据";
        public bool SparsePatrol;
        public int ClusterSize;
        public float ClusterSpacingMeters;
        public float ClusterRadiusMeters;
        public int TargetHz;
        public float SegmentMinSec;
        public float SegmentMaxSec;
        public int MoveDutyPct;
        public int SparseDutyPct = 100;
        public int BotCount;
        public bool AoiBotBackoff;
        public float AoiRadius;
        public float SafeRadius;
        public float SparseDwellMinSec;
        public float SparseDwellMaxSec;

        // Send reason counters (Q1).
        public int SendDepart;
        public int SendArrive;
        public int SendResend;
        public int SendOther;

        // E1 per-bot counters.
        public int ResendCount;
        public int ResendAbandonCount;
        public int HeartbeatOk;
        public int HeartbeatFail;
        public int HeartbeatFailTimeout;
        public int HeartbeatFailDisconnect;
        public int HeartbeatFailReject;
        public int HeartbeatFailOther;
        public int HeartbeatReconnects;
        // Rolling per-minute ok/fail (up to 60 minutes of samples).
        readonly int[] _hbOkPerMin = new int[60];
        readonly int[] _hbFailPerMin = new int[60];
        int _hbMinSlots;
        long _hbMinAnchorMs = -1;

        // E1 observer (AOI push) counters — usually filled on the same bot metrics.
        public int AoiEnterEvents;
        public int AoiLeaveEvents;
        public int AoiMoveEvents;
        public int AoiUnknownMove;
        public int AoiLeaveThenMove;
        public int AoiMoveWithStartMs;
        public int AoiVisiblePeak;
        public int AoiVisibleLast;
        public int AoiGapSkipped;
        public int AoiBareFrames;

        // Aggregate-only samples filled by the runner export (per-bot hz / visible / moving ratio).
        public float BotSendHzP50;
        public float BotSendHzP95;
        public float BotSendHzMax;
        public int BotSendHzSamples;
        public float BotMovingRatioP50;
        public float BotMovingRatioP95;
        public int BotMovingRatioSamples;
        public float AoiVisibleP50;
        public float AoiVisibleP95;
        public float AoiVisibleMax;
        public int AoiVisibleSamples;

        // R1: segment completion reasons + actual depart→depart intervals.
        public const string CompleteArrivedByConfirm = "arrived_by_confirm";
        public const string CompleteTimerExpired = "timer_expired";
        public const string CompleteRejected = "rejected";
        public const string CompleteOther = "other";
        public int SegmentCompleteArrivedByConfirm;
        public int SegmentCompleteTimerExpired;
        public int SegmentCompleteRejected;
        public int SegmentCompleteOther;

        // R3: client-side move path timings (ms). Missing → 0 / 无数据 in JSON note.
        public int MoveStartToWriteSampleCount;
        public int MoveRecvToCallbackSampleCount;
        public float HostCpuPctAvg = -1f;
        public float HostCpuPctPeak = -1f;
        public int HostStallMsMax;
        public int HostPoolQueueMsMax;
        public int HostFrameMsMax;
        public int HostFrameMsP50;
        public int HostGcGen0;
        public int HostGcGen1;
        public int HostGcGen2;
        public string BuildSha = "无数据";
        public string StartedAt = "无数据";
        public string EndedAt = "无数据";
        public string HostCpuNote = "无数据";

        double _movingTimeRatioOverride = -1;

        readonly int[] _latency = new int[256];
        int _latencyCount;
        int _latencyCursor;
        readonly float[] _segSec = new float[512];
        int _segSecCount;
        int _segSecCursor;
        readonly float[] _segMeters = new float[512];
        int _segMetersCount;
        int _segMetersCursor;
        readonly float[] _departIntervalMs = new float[512];
        int _departIntervalCount;
        int _departIntervalCursor;
        readonly int[] _startToWriteMs = new int[256];
        int _startToWriteCount;
        int _startToWriteCursor;
        readonly int[] _recvToCallbackMs = new int[256];
        int _recvToCallbackCount;
        int _recvToCallbackCursor;

        public static bool IsBusy(string code)
        {
            return code == "ERR_OVERLOADED" || code == "ERR_BUSY";
        }

        public static int HoldMsAfterTimeouts(int streak)
        {
            if (streak < 2)
                return 0;
            var hold = 2000;
            for (var i = 2; i < streak && hold < 15000; i++)
                hold = Math.Min(15000, hold * 2);
            return hold;
        }

        public static int HeartbeatPauseMs(int misses)
        {
            if (misses <= 1)
                return 5000;
            var pause = 5000;
            for (var i = 1; i < misses && pause < 30000; i++)
                pause = Math.Min(30000, pause * 2);
            return pause;
        }

        public void CaptureProfile(BotMovementSettings settings, string loopKind)
        {
            if (settings == null)
            {
                LoopKind = string.IsNullOrEmpty(loopKind) ? "无数据" : loopKind;
                return;
            }

            LoopKind = string.IsNullOrEmpty(loopKind) ? "无数据" : loopKind;
            SparsePatrol = settings.SparsePatrol;
            ClusterSize = settings.ClusterSize;
            ClusterSpacingMeters = settings.ClusterSpacingMeters;
            ClusterRadiusMeters = settings.ClusterRadiusMeters;
            TargetHz = settings.TargetHz;
            SegmentMinSec = settings.SegmentMinSec;
            SegmentMaxSec = settings.SegmentMaxSec;
            MoveDutyPct = settings.MoveDutyPct;
            SparseDutyPct = settings.SparseDutyPct;
            SparseDwellMinSec = settings.SparseDwellMinSec;
            SparseDwellMaxSec = settings.SparseDwellMaxSec;
            BotCount = settings.BotCount;
            AoiBotBackoff = settings.AoiBotBackoff;
            AoiRadius = settings.AoiRadius;
            SafeRadius = settings.SafeRadius;
        }

        public void NoteSendReason(string reason)
        {
            if (reason == "depart")
                SendDepart++;
            else if (reason == "arrive")
                SendArrive++;
            else if (reason == "resend")
                SendResend++;
            else
                SendOther++;
        }

        public void NoteSegment(float sec, float meters)
        {
            if (sec < 0f)
                sec = 0f;
            if (meters < 0f)
                meters = 0f;
            _segSec[_segSecCursor] = sec;
            _segSecCursor = (_segSecCursor + 1) % _segSec.Length;
            if (_segSecCount < _segSec.Length)
                _segSecCount++;
            _segMeters[_segMetersCursor] = meters;
            _segMetersCursor = (_segMetersCursor + 1) % _segMeters.Length;
            if (_segMetersCount < _segMeters.Length)
                _segMetersCount++;
        }

        public void NoteActualDepartIntervalMs(long ms)
        {
            if (ms < 0)
                ms = 0;
            if (ms > int.MaxValue)
                ms = int.MaxValue;
            _departIntervalMs[_departIntervalCursor] = ms;
            _departIntervalCursor = (_departIntervalCursor + 1) % _departIntervalMs.Length;
            if (_departIntervalCount < _departIntervalMs.Length)
                _departIntervalCount++;
        }

        public void NoteSegmentComplete(string reason)
        {
            if (reason == CompleteArrivedByConfirm)
                SegmentCompleteArrivedByConfirm++;
            else if (reason == CompleteTimerExpired)
                SegmentCompleteTimerExpired++;
            else if (reason == CompleteRejected)
                SegmentCompleteRejected++;
            else
                SegmentCompleteOther++;
        }

        public void NoteMoveStartToWriteMs(int ms)
        {
            if (ms < 0)
                ms = 0;
            _startToWriteMs[_startToWriteCursor] = ms;
            _startToWriteCursor = (_startToWriteCursor + 1) % _startToWriteMs.Length;
            if (_startToWriteCount < _startToWriteMs.Length)
                _startToWriteCount++;
            MoveStartToWriteSampleCount = _startToWriteCount;
        }

        public void NoteMoveRecvToCallbackMs(int ms)
        {
            if (ms < 0)
                ms = 0;
            _recvToCallbackMs[_recvToCallbackCursor] = ms;
            _recvToCallbackCursor = (_recvToCallbackCursor + 1) % _recvToCallbackMs.Length;
            if (_recvToCallbackCount < _recvToCallbackMs.Length)
                _recvToCallbackCount++;
            MoveRecvToCallbackSampleCount = _recvToCallbackCount;
        }

        public void SetMovingTimeRatioExport(double ratio)
        {
            if (ratio < 0)
                ratio = 0;
            if (ratio > 1)
                ratio = 1;
            _movingTimeRatioOverride = ratio;
        }

        public void NoteLatency(int ms)
        {
            if (ms < 0)
                ms = 0;
            _latency[_latencyCursor] = ms;
            _latencyCursor = (_latencyCursor + 1) % _latency.Length;
            if (_latencyCount < _latency.Length)
                _latencyCount++;
        }

        public void NoteHeartbeat(bool ok, string reason, long nowMs)
        {
            if (ok)
                HeartbeatOk++;
            else
            {
                HeartbeatFail++;
                if (reason == "timeout")
                    HeartbeatFailTimeout++;
                else if (reason == "disconnect")
                    HeartbeatFailDisconnect++;
                else if (reason == "reject")
                    HeartbeatFailReject++;
                else
                    HeartbeatFailOther++;
            }

            if (nowMs <= 0)
                return;
            if (_hbMinAnchorMs < 0)
                _hbMinAnchorMs = nowMs;
            var minute = (int)((nowMs - _hbMinAnchorMs) / 60000L);
            if (minute < 0)
                minute = 0;
            if (minute >= _hbOkPerMin.Length)
                minute = _hbOkPerMin.Length - 1;
            if (minute + 1 > _hbMinSlots)
                _hbMinSlots = minute + 1;
            if (ok)
                _hbOkPerMin[minute]++;
            else
                _hbFailPerMin[minute]++;
        }

        public string HeartbeatMinutesJson()
        {
            if (_hbMinSlots <= 0)
                return "[]";
            var sb = new StringBuilder(_hbMinSlots * 24);
            sb.Append('[');
            for (var i = 0; i < _hbMinSlots; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"m\":").Append(i)
                    .Append(",\"ok\":").Append(_hbOkPerMin[i])
                    .Append(",\"fail\":").Append(_hbFailPerMin[i]).Append('}');
            }

            sb.Append(']');
            return sb.ToString();
        }

        public float SegmentSecPercentile(int pct)
        {
            return FloatPercentile(_segSec, _segSecCount, _segSecCursor, pct);
        }

        public float SegmentMetersPercentile(int pct)
        {
            return FloatPercentile(_segMeters, _segMetersCount, _segMetersCursor, pct);
        }

        public int SegmentSampleCount => _segSecCount;
        public int ActualDepartIntervalSampleCount => _departIntervalCount;

        public float SegmentSecMax()
        {
            return FloatMax(_segSec, _segSecCount, _segSecCursor);
        }

        public float SegmentMetersMax()
        {
            return FloatMax(_segMeters, _segMetersCount, _segMetersCursor);
        }

        public float ActualDepartIntervalPercentile(int pct)
        {
            return FloatPercentile(_departIntervalMs, _departIntervalCount, _departIntervalCursor, pct);
        }

        public float ActualDepartIntervalMax()
        {
            return FloatMax(_departIntervalMs, _departIntervalCount, _departIntervalCursor);
        }

        public int MoveStartToWritePercentile(int pct)
        {
            return IntPercentile(_startToWriteMs, _startToWriteCount, _startToWriteCursor, pct);
        }

        public int MoveRecvToCallbackPercentile(int pct)
        {
            return IntPercentile(_recvToCallbackMs, _recvToCallbackCount, _recvToCallbackCursor, pct);
        }

        static int IntPercentile(int[] ring, int count, int cursor, int pct)
        {
            if (count <= 0)
                return 0;
            var copy = new int[count];
            var start = count == ring.Length ? cursor : 0;
            for (var i = 0; i < count; i++)
                copy[i] = ring[(start + i) % ring.Length];
            Array.Sort(copy);
            var index = (int)Math.Ceiling(pct / 100.0 * count) - 1;
            if (index < 0)
                index = 0;
            if (index >= copy.Length)
                index = copy.Length - 1;
            return copy[index];
        }

        static float FloatPercentile(float[] ring, int count, int cursor, int pct)
        {
            if (count <= 0)
                return 0f;
            var copy = new float[count];
            var start = count == ring.Length ? cursor : 0;
            for (var i = 0; i < count; i++)
                copy[i] = ring[(start + i) % ring.Length];
            Array.Sort(copy);
            var index = (int)Math.Ceiling(pct / 100.0 * count) - 1;
            if (index < 0)
                index = 0;
            if (index >= copy.Length)
                index = copy.Length - 1;
            return copy[index];
        }

        static float FloatMax(float[] ring, int count, int cursor)
        {
            if (count <= 0)
                return 0f;
            var start = count == ring.Length ? cursor : 0;
            var max = ring[start];
            for (var i = 1; i < count; i++)
            {
                var v = ring[(start + i) % ring.Length];
                if (v > max)
                    max = v;
            }

            return max;
        }

        public static float PercentileOf(List<float> values, int pct)
        {
            if (values == null || values.Count == 0)
                return 0f;
            var copy = values.ToArray();
            Array.Sort(copy);
            var index = (int)Math.Ceiling(pct / 100.0 * copy.Length) - 1;
            if (index < 0)
                index = 0;
            if (index >= copy.Length)
                index = copy.Length - 1;
            return copy[index];
        }

        public static float MaxOf(List<float> values)
        {
            if (values == null || values.Count == 0)
                return 0f;
            var max = values[0];
            for (var i = 1; i < values.Count; i++)
            {
                if (values[i] > max)
                    max = values[i];
            }

            return max;
        }

        public double ActualSendHz
        {
            get
            {
                var sec = ElapsedSec;
                return sec <= 0.001 ? 0 : Sent / sec;
            }
        }

        public double UplinkMovePerSec => ActualSendHz;

        public double ActualSuccessHz
        {
            get
            {
                var sec = ElapsedSec;
                return sec <= 0.001 ? 0 : AckOk / sec;
            }
        }

        public double AoiMovePerSec
        {
            get
            {
                var sec = ElapsedSec;
                return sec <= 0.001 ? 0 : AoiMoveEvents / sec;
            }
        }

        public double DutyRatio
        {
            get
            {
                var total = MovingMs + DwellingMs;
                return total <= 0 ? 0 : MovingMs / (double)total;
            }
        }

        /// <summary>
        /// MovingMs ÷ wall sample duration, clamped to [0,1].
        /// Aggregate export should SetMovingTimeRatioExport(mean of per-bot ratios) —
        /// summing MovingMs across bots made this &gt;1 (F6 / 130208 → 454).
        /// </summary>
        public double MovingTimeRatio
        {
            get
            {
                if (_movingTimeRatioOverride >= 0)
                    return _movingTimeRatioOverride;
                var sec = ElapsedSec;
                if (sec <= 0.001)
                    return 0;
                var ratio = MovingMs / 1000.0 / sec;
                if (ratio < 0)
                    return 0;
                if (ratio > 1)
                    return 1;
                return ratio;
            }
        }

        public double MoveStartMsNonZeroRatio
        {
            get
            {
                if (AoiMoveEvents <= 0)
                    return 0;
                return AoiMoveWithStartMs / (double)AoiMoveEvents;
            }
        }

        public double ElapsedSec
        {
            get
            {
                if (SampleEndedMs <= SampleStartedMs)
                    return 0;
                return (SampleEndedMs - SampleStartedMs) / 1000.0;
            }
        }

        public int LatencyPercentile(int pct)
        {
            if (_latencyCount <= 0)
                return 0;
            var copy = new int[_latencyCount];
            var start = _latencyCount == _latency.Length ? _latencyCursor : 0;
            for (var i = 0; i < _latencyCount; i++)
                copy[i] = _latency[(start + i) % _latency.Length];
            Array.Sort(copy);
            var index = (int)Math.Ceiling(pct / 100.0 * _latencyCount) - 1;
            if (index < 0)
                index = 0;
            if (index >= copy.Length)
                index = copy.Length - 1;
            return copy[index];
        }

        public void Add(BotMovementMetrics other)
        {
            if (other == null)
                return;
            Planned += other.Planned;
            Sent += other.Sent;
            QueueAccepted += other.QueueAccepted;
            WriteCompleted += other.WriteCompleted;
            RspRawReceived += other.RspRawReceived;
            RspMatched += other.RspMatched;
            LateRsp += other.LateRsp;
            TimeoutBeforeWrite += other.TimeoutBeforeWrite;
            ExpiredBeforeWrite += other.ExpiredBeforeWrite;
            UnknownOutcome += other.UnknownOutcome;
            Cancelled += other.Cancelled;
            AckOk += other.AckOk;
            Rejected += other.Rejected;
            RejectTooFast += other.RejectTooFast;
            RejectBounds += other.RejectBounds;
            RejectUnwalkable += other.RejectUnwalkable;
            RejectOther += other.RejectOther;
            Timeout += other.Timeout;
            Overloaded += other.Overloaded;
            PoseRefresh += other.PoseRefresh;
            SkippedBusy += other.SkippedBusy;
            SkippedNotReady += other.SkippedNotReady;
            SkippedPoseUncertain += other.SkippedPoseUncertain;
            SkippedSessionNotReady += other.SkippedSessionNotReady;
            SkippedRecoveryHold += other.SkippedRecoveryHold;
            Paused += other.Paused;
            PathRejected += other.PathRejected;
            Corrections += other.Corrections;
            CorrectionDistanceSum += other.CorrectionDistanceSum;
            Turns += other.Turns;
            MovingMs += other.MovingMs;
            DwellingMs += other.DwellingMs;
            SendDepart += other.SendDepart;
            SendArrive += other.SendArrive;
            SendResend += other.SendResend;
            SendOther += other.SendOther;
            ResendCount += other.ResendCount;
            ResendAbandonCount += other.ResendAbandonCount;
            HeartbeatOk += other.HeartbeatOk;
            HeartbeatFail += other.HeartbeatFail;
            HeartbeatFailTimeout += other.HeartbeatFailTimeout;
            HeartbeatFailDisconnect += other.HeartbeatFailDisconnect;
            HeartbeatFailReject += other.HeartbeatFailReject;
            HeartbeatFailOther += other.HeartbeatFailOther;
            HeartbeatReconnects += other.HeartbeatReconnects;
            for (var i = 0; i < other._hbMinSlots && i < _hbOkPerMin.Length; i++)
            {
                _hbOkPerMin[i] += other._hbOkPerMin[i];
                _hbFailPerMin[i] += other._hbFailPerMin[i];
            }
            if (other._hbMinSlots > _hbMinSlots)
                _hbMinSlots = other._hbMinSlots;
            AoiEnterEvents += other.AoiEnterEvents;
            AoiLeaveEvents += other.AoiLeaveEvents;
            AoiMoveEvents += other.AoiMoveEvents;
            AoiUnknownMove += other.AoiUnknownMove;
            AoiLeaveThenMove += other.AoiLeaveThenMove;
            AoiMoveWithStartMs += other.AoiMoveWithStartMs;
            AoiGapSkipped += other.AoiGapSkipped;
            AoiBareFrames += other.AoiBareFrames;
            if (other.AoiVisiblePeak > AoiVisiblePeak)
                AoiVisiblePeak = other.AoiVisiblePeak;
            AoiVisibleLast += other.AoiVisibleLast;
            if (other.SchedulerLagMs > SchedulerLagMs)
                SchedulerLagMs = other.SchedulerLagMs;
            SegmentCompleteArrivedByConfirm += other.SegmentCompleteArrivedByConfirm;
            SegmentCompleteTimerExpired += other.SegmentCompleteTimerExpired;
            SegmentCompleteRejected += other.SegmentCompleteRejected;
            SegmentCompleteOther += other.SegmentCompleteOther;
            var depStart = other._departIntervalCount == other._departIntervalMs.Length
                ? other._departIntervalCursor
                : 0;
            for (var i = 0; i < other._departIntervalCount; i++)
            {
                var ix = (depStart + i) % other._departIntervalMs.Length;
                NoteActualDepartIntervalMs((long)other._departIntervalMs[ix]);
            }

            var stwStart = other._startToWriteCount == other._startToWriteMs.Length
                ? other._startToWriteCursor
                : 0;
            for (var i = 0; i < other._startToWriteCount; i++)
            {
                var ix = (stwStart + i) % other._startToWriteMs.Length;
                NoteMoveStartToWriteMs(other._startToWriteMs[ix]);
            }

            var rtcStart = other._recvToCallbackCount == other._recvToCallbackMs.Length
                ? other._recvToCallbackCursor
                : 0;
            for (var i = 0; i < other._recvToCallbackCount; i++)
            {
                var ix = (rtcStart + i) % other._recvToCallbackMs.Length;
                NoteMoveRecvToCallbackMs(other._recvToCallbackMs[ix]);
            }

            if (LoopKind == "无数据" || string.IsNullOrEmpty(LoopKind))
                LoopKind = other.LoopKind;
            if (BotCount == 0)
            {
                SparsePatrol = other.SparsePatrol;
                ClusterSize = other.ClusterSize;
                ClusterSpacingMeters = other.ClusterSpacingMeters;
                ClusterRadiusMeters = other.ClusterRadiusMeters;
                TargetHz = other.TargetHz;
                SegmentMinSec = other.SegmentMinSec;
                SegmentMaxSec = other.SegmentMaxSec;
                MoveDutyPct = other.MoveDutyPct;
                SparseDutyPct = other.SparseDutyPct;
                SparseDwellMinSec = other.SparseDwellMinSec;
                SparseDwellMaxSec = other.SparseDwellMaxSec;
                BotCount = other.BotCount;
                AoiBotBackoff = other.AoiBotBackoff;
                AoiRadius = other.AoiRadius;
                SafeRadius = other.SafeRadius;
            }
            var segStart = other._segSecCount == other._segSec.Length ? other._segSecCursor : 0;
            for (var i = 0; i < other._segSecCount; i++)
            {
                var ix = (segStart + i) % other._segSec.Length;
                NoteSegment(other._segSec[ix], other._segMeters[ix]);
            }
            if (other.SampleEndedMs > other.SampleStartedMs)
            {
                if (SampleEndedMs <= SampleStartedMs)
                {
                    SampleStartedMs = other.SampleStartedMs;
                    SampleEndedMs = other.SampleEndedMs;
                }
                else
                {
                    if (other.SampleStartedMs < SampleStartedMs)
                        SampleStartedMs = other.SampleStartedMs;
                    if (other.SampleEndedMs > SampleEndedMs)
                        SampleEndedMs = other.SampleEndedMs;
                }
            }
            var latencyStart = other._latencyCount == other._latency.Length
                ? other._latencyCursor
                : 0;
            for (var i = 0; i < other._latencyCount; i++)
                NoteLatency(other._latency[(latencyStart + i) % other._latency.Length]);
        }

        public string ToJson(string runId)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1280);
            sb.Append("{\n");
            sb.Append("  \"run_id\": \"").Append(runId ?? "").Append("\",\n");
            sb.Append("  \"loop_kind\": \"").Append(LoopKind ?? "无数据").Append("\",\n");
            sb.Append("  \"sparse_patrol\": ").Append(SparsePatrol ? "true" : "false").Append(",\n");
            sb.Append("  \"cluster_size\": ").Append(ClusterSize).Append(",\n");
            sb.Append("  \"cluster_spacing_m\": ").Append(ClusterSpacingMeters.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"cluster_radius_m\": ").Append(ClusterRadiusMeters.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"target_hz\": ").Append(TargetHz).Append(",\n");
            sb.Append("  \"segment_min_sec\": ").Append(SegmentMinSec.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"segment_max_sec\": ").Append(SegmentMaxSec.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"move_duty_pct\": ").Append(MoveDutyPct).Append(",\n");
            sb.Append("  \"sparse_duty_pct\": ").Append(SparseDutyPct).Append(",\n");
            sb.Append("  \"sparse_dwell_min_sec\": ").Append(SparseDwellMinSec.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"sparse_dwell_max_sec\": ").Append(SparseDwellMaxSec.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"aoi_radius_m\": ").Append(AoiRadius.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"safe_radius_m\": ").Append(SafeRadius.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"bot_count\": ").Append(BotCount).Append(",\n");
            sb.Append("  \"aoi_bot_backoff\": ").Append(AoiBotBackoff ? "true" : "false").Append(",\n");
            sb.Append("  \"congestion_segment_scale\": ").Append(
                BotCongestionGate.CurrentScale.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"move_planned\": ").Append(Planned).Append(",\n");
            sb.Append("  \"move_sent\": ").Append(Sent).Append(",\n");
            sb.Append("  \"send_depart\": ").Append(SendDepart).Append(",\n");
            sb.Append("  \"send_arrive\": ").Append(SendArrive).Append(",\n");
            sb.Append("  \"send_resend\": ").Append(SendResend).Append(",\n");
            sb.Append("  \"send_other\": ").Append(SendOther).Append(",\n");
            sb.Append("  \"move_resend\": ").Append(ResendCount).Append(",\n");
            sb.Append("  \"move_resend_abandon\": ").Append(ResendAbandonCount).Append(",\n");
            sb.Append("  \"build_sha\": \"").Append(BuildSha ?? "无数据").Append("\",\n");
            sb.Append("  \"started_at\": \"").Append(StartedAt ?? "无数据").Append("\",\n");
            sb.Append("  \"ended_at\": \"").Append(EndedAt ?? "无数据").Append("\",\n");
            sb.Append("  \"sample_started_ms\": ").Append(SampleStartedMs).Append(",\n");
            sb.Append("  \"sample_ended_ms\": ").Append(SampleEndedMs).Append(",\n");
            sb.Append("  \"segment_samples\": ").Append(SegmentSampleCount).Append(",\n");
            sb.Append("  \"segment_sec_p50\": ").Append(SegmentSecPercentile(50).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"segment_sec_p95\": ").Append(SegmentSecPercentile(95).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"segment_sec_max\": ").Append(SegmentSecMax().ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"planned_segment_sec_p50\": ").Append(SegmentSecPercentile(50).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"planned_segment_sec_p95\": ").Append(SegmentSecPercentile(95).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"planned_segment_sec_max\": ").Append(SegmentSecMax().ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"actual_depart_interval_samples\": ").Append(ActualDepartIntervalSampleCount).Append(",\n");
            sb.Append("  \"actual_depart_interval_ms_p50\": ").Append(
                ActualDepartIntervalPercentile(50).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"actual_depart_interval_ms_p95\": ").Append(
                ActualDepartIntervalPercentile(95).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"actual_depart_interval_ms_max\": ").Append(
                ActualDepartIntervalMax().ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"segment_complete_arrived_by_confirm\": ").Append(SegmentCompleteArrivedByConfirm)
                .Append(",\n");
            sb.Append("  \"segment_complete_timer_expired\": ").Append(SegmentCompleteTimerExpired).Append(",\n");
            sb.Append("  \"segment_complete_rejected\": ").Append(SegmentCompleteRejected).Append(",\n");
            sb.Append("  \"segment_complete_other\": ").Append(SegmentCompleteOther).Append(",\n");
            sb.Append("  \"segment_m_p50\": ").Append(SegmentMetersPercentile(50).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"segment_m_p95\": ").Append(SegmentMetersPercentile(95).ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"segment_m_max\": ").Append(SegmentMetersMax().ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"bot_send_hz_samples\": ").Append(BotSendHzSamples).Append(",\n");
            sb.Append("  \"bot_send_hz_p50\": ").Append(BotSendHzP50.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"bot_send_hz_p95\": ").Append(BotSendHzP95.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"bot_send_hz_max\": ").Append(BotSendHzMax.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"bot_moving_ratio_samples\": ").Append(BotMovingRatioSamples).Append(",\n");
            sb.Append("  \"bot_moving_ratio_p50\": ").Append(BotMovingRatioP50.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"bot_moving_ratio_p95\": ").Append(BotMovingRatioP95.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"queue_accepted\": ").Append(QueueAccepted).Append(",\n");
            sb.Append("  \"write_completed\": ").Append(WriteCompleted).Append(",\n");
            sb.Append("  \"rsp_raw_received\": ").Append(RspRawReceived).Append(",\n");
            sb.Append("  \"rsp_matched\": ").Append(RspMatched).Append(",\n");
            sb.Append("  \"late_rsp\": ").Append(LateRsp).Append(",\n");
            sb.Append("  \"timeout_before_write\": ").Append(TimeoutBeforeWrite).Append(",\n");
            sb.Append("  \"expired_before_write\": ").Append(ExpiredBeforeWrite).Append(",\n");
            sb.Append("  \"unknown_after_write\": ").Append(UnknownOutcome).Append(",\n");
            sb.Append("  \"cancel\": ").Append(Cancelled).Append(",\n");
            sb.Append("  \"move_ack_ok\": ").Append(AckOk).Append(",\n");
            sb.Append("  \"move_rejected\": ").Append(Rejected).Append(",\n");
            sb.Append("  \"move_rejected_too_fast\": ").Append(RejectTooFast).Append(",\n");
            sb.Append("  \"move_rejected_bounds\": ").Append(RejectBounds).Append(",\n");
            sb.Append("  \"move_rejected_unwalkable\": ").Append(RejectUnwalkable).Append(",\n");
            sb.Append("  \"move_rejected_other\": ").Append(RejectOther).Append(",\n");
            sb.Append("  \"move_timeout\": ").Append(Timeout).Append(",\n");
            sb.Append("  \"move_overloaded\": ").Append(Overloaded).Append(",\n");
            sb.Append("  \"pose_refresh\": ").Append(PoseRefresh).Append(",\n");
            sb.Append("  \"move_skipped_busy\": ").Append(SkippedBusy).Append(",\n");
            sb.Append("  \"move_skipped_not_ready\": ").Append(SkippedNotReady).Append(",\n");
            sb.Append("  \"move_skipped_pose_uncertain\": ").Append(SkippedPoseUncertain).Append(",\n");
            sb.Append("  \"move_skipped_session_not_ready\": ").Append(SkippedSessionNotReady).Append(",\n");
            sb.Append("  \"move_skipped_recovery_hold\": ").Append(SkippedRecoveryHold).Append(",\n");
            sb.Append("  \"move_paused\": ").Append(Paused).Append(",\n");
            sb.Append("  \"path_rejected\": ").Append(PathRejected).Append(",\n");
            sb.Append("  \"corrections\": ").Append(Corrections).Append(",\n");
            sb.Append("  \"heartbeat_ok\": ").Append(HeartbeatOk).Append(",\n");
            sb.Append("  \"heartbeat_fail\": ").Append(HeartbeatFail).Append(",\n");
            sb.Append("  \"heartbeat_fail_timeout\": ").Append(HeartbeatFailTimeout).Append(",\n");
            sb.Append("  \"heartbeat_fail_disconnect\": ").Append(HeartbeatFailDisconnect).Append(",\n");
            sb.Append("  \"heartbeat_fail_reject\": ").Append(HeartbeatFailReject).Append(",\n");
            sb.Append("  \"heartbeat_fail_other\": ").Append(HeartbeatFailOther).Append(",\n");
            sb.Append("  \"heartbeat_reconnects\": ").Append(HeartbeatReconnects).Append(",\n");
            sb.Append("  \"heartbeat_per_minute\": ").Append(HeartbeatMinutesJson()).Append(",\n");
            sb.Append("  \"actual_send_hz\": ").Append(ActualSendHz.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"uplink_move_per_sec\": ").Append(UplinkMovePerSec.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"turns\": ").Append(Turns).Append(",\n");
            sb.Append("  \"moving_ms\": ").Append(MovingMs).Append(",\n");
            sb.Append("  \"dwelling_ms\": ").Append(DwellingMs).Append(",\n");
            sb.Append("  \"duty\": ").Append(DutyRatio.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"moving_time_ratio\": ").Append(MovingTimeRatio.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"actual_success_hz\": ").Append(ActualSuccessHz.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"rpc_latency_p50\": ").Append(LatencyPercentile(50)).Append(",\n");
            sb.Append("  \"rpc_latency_p95\": ").Append(LatencyPercentile(95)).Append(",\n");
            sb.Append("  \"rpc_latency_p99\": ").Append(LatencyPercentile(99)).Append(",\n");
            sb.Append("  \"scheduler_lag_ms\": ").Append(SchedulerLagMs).Append(",\n");
            sb.Append("  \"move_start_to_write_samples\": ").Append(MoveStartToWriteSampleCount).Append(",\n");
            sb.Append("  \"move_start_to_write_ms_p50\": ").Append(MoveStartToWritePercentile(50)).Append(",\n");
            sb.Append("  \"move_start_to_write_ms_p95\": ").Append(MoveStartToWritePercentile(95)).Append(",\n");
            sb.Append("  \"move_recv_to_callback_samples\": ").Append(MoveRecvToCallbackSampleCount).Append(",\n");
            sb.Append("  \"move_recv_to_callback_ms_p50\": ").Append(MoveRecvToCallbackPercentile(50)).Append(",\n");
            sb.Append("  \"move_recv_to_callback_ms_p95\": ").Append(MoveRecvToCallbackPercentile(95)).Append(",\n");
            sb.Append("  \"host_cpu_pct_avg\": ").Append(
                HostCpuPctAvg < 0 ? "\"无数据\"" : HostCpuPctAvg.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"host_cpu_pct_peak\": ").Append(
                HostCpuPctPeak < 0 ? "\"无数据\"" : HostCpuPctPeak.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"host_cpu_note\": \"").Append(HostCpuNote ?? "无数据").Append("\",\n");
            sb.Append("  \"host_stall_ms_max\": ").Append(HostStallMsMax).Append(",\n");
            sb.Append("  \"host_pool_queue_ms_max\": ").Append(HostPoolQueueMsMax).Append(",\n");
            sb.Append("  \"host_frame_ms_max\": ").Append(HostFrameMsMax).Append(",\n");
            sb.Append("  \"host_frame_ms_p50\": ").Append(HostFrameMsP50).Append(",\n");
            sb.Append("  \"host_gc_gen0\": ").Append(HostGcGen0).Append(",\n");
            sb.Append("  \"host_gc_gen1\": ").Append(HostGcGen1).Append(",\n");
            sb.Append("  \"host_gc_gen2\": ").Append(HostGcGen2).Append(",\n");
            sb.Append("  \"aoi_enter_events\": ").Append(AoiEnterEvents).Append(",\n");
            sb.Append("  \"aoi_leave_events\": ").Append(AoiLeaveEvents).Append(",\n");
            sb.Append("  \"aoi_move_events\": ").Append(AoiMoveEvents).Append(",\n");
            sb.Append("  \"aoi_move_per_sec\": ").Append(AoiMovePerSec.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"aoi_unknown_move\": ").Append(AoiUnknownMove).Append(",\n");
            sb.Append("  \"aoi_leave_then_move\": ").Append(AoiLeaveThenMove).Append(",\n");
            sb.Append("  \"aoi_move_with_start_ms\": ").Append(AoiMoveWithStartMs).Append(",\n");
            sb.Append("  \"aoi_move_start_ms_nonzero_ratio\": ").Append(
                MoveStartMsNonZeroRatio.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"aoi_gap_skipped\": ").Append(AoiGapSkipped).Append(",\n");
            sb.Append("  \"aoi_bare_frames\": ").Append(AoiBareFrames).Append(",\n");
            sb.Append("  \"aoi_visible_peak\": ").Append(AoiVisiblePeak).Append(",\n");
            sb.Append("  \"aoi_visible_last_sum\": ").Append(AoiVisibleLast).Append(",\n");
            sb.Append("  \"aoi_visible_samples\": ").Append(AoiVisibleSamples).Append(",\n");
            sb.Append("  \"aoi_visible_p50\": ").Append(AoiVisibleP50.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"aoi_visible_p95\": ").Append(AoiVisibleP95.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"aoi_visible_max\": ").Append(AoiVisibleMax.ToString("0.###", inv)).Append(",\n");
            sb.Append("  \"note\": \"missing fields are 0 or 无数据; planned_segment_sec=*segment_sec*; ")
                .Append("actual_depart_interval is depart→depart wall ms; ")
                .Append("rpc_latency is write_completed→MoveRsp; move_start_to_write is RequestAsync→write; ")
                .Append("move_recv_to_callback is matched-on-receive-thread→await continuation; ")
                .Append("exact GC pause ms unavailable — use host_stall_ms_max + gen counts\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public string CsvHeader()
        {
            return "loop_kind,sparse_patrol,cluster_size,cluster_spacing_m,cluster_radius_m,target_hz," +
                   "segment_min_sec,segment_max_sec,move_duty_pct,aoi_radius_m,safe_radius_m,bot_count,aoi_bot_backoff," +
                   "move_planned,move_sent,send_depart,send_arrive,send_resend,send_other,move_resend," +
                   "move_resend_abandon,move_ack_ok,move_rejected,move_timeout,unknown_after_write,heartbeat_ok," +
                   "actual_send_hz,actual_success_hz,duty,moving_time_ratio,moving_ms,dwelling_ms,p50,p95,p99," +
                   "segment_samples,segment_sec_p50,segment_sec_p95,segment_sec_max," +
                   "planned_segment_sec_p50,planned_segment_sec_p95,planned_segment_sec_max," +
                   "actual_depart_interval_samples,actual_depart_interval_ms_p50,actual_depart_interval_ms_p95," +
                   "actual_depart_interval_ms_max," +
                   "segment_complete_arrived_by_confirm,segment_complete_timer_expired," +
                   "segment_complete_rejected,segment_complete_other," +
                   "segment_m_p50,segment_m_p95,segment_m_max," +
                   "bot_send_hz_p50,bot_send_hz_p95,bot_send_hz_max," +
                   "move_start_to_write_ms_p50,move_start_to_write_ms_p95," +
                   "move_recv_to_callback_ms_p50,move_recv_to_callback_ms_p95," +
                   "aoi_enter,aoi_leave,aoi_move_events,aoi_unknown_move,aoi_leave_then_move,aoi_gap_skipped," +
                   "aoi_visible_peak,aoi_visible_p50,aoi_visible_p95,aoi_visible_max";
        }

        public string ToCsvRow()
        {
            var inv = CultureInfo.InvariantCulture;
            return (LoopKind ?? "无数据") + "," +
                   (SparsePatrol ? "1" : "0") + "," +
                   ClusterSize + "," +
                   ClusterSpacingMeters.ToString("0.###", inv) + "," +
                   ClusterRadiusMeters.ToString("0.###", inv) + "," +
                   TargetHz + "," +
                   SegmentMinSec.ToString("0.###", inv) + "," +
                   SegmentMaxSec.ToString("0.###", inv) + "," +
                   MoveDutyPct + "," +
                   AoiRadius.ToString("0.###", inv) + "," +
                   SafeRadius.ToString("0.###", inv) + "," +
                   BotCount + "," +
                   (AoiBotBackoff ? "1" : "0") + "," +
                   Planned + "," + Sent + "," +
                   SendDepart + "," + SendArrive + "," + SendResend + "," + SendOther + "," +
                   ResendCount + "," + ResendAbandonCount + "," +
                   AckOk + "," + Rejected + "," + Timeout + "," + UnknownOutcome + "," +
                   HeartbeatOk + "," +
                   ActualSendHz.ToString("0.###", inv) + "," +
                   ActualSuccessHz.ToString("0.###", inv) + "," +
                   DutyRatio.ToString("0.###", inv) + "," +
                   MovingTimeRatio.ToString("0.###", inv) + "," +
                   MovingMs + "," + DwellingMs + "," +
                   LatencyPercentile(50) + "," + LatencyPercentile(95) + "," +
                   LatencyPercentile(99) + "," +
                   SegmentSampleCount + "," +
                   SegmentSecPercentile(50).ToString("0.###", inv) + "," +
                   SegmentSecPercentile(95).ToString("0.###", inv) + "," +
                   SegmentSecMax().ToString("0.###", inv) + "," +
                   SegmentSecPercentile(50).ToString("0.###", inv) + "," +
                   SegmentSecPercentile(95).ToString("0.###", inv) + "," +
                   SegmentSecMax().ToString("0.###", inv) + "," +
                   ActualDepartIntervalSampleCount + "," +
                   ActualDepartIntervalPercentile(50).ToString("0.###", inv) + "," +
                   ActualDepartIntervalPercentile(95).ToString("0.###", inv) + "," +
                   ActualDepartIntervalMax().ToString("0.###", inv) + "," +
                   SegmentCompleteArrivedByConfirm + "," + SegmentCompleteTimerExpired + "," +
                   SegmentCompleteRejected + "," + SegmentCompleteOther + "," +
                   SegmentMetersPercentile(50).ToString("0.###", inv) + "," +
                   SegmentMetersPercentile(95).ToString("0.###", inv) + "," +
                   SegmentMetersMax().ToString("0.###", inv) + "," +
                   BotSendHzP50.ToString("0.###", inv) + "," +
                   BotSendHzP95.ToString("0.###", inv) + "," +
                   BotSendHzMax.ToString("0.###", inv) + "," +
                   MoveStartToWritePercentile(50) + "," + MoveStartToWritePercentile(95) + "," +
                   MoveRecvToCallbackPercentile(50) + "," + MoveRecvToCallbackPercentile(95) + "," +
                   AoiEnterEvents + "," + AoiLeaveEvents + "," + AoiMoveEvents + "," +
                   AoiUnknownMove + "," + AoiLeaveThenMove + "," + AoiGapSkipped + "," +
                   AoiVisiblePeak + "," +
                   AoiVisibleP50.ToString("0.###", inv) + "," +
                   AoiVisibleP95.ToString("0.###", inv) + "," +
                   AoiVisibleMax.ToString("0.###", inv);
        }
    }
}
