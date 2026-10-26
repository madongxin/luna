using System;
using System.Collections.Concurrent;
using System.Threading;

namespace GameMesh.Network
{
    /// <summary>
    /// One in-flight RPC. The key is this connection generation plus GameRequest.Seq.
    /// That seq is the request id. It is not a move state sequence and not a push server_seq.
    /// </summary>
    public sealed class RpcPendingTable
    {
        public const int Open = 0;
        public const int Completed = 1;
        public const int TimedOut = 2;
        public const int Cancelled = 3;
        public const int Failed = 4;

        readonly ConcurrentDictionary<RpcKey, Slot> _open = new ConcurrentDictionary<RpcKey, Slot>();
        readonly ConcurrentDictionary<ulong, int> _seqGeneration = new ConcurrentDictionary<ulong, int>();
        readonly ConcurrentDictionary<RpcKey, byte> _recentTimeout = new ConcurrentDictionary<RpcKey, byte>();

        public RpcPipeMetrics Metrics { get; } = new RpcPipeMetrics();

        public int OpenCount => _open.Count;
        public int Count => _open.Count;

        public bool TryRegister(int generation, ulong requestSeq, int expectedResponseOpcode, out Slot slot)
        {
            slot = new Slot(generation, requestSeq, expectedResponseOpcode);
            var key = new RpcKey(generation, requestSeq);
            if (!_open.TryAdd(key, slot))
                return false;
            _seqGeneration[requestSeq] = generation;
            Interlocked.Increment(ref Metrics.Sent);
            return true;
        }

        public bool TryAbandon(Slot slot)
        {
            if (slot == null)
                return false;
            if (Interlocked.CompareExchange(ref slot.State, Failed, Open) != Open)
                return false;
            _open.TryRemove(new RpcKey(slot.Generation, slot.RequestSeq), out _);
            _seqGeneration.TryRemove(slot.RequestSeq, out _);
            Interlocked.Decrement(ref Metrics.Sent);
            return true;
        }

        public bool TryTimeout(Slot slot, bool cancelled, out int phase)
        {
            phase = Slot.PhaseIdle;
            if (slot == null)
                return false;
            var next = cancelled ? Cancelled : TimedOut;
            lock (slot.Gate)
            {
                if (slot.State != Open)
                    return false;
                phase = slot.Phase;
                slot.State = next;
            }

            var key = new RpcKey(slot.Generation, slot.RequestSeq);
            _open.TryRemove(key, out _);
            _seqGeneration.TryRemove(slot.RequestSeq, out _);
            if (_recentTimeout.Count < 8192)
                _recentTimeout[key] = 1;
            if (cancelled)
                Interlocked.Increment(ref Metrics.Cancelled);
            else
            {
                Interlocked.Increment(ref Metrics.Timeout);
                if (phase == Slot.PhaseIdle)
                    Interlocked.Increment(ref Metrics.ExpiredBeforeWrite);
                else if (phase == Slot.PhaseWriteStarted)
                    Interlocked.Increment(ref Metrics.TimeoutAfterWriteStarted);
                else
                    Interlocked.Increment(ref Metrics.TimeoutAfterWriteComplete);
            }

            return true;
        }

        /// <summary>
        /// Deterministic deadline sweep used by the connection watchdog. Per-request timer
        /// callbacks can be delayed for minutes when hundreds of bots create timer pressure.
        /// </summary>
        public int ExpireDue(long nowMs, Action<Slot, int> onExpired)
        {
            var count = 0;
            foreach (var pair in _open)
            {
                var slot = pair.Value;
                if (slot == null || slot.DeadlineAtMs <= 0 || nowMs < slot.DeadlineAtMs)
                    continue;
                if (!TryTimeout(slot, false, out var phase))
                    continue;
                count++;
                onExpired?.Invoke(slot, phase);
            }

            return count;
        }

        public SendDecision PrepareSend(Slot slot, long nowMs)
        {
            if (slot == null)
                return SendDecision.Send;
            lock (slot.Gate)
            {
                if (slot.State != Open)
                    return SendDecision.Closed;
                if (slot.DeadlineAtMs > 0 && nowMs >= slot.DeadlineAtMs)
                    return SendDecision.Expired;
                slot.Phase = Slot.PhaseWriteStarted;
                return SendDecision.Send;
            }
        }

        public void MarkWriteCompleted(Slot slot)
        {
            if (slot == null)
                return;
            lock (slot.Gate)
            {
                if (slot.Phase == Slot.PhaseWriteStarted)
                    slot.Phase = Slot.PhaseWriteCompleted;
            }

            Interlocked.Increment(ref Metrics.WriteCompleted);
        }

        public Match TryComplete(int generation, ulong responseSeq, int responseOpcode, bool ok, out Slot slot)
        {
            slot = null;
            var key = new RpcKey(generation, responseSeq);
            if (!_open.TryGetValue(key, out slot))
            {
                if (_recentTimeout.TryRemove(key, out _))
                {
                    Interlocked.Increment(ref Metrics.Late);
                    return Match.Late;
                }

                if (_seqGeneration.TryGetValue(responseSeq, out var other) && other != generation)
                {
                    Interlocked.Increment(ref Metrics.GenerationMismatch);
                    return Match.GenerationMismatch;
                }

                Interlocked.Increment(ref Metrics.Unmatched);
                return Match.Unmatched;
            }

            lock (slot.Gate)
            {
                if (slot.ExpectedResponseOpcode != 0 && responseOpcode != slot.ExpectedResponseOpcode)
                {
                    Interlocked.Increment(ref Metrics.OpcodeMismatch);
                    slot = null;
                    return Match.OpcodeMismatch;
                }

                if (slot.State != Open)
                {
                    Interlocked.Increment(ref Metrics.Late);
                    slot = null;
                    return Match.Late;
                }

                slot.State = Completed;
            }

            _open.TryRemove(key, out _);
            _seqGeneration.TryRemove(responseSeq, out _);
            Interlocked.Increment(ref Metrics.PendingHit);
            if (ok)
                Interlocked.Increment(ref Metrics.AckOk);
            else
                Interlocked.Increment(ref Metrics.ExplicitReject);
            return Match.Completed;
        }

        public void FailAll(System.Collections.Generic.List<object> tags)
        {
            foreach (var pair in _open)
            {
                lock (pair.Value.Gate)
                {
                    if (pair.Value.State != Open)
                        continue;
                    pair.Value.State = Failed;
                }
                if (pair.Value.State != Failed)
                    continue;
                if (_open.TryRemove(pair.Key, out _))
                {
                    _seqGeneration.TryRemove(pair.Key.RequestSeq, out _);
                    _recentTimeout[pair.Key] = 1;
                    Interlocked.Increment(ref Metrics.OtherTerminal);
                    tags?.Add(pair.Value.Tag);
                }
            }
        }

        public void NoteDequeued()
        {
            Interlocked.Increment(ref Metrics.Dequeued);
        }

        public void NoteWriteStarted()
        {
            Interlocked.Increment(ref Metrics.WriteStarted);
        }

        public void NoteWriteRequested()
        {
            Interlocked.Increment(ref Metrics.WriteRequested);
            Interlocked.Increment(ref Metrics.QueueAccepted);
        }

        public void NoteSkippedBeforeWrite()
        {
            Interlocked.Increment(ref Metrics.SkippedBeforeWrite);
        }

        public void NoteBytesSent()
        {
            Interlocked.Increment(ref Metrics.BytesSent);
        }

        public void NoteFrameDecoded()
        {
            Interlocked.Increment(ref Metrics.FramesDecoded);
        }

        public void NoteBytesReceived(int n)
        {
            Interlocked.Add(ref Metrics.BytesReceived, n);
        }

        public void NoteOpcodeUnknown()
        {
            Interlocked.Increment(ref Metrics.OpcodeUnknown);
        }

        public void NoteReadLoopLag(long elapsedMs)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref Metrics.ReadLoopLagMs);
                if (elapsedMs <= current)
                    return;
            }
            while (Interlocked.CompareExchange(ref Metrics.ReadLoopLagMs, elapsedMs, current) != current);
        }

        public readonly struct RpcKey : IEquatable<RpcKey>
        {
            public readonly int Generation;
            public readonly ulong RequestSeq;

            public RpcKey(int generation, ulong requestSeq)
            {
                Generation = generation;
                RequestSeq = requestSeq;
            }

            public bool Equals(RpcKey other) => Generation == other.Generation && RequestSeq == other.RequestSeq;

            public override bool Equals(object obj) => obj is RpcKey other && Equals(other);

            public override int GetHashCode() => (Generation * 397) ^ RequestSeq.GetHashCode();
        }

        public sealed class Slot
        {
            public readonly int Generation;
            public readonly ulong RequestSeq;
            public readonly int ExpectedResponseOpcode;
            public object Tag;
            public const int PhaseIdle = 0;
            public const int PhaseWriteStarted = 1;
            public const int PhaseWriteCompleted = 2;
            public int State;
            public int Phase;
            public long DeadlineAtMs;
            public readonly object Gate = new object();

            public Slot(int generation, ulong requestSeq, int expectedResponseOpcode)
            {
                Generation = generation;
                RequestSeq = requestSeq;
                ExpectedResponseOpcode = expectedResponseOpcode;
            }
        }

        public enum SendDecision
        {
            Send = 0,
            Expired = 1,
            Closed = 2
        }

        public enum Match
        {
            Completed = 0,
            Late = 1,
            Unmatched = 2,
            GenerationMismatch = 3,
            OpcodeMismatch = 4
        }
    }

    public sealed class RpcPipeMetrics
    {
        public long WriteRequested;
        public long QueueAccepted;
        public long SkippedBeforeWrite;
        public long BytesSent;
        public long Dequeued;
        public long WriteStarted;
        public long WriteCompleted;
        public long ExpiredBeforeWrite;
        public long TimeoutAfterWriteStarted;
        public long TimeoutAfterWriteComplete;
        public long FramesDecoded;
        public long BytesReceived;
        public long OpcodeUnknown;
        public long PendingHit;
        public long Sent;
        public long AckOk;
        public long ExplicitReject;
        public long Timeout;
        public long Cancelled;
        public long OtherTerminal;
        public long Late;
        public long Unmatched;
        public long GenerationMismatch;
        public long OpcodeMismatch;
        public long ReadLoopLagMs;

        public readonly LatencyRing QueueWaitMs = new LatencyRing();

        public long Accounted => AckOk + ExplicitReject + Timeout + Cancelled + OtherTerminal;

        public bool Conserves(long stillPending) =>
            Sent == Accounted + stillPending;
    }

    public sealed class LatencyRing
    {
        readonly int[] _samples = new int[64];
        int _count;
        int _cursor;
        int _max;

        public int Max => _max;

        public void Add(int ms)
        {
            if (ms < 0)
                ms = 0;
            _samples[_cursor] = ms;
            _cursor = (_cursor + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
            if (ms > _max)
                _max = ms;
        }

        public int Percentile(int pct)
        {
            if (_count <= 0)
                return 0;
            var copy = new int[_count];
            var start = _count == _samples.Length ? _cursor : 0;
            for (var i = 0; i < _count; i++)
                copy[i] = _samples[(start + i) % _samples.Length];
            System.Array.Sort(copy);
            var index = (int)System.Math.Ceiling(pct / 100.0 * _count) - 1;
            if (index < 0)
                index = 0;
            if (index >= copy.Length)
                index = copy.Length - 1;
            return copy[index];
        }
    }
}
