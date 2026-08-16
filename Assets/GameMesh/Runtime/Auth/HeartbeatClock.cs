using System;
using System.Diagnostics;

namespace GameMesh.Auth
{
    public sealed class HeartbeatClock
    {
        readonly Stopwatch _watch = Stopwatch.StartNew();
        public long MonotonicMs => _watch.ElapsedMilliseconds;
        public int LastRttMs { get; private set; }
        public int SmoothedRttMs { get; private set; }
        public int JitterMs { get; private set; }
        public long ServerTimeOffsetMs { get; private set; }
        public long LastReceivedMonotonicMs { get; private set; }
        public bool HasSample { get; private set; }

        public void OnReply(long sendMonotonicMs, long recvMonotonicMs, long serverTimeMs, int jitterHintMs)
        {
            var rtt = (int)Math.Max(0, recvMonotonicMs - sendMonotonicMs);
            LastRttMs = rtt;
            SmoothedRttMs = !HasSample ? rtt : (SmoothedRttMs * 7 + rtt) / 8;
            JitterMs = jitterHintMs > 0 ? jitterHintMs : Math.Abs(rtt - SmoothedRttMs);
            var localUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ServerTimeOffsetMs = serverTimeMs - (localUnixMs - rtt / 2);
            LastReceivedMonotonicMs = recvMonotonicMs;
            HasSample = true;
        }

        public bool IdleTimedOut(int idleTimeoutMs)
        {
            if (idleTimeoutMs <= 0 || !HasSample)
                return false;
            return MonotonicMs - LastReceivedMonotonicMs > idleTimeoutMs;
        }
    }
}
