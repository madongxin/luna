using System;
using System.Collections;
using GameMesh.Aoi;
using GameMesh.Auth;
using GameMesh.Bootstrap;
using GameMesh.Network;
using GameMesh.Player;
using GameMesh.Protocol;
using Google.Protobuf;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameMesh.Tests.PlayMode
{
    public sealed class GameMeshPlayModeTests
    {
        [UnityTest]
        public IEnumerator Socket_HalfPacketAndReconnect()
        {
            using var server = new FakeGatewayServer { SplitWrites = true };
            var go = new GameObject("gm-test");
            var dispatcher = go.AddComponent<GameMeshMainThreadDispatcher>();
            var conn = new GameConnection(dispatcher);
            var connect = conn.ConnectAsync("127.0.0.1", server.Port, default);
            yield return WaitTask(connect, 3f);
            Assert.AreEqual(ConnectionState.Handshaking, conn.State);

            var req = conn.RequestAsync(new GameRequest { Register = new RegisterReq { DeviceId = "play" } },
                TimeSpan.FromSeconds(3), default);
            yield return WaitTask(req, 3f);
            Assert.IsTrue(req.Result.Register.Ok);

            server.DropConnectionAfterFirstFrame = true;
            var drop = conn.DisconnectAsync(DisconnectReason.RemoteClose, default);
            yield return WaitTask(drop, 2f);
            Assert.AreEqual(ConnectionState.Disconnected, conn.State);

            var reconnect = conn.ConnectAsync("127.0.0.1", server.Port, default);
            yield return WaitTask(reconnect, 3f);
            Assert.AreEqual(ConnectionState.Handshaking, conn.State);
            var again = conn.RequestAsync(new GameRequest { Login = new LoginReq { PlayerId = 1 } },
                TimeSpan.FromSeconds(3), default);
            yield return WaitTask(again, 3f);
            Assert.IsTrue(again.Result.Login.Ok);
            yield return WaitTask(conn.DisconnectAsync(DisconnectReason.ClientRequest, default), 2f);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator BadProtobuf_FromFakeGateway_FailClosed()
        {
            using var server = new FakeGatewayServer();
            var go = new GameObject("gm-bad-proto");
            var dispatcher = go.AddComponent<GameMeshMainThreadDispatcher>();
            var conn = new GameConnection(dispatcher);
            var connect = conn.ConnectAsync("127.0.0.1", server.Port, default);
            yield return WaitTask(connect, 3f);
            Assert.AreEqual(ConnectionState.Handshaking, conn.State);
            var send = server.SendRawAsync(new byte[] { 0xff, 0x00, 0xab, 0xcd, 0x12, 0x34, 0x56 });
            yield return WaitTask(send, 2f);
            var t = 0f;
            while (conn.State != ConnectionState.Disconnected && t < 3f)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.AreEqual(ConnectionState.Disconnected, conn.State);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator Reconnect_SingleFlightOnMainThread()
        {
            var policy = new ReconnectPolicy();
            Assert.IsTrue(policy.TryBegin(Time.unscaledTime, 4, 30000, out _));
            Assert.IsFalse(policy.TryBegin(Time.unscaledTime, 4, 30000, out var reason));
            Assert.AreEqual("in-flight", reason);
            yield return null;
            policy.EndSuccess();
            Assert.IsTrue(policy.TryBegin(Time.unscaledTime, 4, 30000, out _));
        }

        [UnityTest]
        public IEnumerator Aoi_CreatesMovesAndDestroysRemoteView()
        {
            var host = new GameObject("aoi-host");
            var world = new AoiWorld { LocalPlayerId = 1 };
            world.SetMapInstance(1);
            world.ApplySnapshot(new[]
            {
                new EntitySnapshotDto
                {
                    EntityId = 2, PlayerId = 2, Name = "B", X = 0, Y = 0, Z = 0, Hp = 10, MaxHp = 10,
                    StateSeq = 1, MapInstanceId = 1
                }
            });
            var view = RemotePlayerView.Spawn(world.Entities[2], 50);
            Assert.IsNotNull(GameObject.Find("RemotePlayer_2"));
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Move,
                MapInstanceId = 1,
                Entity = new EntitySnapshotDto
                {
                    EntityId = 2, PlayerId = 2, X = 3, Y = 0, Z = 0, StateSeq = 2, MapInstanceId = 1
                }
            });
            view.Apply(world.Entities[2]);
            yield return new WaitForSeconds(0.2f);
            Assert.Greater(view.transform.position.x, 0.2f);
            world.ApplyDelta(new AoiDeltaDto
            {
                Op = AoiOp.Leave,
                MapInstanceId = 1,
                Entity = new EntitySnapshotDto { EntityId = 2, PlayerId = 2, MapInstanceId = 1 }
            });
            UnityEngine.Object.Destroy(view.gameObject);
            yield return null;
            Assert.IsNull(GameObject.Find("RemotePlayer_2"));
            UnityEngine.Object.Destroy(host);
        }

        [UnityTest]
        public IEnumerator LogoutAndSessionReplaced_ClearRemoteViewsAndKeepPlayerId()
        {
            var client = GameMeshClient.Instance;
            Assert.IsNotNull(client);
            var previousId = client.Session.PlayerId;
            client.Session.ApplyLogin(42, "sess", "tok", 1, "Ada");
            client.Session.DeviceId = "dev-keep";
            client.Aoi.LocalPlayerId = 42;
            client.Aoi.SetMapInstance(9);
            client.Aoi.ApplySnapshot(new[]
            {
                new EntitySnapshotDto
                {
                    EntityId = 7, PlayerId = 7, Name = "B", X = 1, Y = 0, Z = 0, Hp = 10, MaxHp = 10,
                    StateSeq = 1, MapInstanceId = 9
                }
            });
            yield return null;
            yield return null;
            Assert.IsNotNull(GameObject.Find("RemotePlayer_7"));

            var notify = new GameResponse
            {
                SessionReplaced = new SessionReplacedNotify
                {
                    ReasonCode = "ERR_SESSION_REPLACED",
                    Message = "kicked"
                }
            };
            Assert.IsTrue(client.ApplyInnerPush(notify));
            yield return null;
            yield return null;
            Assert.IsNull(GameObject.Find("RemotePlayer_7"));
            Assert.AreEqual(42ul, client.Session.PlayerId);
            Assert.AreEqual("Ada", client.Session.DisplayName);
            Assert.IsTrue(client.Session.SessionReplaced);
            Assert.IsFalse(client.Session.AutoReconnect);
            Assert.IsTrue(string.IsNullOrEmpty(client.Session.Token));
            client.Session.PlayerId = previousId;
            client.Session.SessionReplaced = false;
            client.Session.AutoReconnect = true;
        }

        static IEnumerator WaitTask(System.Threading.Tasks.Task task, float timeout)
        {
            var t = 0f;
            while (!task.IsCompleted && t < timeout)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!task.IsCompleted)
                Assert.Fail("task timeout");
            if (task.IsFaulted)
                throw task.Exception.GetBaseException();
        }
    }
}
