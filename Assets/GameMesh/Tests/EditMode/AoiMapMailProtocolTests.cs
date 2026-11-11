using System.IO;
using GameMesh.Aoi;
using GameMesh.Auth;
using GameMesh.Player;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace GameMesh.Tests.EditMode
{
    public sealed class AoiMapMailProtocolTests
    {
        [Test]
        public void ProtocolManifest_HashMatchesSchema()
        {
            var protoDir = Path.Combine(Application.dataPath, "GameMesh", "Protocol");
            var manifestPath = Path.Combine(protoDir, "protocol_manifest.json");
            var schemaPath = Path.Combine(protoDir, "Schema", "game.proto");
            var generatedPath = Path.Combine(protoDir, "Generated", "Game.cs");
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonUtility.FromJson<ProtocolManifest>(json);
            ProtocolContract.ValidateManifest(manifest, schemaPath, generatedPath);
        }

        [Test]
        public void Protocol_RequiredTypesPresent()
        {
            var missing = ProtocolCapabilities.MissingRequiredTypes();
            CollectionAssert.IsEmpty(missing);
            Assert.IsTrue(ProtocolCapabilities.HasType("PlayerAttributes"));
            Assert.IsTrue(ProtocolCapabilities.HasType("MoveReq"));
            Assert.IsTrue(ProtocolCapabilities.HasType("AoiDelta"));
            Assert.IsTrue(ProtocolCapabilities.HasType("PlayerMailSendReq"));
            Assert.IsTrue(ProtocolCapabilities.HasType("MailboxChangedNotify"));
            var req = new GameRequest { Move = new MoveReq() };
            Assert.AreEqual(GameRequest.BodyOneofCase.Move, req.BodyCase);
            Assert.IsTrue(ProtocolCapabilities.HasType("ClientHelloReq"));
            Assert.IsTrue(ProtocolCapabilities.HasType("WorldSnapshotReq"));
            Assert.IsTrue(ProtocolCapabilities.HasType("RespawnReq"));
            Assert.IsTrue(ProtocolCapabilities.HasType("MapManifestEntry"));
            Assert.IsTrue(ProtocolCapabilities.HasType("SessionReplacedNotify"));
            Assert.AreEqual(GameRequest.BodyOneofCase.ClientHello, new GameRequest { ClientHello = new ClientHelloReq() }.BodyCase);
            Assert.AreEqual(GameResponse.BodyOneofCase.ServerHello, new GameResponse { ServerHello = new ServerHelloRsp() }.BodyCase);
            Assert.AreEqual(GameResponse.BodyOneofCase.SessionReplaced,
                new GameResponse { SessionReplaced = new SessionReplacedNotify() }.BodyCase);
            Assert.AreEqual(GameResponse.BodyOneofCase.FullSnapshot, new GameResponse { FullSnapshot = new FullStateSnapshotRsp() }.BodyCase);
            Assert.AreEqual(80, GameRequest.InteractPortalFieldNumber);
            Assert.AreEqual(81, GameResponse.InteractPortalFieldNumber);
            Assert.AreEqual(4, MapManifestEntry.SceneNameFieldNumber);
            Assert.AreEqual(5, MapManifestEntry.KindFieldNumber);
            Assert.AreEqual(6, MapManifestEntry.VisualMapTemplateIdFieldNumber);
            Assert.AreEqual(7, MapManifestEntry.PortalsFieldNumber);
            Assert.IsTrue(ProtocolCapabilities.HasType("InteractPortalReq"));
            Assert.IsTrue(ProtocolCapabilities.HasType("InteractPortalRsp"));
            Assert.IsTrue(ProtocolCapabilities.HasType("PortalDef"));
            Assert.AreEqual(GameRequest.BodyOneofCase.InteractPortal,
                new GameRequest { InteractPortal = new InteractPortalReq() }.BodyCase);
            Assert.AreEqual(GameResponse.BodyOneofCase.InteractPortal,
                new GameResponse { InteractPortal = new InteractPortalRsp() }.BodyCase);
            Assert.AreEqual(51, GameRequest.FriendListFieldNumber);
            Assert.AreEqual(81, GameRequest.FriendSearchFieldNumber);
            Assert.AreEqual(82, GameRequest.FriendApplyFieldNumber);
            Assert.AreEqual(83, GameRequest.FriendAcceptFieldNumber);
            Assert.AreEqual(84, GameRequest.FriendRejectFieldNumber);
            Assert.AreEqual(85, GameRequest.FriendDeleteFieldNumber);
            Assert.AreEqual(86, GameRequest.FriendRequestListFieldNumber);
            Assert.AreEqual(87, GameRequest.FriendBlockFieldNumber);
            Assert.AreEqual(88, GameRequest.FriendUnblockFieldNumber);
            Assert.AreEqual(89, GameRequest.FriendBlockListFieldNumber);
            Assert.AreEqual(51, GameResponse.FriendListFieldNumber);
            Assert.AreEqual(82, GameResponse.FriendApplyFieldNumber);
            Assert.AreEqual(90, GameResponse.FriendRequestPushFieldNumber);
            Assert.AreEqual(91, GameResponse.FriendAddedPushFieldNumber);
            Assert.AreEqual(92, GameResponse.FriendRemovedPushFieldNumber);
            Assert.AreEqual(93, GameResponse.FriendPresencePushFieldNumber);
            Assert.AreEqual(94, GameResponse.FriendSearchFieldNumber);
            Assert.IsTrue(ProtocolCapabilities.HasType("FriendBrief"));
            Assert.IsTrue(ProtocolCapabilities.HasType("FriendSearchReq"));
            Assert.IsTrue(ProtocolCapabilities.HasType("FriendRequestPush"));
            Assert.AreEqual(GameRequest.BodyOneofCase.FriendSearch,
                new GameRequest { FriendSearch = new FriendSearchReq() }.BodyCase);
            Assert.AreEqual(GameResponse.BodyOneofCase.FriendAccept,
                new GameResponse { FriendAccept = new FriendAcceptRsp() }.BodyCase);
            var rsp = new GameResponse
            {
                AoiDelta = new AoiDelta(),
            };
            Assert.AreEqual(GameResponse.BodyOneofCase.AoiDelta, rsp.BodyCase);
            rsp = new GameResponse { PlayerMailSend = new PlayerMailSendRsp() };
            Assert.AreEqual(GameResponse.BodyOneofCase.PlayerMailSend, rsp.BodyCase);
            rsp = new GameResponse { MailboxChanged = new MailboxChangedNotify() };
            Assert.AreEqual(GameResponse.BodyOneofCase.MailboxChanged, rsp.BodyCase);
        }

        [Test]
        public void MapRle_RoundTripAndStableHash()
        {
            var cells = new[] { true, true, false, false, false, true };
            var rle = MapStaticData.EncodeRle(cells);
            CollectionAssert.AreEqual(new[] { 1, 2, 0, 3, 1, 1 }, rle);
            CollectionAssert.AreEqual(cells, MapStaticData.DecodeRle(rle, cells.Length));
            Assert.AreEqual(1, MapStaticData.Col(1.2f, 0f, 1f));
            Assert.AreEqual(2, MapStaticData.Row(2.9f, 0f, 1f));
            var data = new MapStaticData
            {
                map_template_id = 1001,
                scene_name = "MainScene",
                aoi_cell_size = 12f,
                nav_sample_step = 1f,
                grid_width = 3,
                grid_height = 2,
                walkable_rle = rle,
                bounds_min = new MapVec3(0, 0, 0),
                bounds_max = new MapVec3(3, 1, 2),
                spawn_points = { new MapSpawnPoint { id = "default", x = 1, y = 0, z = 1, yaw = 90 } }
            };
            Assert.IsTrue(data.TryGetWalkable(1.2f, 0.1f, cells, out var col, out var row));
            Assert.AreEqual(1, col);
            Assert.AreEqual(0, row);
            var json = data.ToDeterministicJson();
            StringAssert.Contains("\"walkable_rle\": [1, 2, 0, 3, 1, 1]", json);
            StringAssert.Contains("\"bounds_min\": [0, 0, 0]", json);
            StringAssert.Contains("\"id\":\"default\"", json);
            StringAssert.Contains("\"aoi_cell_size\": 12.0", json);
            var a = data.Sha256();
            var b = data.Sha256();
            Assert.AreEqual(a, b);
            Assert.AreEqual(64, a.Length);
        }

        [Test]
        public void Aoi_EnterMoveLeave_IdempotentAndIgnoresOldSeq()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 1, StateSeq = 1, MapInstanceId = 9 }
            });
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 2, StateSeq = 1, MapInstanceId = 9 }
            });
            Assert.AreEqual(1, world.Entities.Count);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 5, StateSeq = 3, MapInstanceId = 9 }
            });
            Assert.AreEqual(5f, world.Entities[2].X);
            // Same state_seq with a new body pose is an along-path update — must apply.
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 7, StateSeq = 3, MapInstanceId = 9 }
            }));
            Assert.AreEqual(7f, world.Entities[2].X);
            Assert.AreEqual(3UL, world.Entities[2].StateSeq);
            // Strictly older seq is dropped.
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 0, StateSeq = 2, MapInstanceId = 9 }
            });
            Assert.AreEqual(7f, world.Entities[2].X);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, MapInstanceId = 9 }
            });
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, MapInstanceId = 9 }
            });
            Assert.AreEqual(0, world.Entities.Count);
        }

        [Test]
        public void Aoi_LeaveWithOnlyPlayerIdRemovesTheBody()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 99, PlayerId = 42, X = 3, StateSeq = 4, MapInstanceId = 9
                }
            });
            Assert.IsTrue(world.Entities.ContainsKey(42));
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 0, PlayerId = 42, MapInstanceId = 9 }
            }));
            Assert.AreEqual(0, world.Entities.Count);
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 0, PlayerId = 42, MapInstanceId = 9 }
            }));
        }

        [Test]
        public void Aoi_SameSeqNewPositionAndServerTimeApplies()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                ServerTimeMs = 1000,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 0, PlayerId = 42, X = 1, StateSeq = 4, MapInstanceId = 9, ServerTimeMs = 1000
                }
            });
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                ServerTimeMs = 1800,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 0, PlayerId = 42, X = 8, Z = 2, StateSeq = 4, MapInstanceId = 9,
                    ServerTimeMs = 1800
                }
            }));
            var body = world.Entities[42];
            Assert.AreEqual(8f, body.X);
            Assert.AreEqual(2f, body.Z);
            Assert.AreEqual(1800L, body.ServerTimeMs);
            Assert.AreEqual(4UL, body.StateSeq);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                ServerTimeMs = 1900,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 0, PlayerId = 42, X = 0, StateSeq = 3, MapInstanceId = 9, ServerTimeMs = 1900
                }
            });
            Assert.AreEqual(8f, body.X);
            Assert.AreEqual(1800L, body.ServerTimeMs);
        }

        [Test]
        public void Aoi_MoveWithoutNameOrHp_KeepsEnterValues()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 2, PlayerId = 2, Name = "Bot002", Hp = 80, MaxHp = 100, StateSeq = 1, MapInstanceId = 9
                }
            });
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 4, Yaw = 90, StateSeq = 2, MapInstanceId = 9 }
            });
            var e = world.Entities[2];
            Assert.AreEqual("Bot002", e.Name);
            Assert.AreEqual(80f, e.Hp);
            Assert.AreEqual(100f, e.MaxHp);
            Assert.AreEqual(4f, e.X);
            Assert.AreEqual(90f, e.Yaw);
            Assert.AreEqual(2UL, e.StateSeq);

            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 2, PlayerId = 2, Name = "Renamed", Hp = 0, MaxHp = 100, StateSeq = 3, MapInstanceId = 9
                }
            });
            Assert.AreEqual("Renamed", e.Name);
            Assert.AreEqual(0f, e.Hp);
            Assert.AreEqual(100f, e.MaxHp);
        }

        [Test]
        public void Aoi_RejectsOtherMapAndSelf()
        {
            var world = new AoiWorld { LocalPlayerId = 7 };
            world.SetMapInstance(1);
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 2,
                Entity = new EntitySnapshotDto { EntityId = 8, PlayerId = 8, MapInstanceId = 2 }
            }));
            Assert.AreEqual(1, world.ProtocolErrors);
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 1,
                Entity = new EntitySnapshotDto { EntityId = 7, PlayerId = 7, MapInstanceId = 1 }
            }));
            Assert.AreEqual(0, world.Entities.Count);
        }

        [Test]
        public void MoveSampler_RateAndInvalidCoords()
        {
            var sampler = new MoveSampler { SendHz = 10f, PositionThreshold = 0.05f };
            Assert.IsFalse(sampler.ShouldSend(new Vector3(float.NaN, 0, 0), 0, 1f, out var reject));
            Assert.AreEqual("NaN/Inf", reject);
            Assert.IsTrue(sampler.ShouldSend(new Vector3(0, 0, 0), 0, 1f, out _));
            sampler.MarkSent(Vector3.zero, 0, 1f);
            Assert.IsFalse(sampler.ShouldSend(new Vector3(0.01f, 0, 0), 0, 1.01f, out _));
            Assert.IsTrue(sampler.ShouldSend(new Vector3(1, 0, 0), 0, 1.2f, out _));
            sampler.MarkSent(new Vector3(1, 0, 0), 0, 1.2f);
            Assert.IsFalse(sampler.ShouldSend(new Vector3(1, 0, 0), 0, 3f, out _), "stationary must not send a zero delta");
            var fast = new MoveSampler { SendHz = 10f, FastSendHz = 20f, FastSpeed = 6f, PositionThreshold = 0.05f };
            fast.MarkSent(Vector3.zero, 0, 1f);
            Assert.IsTrue(fast.ShouldSend(new Vector3(1f, 0, 0), 0, 1.06f, out _), "fast motion may send at 20 Hz");
        }

        [Test]
        public void RequestSeq_ResetsAfterLoginAndEnterMap()
        {
            var conn = new GameConnection(new ImmediateDispatcher());
            var hello = new GameRequest { ClientHello = new ClientHelloReq() };
            var login = new GameRequest { Login = new LoginReq() };
            Assert.AreEqual(1UL, conn.AssignRequestSeq(hello));
            Assert.AreEqual(2UL, conn.AssignRequestSeq(login));
            conn.ResetRequestSeq();
            var enter = new GameRequest { EnterMap = new EnterMapReq() };
            Assert.AreEqual(1UL, conn.AssignRequestSeq(enter));
            conn.ResetRequestSeq();
            var move = new GameRequest { Move = new MoveReq() };
            var move2 = new GameRequest { Move = new MoveReq() };
            Assert.AreEqual(1UL, conn.AssignRequestSeq(move));
            Assert.AreEqual(2UL, conn.AssignRequestSeq(move2));
            Assert.Greater(move2.Seq, move.Seq);
            var afterReconnect = new GameRequest { Move = new MoveReq() };
            Assert.AreEqual(3UL, conn.AssignRequestSeq(afterReconnect));
            conn.DisconnectAsync(GameMesh.Network.DisconnectReason.Reconnect, System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();
            var afterDisconnect = new GameRequest { Move = new MoveReq() };
            Assert.AreEqual(4UL, conn.AssignRequestSeq(afterDisconnect));
        }

        [Test]
        public void Logout_NotOkMarksSessionUnknown()
        {
            var session = new GameSession();
            session.ApplyLogin(9, "sess", "token", 3, "Luna");
            var rsp = new GameResponse
            {
                Ok = true,
                ErrorCode = "ERR_SESSION_EXPIRED",
                Logout = new LogoutRsp { Ok = false, Message = "no" }
            };
            var parsed = AuthResponse.FromLogout(rsp, true);
            Assert.IsFalse(parsed.AuthorityOk);
            session.ApplyLogoutOutcome(parsed);
            Assert.IsTrue(session.LogoutUnconfirmed);
            Assert.IsFalse(session.AutoReconnect);
            Assert.IsFalse(session.HasIdentity);

            var okSession = new GameSession();
            okSession.ApplyLogin(9, "sess", "token", 3, "Luna");
            var ok = AuthResponse.FromLogout(new GameResponse
            {
                Ok = true,
                Logout = new LogoutRsp { Ok = true }
            }, true);
            okSession.ApplyLogoutOutcome(ok);
            Assert.IsFalse(okSession.LogoutUnconfirmed);
            Assert.IsFalse(okSession.HasIdentity);
        }

        [Test]
        public void RemotePoseBuffer_InterpolatesAndDropsStaleSeq()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(0, 0, 0, 0, 1f, 1));
            Assert.IsTrue(buffer.Push(10, 0, 0, 90, 1.1f, 2));
            Assert.IsTrue(buffer.TrySample(1.075f, 0.05f, out var quarterX, out _, out _, out _));
            Assert.AreEqual(2.5f, quarterX, 0.01f,
                "AOI body playback must stay linear instead of braking at every sample");
            Assert.IsTrue(buffer.TrySample(1.10f, 0.05f, out var x, out _, out _, out var yaw));
            Assert.AreEqual(5f, x, 0.01f);
            Assert.AreEqual(45f, yaw, 0.01f);
            // Same seq with a new body pose is kept (along-path). Older seq is dropped.
            Assert.IsTrue(buffer.Push(20, 0, 0, 90, 1.2f, 2));
            Assert.IsFalse(buffer.Push(99, 0, 0, 0, 1.25f, 1));
            Assert.IsTrue(buffer.TrySample(1.20f, 0.05f, out x, out _, out _, out yaw));
            Assert.Greater(x, 10f);
            Assert.LessOrEqual(x, 20f);
            Assert.AreEqual(90f, yaw, 0.01f);
        }

        [Test]
        public void RemotePoseBuffer_MovePositionIsTheCurrentPoint()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(0, 0, 0, 0, 1f, 1));
            Assert.IsTrue(buffer.Push(10, 0, 0, 90, 1.1f, 2));
            // Past the newest MOVE, stay on that position. Do not read vx/vz/move_start_ms.
            Assert.IsTrue(buffer.TrySample(1.35f, 0f, out var x, out _, out _, out var yaw));
            Assert.AreEqual(10f, x, 0.01f);
            Assert.AreEqual(90f, yaw, 0.01f);
            Assert.IsTrue(buffer.TrySample(2f, 0f, out x, out _, out _, out _));
            Assert.AreEqual(10f, x, 0.01f);
        }

        [Test]
        public void RemotePoseBuffer_SameSeqPositionIsPlayed()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(0, 0, 0, 0, 1f, 4));
            Assert.IsTrue(buffer.Push(2, 0, 0, 0, 1.2f, 4));
            Assert.IsTrue(buffer.TrySample(1.4f, 0f, out var x, out _, out _, out _));
            Assert.AreEqual(2f, x, 0.01f);
            Assert.IsTrue(buffer.Push(2, 0, 4, 0, 1.6f, 4));
            Assert.IsTrue(buffer.TrySample(1.8f, 0f, out x, out _, out var z, out _));
            Assert.AreEqual(2f, x, 0.01f);
            Assert.AreEqual(4f, z, 0.01f);
        }

        [Test]
        public void RemotePoseBuffer_ZeroStepDoesNotDrift()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(3, 0, 5, 10, 1f, 1));
            Assert.IsTrue(buffer.Push(3, 0, 5, 10, 1.2f, 1));
            Assert.IsTrue(buffer.TrySample(4f, 0f, out var x, out _, out var z, out _));
            Assert.AreEqual(3f, x, 0.01f);
            Assert.AreEqual(5f, z, 0.01f);
        }

        [Test]
        public void RemoteLocomotion_MovingStepPlaysRunAndZeroStepStops()
        {
            // 2m in 0.2s is the near-AOI pace. The blend tree's run clip is Speed 6.
            RemotePlayerView.LocomotionFromStep(2f, 0.2f, out var speed, out var hold);
            Assert.AreEqual(6f, speed, 0.01f);
            Assert.AreEqual(0.35f, hold, 0.01f);

            RemotePlayerView.LocomotionFromStep(0f, 0.2f, out speed, out hold);
            Assert.AreEqual(0f, speed, 0.001f);
            Assert.AreEqual(0f, hold, 0.001f);

            // 30m over 3s is the far AOI step. Walk it at 10 m/s; do not cross it in one frame.
            Assert.AreEqual(10f, RemotePlayerView.ChaseMetersPerSec(30f, 3f), 0.01f);
            Assert.AreEqual(10f, RemotePlayerView.ChaseMetersPerSec(30f, 0.05f), 0.01f);
            Assert.AreEqual(0f, RemotePlayerView.ChaseMetersPerSec(0f, 0.2f), 0.001f);
            Assert.IsTrue(RemotePoseBuffer.IsAlongPathUpdate(4, 4));
            Assert.IsFalse(RemotePoseBuffer.IsAlongPathUpdate(3, 4));
        }

        [Test]
        public void RemotePoseBuffer_LongFrameCannotBecomeOneFrameTeleport()
        {
            Assert.AreEqual(0.05f, RemotePoseBuffer.ClampRenderDelta(2f), 0.0001f);
            Assert.AreEqual(0.016f, RemotePoseBuffer.ClampRenderDelta(0.016f), 0.0001f);
            Assert.AreEqual(0.001f, RemotePoseBuffer.ClampRenderDelta(0f), 0.0001f);
            // At the 10 m/s authority cap, one rendered frame advances at most 0.5m.
            Assert.LessOrEqual(10f * RemotePoseBuffer.ClampRenderDelta(2f), 0.5001f);
        }

        [Test]
        public void RemotePoseBuffer_FarSameSeqGap_IsInterpolatedNotSnapped()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(0, 0, 0, 0, 10f, 7));
            // Far AOI intentionally sends the same path state about every three seconds.
            Assert.IsTrue(buffer.Push(18, 0, 0, 90, 13f, 7));
            Assert.AreEqual(2, buffer.Count);
            Assert.IsTrue(buffer.TrySample(13f, 1.5f, out var x, out _, out _, out var yaw));
            Assert.AreEqual(9f, x, 0.01f);
            Assert.AreEqual(45f, yaw, 0.01f);
            // The later same-seq position is the current point, including after its time.
            Assert.IsTrue(buffer.TrySample(20f, 0f, out x, out _, out _, out yaw));
            Assert.AreEqual(18f, x, 0.01f);
            Assert.AreEqual(90f, yaw, 0.01f);
        }

        [Test]
        public void RecoverPose_KeepsHorizontalStepOutOfAHole()
        {
            var stuck = new Vector3(-180f, -28.19f, 796f);
            var previous = new Vector3(-172.3f, -27.4f, 805.1f);
            Assert.IsFalse(GameMeshWorldBinder.TryRecoverPose(
                stuck, true, previous, false, 0f, -37.5f, out _));

            var buried = new Vector3(-180f, -34.9f, 796f);
            Assert.IsTrue(GameMeshWorldBinder.TryRecoverPose(
                buried, true, previous, true, -27.5f, -37.5f, out var lifted));
            Assert.AreEqual(-180f, lifted.x, 0.001f);
            Assert.AreEqual(796f, lifted.z, 0.001f);
            Assert.Greater(lifted.y, -27.5f);

            var voided = new Vector3(-180f, -40f, 796f);
            Assert.IsTrue(GameMeshWorldBinder.TryRecoverPose(
                voided, true, previous, false, 0f, -37.5f, out var restored));
            Assert.AreEqual(-180f, restored.x, 0.001f);
            Assert.AreEqual(796f, restored.z, 0.001f);
            Assert.AreEqual(previous.y, restored.y, 0.001f);
        }

        [Test]
        public void AoiSampleClock_RewoundConfirmTimeStillAdvancesThePath()
        {
            Assert.IsTrue(AoiSampleClock.IsRewound(4_500_000_000L, 4_500_010_000L));
            Assert.IsFalse(AoiSampleClock.IsRewound(4_500_010_000L, 4_500_010_000L));
            Assert.IsFalse(AoiSampleClock.IsRewound(4_500_010_004L, 4_500_010_000L));
            Assert.IsFalse(AoiSampleClock.IsRewound(0L, 4_500_010_000L));

            // 32m at 10 m/s is the far-AOI step. Place it on the timeline instead of dropping it.
            var placed = AoiSampleClock.PlaceRewound(110f, 100f, 32f, 10f);
            Assert.AreEqual(103.2f, placed, 0.001f);
            Assert.Greater(placed, 100f);
            Assert.Less(placed, 110f);
        }

        [Test]
        public void RemotePoseBuffer_SeqGapStillInterpolatesFromLastVisiblePose()
        {
            var buffer = new RemotePoseBuffer();
            Assert.IsTrue(buffer.Push(0, 0, 0, 0, 1f, 1));
            Assert.IsTrue(buffer.Push(3, 0, 0, 0, 1.05f, 2));
            // Missing intermediate seqs are coalesced/lost samples, not a teleport instruction.
            Assert.IsTrue(buffer.Push(30, 0, 0, 0, 1.6f, 9));
            Assert.AreEqual(3, buffer.Count);
            Assert.IsTrue(buffer.TrySample(1.6f, 0.275f, out var x, out _, out _, out _));
            Assert.AreEqual(16.5f, x, 0.01f);
            Assert.IsTrue(buffer.TrySample(1.7f, 0f, out x, out _, out _, out _));
            Assert.AreEqual(30f, x, 0.01f);
        }

        [Test]
        public void HeartbeatClock_TimesOutWithoutReply()
        {
            var clock = new HeartbeatClock();
            clock.Arm(clock.MonotonicMs);
            Assert.IsFalse(clock.ResponseTimedOut(15000));
        }

        [Test]
        public void ConnectionState_LegalHappyPath()
        {
            var s = ConnectionState.Disconnected;
            s = ConnectionStateMachine.Transition(s, ConnectionState.Connecting);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Handshaking);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Connected);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Authenticating);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Authenticated);
            s = ConnectionStateMachine.Transition(s, ConnectionState.EnteringWorld);
            s = ConnectionStateMachine.Transition(s, ConnectionState.InWorld);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Closing);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Disconnected);
            Assert.AreEqual(ConnectionState.Disconnected, s);
            s = ConnectionState.InWorld;
            s = ConnectionStateMachine.Transition(s, ConnectionState.LoggingOut);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Closing);
            s = ConnectionStateMachine.Transition(s, ConnectionState.Disconnected);
            Assert.AreEqual(ConnectionState.Disconnected, s);
        }

        [Test]
        public void Aoi_LeaveThenMove_DoesNotCreateBody()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            var errors = world.ProtocolErrors;
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, MapInstanceId = 9 }
            }));
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, X = 5, StateSeq = 2, MapInstanceId = 9 }
            }));
            Assert.AreEqual(0, world.Entities.Count);
            Assert.IsFalse(world.Entities.ContainsKey(42));
            Assert.AreEqual(errors, world.ProtocolErrors);
        }

        [Test]
        public void Aoi_SecondLeave_IsIgnored()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, X = 1, StateSeq = 1, MapInstanceId = 9 }
            }));
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, MapInstanceId = 9 }
            }));
            var errors = world.ProtocolErrors;
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, MapInstanceId = 9 }
            }));
            Assert.AreEqual(0, world.Entities.Count);
            Assert.AreEqual(errors, world.ProtocolErrors);
        }

        [Test]
        public void Aoi_EnterWhilePresent_UpdatesTheSameRow()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 42, PlayerId = 42, Name = "Bot042", X = 1, Hp = 80, MaxHp = 100,
                    StateSeq = 5, MapInstanceId = 9
                }
            }));
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 42, PlayerId = 42, Name = "Stale", X = 99, Hp = 1, MaxHp = 100,
                    StateSeq = 4, MapInstanceId = 9
                }
            }));
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 42, PlayerId = 42, Name = "Bot042b", X = 8, Hp = 70, MaxHp = 100,
                    StateSeq = 6, MapInstanceId = 9
                }
            }));
            Assert.AreEqual(1, world.Entities.Count);
            var body = world.Entities[42];
            Assert.AreEqual(8f, body.X);
            Assert.AreEqual("Bot042b", body.Name);
            Assert.AreEqual(70f, body.Hp);
            Assert.AreEqual(100f, body.MaxHp);
            Assert.AreEqual(6ul, body.StateSeq);
        }

        [Test]
        public void Aoi_MoveThenLeave_RemovesBodyAndModelKey()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            ulong removed = 0;
            var modelKeys = 0;
            world.BodyRemoved += id =>
            {
                if (id == 42)
                {
                    removed = id;
                    modelKeys--;
                }
            };
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Enter,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, X = 1, StateSeq = 1, MapInstanceId = 9 }
            }));
            modelKeys = 1;
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, X = 6, StateSeq = 2, MapInstanceId = 9 }
            }));
            Assert.AreEqual(6f, world.Entities[42].X);
            Assert.IsTrue(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, MapInstanceId = 9 }
            }));
            Assert.AreEqual(0, world.Entities.Count);
            Assert.IsFalse(world.Entities.ContainsKey(42));
            Assert.AreEqual(42ul, removed);
            Assert.AreEqual(0, modelKeys);
        }

        [Test]
        public void Aoi_MoveWithoutEnter_DoesNotCreate()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            Assert.IsFalse(world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 42, PlayerId = 42, X = 4, StateSeq = 1, MapInstanceId = 9 }
            }));
            Assert.AreEqual(0, world.Entities.Count);
            Assert.AreEqual(0, world.ProtocolErrors);
        }
    }
}
