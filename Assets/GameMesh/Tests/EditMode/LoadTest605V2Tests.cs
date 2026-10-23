using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.LoadTest;
using GameMesh.Network;
using GameMesh.Protocol;
using NUnit.Framework;

namespace GameMesh.Tests.EditMode
{
    public sealed class LoadTest605V2Tests
    {
        [Test]
        public void Pending_TimeoutThenLate_DoesNotCountAck()
        {
            var table = new RpcPendingTable();
            Assert.IsTrue(table.TryRegister(1, 10, 7, out var slot));
            Assert.IsTrue(table.TryTimeout(slot, false));
            var match = table.TryComplete(1, 10, 7, true, out _);
            Assert.AreEqual(RpcPendingTable.Match.Late, match);
            Assert.AreEqual(0, table.Metrics.AckOk);
            Assert.AreEqual(1, table.Metrics.Timeout);
            Assert.AreEqual(1, table.Metrics.Late);
            Assert.IsTrue(table.Metrics.Conserves(table.Count));
        }

        [Test]
        public void Pending_TimeoutAndComplete_OneWinner()
        {
            var table = new RpcPendingTable();
            Assert.IsTrue(table.TryRegister(1, 4, 0, out var slot));
            var timeoutWon = 0;
            var completeWon = 0;
            Parallel.For(0, 2, i =>
            {
                if (i == 0)
                {
                    if (table.TryTimeout(slot, false))
                        Interlocked.Increment(ref timeoutWon);
                }
                else if (table.TryComplete(1, 4, 0, true, out _) == RpcPendingTable.Match.Completed)
                {
                    Interlocked.Increment(ref completeWon);
                }
            });
            Assert.AreEqual(1, timeoutWon + completeWon);
            Assert.AreEqual(1, table.Metrics.Timeout + table.Metrics.AckOk);
            Assert.IsTrue(table.Metrics.Conserves(table.Count));
        }

        [Test]
        public void Pending_OldGeneration_DoesNotCompleteNewRequest()
        {
            var table = new RpcPendingTable();
            Assert.IsTrue(table.TryRegister(2, 8, 0, out _));
            var match = table.TryComplete(1, 8, 0, true, out _);
            Assert.AreEqual(RpcPendingTable.Match.GenerationMismatch, match);
            Assert.AreEqual(0, table.Metrics.AckOk);
            Assert.AreEqual(1, table.Count);
            var ok = table.TryComplete(2, 8, 0, true, out _);
            Assert.AreEqual(RpcPendingTable.Match.Completed, ok);
            Assert.AreEqual(1, table.Metrics.AckOk);
        }

        [Test]
        public void Pending_OpcodeMismatch_DoesNotComplete()
        {
            var table = new RpcPendingTable();
            Assert.IsTrue(table.TryRegister(1, 3, 11, out _));
            var mismatch = table.TryComplete(1, 3, 12, true, out _);
            Assert.AreEqual(RpcPendingTable.Match.OpcodeMismatch, mismatch);
            Assert.AreEqual(0, table.Metrics.AckOk);
            var ok = table.TryComplete(1, 3, 11, false, out _);
            Assert.AreEqual(RpcPendingTable.Match.Completed, ok);
            Assert.AreEqual(1, table.Metrics.ExplicitReject);
            Assert.AreEqual(0, table.Metrics.AckOk);
        }

        [Test]
        public void Conservation_Screenshot413_IsStillPending()
        {
            const long sent = 72096;
            const long ack = 34103;
            const long reject = 0;
            const long timeout = 37580;
            const long cancelled = 0;
            const long other = 0;
            const long stillPending = sent - ack - reject - timeout - cancelled - other;
            Assert.AreEqual(413, stillPending);
            var metrics = new RpcPipeMetrics
            {
                Sent = sent,
                AckOk = ack,
                ExplicitReject = reject,
                Timeout = timeout,
                Cancelled = cancelled,
                OtherTerminal = other
            };
            Assert.IsTrue(metrics.Conserves(stillPending));
        }

        [Test]
        public void MoveTimeout_DoesNotResumeFromStalePoseUntilSecondTimeout()
        {
            Assert.AreEqual(0, BotMovementMetrics.HoldMsAfterTimeouts(1));
            Assert.AreEqual(2000, BotMovementMetrics.HoldMsAfterTimeouts(2));
            Assert.AreEqual(4000, BotMovementMetrics.HoldMsAfterTimeouts(3));
            Assert.AreEqual(15000, BotMovementMetrics.HoldMsAfterTimeouts(10));
            Assert.IsTrue(BotMovementMetrics.IsBusy("ERR_OVERLOADED"));
            Assert.IsTrue(BotMovementMetrics.IsBusy("ERR_BUSY"));
            Assert.IsFalse(BotMovementMetrics.IsBusy("ERR_MOVE_TOO_FAST"));
        }

        [Test]
        public void HeartbeatPause_IsBoundedAndSeparateFromMoveTimeout()
        {
            Assert.AreEqual(5000, BotMovementMetrics.HeartbeatPauseMs(1));
            Assert.AreEqual(10000, BotMovementMetrics.HeartbeatPauseMs(2));
            Assert.AreEqual(30000, BotMovementMetrics.HeartbeatPauseMs(8));
            Assert.AreEqual(2000, BotMovementMetrics.HoldMsAfterTimeouts(2));
        }

        [Test]
        public void Snapshot_EmptyDoesNotReplaceKnownSet()
        {
            Assert.IsFalse(WorldSnapshotApplier.ShouldReplaceEntities(98, 0, true, "INSTANCE_LIVE"));
            Assert.IsFalse(WorldSnapshotApplier.ShouldReplaceEntities(98, 0, false, ""));
            Assert.IsFalse(WorldSnapshotApplier.ShouldReplaceEntities(98, 0, true, "NOT_READY"));
            Assert.IsFalse(WorldSnapshotApplier.ShouldReplaceEntities(0, 0, true, "NOT_READY"));
            Assert.IsTrue(WorldSnapshotApplier.ShouldReplaceEntities(0, 0, true, "INSTANCE_LIVE"));
            Assert.IsTrue(WorldSnapshotApplier.ShouldReplaceEntities(98, 40, true, "INSTANCE_LIVE"));
        }

        [Test]
        public void Snapshot_OldEpochAndOtherInstance_AreRejected()
        {
            Assert.IsFalse(WorldSnapshotApplier.AcceptsEpoch(5, 4));
            Assert.IsTrue(WorldSnapshotApplier.AcceptsEpoch(5, 5));
            Assert.IsTrue(WorldSnapshotApplier.AcceptsEpoch(0, 4));
            Assert.IsFalse(BotMovementSettings.SameMapInstance(100, 200));
            Assert.IsTrue(BotMovementSettings.SameMapInstance(100, 100));
        }

        [Test]
        public void PushGap_SnapshotBaseline_ThenContiguousDelta()
        {
            var push = new PushReliability();
            push.Observe(1);
            push.MarkApplied(1);
            Assert.AreEqual(PushReliability.Decision.Gap, push.Observe(3));
            Assert.IsTrue(push.HasGap);
            var cache = new PushGapCache();
            Assert.IsTrue(cache.TryBuffer(3, new GameResponse()));
            Assert.IsFalse(cache.TryTake(2, out _));
            push.Reset(2);
            Assert.IsFalse(push.HasGap);
            Assert.AreEqual(PushReliability.Decision.Apply, push.Observe(3));
            push.MarkApplied(3);
            Assert.AreEqual(PushReliability.Decision.Duplicate, push.Observe(2));
        }

        [Test]
        public void PendingMatch_DoesNotNeedMainThread()
        {
            var table = new RpcPendingTable();
            Assert.IsTrue(table.TryRegister(1, 1, 0, out _));
            var match = table.TryComplete(1, 1, 0, true, out _);
            Assert.AreEqual(RpcPendingTable.Match.Completed, match);
            Assert.AreEqual(1, table.Metrics.PendingHit);
            Assert.AreEqual(0, table.Metrics.ReadLoopLagMs);
        }
    }
}
