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

        public bool TryTimeout(Slot slot, bool cancelled, out bool frameWritten)
        {
            frameWritten = false;
            if (slot == null)
                return false;
            var next = cancelled ? Cancelled : TimedOut;
            lock (slot.Gate)
            {
                if (slot.State != Open)
                    return false;
                frameWritten = slot.Written != 0;
                slot.State = next;
            }

            var key = new RpcKey(slot.Generation, slot.RequestSeq);
            _open.TryRemove(key, out _);
            _seqGeneration.TryRemove(slot.RequestSeq, out _);
            _recentTimeout[key] = 1;
            if (_recentTimeout.Count > 2048)
                _recentTimeout.Clear();
            if (cancelled)
                Interlocked.Increment(ref Metrics.Cancelled);
            else
                Interlocked.Increment(ref Metrics.Timeout);
            return true;
        }

        public bool TryBeginWrite(int generation, ulong requestSeq)
        {
            var key = new RpcKey(generation, requestSeq);
            if (!_open.TryGetValue(key, out var slot))
                return !_recentTimeout.ContainsKey(key);
            lock (slot.Gate)
            {
                if (slot.State != Open)
                    return false;
                slot.Written = 1;
                return true;
            }
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
            public int State;
            public int Written;
            public readonly object Gate = new object();

            public Slot(int generation, ulong requestSeq, int expectedResponseOpcode)
            {
                Generation = generation;
                RequestSeq = requestSeq;
                ExpectedResponseOpcode = expectedResponseOpcode;
            }
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
        public long FramesDecoded;
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

        public long Accounted => AckOk + ExplicitReject + Timeout + Cancelled + OtherTerminal;

        public bool Conserves(long stillPending) =>
            Sent == Accounted + stillPending;
    }
}
