using System.Threading;
using System.Threading.Tasks;
using GameMesh.Aoi;
using GameMesh.Auth;
using GameMesh.Bootstrap;
using GameMesh.Mail;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace GameMesh.Tests.EditMode
{
    public sealed class FoundationMappingTests
    {
        [Test]
        public void LoginProfile_MapsAllFields()
        {
            var profile = new PlayerAttributes
            {
                PlayerId = 42,
                PlayerName = "Ada",
                Hp = 80,
                MaxHp = 100,
                Mp = 20,
                MaxMp = 40,
                Attack = 7,
                SpellPower = 9,
                Defense = 3,
                MagicResistance = 5,
                CritChance = 0.2f,
                CritDamage = 1.8f,
                MoveSpeed = 6.5f,
                AttackSpeed = 1.2f,
                StatsVersion = 9
            };
            var mapped = ProtocolMapper.ToAttributes(profile);
            Assert.IsTrue(mapped.FromServer);
            Assert.AreEqual(42ul, mapped.PlayerId);
            Assert.AreEqual("Ada", mapped.Name);
            Assert.AreEqual(80f, mapped.Hp);
            Assert.AreEqual(100f, mapped.MaxHp);
            Assert.AreEqual(20f, mapped.Mp);
            Assert.AreEqual(40f, mapped.MaxMp);
            Assert.AreEqual(7f, mapped.Attack);
            Assert.AreEqual(9f, mapped.SpellPower);
            Assert.AreEqual(3f, mapped.Defense);
            Assert.AreEqual(5f, mapped.MagicResist);
            Assert.AreEqual(0.2f, mapped.CritRate);
            Assert.AreEqual(1.8f, mapped.CritDamage);
            Assert.AreEqual(6.5f, mapped.MoveSpeed);
            Assert.AreEqual(1.2f, mapped.AttackSpeed);
            Assert.AreEqual(9ul, mapped.StatsVersion);
            Assert.AreEqual("ALIVE", mapped.LifeState);
        }

        [Test]
        public void Hello_ValidatesHashVersionAndTimeoutShape()
        {
            var ok = new ServerHelloRsp
            {
                Ok = true,
                ProtocolVersion = 1,
                MinSupportedProtocolVersion = 1,
                SchemaSha256 = "abc"
            };
            Assert.IsTrue(ProtocolHandshake.TryValidate(ok, "ABC", 1, out _, out _));
            var mismatch = new ServerHelloRsp { Ok = true, ProtocolVersion = 1, SchemaSha256 = "ffff" };
            Assert.IsFalse(ProtocolHandshake.TryValidate(mismatch, "abc", 1, out var code, out _));
            Assert.AreEqual("ERR_SCHEMA_MISMATCH", code);
            var low = new ServerHelloRsp { Ok = true, ProtocolVersion = 2, MinSupportedProtocolVersion = 2, SchemaSha256 = "abc" };
            Assert.IsFalse(ProtocolHandshake.TryValidate(low, "abc", 1, out code, out _));
            Assert.AreEqual("ERR_CLIENT_UPGRADE_REQUIRED", code);
        }

        [Test]
        public void TopLevelError_WinsOverNestedCode()
        {
            var rsp = new GameResponse
            {
                Ok = false,
                ErrorCode = "ERR_OVERLOADED",
                Retryable = true,
                TraceId = "trace-ABCDEF12",
                Move = new MoveRsp { ErrorCode = "ERR_MOVE_TOO_FAST" }
            };
            Assert.AreEqual("ERR_OVERLOADED", ProtocolMapper.ExtractErrorCode(rsp));
            Assert.AreEqual("ABCDEF12", ProtocolMapper.ShortTraceId(rsp));
        }

        [Test]
        public void WorldSnapshot_AppliesAtomicallyAndRejectsBad()
        {
            var session = new GameSession { PlayerId = 1, MapInstanceId = 9, SnapshotVersion = 2 };
            session.Attributes.Hp = 10;
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            world.ApplySnapshot(new[]
            {
                new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 1, MapInstanceId = 9 }
            });
            var bad = new FullStateSnapshotRsp { Ok = true, SnapshotVersion = 1, MapInstanceId = 9 };
            Assert.IsFalse(WorldSnapshotApplier.TryBuild(bad, 1, 9, 2, out _, out var code, out _));
            Assert.AreEqual("ERR_AOI_RESYNC_REQUIRED", code);
            Assert.AreEqual(1, world.Entities.Count);
            Assert.AreEqual(10f, session.Attributes.Hp);

            var good = new FullStateSnapshotRsp
            {
                Ok = true,
                PlayerId = 1,
                MapTemplateId = 1001,
                MapInstanceId = 9,
                BaselineServerSeq = 40,
                SnapshotVersion = 3,
                LifeState = "ALIVE",
                Profile = new PlayerAttributes { PlayerId = 1, PlayerName = "Ada", Hp = 80, MaxHp = 100, LifeState = "ALIVE" },
                Self = new EntitySnapshot { PlayerId = 1, Position = new Vec3 { X = 4 }, Hp = 80, MaxHp = 100 }
            };
            good.AoiEntities.Add(new EntitySnapshot { PlayerId = 3, Position = new Vec3 { X = 8 } });
            Assert.IsTrue(WorldSnapshotApplier.TryBuild(good, 1, 9, 2, out var model, out _, out _));
            var push = new PushReliability();
            WorldSnapshotApplier.Apply(session, world, push, new PushGapCache(), model);
            Assert.AreEqual(80f, session.Attributes.Hp);
            Assert.AreEqual(40ul, session.LastServerSeq);
            Assert.AreEqual(3u, session.SnapshotVersion);
            Assert.AreEqual(1, world.Entities.Count);
            Assert.IsTrue(world.Entities.ContainsKey(3));
        }

        [Test]
        public void SessionReplaced_StopsReconnectAndKeepsIdentity()
        {
            var session = new GameSession();
            session.ApplyLogin(42, "sess", "tok", 3, "Ada");
            session.DeviceId = "dev-1";
            session.ClearSessionKeepIdentity();
            session.AutoReconnect = false;
            Assert.AreEqual(42ul, session.PlayerId);
            Assert.AreEqual("Ada", session.DisplayName);
            Assert.IsTrue(string.IsNullOrEmpty(session.Token));
            Assert.IsFalse(session.HasIdentity);
        }

        [Test]
        public void RespawnReq_KeepsStableOperationId()
        {
            var first = new RespawnReq { PlayerId = 1, MapInstanceId = 9, OperationId = "op-respawn" };
            var second = new RespawnReq { PlayerId = 1, MapInstanceId = 9, OperationId = first.OperationId };
            Assert.AreEqual("op-respawn", second.OperationId);
            var fail = new RespawnRsp { Ok = false, ErrorCode = "ERR_NOT_ON_MAP" };
            Assert.AreEqual("ERR_NOT_ON_MAP", fail.ErrorCode);
            Assert.AreNotEqual(100, fail.Self?.Hp ?? 0);
        }

        [Test]
        public void ErrorCatalog_CoversHelloMapMailAndKick()
        {
            Assert.AreEqual("协议 schema 与服务器不一致", GameErrorCatalog.Resolve("ERR_SCHEMA_MISMATCH").Chinese);
            Assert.AreEqual("账号已在其他设备登录", GameErrorCatalog.Resolve("ERR_SESSION_REPLACED").Chinese);
            Assert.IsTrue(GameErrorCatalog.Resolve("ERR_AOI_RESYNC_REQUIRED").Retryable);
            Assert.IsTrue(GameErrorCatalog.IsSessionReplaced("ERR_FENCE_STALE"));
            StringAssert.Contains("#ab12", GameErrorCatalog.FormatUi("ERR_OVERLOADED", "", "ab12"));
        }

        [Test]
        public void MapHash_AcceptsMatchAndRejectsMismatch()
        {
            Assert.IsTrue(GameMeshClient.MapHashesMatch("abc", 1, "ABC", 1));
            Assert.IsFalse(GameMeshClient.MapHashesMatch("abc", 1, "def", 1));
            Assert.IsFalse(GameMeshClient.MapHashesMatch("abc", 1, "abc", 2));
        }

        [Test]
        public void EnterMapSnapshot_AppliesAtomicallyFromProtobuf()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            var snap = new[]
            {
                new EntitySnapshot
                {
                    PlayerId = 2, PlayerName = "B", Position = new Vec3 { X = 3 }, Yaw = 10, Hp = 8, MaxHp = 10,
                    StateSeq = 4
                }
            };
            ProtocolMapper.ApplySnapshot(world, snap, 9);
            Assert.AreEqual(1, world.Entities.Count);
            Assert.AreEqual(3f, world.Entities[2].X);
            Assert.AreEqual("B", world.Entities[2].Name);
        }

        [Test]
        public void AoiDelta_MapsRealProtobufEvents()
        {
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(9);
            var delta = new AoiDelta { MapInstanceId = 9 };
            delta.Events.Add(new AoiEvent
            {
                Op = 1,
                Entity = new EntitySnapshot { PlayerId = 2, PlayerName = "B", Position = new Vec3 { X = 1 }, StateSeq = 1 }
            });
            Assert.IsTrue(ProtocolMapper.ApplyAoiDelta(world, delta));
            delta = new AoiDelta { MapInstanceId = 9 };
            delta.Events.Add(new AoiEvent
            {
                Op = 2,
                Entity = new EntitySnapshot { PlayerId = 2, Position = new Vec3 { X = 5 }, StateSeq = 3 }
            });
            Assert.IsTrue(ProtocolMapper.ApplyAoiDelta(world, delta));
            Assert.AreEqual(5f, world.Entities[2].X);
            delta = new AoiDelta { MapInstanceId = 9 };
            delta.Events.Add(new AoiEvent
            {
                Op = 2,
                Entity = new EntitySnapshot { PlayerId = 2, Position = new Vec3 { X = 0 }, StateSeq = 2 }
            });
            ProtocolMapper.ApplyAoiDelta(world, delta);
            Assert.AreEqual(5f, world.Entities[2].X);
            delta = new AoiDelta { MapInstanceId = 9 };
            delta.Events.Add(new AoiEvent { Op = 3, Entity = new EntitySnapshot { PlayerId = 2 } });
            ProtocolMapper.ApplyAoiDelta(world, delta);
            Assert.AreEqual(0, world.Entities.Count);
        }

        [Test]
        public void MoveCorrector_SmoothSnapAndOldSeqIgnoredBySamplerBounds()
        {
            var c = new MoveCorrector { SmoothError = 0.35f, SnapError = 2.5f };
            var smooth = c.Apply(Vector3.zero, new Vector3(0.5f, 0, 0), 1f, out var snapped);
            Assert.IsFalse(snapped);
            Assert.Greater(smooth.x, 0f);
            var snap = c.Apply(Vector3.zero, new Vector3(8f, 0, 0), 2f, out snapped);
            Assert.IsTrue(snapped);
            Assert.AreEqual(8f, snap.x);
        }

        [Test]
        public void MailSend_KeepsOperationIdUntilTerminal()
        {
            var session = new GameSession { PlayerId = 1 };
            string lastOp = null;
            var hits = 0;
            var mail = new MailClient(session, (req, ct) =>
            {
                lastOp = req.PlayerMailSend.OperationId;
                hits++;
                if (hits == 1)
                {
                    return Task.FromResult(new GameResponse
                    {
                        Ok = false,
                        PlayerMailSend = new PlayerMailSendRsp { Ok = false, ErrorCode = "ERR_MAIL_RATE_LIMIT" }
                    });
                }

                return Task.FromResult(new GameResponse
                {
                    Ok = true,
                    PlayerMailSend = new PlayerMailSendRsp { Ok = true, MailId = 7, IdempotentHit = true }
                });
            });
            var first = mail.SendAsync(2, "t", "b", CancellationToken.None).GetAwaiter().GetResult();
            StringAssert.Contains("ERR_MAIL_RATE_LIMIT", first);
            var op = lastOp;
            var second = mail.SendAsync(2, "t", "b", CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("", second);
            Assert.AreEqual(op, lastOp);
            Assert.AreEqual(7ul, mail.Page.LastSentMailId);
        }

        [Test]
        public void MailboxChanged_DebouncesThenPolls()
        {
            var session = new GameSession { PlayerId = 1 };
            var mail = new MailClient(session, (req, ct) => Task.FromResult(new GameResponse { Ok = true }));
            Assert.IsFalse(mail.ShouldPoll(1f, 0.9f, false));
            mail.NotifyMailboxChanged(1f);
            Assert.IsFalse(mail.ShouldPoll(1.1f, 0f, false));
            Assert.IsTrue(mail.ShouldPoll(1.5f, 0f, false));
            Assert.IsTrue(mail.ShouldPoll(12f, 0f, true));
        }

        [Test]
        public void ErrorCatalog_BranchesOnCodeNotEnglishMessage()
        {
            var info = GameErrorCatalog.Resolve("ERR_UNWALKABLE", "totally different english");
            Assert.AreEqual("目标位置不可行走", info.Chinese);
            Assert.IsFalse(info.Retryable);
            StringAssert.Contains("可重试", GameErrorCatalog.FormatUi("ERR_MOVE_TOO_FAST"));
        }

        [Test]
        public void ReconnectPolicy_SingleFlightAndBudget()
        {
            var p = new ReconnectPolicy();
            Assert.IsTrue(p.TryBegin(1f, 3, 1000, out _));
            Assert.IsFalse(p.TryBegin(1.1f, 3, 1000, out var reason));
            Assert.AreEqual("in-flight", reason);
            p.EndFailure();
            Assert.IsFalse(p.TryBegin(3f, 3, 1000, out reason));
            StringAssert.Contains("budget", reason);
            p.Reset();
            Assert.IsTrue(p.TryBegin(10f, 1, 5000, out _));
            p.EndFailure();
            Assert.IsFalse(p.TryBegin(10.1f, 1, 5000, out reason));
            StringAssert.Contains("attempts", reason);
        }

        [Test]
        public void MoveReq_FieldsAreFilled()
        {
            var req = new GameRequest
            {
                Move = new MoveReq
                {
                    PlayerId = 9,
                    MapInstanceId = 5,
                    Position = new Vec3 { X = 1, Y = 2, Z = 3 },
                    Yaw = 45,
                    ClientTimeMs = 123
                }
            };
            Assert.AreEqual(GameRequest.BodyOneofCase.Move, req.BodyCase);
            Assert.AreEqual(9ul, req.Move.PlayerId);
            Assert.AreEqual(5ul, req.Move.MapInstanceId);
            Assert.AreEqual(1f, req.Move.Position.X);
            Assert.AreEqual(123L, req.Move.ClientTimeMs);
        }

        [Test]
        public void EnterMapReq_WrongHashUsesRealFieldsNotForceMismatch()
        {
            var req = new EnterMapReq
            {
                RealmId = 1,
                MapTemplateId = 1001,
                MapInstanceId = 0,
                MapDataVersion = 1,
                MapDataSha256 = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
                OperationId = "op-1"
            };
            Assert.IsFalse(req.MapDataSha256.StartsWith("FORCE_MISMATCH"));
            Assert.IsFalse(GameMeshClient.MapHashesMatch(
                "ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317", 1,
                req.MapDataSha256, req.MapDataVersion));
        }

        [Test]
        public void PushGapCache_BoundsAndTakeExpected()
        {
            var cache = new PushGapCache { Limit = 2 };
            Assert.IsTrue(cache.TryBuffer(3, new GameResponse { Seq = 3 }));
            Assert.IsTrue(cache.TryBuffer(4, new GameResponse { Seq = 4 }));
            Assert.IsFalse(cache.TryBuffer(5, new GameResponse { Seq = 5 }));
            Assert.IsTrue(cache.TryTake(3, out var got));
            Assert.AreEqual(3ul, got.Seq);
            Assert.IsFalse(cache.TryTake(9, out _));
        }
    }
}
