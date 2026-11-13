using GameMesh.Aoi;
using GameMesh.LoadTest;
using GameMesh.Map;
using GameMesh.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace GameMesh.Tests.EditMode
{
    public sealed class BotMovementTests
    {
        static BotMovementSettings Settings(int hz = 5)
        {
            return new BotMovementSettings { TargetHz = hz, JitterPct = 10, Seed = 20261021 };
        }

        [Test]
        public void Cli01_SameSeed_ReplaysWaypointSequence()
        {
            var a = new BotMovementPlanner(BotMovementProfile.LocalWander, 7, Settings(),
                new Vector3(180f, -18f, 350f));
            var b = new BotMovementPlanner(BotMovementProfile.LocalWander, 7, Settings(),
                new Vector3(180f, -18f, 350f));
            var pos = new Vector3(180f, -18f, 350f);
            for (var i = 0; i < 12; i++)
            {
                var left = a.Next(pos);
                var right = b.Next(pos);
                Assert.AreEqual(left.Send, right.Send);
                Assert.AreEqual(left.Reason, right.Reason);
                Assert.AreEqual(left.WaypointIndex, right.WaypointIndex);
                if (left.Send)
                {
                    Assert.AreEqual(left.Position, right.Position);
                    pos = left.Position;
                }
            }

            var otherSeed = Settings();
            otherSeed.Seed = 7;
            var samePlanner = new BotMovementPlanner(BotMovementProfile.LocalWander, 7, Settings(),
                new Vector3(180f, -18f, 350f));
            var other = new BotMovementPlanner(BotMovementProfile.LocalWander, 7, otherSeed,
                new Vector3(180f, -18f, 350f));
            var again = new Vector3(180f, -18f, 350f);
            var same = "";
            var changed = "";
            for (var i = 0; i < 12; i++)
            {
                var left = samePlanner.Next(again);
                var right = other.Next(again);
                if (!left.Send && !right.Send)
                    continue;
                same += left.Send ? left.Position.ToString() : left.Reason;
                changed += right.Send ? right.Position.ToString() : right.Reason;
                if (left.Send)
                    again = left.Position;
            }

            Assert.AreNotEqual(same, changed);
        }

        [Test]
        public void Cli02_Idle_SendsNoMove()
        {
            var planner = new BotMovementPlanner(BotMovementProfile.Idle, 1, Settings(), Vector3.zero);
            for (var i = 0; i < 5; i++)
            {
                var plan = planner.Next(Vector3.zero);
                Assert.IsFalse(plan.Send);
                Assert.AreEqual("idle", plan.Reason);
            }
        }

        [Test]
        public void Cli03_Wander_StaysInsideSafeDisk_AndKeepsHeight()
        {
            var settings = Settings();
            var spawn = new Vector3(181.9f, -18.7f, 351.5f);
            var planner = new BotMovementPlanner(BotMovementProfile.LocalWander, 3, settings, spawn);
            var pos = new Vector3(spawn.x, -18.7f, spawn.z);
            var moved = false;
            for (var i = 0; i < 40; i++)
            {
                var plan = planner.Next(pos);
                if (!plan.Send)
                    continue;
                moved = true;
                Assert.AreEqual(-18.7f, plan.Position.y, 0.001f);
                var dx = plan.Position.x - pos.x;
                var dz = plan.Position.z - pos.z;
                var step = Mathf.Sqrt(dx * dx + dz * dz);
                Assert.LessOrEqual(step, settings.MaxStepMeters + 0.02f);
                var ox = plan.Position.x - spawn.x;
                var oz = plan.Position.z - spawn.z;
                Assert.LessOrEqual(ox * ox + oz * oz, settings.SafeRadius * settings.SafeRadius + 0.1f);
                pos = plan.Position;
            }

            Assert.IsTrue(moved);
        }

        [Test]
        public void Cli04_Shuttle_CrossesCell_NotInterest_IncludingNegativeOrigin()
        {
            var settings = Settings();
            settings.CellSize = 32f;
            settings.AoiRadius = 32f;
            settings.SafeRadius = 24f;
            var spawn = new Vector3(-20f, 2f, -8f);
            var planner = new BotMovementPlanner(BotMovementProfile.AoiBoundaryShuttle, 4, settings, spawn);
            var pos = spawn;
            var crossedCell = false;
            for (var i = 0; i < 80; i++)
            {
                var plan = planner.Next(pos);
                if (!plan.Send)
                    continue;
                Assert.AreEqual(2f, plan.Position.y, 0.001f);
                Assert.IsFalse(plan.CrossesInterest);
                if (plan.CrossesCell)
                    crossedCell = true;
                var ox = plan.Position.x - spawn.x;
                var oz = plan.Position.z - spawn.z;
                Assert.LessOrEqual(ox * ox + oz * oz, settings.SafeRadius * settings.SafeRadius + 0.1f);
                pos = plan.Position;
            }

            Assert.IsTrue(crossedCell);
        }

        [Test]
        public void Cli05_Counters_StayIndependent()
        {
            var metrics = new BotMovementMetrics();
            metrics.Planned = 10;
            metrics.Sent = 4;
            metrics.AckOk = 3;
            metrics.Rejected = 1;
            metrics.Timeout = 0;
            metrics.SkippedBusy = 6;
            metrics.SampleStartedMs = 0;
            metrics.SampleEndedMs = 2000;
            Assert.AreEqual(2.0, metrics.ActualSendHz, 0.001);
            Assert.AreEqual(1.5, metrics.ActualSuccessHz, 0.001);
            var json = metrics.ToJson("unit");
            StringAssert.Contains("\"move_planned\": 10", json);
            StringAssert.Contains("\"move_sent\": 4", json);
            StringAssert.Contains("\"move_ack_ok\": 3", json);
            StringAssert.Contains("\"move_skipped_busy\": 6", json);
            Assert.AreEqual(500, new BotMovementClock(new BotMovementSettings { TargetHz = 2 }, 1).IntervalMs);
            Assert.AreEqual(200, new BotMovementClock(Settings(5), 1).IntervalMs);
            Assert.AreEqual(100, new BotMovementClock(new BotMovementSettings { TargetHz = 10 }, 1).IntervalMs);
            Assert.AreNotEqual(5.0, metrics.ActualSendHz);
        }

        [Test]
        public void Cli06_BusySlot_DoesNotReplayMissedTicks()
        {
            var clock = new BotMovementClock(Settings(), 0);
            long now = 0;
            clock.Evaluate(now, true, 0);
            var sends = 0;
            long busyUntil = 0;
            for (var step = 0; step < 40; step++)
            {
                now += 50;
                var inflight = now < busyUntil ? 1 : 0;
                var tick = clock.Evaluate(now, true, inflight);
                if (tick.Kind != BotMoveTickKind.Send)
                    continue;
                sends++;
                busyUntil = now + 250;
            }

            Assert.Greater(clock.Metrics.SkippedBusy, 0);
            Assert.LessOrEqual(clock.Metrics.Planned, 16);
            Assert.AreEqual(sends + clock.Metrics.SkippedBusy, clock.Metrics.Planned);
            Assert.LessOrEqual(sends, 8);

            var slow = new BotMovementClock(Settings(), 1);
            now = 0;
            slow.Evaluate(now, true, 0);
            sends = 0;
            busyUntil = 0;
            for (var step = 0; step < 40; step++)
            {
                now += 50;
                var inflight = now < busyUntil ? 1 : 0;
                var tick = slow.Evaluate(now, true, inflight);
                if (tick.Kind != BotMoveTickKind.Send)
                    continue;
                sends++;
                busyUntil = now + 500;
            }

            Assert.Greater(slow.Metrics.SkippedBusy, 0);
            Assert.LessOrEqual(sends, 4);
            Assert.AreEqual(sends + slow.Metrics.SkippedBusy, slow.Metrics.Planned);
        }

        [Test]
        public void Cli07_Reject_DropsTheSameGoal()
        {
            var settings = Settings();
            var spawn = new Vector3(100f, 1f, 40f);
            var planner = new BotMovementPlanner(BotMovementProfile.BidirectionalPatrol, 2, settings, spawn);
            MovePlan first = default;
            var pos = spawn;
            for (var i = 0; i < 8 && !first.Send; i++)
                first = planner.Next(pos);
            Assert.IsTrue(first.Send);
            planner.NoteRejected();
            MovePlan second = default;
            for (var i = 0; i < 8 && !second.Send; i++)
                second = planner.Next(pos);
            Assert.IsTrue(second.Send);
            Assert.AreNotEqual(first.Position, second.Position);
        }

        [Test]
        public void Cli08_NotReady_DoesNotSend()
        {
            var clock = new BotMovementClock(Settings(), 9);
            clock.Evaluate(0, false, 0);
            var tick = clock.Evaluate(1000, false, 0);
            Assert.AreEqual(BotMoveTickKind.SkipNotReady, tick.Kind);
            Assert.AreEqual(1, clock.Metrics.SkippedNotReady);
            Assert.AreEqual(0, clock.Metrics.Sent);
        }

        [Test]
        public void Cli09_Visibility_RequiresMapInstanceId()
        {
            Assert.IsTrue(BotMovementSettings.SameMapInstance(42, 42));
            Assert.IsFalse(BotMovementSettings.SameMapInstance(42, 43));
            Assert.IsFalse(BotMovementSettings.SameMapInstance(0, 0));
        }

        [Test]
        public void Cli10_LiveModelSync_NotRun()
        {
            Assert.Ignore("NOT RUN: needs two live clients on one map_instance_id. No production wave was started.");
        }

        [Test]
        public void Cli11_Rates_UseSampleWindow()
        {
            var metrics = new BotMovementMetrics();
            metrics.Sent = 50;
            metrics.AckOk = 40;
            metrics.SampleStartedMs = 1000;
            metrics.SampleEndedMs = 11000;
            Assert.AreEqual(5.0, metrics.ActualSendHz, 0.001);
            Assert.AreEqual(4.0, metrics.ActualSuccessHz, 0.001);
            metrics.NoteLatency(10);
            metrics.NoteLatency(20);
            metrics.NoteLatency(100);
            Assert.AreEqual(20, metrics.LatencyPercentile(50));
            Assert.GreaterOrEqual(metrics.LatencyPercentile(99), 20);
        }

        [Test]
        public void Cli12_Weights_AndSingleStrategy()
        {
            var bad = new BotMovementSettings { IdlePct = 10, WanderPct = 10, ShuttlePct = 10, HotspotPct = 10, PatrolPct = 10 };
            Assert.AreEqual("strategy weights must sum to 100", bad.Validate());
            var hz = Settings();
            hz.TargetHz = 7;
            Assert.AreEqual("client send hz must be 2, 5, or 10", hz.Validate());

            var forced = Settings();
            forced.SingleProfile = "HotspotGather";
            for (var i = 0; i < 20; i++)
                Assert.AreEqual(BotMovementProfile.HotspotGather, forced.ProfileFor(i));

            var mixed = Settings();
            var idle = 0;
            var wander = 0;
            var shuttle = 0;
            var hotspot = 0;
            var patrol = 0;
            const int n = 4000;
            for (var i = 0; i < n; i++)
            {
                switch (mixed.ProfileFor(i))
                {
                    case BotMovementProfile.Idle: idle++; break;
                    case BotMovementProfile.LocalWander: wander++; break;
                    case BotMovementProfile.AoiBoundaryShuttle: shuttle++; break;
                    case BotMovementProfile.HotspotGather: hotspot++; break;
                    default: patrol++; break;
                }
            }

            Assert.AreEqual(n, idle + wander + shuttle + hotspot + patrol);
            Assert.AreEqual(mixed.ProfileFor(3), mixed.ProfileFor(3));
            AssertNear(idle, n * 0.10, n * 0.03);
            AssertNear(wander, n * 0.35, n * 0.03);
            AssertNear(shuttle, n * 0.20, n * 0.03);
            AssertNear(hotspot, n * 0.20, n * 0.03);
            AssertNear(patrol, n * 0.15, n * 0.03);

            var left = new BotMovementPlanner(BotMovementProfile.BidirectionalPatrol, 0, Settings(), Vector3.zero);
            var right = new BotMovementPlanner(BotMovementProfile.BidirectionalPatrol, 1, Settings(), Vector3.zero);
            MovePlan a = default;
            MovePlan b = default;
            for (var i = 0; i < 6 && !a.Send; i++)
                a = left.Next(Vector3.zero);
            for (var i = 0; i < 6 && !b.Send; i++)
                b = right.Next(Vector3.zero);
            Assert.IsTrue(a.Send && b.Send);
            Assert.Less(a.Position.x * b.Position.x, 0f);
        }

        [Test]
        public void StepLimit_UsesClientTime_NotSendHz()
        {
            var dt = 0.05f;
            var step = MoveStepLimit.PlannedMeters(dt, 5.5f);
            Assert.AreEqual(0.275f, step, 0.001f);
            Assert.LessOrEqual(step, MoveStepLimit.MaxMeters(dt) + 0.0001f);
            var far = MoveStepLimit.Clamp(Vector3.zero, new Vector3(5f, -20f, 0f), MoveStepLimit.MaxMeters(0.1f));
            Assert.LessOrEqual(far.magnitude, 1.001f);
            Assert.Greater(600, MoveStepLimit.SuzhouLineCap);
            Assert.AreEqual(300, MoveStepLimit.SuzhouLineCap);
            Assert.AreEqual(2400, MoveStepLimit.SuzhouLineCap * MoveStepLimit.SuzhouMaxLines);
            Assert.Greater(10000, MoveStepLimit.SuzhouMaxPlayers);
            Assert.AreEqual(200, MoveStepLimit.VisibleOthers);
        }

        [Test]
        public void OutAndBack_WalksFiftyMetersThenReturnsHome()
        {
            var settings = Settings();
            var spawn = new Vector3(10f, 3f, -4f);
            var left = new BotMovementPlanner(BotMovementProfile.Idle, 5, settings, spawn);
            var right = new BotMovementPlanner(BotMovementProfile.Idle, 6, settings, spawn);
            var same = new BotMovementPlanner(BotMovementProfile.Idle, 5, settings, spawn);
            var leftTarget = left.NextOutAndBack(spawn, 50f);
            var rightTarget = right.NextOutAndBack(spawn, 50f);
            var sameTarget = same.NextOutAndBack(spawn, 50f);
            Assert.AreEqual(50f, Flat(spawn, leftTarget.Position), 0.05f);
            Assert.AreEqual(50f, Flat(spawn, rightTarget.Position), 0.05f);
            Assert.AreEqual(leftTarget.Position, sameTarget.Position);
            Assert.Greater(Flat(leftTarget.Position, rightTarget.Position), 1f);
            Assert.AreEqual(3f, leftTarget.Position.y, 0.001f);

            var walker = new BotMovementPlanner(BotMovementProfile.Idle, 5, settings, spawn);
            var pos = spawn;
            var returned = false;
            for (var i = 0; i < 80; i++)
            {
                var plan = walker.NextOutAndBack(pos, 1.1f);
                Assert.IsTrue(plan.Send);
                Assert.AreEqual(3f, plan.Position.y, 0.001f);
                Assert.LessOrEqual(Flat(pos, plan.Position), 1.12f);
                if (plan.Reason == "return")
                {
                    Assert.GreaterOrEqual(Flat(pos, spawn), 49f);
                    returned = true;
                    pos = plan.Position;
                    break;
                }

                Assert.AreEqual("outbound", plan.Reason);
                Assert.Greater(Flat(plan.Position, spawn), Flat(pos, spawn));
                pos = plan.Position;
            }

            Assert.IsTrue(returned);
            var home = walker.NextOutAndBack(leftTarget.Position, 50f);
            Assert.AreEqual("return", home.Reason);
            Assert.LessOrEqual(Flat(home.Position, spawn), 0.05f);
            var nextLeg = walker.NextOutAndBack(home.Position, 1.1f);
            Assert.AreEqual("outbound", nextLeg.Reason);
            Assert.Greater(Flat(nextLeg.Position, spawn), 0.5f);

            var rejected = new BotMovementPlanner(BotMovementProfile.Idle, 5, settings, spawn);
            var target = rejected.NextOutAndBack(spawn, 50f);
            rejected.NoteRejected();
            var turned = rejected.NextOutAndBack(target.Position, 5f);
            Assert.AreEqual("outbound", turned.Reason);
            Assert.Greater(Flat(turned.Position, target.Position), 0.02f);
        }

        [Test]
        public void Homes_StayWithinTwentyCellsOfPlayerSpawn()
        {
            var cells = new bool[80 * 80];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = true;
            const float step = 8f;
            const float anchorX = (40 + 0.5f) * step;
            const float anchorZ = (40 + 0.5f) * step;
            BotHomeLayout.InstallForTests(0f, 0f, step, 80, 80, cells, anchorX, anchorZ);
            try
            {
                BotHomeLayout.BeginWave();
                var homes = new Vector3[40];
                for (var i = 0; i < homes.Length; i++)
                    homes[i] = BotHomeLayout.Pick(i, homes.Length, -18f);
                var distinct = 0;
                for (var i = 0; i < homes.Length; i++)
                {
                    Assert.AreEqual(-18f, homes[i].y, 0.001f);
                    var col = (int)System.Math.Floor(homes[i].x / step);
                    var row = (int)System.Math.Floor(homes[i].z / step);
                    Assert.LessOrEqual(System.Math.Abs(col - 40), BotHomeLayout.SpawnCellRadius);
                    Assert.LessOrEqual(System.Math.Abs(row - 40), BotHomeLayout.SpawnCellRadius);
                    var same = false;
                    for (var j = 0; j < i; j++)
                    {
                        if (homes[j] == homes[i])
                            same = true;
                    }

                    if (!same)
                        distinct++;
                }

                Assert.Greater(distinct, 10);
            }
            finally
            {
                BotHomeLayout.Clear();
            }
        }

        [Test]
        public void AroundHome_StaysInsideOneHundredMeters()
        {
            var home = new Vector3(1000f, -18f, -500f);
            var settings = Settings();
            settings.HomeRadiusMeters = 100f;
            var planner = new BotMovementPlanner(BotMovementProfile.Idle, 3, settings, home);
            var pos = home;
            for (var i = 0; i < 40; i++)
            {
                var plan = planner.NextAroundHome(pos, 10f);
                Assert.IsTrue(plan.Send);
                Assert.AreEqual("wander", plan.Reason);
                Assert.LessOrEqual(Flat(home, plan.Position), 100.05f);
                Assert.LessOrEqual(Flat(pos, plan.Position), 10.05f);
                pos = plan.Position;
            }

            var far = new Vector3(home.x + 800f, home.y, home.z);
            var scatter = new BotMovementPlanner(BotMovementProfile.Idle, 4, settings, home);
            var step = scatter.NextAroundHome(far, 10f);
            Assert.AreEqual("scatter", step.Reason);
            Assert.Less(Flat(home, step.Position), Flat(home, far));
        }

        [Test]
        public void SparsePatrol_Defaults_AndValidate()
        {
            var settings = new BotMovementSettings();
            Assert.IsTrue(settings.SparsePatrol);
            Assert.AreEqual(60, settings.ClusterSize);
            Assert.AreEqual(100, settings.MoveDutyPct);
            Assert.AreEqual(10f, settings.SegmentMinSec, 0.001f);
            Assert.AreEqual(30f, settings.SegmentMaxSec, 0.001f);
            Assert.IsNull(settings.Validate());
            settings.ClusterSize = 20;
            Assert.AreEqual("cluster_size must be 0 or 40..80", settings.Validate());
            settings.ClusterSize = 0;
            Assert.IsNull(settings.Validate());
        }

        [Test]
        public void SparsePatrol_OnlyDepartAndOptionalArrive()
        {
            var settings = Settings();
            settings.SparsePatrol = true;
            settings.SegmentMinSec = 10f;
            settings.SegmentMaxSec = 10f;
            settings.MoveDutyPct = 100;
            settings.WalkSpeed = 5.5f;
            settings.ServerMoveSpeed = 10f;
            settings.ClusterSize = 60;
            var sparse = new SparsePatrolController(3, settings);
            var metrics = new BotMovementMetrics();
            long now = 0;
            sparse.Arm(now);
            var confirmed = new Vector3(100f, -18f, 200f);
            var plan = sparse.Poll(confirmed, now, metrics, out _);

            Assert.IsTrue(plan.Send);
            Assert.AreEqual("depart", plan.Reason);
            // Sparse legs cover the full interval at the server's authority speed; using
            // WalkSpeed here made the body arrive early and visibly stand still.
            Assert.AreEqual(100f, Flat(confirmed, plan.Position), 6f);
            sparse.NoteSent(plan.Yaw, now, plan.Reason, metrics);

            // Mid-segment: no MOVE even as wall clock advances.
            var segmentMs = (long)(Flat(confirmed, plan.Position) /
                                   settings.ServerMoveSpeed * 1000f);
            now = segmentMs - 1;
            var mid = sparse.Poll(confirmed, now, metrics, out _);
            Assert.IsFalse(mid.Send);

            // Server shortened: confirmed still at start when segment ends → arrive.
            now = segmentMs;
            var arrive = sparse.Poll(confirmed, now, metrics, out _);
            Assert.IsTrue(arrive.Send);
            Assert.AreEqual("arrive", arrive.Reason);
            Assert.AreEqual(plan.Position, arrive.Position);
            sparse.NoteSent(arrive.Yaw, now, arrive.Reason, metrics);

            // Already at goal: no arrive packet.
            var done = new SparsePatrolController(4, settings);
            metrics = new BotMovementMetrics();
            now = 0;
            done.Arm(now);
            plan = done.Poll(confirmed, now, metrics, out _);
            Assert.AreEqual("depart", plan.Reason);
            done.NoteSent(plan.Yaw, now, "depart", metrics);
            var atGoal = plan.Position;
            var doneSegmentMs = (long)(Flat(confirmed, atGoal) /
                                       settings.ServerMoveSpeed * 1000f);
            now += doneSegmentMs;
            var nextDepart = done.Poll(atGoal, now, metrics, out _);
            Assert.IsTrue(nextDepart.Send);
            Assert.AreEqual("depart", nextDepart.Reason);
            Assert.AreEqual(doneSegmentMs, metrics.MovingMs);
            Assert.AreEqual(0, metrics.DwellingMs);
            Assert.AreEqual(1.0, metrics.DutyRatio, 0.001);
        }

        [Test]
        public void Sparse_AlreadyAtGoal_DoesNotWaitOutTheSegment()
        {
            var settings = Settings();
            settings.SegmentMinSec = 10f;
            settings.SegmentMaxSec = 10f;
            settings.ServerMoveSpeed = 10f;
            var sparse = new SparsePatrolController(268, settings);
            var start = new Vector3(160f, -18f, 658f);
            sparse.Arm(0);
            var metrics = new BotMovementMetrics();
            var depart = sparse.Poll(start, 0, metrics, out _);
            Assert.IsTrue(depart.Send);
            sparse.NoteSent(depart.Yaw, 0, depart.Reason, metrics);

            var next = sparse.Poll(depart.Position, 500, metrics, out _);
            Assert.IsTrue(next.Send);
            Assert.AreEqual("depart", next.Reason);
            Assert.Greater(Flat(depart.Position, next.Position), 1f);
        }

        [Test]
        public void Sparse_AfterArrival_NextPollLeavesThatPoint()
        {
            var settings = Settings();
            settings.SegmentMinSec = 10f;
            settings.SegmentMaxSec = 10f;
            settings.ServerMoveSpeed = 10f;
            var sparse = new SparsePatrolController(92, settings);
            var start = new Vector3(128.3f, -18f, 625.9f);
            sparse.Arm(0);
            var metrics = new BotMovementMetrics();
            var depart = sparse.Poll(start, 0, metrics, out _);
            Assert.IsTrue(depart.Send);
            Assert.AreEqual("depart", depart.Reason);
            sparse.NoteSent(depart.Yaw, 0, depart.Reason, metrics);

            var arrived = depart.Position;
            var segmentMs = (long)(Flat(start, arrived) / settings.ServerMoveSpeed * 1000f);
            var next = sparse.Poll(arrived, segmentMs, metrics, out _);
            Assert.IsTrue(next.Send);
            Assert.AreEqual("depart", next.Reason);
            Assert.Greater(Flat(arrived, next.Position), 1f);
        }

        [Test]
        public void SparsePatrol_KeepInside_StaysInAoiDisk()
        {
            var settings = Settings();
            settings.SegmentMinSec = 10f;
            settings.SegmentMaxSec = 10f;
            settings.ServerMoveSpeed = 10f;
            settings.AoiRadius = 32f;
            var sparse = new SparsePatrolController(9, settings);
            var home = new Vector3(65f, -30f, 737f);
            sparse.KeepInside(home, settings.AoiRadius);
            sparse.Arm(0);
            var plan = sparse.Poll(home, 0, new BotMovementMetrics(), out _);
            Assert.IsTrue(plan.Send);
            Assert.LessOrEqual(Flat(home, plan.Position), settings.AoiRadius);
        }

        [Test]
        public void SparsePatrol_DutyAndTurnsAggregate()
        {
            var metrics = new BotMovementMetrics();
            metrics.Sent = 12;
            metrics.Turns = 3;
            metrics.MovingMs = 60000;
            metrics.DwellingMs = 40000;
            metrics.SampleStartedMs = 0;
            metrics.SampleEndedMs = 100000;
            Assert.AreEqual(0.12, metrics.UplinkMovePerSec, 0.001);
            Assert.AreEqual(0.6, metrics.DutyRatio, 0.001);
            var json = metrics.ToJson("sparse");
            StringAssert.Contains("\"uplink_move_per_sec\": 0.12", json);
            StringAssert.Contains("\"turns\": 3", json);
            StringAssert.Contains("\"duty\": 0.6", json);
            StringAssert.Contains("\"moving_ms\": 60000", json);
            StringAssert.Contains("\"dwelling_ms\": 40000", json);

            var sum = new BotMovementMetrics();
            sum.Add(metrics);
            var other = new BotMovementMetrics { Turns = 2, MovingMs = 1000, DwellingMs = 500 };
            sum.Add(other);
            Assert.AreEqual(5, sum.Turns);
            Assert.AreEqual(61000, sum.MovingMs);
            Assert.AreEqual(40500, sum.DwellingMs);

            var settings = Settings();
            settings.SegmentMinSec = 10f;
            settings.SegmentMaxSec = 10f;
            settings.MoveDutyPct = 100;
            var turnMetrics = new BotMovementMetrics();
            var sparse = new SparsePatrolController(1, settings);
            sparse.Arm(0);
            var from = Vector3.zero;
            long t = 0;
            var first = sparse.Poll(from, t, turnMetrics, out _);

            sparse.NoteSent(0f, t, "depart", turnMetrics);
            sparse.NoteSent(40f, t + 100, "arrive", turnMetrics);
            Assert.AreEqual(1, turnMetrics.Turns);
        }

        [Test]
        public void SparsePatrol_StepDt_UsesFullClientGap()
        {
            // Sparse path must not chop client_time gaps to 1s (that shattered one leg).
            var dt = 18f;
            var max = MoveStepLimit.MaxMeters(dt);
            Assert.AreEqual(180f, max, 0.001f);
            var first = MoveStepLimit.MaxMeters(10f);
            Assert.AreEqual(100f, first, 0.001f);
        }

        [Test]
        public void Clusters_AreNinetySixMetersApart_AndTwelveMeterRadius()
        {
            var cells = new bool[200 * 200];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = true;
            const float step = 8f;
            const float anchorX = (100 + 0.5f) * step;
            const float anchorZ = (100 + 0.5f) * step;
            BotHomeLayout.InstallForTests(0f, 0f, step, 200, 200, cells, anchorX, anchorZ);
            try
            {
                BotHomeLayout.BeginWave();
                const int clusterSize = 60;
                const int bots = 180;
                var homes = new Vector3[bots];
                for (var i = 0; i < bots; i++)
                    homes[i] = BotHomeLayout.PickClustered(i, bots, -18f, clusterSize, 96f, 12f);

                var centers = new Vector3[3];
                for (var c = 0; c < 3; c++)
                {
                    var sx = 0f;
                    var sz = 0f;
                    for (var i = 0; i < clusterSize; i++)
                    {
                        sx += homes[c * clusterSize + i].x;
                        sz += homes[c * clusterSize + i].z;
                    }

                    centers[c] = new Vector3(sx / clusterSize, 0f, sz / clusterSize);
                }

                Assert.GreaterOrEqual(Flat(centers[0], centers[1]), 90f);
                Assert.GreaterOrEqual(Flat(centers[0], centers[2]), 90f);
                for (var i = 0; i < clusterSize; i++)
                    Assert.LessOrEqual(Flat(homes[i], centers[0]), 12f + step);

                BotHomeLayout.BeginWave();
                var legacy = BotHomeLayout.Pick(0, bots, -18f);
                BotHomeLayout.BeginWave();
                Assert.AreEqual(legacy, BotHomeLayout.PickClustered(0, bots, -18f, 0));
            }
            finally
            {
                BotHomeLayout.Clear();
            }
        }

        [Test]
        public void E1_ReplyDelayOverTwoSeconds_TriggersSameTargetResend()
        {
            // Evidence: with aoi_bot_backoff off, a 3s reply miss arms the latch and the
            // next poll resends the same point (current production behavior).
            var policy = new BotResendPolicy { BackoffEnabled = false };
            const float fromX = 10f, fromZ = 20f;
            const float targetX = 40f, targetZ = 20f;
            Assert.IsTrue(MoveRecovery.ShouldResendSamePoint(fromX, fromZ, targetX, targetZ, true));

            policy.Arm(new Vector3(targetX, 0f, targetZ), nowMs: 0);
            Assert.IsTrue(policy.Armed);
            Assert.IsTrue(policy.TryTakeResend(2000, fromX, fromZ, out var first, out var abandon));
            Assert.IsFalse(abandon);
            Assert.AreEqual(targetX, first.x, 0.001f);
            Assert.AreEqual(1, policy.ResendCount);

            // Another 3s miss → immediate resend again (storm path).
            policy.Arm(new Vector3(targetX, 0f, targetZ), nowMs: 5000);
            Assert.IsTrue(policy.TryTakeResend(5000, fromX, fromZ, out _, out abandon));
            Assert.IsFalse(abandon);
            Assert.AreEqual(2, policy.ResendCount);

            var metrics = new BotMovementMetrics();
            metrics.CaptureProfile(new BotMovementSettings(), "Sparse");
            metrics.ResendCount = policy.ResendCount;
            metrics.Timeout = 2;
            metrics.Sent = 4;
            metrics.SampleStartedMs = 0;
            metrics.SampleEndedMs = 10000;
            var json = metrics.ToJson("e1");
            StringAssert.Contains("\"loop_kind\": \"Sparse\"", json);
            StringAssert.Contains("\"move_resend\": 2", json);
            StringAssert.Contains("\"actual_send_hz\":", json);
            StringAssert.Contains("loop_kind", metrics.CsvHeader());
        }

        [Test]
        public void E2_Backoff_CutsResendsBelowOneFifthOfImmediateStorm()
        {
            const float fromX = 0f, fromZ = 0f;
            const float targetX = 50f, targetZ = 0f;
            // Long enough that the no-backoff path storms; backoff abandons after N.
            const int windowMs = 60000;

            var baseline = new BotResendPolicy { BackoffEnabled = false };
            SimulateCongestedResends(baseline, fromX, fromZ, targetX, targetZ, windowMs,
                rttMs: 3000, stopAfterAbandon: false);
            Assert.GreaterOrEqual(baseline.ResendCount, 15, "E1 path must storm under 3s RTT");

            var backed = new BotResendPolicy
            {
                BackoffEnabled = true,
                MaxSameTargetResends = 3,
                BackoffCapSec = 30
            };
            SimulateCongestedResends(backed, fromX, fromZ, targetX, targetZ, windowMs,
                rttMs: 3000, stopAfterAbandon: true);
            Assert.LessOrEqual(backed.ResendCount, baseline.ResendCount / 5,
                "backoff resends must be ≤ 1/5 of the E1 storm count");
            Assert.AreEqual(3, backed.ResendCount);
            Assert.AreEqual(1, backed.AbandonCount);

            backed.Clear();
            Assert.IsFalse(backed.Armed);
            Assert.AreEqual(0, backed.SameTargetResends);
        }

        [Test]
        public void E2_CongestionGate_ScalesSegmentsThenRecovers()
        {
            BotCongestionGate.Reset();
            BotCongestionGate.Enabled = true;
            BotCongestionGate.TimeoutRatioThreshold = 0.30f;
            long t = 1000;
            for (var i = 0; i < 20; i++)
            {
                BotCongestionGate.NoteSend(t);
                BotCongestionGate.NoteTimeout(t);
                t += 100;
            }

            Assert.GreaterOrEqual(BotCongestionGate.CurrentScale, 2f);
            Assert.LessOrEqual(BotCongestionGate.CurrentScale, 4f);

            // Healthy window: many sends, few timeouts → scale back to 1.
            t = 20000;
            for (var i = 0; i < 40; i++)
            {
                BotCongestionGate.NoteSend(t);
                t += 50;
            }

            Assert.AreEqual(1f, BotCongestionGate.SegmentScale(t), 0.001f);
            BotCongestionGate.Enabled = false;
            BotCongestionGate.Reset();
        }

        [Test]
        public void E1_ObserverStats_LeaveThenMoveAndStartMs()
        {
            var push = new BotPushTracker();
            push.BindInstance(9, 1);
            var enter = new GameResponse
            {
                AoiDelta = new AoiDelta { MapInstanceId = 9 }
            };
            enter.AoiDelta.Events.Add(new AoiEvent
            {
                Op = (int)AoiOp.Enter,
                Entity = new EntitySnapshot { PlayerId = 100 }
            });
            Assert.IsTrue(push.ObserveServerPush(
                new ServerPushEnvelope { Reliable = true, ServerSeq = 1 }, enter));

            var leave = new GameResponse
            {
                AoiDelta = new AoiDelta { MapInstanceId = 9 }
            };
            leave.AoiDelta.Events.Add(new AoiEvent
            {
                Op = (int)AoiOp.Leave,
                Entity = new EntitySnapshot { PlayerId = 100 }
            });
            Assert.IsTrue(push.ObserveServerPush(
                new ServerPushEnvelope { Reliable = true, ServerSeq = 2 }, leave));

            var move = new GameResponse
            {
                AoiDelta = new AoiDelta { MapInstanceId = 9 }
            };
            move.AoiDelta.Events.Add(new AoiEvent
            {
                Op = (int)AoiOp.Move,
                Entity = new EntitySnapshot { PlayerId = 100, MoveStartMs = 12345 }
            });
            Assert.IsTrue(push.ObserveServerPush(
                new ServerPushEnvelope { Reliable = true, ServerSeq = 3 }, move));

            Assert.AreEqual(1, push.AoiMoveEvents);
            Assert.AreEqual(1, push.UnknownMoveCount);
            Assert.AreEqual(1, push.LeaveThenMoveCount);
            Assert.AreEqual(1, push.MoveWithStartMsCount);

            var metrics = new BotMovementMetrics();
            push.CopyInto(metrics);
            Assert.AreEqual(1.0, metrics.MoveStartMsNonZeroRatio, 0.001);
        }

        static void SimulateCongestedResends(BotResendPolicy policy,
            float fromX, float fromZ, float targetX, float targetZ, int windowMs, int rttMs,
            bool stopAfterAbandon)
        {
            long now = 0;
            var target = new Vector3(targetX, 0f, targetZ);
            while (now < windowMs)
            {
                policy.Arm(target, now);
                var deadline = now + rttMs + policy.BackoffCapSec * 1000L;
                var took = false;
                while (now < deadline && now < windowMs)
                {
                    if (policy.TryTakeResend(now, fromX, fromZ, out _, out var abandon))
                    {
                        took = true;
                        now += rttMs;
                        break;
                    }

                    if (abandon)
                    {
                        if (stopAfterAbandon)
                            return;
                        now += 50;
                        break;
                    }

                    var wait = policy.WaitMsUntilResend(now);
                    now += wait > 0 ? System.Math.Min(wait, 1000) : 50;
                }

                if (!took && !stopAfterAbandon)
                    now += rttMs;
            }
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static void AssertNear(int actual, double expected, double slack)
        {
            Assert.LessOrEqual(System.Math.Abs(actual - expected), slack);
        }
    }
}
