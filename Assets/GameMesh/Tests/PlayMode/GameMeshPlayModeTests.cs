using System;
using System.Collections;
using GameMesh.Aoi;
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
            Assert.AreEqual(ConnectionState.Connected, conn.State);

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
            Assert.AreEqual(ConnectionState.Connected, conn.State);
            var again = conn.RequestAsync(new GameRequest { Login = new LoginReq { PlayerId = 1 } },
                TimeSpan.FromSeconds(3), default);
            yield return WaitTask(again, 3f);
            Assert.IsTrue(again.Result.Login.Ok);
            yield return WaitTask(conn.DisconnectAsync(DisconnectReason.ClientRequest, default), 2f);
            UnityEngine.Object.Destroy(go);
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
