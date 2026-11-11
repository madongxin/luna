using GameMesh.Aoi;
using GameMesh.Bootstrap;
using GameMesh.Player;
using NUnit.Framework;
using UnityEngine;

namespace GameMesh.Tests.EditMode
{
    public sealed class AoiInterpBufferTests
    {
        static AoiInterpSettings SchemeA()
        {
            var s = AoiInterpSettings.Disabled;
            s.Enabled = true;
            s.CoastEnabled = true;
            s.DelayDefault = 0.2f;
            s.DelayMin = 0.15f;
            s.DelayMax = 0.25f;
            s.CoastMaxSec = 0.3f;
            s.MoveSpeed = 10f;
            return s;
        }

        [Test]
        public void Config_SamplingAndCoastDefaultOff()
        {
            var cfg = ScriptableObject.CreateInstance<GameMeshClientConfig>();
            try
            {
                Assert.IsFalse(cfg.aoiInterpEnabled);
                Assert.IsFalse(cfg.aoiInterpCoastEnabled);
                Assert.IsFalse(cfg.aoiInterpAllowSnap);
                Assert.AreEqual(200, cfg.aoiInterpDelayMs);
                Assert.AreEqual(150, cfg.aoiInterpDelayMinMs);
                Assert.AreEqual(250, cfg.aoiInterpDelayMaxMs);
                Assert.AreEqual(300, cfg.aoiInterpCoastMaxMs);
                var s = AoiInterpSettings.FromConfig(cfg);
                Assert.IsFalse(s.Enabled);
                Assert.IsFalse(s.CoastEnabled);
                Assert.AreEqual(0.2f, s.DelayDefault, 0.0001f);
                Assert.AreEqual(0.3f, s.CoastMaxSec, 0.0001f);
            }
            finally
            {
                Object.DestroyImmediate(cfg);
            }
        }

        [Test]
        public void Buffer_KeepsTwelveSamplesAndDropsOldest()
        {
            var buffer = new RemotePoseBuffer();
            for (var i = 0; i < 13; i++)
                Assert.IsTrue(buffer.Push(i, 0, 0, 0, i * 0.18f, 1));
            Assert.AreEqual(12, buffer.Count);
            Assert.AreEqual(1f, buffer.At(0).X, 0.01f);
            Assert.AreEqual(12f, buffer.At(11).X, 0.01f);
        }

        [Test]
        public void Buffer_180msPathDoesNotStallBeforeTip()
        {
            var buffer = new RemotePoseBuffer();
            const float gap = 0.18f;
            const float delay = 0.2f;
            const int n = 20;
            var nextPush = 0;
            var tip = (n - 1) * gap;
            var now = delay + 0.016f;
            var prevX = 0f;
            var slow = 0;
            var first = true;
            while (now < tip + delay - 0.02f)
            {
                while (nextPush < n && nextPush * gap <= now + 0.0001f)
                {
                    buffer.Push(nextPush * 1.8f, 0, 0, 0, nextPush * gap, 1);
                    nextPush++;
                }

                Assert.IsTrue(buffer.TrySample(now, delay, out var x, out _, out _, out _));
                var tRender = now - delay;
                Assert.Greater(tRender, 0f);
                Assert.Less(tRender, tip);
                if (!first)
                {
                    var speed = (x - prevX) / 0.016f;
                    if (speed < 0.05f)
                        slow++;
                    else
                        slow = 0;
                    Assert.Less(slow, 2, "stalled at t_render=" + tRender);
                    Assert.LessOrEqual(Mathf.Abs(speed - 10f), 1.1f);
                }

                first = false;
                prevX = x;
                now += 0.016f;
            }
        }

        [Test]
        public void Buffer_FarIntervalStallDoesNotExceedSchemeABudget()
        {
            var buffer = new RemotePoseBuffer();
            const float interval = 1f;
            const float delay = 0.25f;
            const float coast = 0.3f;
            const int n = 5;
            buffer.Push(0f, 0, 0, 0, 0f, 1);
            buffer.Push(10f, 0, 0, 0, interval, 1);
            var nextPush = 2;
            var now = interval;
            var stall = 0f;
            var maxStall = 0f;
            var prevX = float.NaN;
            var moving = false;
            while (now <= (n - 1) * interval + delay)
            {
                while (nextPush < n && nextPush * interval <= now + 0.0001f)
                {
                    buffer.Push(nextPush * 10f, 0, 0, 0, nextPush * interval, 1);
                    nextPush++;
                }

                Assert.IsTrue(buffer.TrySample(now, delay, coast, 10f, out var hit));
                var speed = float.IsNaN(prevX) ? 0f : (hit.X - prevX) / 0.016f;
                if (moving && speed < 0.05f)
                {
                    stall += 0.016f;
                    if (stall > maxStall)
                        maxStall = stall;
                }
                else
                    stall = 0f;
                if (speed >= 0.05f)
                    moving = true;
                prevX = hit.X;
                now += 0.016f;
            }

            var budget = Mathf.Max(0f, interval - delay - coast) + 0.02f;
            Assert.LessOrEqual(maxStall, budget + 0.05f);
        }

        [Test]
        public void Buffer_CoastsAlongLastSegmentThenHolds()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(0, 0, 0, 0, 1f, 2));
            Assert.IsTrue(buffer.Push(2, 0, 0, 90, 1.2f, 2));
            Assert.IsTrue(buffer.TrySample(1.5f, 0.1f, 0.3f, 10f, out var hit));
            Assert.AreEqual(4f, hit.X, 0.01f);
            Assert.IsTrue(hit.Coasting);
            Assert.IsTrue(hit.Underrun);
            Assert.IsTrue(buffer.TrySample(1.7f, 0.1f, 0.3f, 10f, out hit));
            Assert.AreEqual(5f, hit.X, 0.01f);
            Assert.IsFalse(hit.Coasting);
            Assert.IsTrue(buffer.TrySample(1.5f, 0.1f, 0f, 10f, out hit));
            Assert.AreEqual(2f, hit.X, 0.01f);
        }

        [Test]
        public void Buffer_SinglePointDoesNotCoast()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(7, 0, 9, 0, 1f, 1));
            Assert.IsTrue(buffer.TrySample(2f, 0.2f, 0.3f, 10f, out var hit));
            Assert.AreEqual(7f, hit.X, 0.01f);
            Assert.AreEqual(9f, hit.Z, 0.01f);
            Assert.IsFalse(hit.Coasting);
        }

        [Test]
        public void Follow_WalkCatchupAndNoSnapByDefault()
        {
            var snap = AoiInterpFollow.Step(0, 0, 0.3f, 0, 1, 0, 0.016f, 10f, false);
            Assert.AreEqual(0.3f, snap.X, 0.001f);
            Assert.IsFalse(snap.Catchup);

            var catchup = AoiInterpFollow.Step(0, 0, 4f, 0, 1, 0, 0.016f, 10f, false);
            Assert.IsTrue(catchup.Catchup);
            Assert.AreEqual(10f * 1.5f * 0.016f, catchup.X, 0.001f);
            Assert.IsFalse(catchup.Snapped);

            var far = AoiInterpFollow.Step(0, 0, 10f, 0, 1, 0, 0.016f, 10f, false);
            Assert.IsTrue(far.TeleportLogged);
            Assert.IsFalse(far.Snapped);
            Assert.AreEqual(10f * 0.016f, far.X, 0.001f);

            var allowed = AoiInterpFollow.Step(0, 0, 10f, 0, 1, 0, 0.016f, 10f, true);
            Assert.IsTrue(allowed.Snapped);
            Assert.AreEqual(10f, allowed.X, 0.001f);
        }

        [Test]
        public void Follow_ReverseBrakeThenShortOvershootCorrection()
        {
            var brake = AoiInterpFollow.Step(5, 0, 2, 0, 1, 0, 0.016f, 10f, false);
            Assert.IsTrue(brake.Brake);
            Assert.AreEqual(5f, brake.X, 0.001f);

            var correct = AoiInterpFollow.Step(5, 0, 4.7f, 0, 1, 0, 0.016f, 10f, false);
            Assert.IsFalse(correct.Brake);
            Assert.AreEqual(4.7f, correct.X, 0.001f);
        }

        [Test]
        public void Delay_SchemeAStaysIn250AndSlewTowardMin()
        {
            var s = SchemeA();
            var delay = new AoiInterpDelay();
            delay.Reset(s.DelayDefault);
            delay.OnArrived(0f, s);
            Assert.AreEqual(0.2f, delay.Delay, 0.0001f);
            var t = 0.18f;
            for (var i = 0; i < 12; i++)
            {
                delay.OnArrived(t, s);
                t += 0.18f;
                Assert.GreaterOrEqual(delay.Delay, s.DelayMin - 0.0001f);
                Assert.LessOrEqual(delay.Delay, s.DelayMax + 0.0001f);
            }

            Assert.AreEqual(s.DelayMin, delay.Delay, 0.021f);
            for (var i = 0; i < 6; i++)
            {
                delay.OnArrived(t, s);
                t += 1f;
                Assert.LessOrEqual(delay.Delay, s.DelayMax + 0.0001f);
            }

            Assert.AreEqual(s.DelayMax, delay.Delay, 0.021f);
        }

        [Test]
        public void LateMove_FollowClosesGapWithoutTeleport()
        {
            var buffer = new RemotePoseBuffer();
            var times = new[] { 0f, 0.18f, 0.36f, 0.54f, 1.22f };
            for (var i = 0; i < times.Length - 1; i++)
                buffer.Push(i * 1.8f, 0, 0, 0, times[i], 4);

            var now = 0.9f;
            var p = 0f;
            RemotePoseBuffer.Hit hit;
            while (now < 1.21f)
            {
                buffer.TrySample(now, 0.2f, 0.3f, 10f, out hit);
                var step = AoiInterpFollow.Step(p, 0, hit.X, 0, 1.8f, 0, 0.016f, 10f, false);
                Assert.LessOrEqual(Mathf.Abs(step.X - p), 10f * 1.5f * 0.05f + 0.01f);
                p = step.X;
                now += 0.016f;
            }

            buffer.Push(7.2f, 0, 0, 0, 1.22f, 4);
            buffer.TrySample(1.22f, 0.2f, 0.3f, 10f, out hit);
            var dist = Mathf.Abs(hit.X - p);
            now = 1.22f;
            var closed = false;
            for (var i = 0; i < 2; i++)
            {
                now += 0.016f;
                buffer.TrySample(now, 0.2f, 0.3f, 10f, out hit);
                var step = AoiInterpFollow.Step(p, 0, hit.X, 0, 1.8f, 0, 0.016f, 10f, false);
                Assert.LessOrEqual(Mathf.Abs(step.X - p), 0.4f + 0.0001f);
                p = step.X;
                var next = Mathf.Abs(hit.X - p);
                if (next < dist)
                    closed = true;
                dist = next;
            }

            Assert.IsTrue(closed || dist <= 0.4f);
        }

        [Test]
        public void OlderSeqDropped_SameSeqNewPositionKept()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(0, 0, 0, 0, 1f, 5));
            Assert.IsTrue(buffer.Push(2, 0, 0, 0, 1.2f, 5));
            Assert.IsFalse(buffer.Push(99, 0, 0, 0, 1.3f, 4));
            Assert.IsTrue(buffer.TrySample(1.2f, 0f, out var x, out _, out _, out _));
            Assert.AreEqual(2f, x, 0.01f);
        }

        [Test]
        public void Leave_ClearsBufferSameCall()
        {
            var buffer = new RemotePoseBuffer();
            buffer.Push(1, 0, 2, 0, 1f, 3);
            buffer.Push(3, 0, 2, 0, 1.2f, 3);
            var gen = buffer.BumpGeneration();
            Assert.AreEqual(0, buffer.Count);
            Assert.Greater(gen, 0u);
            Assert.IsFalse(buffer.TrySample(2f, 0.2f, out _, out _, out _, out _));
        }

        [Test]
        public void LeaveThenEnter_DoesNotKeepOldPoints()
        {
            var buffer = new RemotePoseBuffer();
            buffer.Push(1, 0, 1, 0, 1f, 1);
            buffer.Push(4, 0, 1, 0, 1.2f, 1);
            buffer.BumpGeneration();
            Assert.IsTrue(buffer.Push(80, 0, 90, 0, 1.3f, 8));
            Assert.AreEqual(1, buffer.Count);
            Assert.IsTrue(buffer.TrySample(1.3f, 0f, out var x, out _, out var z, out _));
            Assert.AreEqual(80f, x, 0.01f);
            Assert.AreEqual(90f, z, 0.01f);
        }

        [Test]
        public void AoiWorld_LeaveAndClearNotifyBodyRemoved()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            ulong last = 0;
            var n = 0;
            world.BodyRemoved += id =>
            {
                last = id;
                n++;
            };
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                Entity = new EntitySnapshotDto { PlayerId = 7, X = 1, Z = 2, StateSeq = 1 }
            }));
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                Entity = new EntitySnapshotDto { PlayerId = 7 }
            }));
            Assert.AreEqual(7ul, last);
            Assert.AreEqual(0, world.Entities.Count);
            Assert.GreaterOrEqual(n, 1);

            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                Entity = new EntitySnapshotDto { PlayerId = 8, X = 3 }
            });
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                Entity = new EntitySnapshotDto { PlayerId = 9, X = 4 }
            });
            n = 0;
            world.Clear();
            Assert.AreEqual(0, world.Entities.Count);
            Assert.AreEqual(2, n);
        }

        [Test]
        public void AoiWorld_SnapshotEnterBumpsPoseEpoch()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.ApplySnapshot(new[]
            {
                new EntitySnapshotDto { PlayerId = 4, X = 1, StateSeq = 1 }
            });
            Assert.AreEqual(0u, world.Entities[4].PoseEpoch);
            world.ApplySnapshot(new[]
            {
                new EntitySnapshotDto { PlayerId = 4, X = 9, StateSeq = 2 }
            });
            Assert.AreEqual(1u, world.Entities[4].PoseEpoch);
            Assert.AreEqual(9f, world.Entities[4].X);
        }

        [Test]
        public void AoiWorld_SnapshotReplaceNotifiesMissingIds()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            ulong missing = 0;
            world.BodyRemoved += id => missing = id;
            world.ApplySnapshot(new[]
            {
                new EntitySnapshotDto { PlayerId = 2, X = 1 },
                new EntitySnapshotDto { PlayerId = 3, X = 2 }
            });
            world.ApplySnapshot(new[]
            {
                new EntitySnapshotDto { PlayerId = 3, X = 8 }
            });
            Assert.AreEqual(2ul, missing);
            Assert.IsFalse(world.Entities.ContainsKey(2));
            Assert.AreEqual(8f, world.Entities[3].X);
        }

        [Test]
        public void AoiWorld_LocalPlayerNeverEntersRemoteTable()
        {
            var world = new AoiWorld { LocalPlayerId = 42 };
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                Entity = new EntitySnapshotDto { PlayerId = 42, X = 9 }
            }));
            Assert.AreEqual(0, world.Entities.Count);
        }

        [Test]
        public void View_HideNowClearsPoseAndNameplateSameCall()
        {
            var state = new RemoteEntityState
            {
                EntityId = 9, PlayerId = 9, X = 3, Z = 4, Name = "Bot009", StateSeq = 2
            };
            var view = RemotePlayerView.Spawn(state, 200, false);
            try
            {
                Assert.Greater(view.PoseCount, 0);
                var gen = view.PoseGeneration;
                view.HideNow();
                Assert.AreEqual(0, view.PoseCount);
                Assert.Greater(view.PoseGeneration, gen);
                Assert.IsTrue(view.Hidden);
                Assert.IsFalse(view.gameObject.activeSelf);
                var plate = view.transform.Find("Name");
                Assert.IsNotNull(plate);
                Assert.IsFalse(plate.gameObject.activeSelf);

                view.Apply(new RemoteEntityState
                {
                    EntityId = 9, PlayerId = 9, X = 8, Z = 8, StateSeq = 3
                });
                Assert.AreEqual(0, view.PoseCount);

                var enter = new RemoteEntityState
                {
                    EntityId = 9, PlayerId = 9, X = 50, Z = 60, StateSeq = 8
                };
                view.ResetForEnter(enter);
                view.Apply(enter);
                Assert.IsFalse(view.Hidden);
                Assert.AreEqual(1, view.PoseCount);
                Assert.IsTrue(view.gameObject.activeSelf);
                Assert.IsTrue(plate.gameObject.activeSelf);
                Assert.IsTrue(view.TrySamplePose(Time.unscaledTime, 0f, 0f, 10f, out var hit));
                Assert.AreEqual(50f, hit.X, 0.05f);
                Assert.AreEqual(60f, hit.Z, 0.05f);
            }
            finally
            {
                Object.DestroyImmediate(view.gameObject);
            }
        }

        [Test]
        public void View_SnapshotEpochReplacesPoseTrail()
        {
            var state = new RemoteEntityState
            {
                EntityId = 4, PlayerId = 4, X = 0, Z = 0, StateSeq = 1, PoseEpoch = 0
            };
            var view = RemotePlayerView.Spawn(state, 200, false);
            try
            {
                view.Apply(new RemoteEntityState
                {
                    EntityId = 4, PlayerId = 4, X = 2, Z = 0, StateSeq = 1, PoseEpoch = 0
                });
                Assert.Greater(view.PoseCount, 1);
                view.Apply(new RemoteEntityState
                {
                    EntityId = 4, PlayerId = 4, X = 80, Z = 0, StateSeq = 9, PoseEpoch = 1
                });
                Assert.AreEqual(1, view.PoseCount);
                Assert.IsTrue(view.TrySamplePose(Time.unscaledTime, 0f, 0f, 10f, out var hit));
                Assert.AreEqual(80f, hit.X, 0.05f);
            }
            finally
            {
                Object.DestroyImmediate(view.gameObject);
            }
        }
    }
}
