using System.Collections.Generic;
using GameMesh.Aoi;
using GameMesh.LoadTest;
using GameMesh.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace GameMesh.Tests.EditMode
{
    /// <summary>
    /// Q1 evidence tests. Written first; they lock current sparse report behavior.
    /// </summary>
    public sealed class BotReportAuditTests
    {
        static BotMovementSettings SparseSettings()
        {
            return new BotMovementSettings
            {
                TargetHz = 5,
                SparsePatrol = true,
                SegmentMinSec = 10f,
                SegmentMaxSec = 30f,
                MoveDutyPct = 100,
                ServerMoveSpeed = 10f,
                WalkSpeed = 5.5f,
                AoiRadius = 32f,
                ClusterSize = 60,
                Seed = 20261113
            };
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        [Test]
        public void Q1_I1_KeepInside32_SegmentDuration_P50_IsAbout3to5Sec_NotConfigured10to30()
        {
            // Evidence for I1: KeepInside clamps goals to ~31m, then _segmentSec =
            // distance/ServerMoveSpeed(10) → ~3–5s, not SegmentMinSec/MaxSec 10–30.
            var settings = SparseSettings();
            var home = new Vector3(160f, -18f, 658f);
            var sparse = new SparsePatrolController(7, settings);
            sparse.KeepInside(home, settings.AoiRadius);
            sparse.Arm(0);

            var secs = new List<float>(1000);
            var meters = new List<float>(1000);
            var confirmed = home;
            long now = 0;
            var metrics = new BotMovementMetrics();
            for (var i = 0; i < 1000; i++)
            {
                MovePlan plan;
                do
                {
                    plan = sparse.Poll(confirmed, now, metrics, out var wait);
                    if (!plan.Send)
                        now += wait > 0 ? wait : 50;
                } while (!plan.Send);

                Assert.AreEqual("depart", plan.Reason);
                secs.Add(sparse.LastSegmentSec);
                meters.Add(sparse.LastSegmentMeters);
                sparse.NoteSent(plan.Yaw, now, plan.Reason, metrics);
                // Instantly arrive so the next Poll starts a new segment (no network).
                confirmed = plan.Position;
                now += (long)(sparse.LastSegmentSec * 1000f) + 1;
            }

            secs.Sort();
            meters.Sort();
            var p50 = secs[secs.Count / 2];
            var m50 = meters[meters.Count / 2];
            Assert.GreaterOrEqual(p50, 2.5f, "p50 segment sec unexpectedly short");
            Assert.LessOrEqual(p50, 5.5f,
                "I1 evidence: KeepInside+speed rewrite makes p50 ~3–5s, not SegmentMinSec=10");
            Assert.Less(p50, settings.SegmentMinSec - 0.5f,
                "configured SegmentMinSec=10 must not match actual p50 under KeepInside");
            Assert.LessOrEqual(m50, settings.AoiRadius,
                "KeepInside must keep segment distance inside the AOI disk");
        }

        [Test]
        public void Q1_I2_SlowConfirm_ProducesArriveAndResendBaseline()
        {
            // Evidence for I2: when confirmed pose stays at depart start past segment end,
            // Poll emits arrive; a 2s authority miss arms resend (backoff off).
            var settings = SparseSettings();
            settings.AoiBotBackoff = false;
            var home = new Vector3(100f, -18f, 200f);
            var sparse = new SparsePatrolController(3, settings);
            sparse.KeepInside(home, 32f);
            sparse.Arm(0);
            var metrics = new BotMovementMetrics();
            long now = 0;
            var depart = sparse.Poll(home, now, metrics, out _);
            Assert.IsTrue(depart.Send);
            Assert.AreEqual("depart", depart.Reason);
            sparse.NoteSent(depart.Yaw, now, "depart", metrics);

            // Confirmed stuck at home (slow MoveRsp). After segment clock, NeedArrive fires.
            now = (long)(sparse.LastSegmentSec * 1000f) + 1;
            var arrive = sparse.Poll(home, now, metrics, out _);
            Assert.IsTrue(arrive.Send, "stuck confirm past segment end must request arrive");
            Assert.AreEqual("arrive", arrive.Reason);
            sparse.NoteSent(arrive.Yaw, now, "arrive", metrics);

            var policy = new BotResendPolicy { BackoffEnabled = false };
            policy.Arm(depart.Position, now);
            Assert.IsTrue(policy.TryTakeResend(now + 3000, home.x, home.z, out _, out var abandon));
            Assert.IsFalse(abandon);
            metrics.NoteSendReason("resend");
            metrics.ResendCount = policy.ResendCount;

            Assert.AreEqual(1, metrics.SendDepart);
            Assert.AreEqual(1, metrics.SendArrive);
            Assert.AreEqual(1, metrics.SendResend);
            // Baseline for Q3: at least one arrive + one resend under 3s delayed confirm.
            Assert.GreaterOrEqual(metrics.SendArrive + metrics.SendResend, 2);
        }

        [Test]
        public void Q1_I3_MoveDutyPct50_SparseStillNear100PercentMoving()
        {
            // Evidence for I3: MoveDutyPct is ignored in sparse continuous mode.
            var settings = SparseSettings();
            settings.MoveDutyPct = 50;
            var sparse = new SparsePatrolController(1, settings);
            sparse.KeepInside(new Vector3(0, 0, 0), 32f);
            sparse.Arm(0);
            var metrics = new BotMovementMetrics { SampleStartedMs = 0 };
            var confirmed = Vector3.zero;
            long now = 0;
            for (var step = 0; step < 40; step++)
            {
                var plan = sparse.Poll(confirmed, now, metrics, out var wait);
                if (plan.Send)
                {
                    sparse.NoteSent(plan.Yaw, now, plan.Reason, metrics);
                    confirmed = plan.Position;
                    now += (long)(Mathf.Max(0.05f, sparse.LastSegmentSec) * 1000f);
                }
                else
                    now += wait > 0 ? wait : 50;
            }

            metrics.SampleEndedMs = now;
            Assert.AreEqual(0, metrics.DwellingMs,
                "I3: sparse continuous never accounts DwellingMs today");
            Assert.Greater(metrics.MovingMs, 0);
            Assert.GreaterOrEqual(metrics.DutyRatio, 0.99,
                "I3 evidence: MoveDutyPct=50 still yields ~100% duty (MovingMs only)");
        }

        [Test]
        public void Q1_F5_GapMustStillCountAoiEventsForStats()
        {
            // Failing until stats-only gap path counts MOVE/ENTER without advancing the cursor.
            var push = new BotPushTracker();
            push.BindInstance(9, 1);
            Assert.IsTrue(push.ObserveServerPush(
                new ServerPushEnvelope { Reliable = true, ServerSeq = 1 },
                Delta(9, AoiOp.Enter, 42)));
            Assert.AreEqual(1, push.AoiEnterEvents);

            // Gap: seq jumps 1 → 3. Reliability book stays broken, but stats must still see MOVE.
            Assert.IsFalse(push.ObserveServerPush(
                new ServerPushEnvelope { Reliable = true, ServerSeq = 3 },
                Delta(9, AoiOp.Move, 42)));
            Assert.IsTrue(push.HasGap);
            Assert.AreEqual(1, push.AoiMoveEvents,
                "F5 fix: gapped AOI frames must still increment move counters for export");
            Assert.Greater(push.AoiGapSkipped, 0);
        }

        [Test]
        public void Q1_F5_BareAoiDelta_IsCounted()
        {
            var push = new BotPushTracker();
            push.BindInstance(9, 1);
            var delta = new AoiDelta { MapInstanceId = 9 };
            delta.Events.Add(new AoiEvent
            {
                Op = (int)AoiOp.Enter,
                Entity = new EntitySnapshot { PlayerId = 7 }
            });
            push.ObserveBareAoiDelta(delta);
            Assert.AreEqual(1, push.AoiEnterEvents);
            Assert.AreEqual(1, push.VisibleCount);
        }

        [Test]
        public void Q2_SparseDuty60_ActualDutyWithin55to65_OverTenMinutes()
        {
            var settings = SparseSettings();
            settings.SparseDutyPct = 60;
            settings.SparseDwellMinSec = 2f;
            settings.SparseDwellMaxSec = 20f;
            settings.SyncWave = false;
            var sparse = new SparsePatrolController(11, settings);
            sparse.KeepInside(new Vector3(160f, -18f, 658f), 32f);
            sparse.Arm(0);
            var metrics = new BotMovementMetrics { SampleStartedMs = 0 };
            var confirmed = new Vector3(160f, -18f, 658f);
            const long endMs = 10 * 60 * 1000L;
            long now = 0;
            while (now < endMs)
            {
                var plan = sparse.Poll(confirmed, now, metrics, out var wait);
                if (plan.Send)
                {
                    sparse.NoteSent(plan.Yaw, now, plan.Reason, metrics);
                    if (plan.Reason == "depart" || plan.Reason == "arrive")
                        confirmed = plan.Position;
                }

                now += wait > 0 ? wait : 50;
            }

            metrics.SampleEndedMs = now;
            Assert.Greater(metrics.DwellingMs, 0);
            Assert.GreaterOrEqual(metrics.DutyRatio, 0.55, metrics.DutyRatio.ToString("0.###"));
            Assert.LessOrEqual(metrics.DutyRatio, 0.65, metrics.DutyRatio.ToString("0.###"));
        }

        [Test]
        public void Q2_StartPhase_IsSpreadWhenDutyBelow100()
        {
            var settings = SparseSettings();
            settings.SparseDutyPct = 60;
            settings.SyncWave = false;
            settings.SegmentMaxSec = 20f;
            var starts = new List<long>();
            for (var i = 0; i < 200; i++)
            {
                var sparse = new SparsePatrolController(i, settings);
                sparse.Arm(0);
                starts.Add(sparse.StartReadyMs);
            }

            starts.Sort();
            Assert.Greater(starts[starts.Count - 1], 0);
            // No single second should contain more than ~15% of bots.
            var worstBucket = 0;
            for (var i = 0; i < starts.Count; )
            {
                var sec = starts[i] / 1000L;
                var j = i;
                while (j < starts.Count && starts[j] / 1000L == sec)
                    j++;
                var n = j - i;
                if (n > worstBucket)
                    worstBucket = n;
                i = j;
            }

            Assert.Less(worstBucket, starts.Count * 0.15 + 1);
        }

        [Test]
        public void Q2_Duty100_DepartSequenceMatchesContinuousSparse()
        {
            // Default SparseDutyPct=100 must keep the previous continuous depart stream.
            Vector3[] Run(int duty)
            {
                var settings = SparseSettings();
                settings.SparseDutyPct = duty;
                settings.SyncWave = false;
                var sparse = new SparsePatrolController(5, settings);
                sparse.KeepInside(new Vector3(100f, -18f, 200f), 32f);
                sparse.Arm(0);
                var metrics = new BotMovementMetrics();
                var confirmed = new Vector3(100f, -18f, 200f);
                long now = 0;
                var goals = new List<Vector3>();
                for (var n = 0; n < 8; )
                {
                    var plan = sparse.Poll(confirmed, now, metrics, out var wait);
                    if (plan.Send && plan.Reason == "depart")
                    {
                        goals.Add(plan.Position);
                        sparse.NoteSent(plan.Yaw, now, plan.Reason, metrics);
                        confirmed = plan.Position;
                        now += (long)(sparse.LastSegmentSec * 1000f) + 1;
                        n++;
                    }
                    else
                        now += wait > 0 ? wait : 50;
                }

                return goals.ToArray();
            }

            var a = Run(100);
            var b = Run(100);
            Assert.AreEqual(a.Length, b.Length);
            for (var i = 0; i < a.Length; i++)
                Assert.AreEqual(a[i], b[i]);
            Assert.AreEqual(0f, SparsePatrolController.ComputeDwellSec(4f, 100, 2f, 20f));
            Assert.AreEqual(8f / 3f, SparsePatrolController.ComputeDwellSec(4f, 60, 2f, 20f), 0.01f);
        }

        [Test]
        public void Q1_I4_SparseBackoffPolicy_ArmsWhenEnabled()
        {
            // aoi_bot_backoff must be usable on the same policy the sparse loop calls.
            var on = new BotResendPolicy { BackoffEnabled = true, MaxSameTargetResends = 3 };
            on.Arm(new Vector3(10, 0, 0), 0);
            Assert.IsTrue(on.TryTakeResend(0, 0, 0, out _, out _));
            on.Arm(new Vector3(10, 0, 0), 100);
            Assert.IsFalse(on.TryTakeResend(100, 0, 0, out _, out _),
                "after first resend, backoff waits before the next");
            Assert.IsTrue(on.TryTakeResend(100 + 2000, 0, 0, out _, out _));
        }

        [Test]
        public void Q3_ArriveWait_AndBackoff_CutArrivePlusResendBelowOneThirdOfBaseline()
        {
            var baseline = CountArriveAndResend(arriveWait: false, backoff: false);
            var gated = CountArriveAndResend(arriveWait: true, backoff: true);
            Assert.Greater(baseline, 0);
            Assert.LessOrEqual(gated, baseline / 3,
                "Q3: arrive_wait+backoff must cut arrive+resend to ≤1/3 of Q1 baseline");
            Assert.AreEqual(3000, SparsePatrolController.ArriveWaitMs(0));
            Assert.AreEqual(4500, SparsePatrolController.ArriveWaitMs(1500));
        }

        [Test]
        public void Q3_AbandonTarget_PicksNewGoal()
        {
            var settings = SparseSettings();
            settings.AoiBotBackoff = true;
            var sparse = new SparsePatrolController(8, settings);
            sparse.KeepInside(new Vector3(0, 0, 0), 32f);
            sparse.Arm(0);
            var metrics = new BotMovementMetrics();
            var first = sparse.Poll(Vector3.zero, 0, metrics, out _);
            Assert.IsTrue(first.Send);
            sparse.NoteSent(first.Yaw, 0, "depart", metrics);
            sparse.AbandonTarget();
            var next = sparse.Poll(Vector3.zero, 100, metrics, out _);
            Assert.IsTrue(next.Send);
            Assert.AreEqual("depart", next.Reason);
            Assert.AreNotEqual(first.Position, next.Position);
        }

        static int CountArriveAndResend(bool arriveWait, bool backoff)
        {
            var settings = SparseSettings();
            settings.SparseArriveWait = arriveWait;
            settings.AoiBotBackoff = backoff;
            settings.SparseDutyPct = 100;
            var sparse = new SparsePatrolController(3, settings);
            sparse.KeepInside(new Vector3(100f, -18f, 200f), 32f);
            sparse.Arm(0);
            var metrics = new BotMovementMetrics();
            var policy = new BotResendPolicy
            {
                BackoffEnabled = backoff,
                MaxSameTargetResends = 3,
                BackoffCapSec = 30
            };
            var home = new Vector3(100f, -18f, 200f);
            long now = 0;
            const long end = 60000;
            while (now < end)
            {
                if (policy.Armed)
                {
                    if (policy.TryTakeResend(now, home.x, home.z, out _, out var abandon))
                        metrics.NoteSendReason("resend");
                    else if (abandon)
                        sparse.AbandonTarget();
                    now += 50;
                    continue;
                }

                var plan = sparse.Poll(home, now, metrics, out var wait);
                if (plan.Send)
                {
                    sparse.NoteSent(plan.Yaw, now, plan.Reason, metrics);
                    // Simulate 3s delayed authority: never ack, arm resend after 2s budget.
                    if (plan.Reason == "depart")
                        policy.Arm(plan.Position, now + 2000);
                }

                now += wait > 0 ? wait : 50;
            }

            return metrics.SendArrive + metrics.SendResend;
        }

        [Test]
        public void Q4_HeartbeatReasons_AreClassified()
        {
            var m = new BotMovementMetrics();
            m.NoteHeartbeat(true, null, 0);
            m.NoteHeartbeat(false, "timeout", 60_000);
            m.NoteHeartbeat(false, "disconnect", 60_000);
            m.NoteHeartbeat(false, "reject", 120_000);
            Assert.AreEqual(1, m.HeartbeatOk);
            Assert.AreEqual(3, m.HeartbeatFail);
            Assert.AreEqual(1, m.HeartbeatFailTimeout);
            Assert.AreEqual(1, m.HeartbeatFailDisconnect);
            Assert.AreEqual(1, m.HeartbeatFailReject);
            StringAssert.Contains("\"fail\":1", m.HeartbeatMinutesJson());
        }

        static GameResponse Delta(ulong mapInstanceId, AoiOp op, ulong playerId)
        {
            var rsp = new GameResponse
            {
                AoiDelta = new AoiDelta { MapInstanceId = mapInstanceId }
            };
            rsp.AoiDelta.Events.Add(new AoiEvent
            {
                Op = (int)op,
                Entity = new EntitySnapshot { PlayerId = playerId }
            });
            return rsp;
        }
    }
}
