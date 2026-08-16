using System.IO;
using GameMesh.Aoi;
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
            Assert.AreEqual(GameRequest.BodyOneofCase.ClientHello, new GameRequest { ClientHello = new ClientHelloReq() }.BodyCase);
            Assert.AreEqual(GameResponse.BodyOneofCase.ServerHello, new GameResponse { ServerHello = new ServerHelloRsp() }.BodyCase);
            Assert.AreEqual(GameResponse.BodyOneofCase.FullSnapshot, new GameResponse { FullSnapshot = new FullStateSnapshotRsp() }.BodyCase);
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
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 9,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, X = 0, StateSeq = 2, MapInstanceId = 9 }
            });
            Assert.AreEqual(5f, world.Entities[2].X);
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
        }
    }
}
