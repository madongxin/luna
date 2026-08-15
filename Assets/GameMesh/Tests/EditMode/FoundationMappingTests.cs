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
    }
}
