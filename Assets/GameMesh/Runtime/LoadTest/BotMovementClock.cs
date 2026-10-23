using System.Diagnostics;

namespace GameMesh.LoadTest
{
    public enum BotMoveTickKind
    {
        Wait = 0,
        SkipNotReady = 1,
        SkipBusy = 2,
        Send = 3
    }

    public struct BotMoveTick
    {
        public BotMoveTickKind Kind;
        public int WaitMs;
    }

    /// <summary>
    /// One send slot per interval. Missed intervals are dropped, not replayed.
    /// </summary>
    public sealed class BotMovementClock
    {
        readonly BotMovementSettings _settings;
        readonly int _botIndex;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        bool _armed;
        long _nextDueMs;
        int _tick;

        public BotMovementMetrics Metrics { get; } = new BotMovementMetrics();

        public BotMovementClock(BotMovementSettings settings, int botIndex)
        {
            _settings = settings ?? new BotMovementSettings();
            _botIndex = botIndex;
        }

        public long NowMs => _clock.ElapsedMilliseconds;

        public int IntervalMs
        {
            get
            {
                var hz = _settings.TargetHz < 1 ? 1 : _settings.TargetHz;
                return 1000 / hz;
            }
        }

        public BotMoveTick Evaluate(bool ready, int inflight)
        {
            return Evaluate(NowMs, ready, inflight);
        }

        public BotMoveTick Evaluate(long nowMs, bool ready, int inflight)
        {
            if (!_armed)
            {
                _armed = true;
                Metrics.SampleStartedMs = nowMs;
                _nextDueMs = nowMs + JitterMs(0);
                return Wait(_nextDueMs - nowMs);
            }

            if (nowMs < _nextDueMs)
                return Wait(_nextDueMs - nowMs);

            _tick++;
            Metrics.Planned++;
            Metrics.SampleEndedMs = nowMs;
            if (nowMs > _nextDueMs)
            {
                var lag = (int)(nowMs - _nextDueMs);
                if (lag > Metrics.SchedulerLagMs)
                    Metrics.SchedulerLagMs = lag;
            }
            // Drop every missed slot. Recovery must not burst.
            _nextDueMs = nowMs + IntervalMs + JitterMs(_tick);
            var cap = _settings.MaxInflight < 1 ? 1 : _settings.MaxInflight;
            if (!ready)
            {
                Metrics.SkippedNotReady++;
                return new BotMoveTick { Kind = BotMoveTickKind.SkipNotReady, WaitMs = IntervalMs };
            }

            if (inflight >= cap)
            {
                Metrics.SkippedBusy++;
                return new BotMoveTick { Kind = BotMoveTickKind.SkipBusy, WaitMs = IntervalMs };
            }

            return new BotMoveTick { Kind = BotMoveTickKind.Send, WaitMs = 0 };
        }

        int JitterMs(int tick)
        {
            var pct = _settings.JitterPct;
            if (pct <= 0)
                return 0;
            var span = IntervalMs * pct / 100;
            if (span <= 0)
                return 0;
            var roll = BotMovementSettings.StablePercent(
                _settings.Seed ^ (tick * 17), _botIndex + tick);
            return (roll % (span * 2 + 1)) - span;
        }

        static BotMoveTick Wait(long ms)
        {
            if (ms < 1)
                ms = 1;
            if (ms > 200)
                ms = 200;
            return new BotMoveTick { Kind = BotMoveTickKind.Wait, WaitMs = (int)ms };
        }
    }
}
