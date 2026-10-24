using System;
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
        public int Paused;
        public int PathRejected;
        public int Corrections;
        public float CorrectionDistanceSum;
        public int SchedulerLagMs;
        public long SampleStartedMs;
        public long SampleEndedMs;
        readonly int[] _latency = new int[256];
        int _latencyCount;
        int _latencyCursor;

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

        public void NoteLatency(int ms)
        {
            if (ms < 0)
                ms = 0;
            _latency[_latencyCursor] = ms;
            _latencyCursor = (_latencyCursor + 1) % _latency.Length;
            if (_latencyCount < _latency.Length)
                _latencyCount++;
        }

        public double ActualSendHz
        {
            get
            {
                var sec = ElapsedSec;
                return sec <= 0.001 ? 0 : Sent / sec;
            }
        }

        public double ActualSuccessHz
        {
            get
            {
                var sec = ElapsedSec;
                return sec <= 0.001 ? 0 : AckOk / sec;
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
            Paused += other.Paused;
            PathRejected += other.PathRejected;
            Corrections += other.Corrections;
            CorrectionDistanceSum += other.CorrectionDistanceSum;
            if (other.SchedulerLagMs > SchedulerLagMs)
                SchedulerLagMs = other.SchedulerLagMs;
        }

        public string ToJson(string runId)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\n");
            sb.Append("  \"run_id\": \"").Append(runId ?? "").Append("\",\n");
            sb.Append("  \"move_planned\": ").Append(Planned).Append(",\n");
            sb.Append("  \"move_sent\": ").Append(Sent).Append(",\n");
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
            sb.Append("  \"move_paused\": ").Append(Paused).Append(",\n");
            sb.Append("  \"path_rejected\": ").Append(PathRejected).Append(",\n");
            sb.Append("  \"corrections\": ").Append(Corrections).Append(",\n");
            sb.Append("  \"actual_send_hz\": ").Append(ActualSendHz.ToString("0.###")).Append(",\n");
            sb.Append("  \"actual_success_hz\": ").Append(ActualSuccessHz.ToString("0.###")).Append(",\n");
            sb.Append("  \"rpc_latency_p50\": ").Append(LatencyPercentile(50)).Append(",\n");
            sb.Append("  \"rpc_latency_p95\": ").Append(LatencyPercentile(95)).Append(",\n");
            sb.Append("  \"rpc_latency_p99\": ").Append(LatencyPercentile(99)).Append(",\n");
            sb.Append("  \"scheduler_lag_ms\": ").Append(SchedulerLagMs).Append(",\n");
            sb.Append("  \"note\": \"live stages are not filled by this export\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
