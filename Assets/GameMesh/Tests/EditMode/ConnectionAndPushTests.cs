using System;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Network;
using GameMesh.Protocol;
using Google.Protobuf;
using NUnit.Framework;

namespace GameMesh.Tests.EditMode
{
    public sealed class ConnectionAndPushTests
    {
        [Test]
        public void StateMachine_RejectsIllegal()
        {
            Assert.IsFalse(ConnectionStateMachine.CanTransition(ConnectionState.Disconnected, ConnectionState.InWorld));
            Assert.Throws<GameMeshException>(() =>
                ConnectionStateMachine.Transition(ConnectionState.Disconnected, ConnectionState.Authenticated));
            Assert.AreEqual(ConnectionState.Connecting,
                ConnectionStateMachine.Transition(ConnectionState.Disconnected, ConnectionState.Connecting));
        }

        [Test]
        public void RequestAsync_MatchesOutOfOrderSeq()
        {
            using (var server = new FakeGatewayServer())
            {
                server.Handler = req =>
                {
                    if (req.Seq == 1)
                        Thread.Sleep(80);
                    return FakeGatewayServer.DefaultHandler(req);
                };
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None).GetAwaiter().GetResult();
                    var t1 = conn.RequestAsync(new GameRequest { Register = new RegisterReq { DeviceId = "a" } },
                        TimeSpan.FromSeconds(3), CancellationToken.None);
                    var t2 = conn.RequestAsync(new GameRequest { Login = new LoginReq { PlayerId = 1 } },
                        TimeSpan.FromSeconds(3), CancellationToken.None);
                    SpinPump(dispatcher, () => t1.IsCompleted && t2.IsCompleted, 3000);
                    var r1 = t1.GetAwaiter().GetResult();
                    var r2 = t2.GetAwaiter().GetResult();
                    Assert.AreEqual(1ul, r1.Seq);
                    Assert.AreEqual(2ul, r2.Seq);
                    Assert.IsTrue(r1.Register.Ok);
                    Assert.IsTrue(r2.Login.Ok);
                }
                finally
                {
                    conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None).GetAwaiter().GetResult();
                    conn.DisposeAsync().GetAwaiter().GetResult();
                }
            }
        }

        [Test]
        public void TimeoutAndDisconnect_ReleasePending()
        {
            using (var server = new FakeGatewayServer())
            {
                server.Handler = _ =>
                {
                    Thread.Sleep(400);
                    return new GameResponse { Ok = true };
                };
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None).GetAwaiter().GetResult();
                    var pending = conn.RequestAsync(new GameRequest { MapPing = new MapPingReq() },
                        TimeSpan.FromMilliseconds(50), CancellationToken.None);
                    SpinPump(dispatcher, () => pending.IsCompleted, 1000);
                    Assert.IsTrue(pending.IsFaulted);
                }
                finally
                {
                    conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None).GetAwaiter().GetResult();
                    conn.DisposeAsync().GetAwaiter().GetResult();
                }
            }
        }

        [Test]
        public void Push_DoesNotCompleteRequest()
        {
            using (var server = new FakeGatewayServer())
            {
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                GameResponse push = null;
                conn.PushReceived += r => push = r;
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None).GetAwaiter().GetResult();
                    var inner = new GameResponse { Seq = 0, Ok = true, Message = "mail" };
                    var envelope = new GameResponse
                    {
                        Seq = 0,
                        ServerPush = new ServerPushEnvelope
                        {
                            ServerSeq = 3,
                            Reliable = true,
                            MessageType = "mailbox.changed.v1",
                            Payload = ByteString.CopyFrom(inner.ToByteArray())
                        }
                    };
                    server.PushAsync(envelope).GetAwaiter().GetResult();
                    SpinPump(dispatcher, () => push != null, 2000);
                    Assert.IsNotNull(push);
                    Assert.AreEqual(GameResponse.BodyOneofCase.ServerPush, push.BodyCase);
                    var parsed = GameResponse.Parser.ParseFrom(push.ServerPush.Payload);
                    Assert.AreEqual("mail", parsed.Message);
                }
                finally
                {
                    conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None).GetAwaiter().GetResult();
                    conn.DisposeAsync().GetAwaiter().GetResult();
                }
            }
        }

        [Test]
        public void PushReliability_DedupGapAndAckOrder()
        {
            var rel = new PushReliability();
            Assert.AreEqual(PushReliability.Decision.Apply, rel.Observe(1));
            rel.MarkApplied(1);
            Assert.AreEqual(PushReliability.Decision.Duplicate, rel.Observe(1));
            Assert.AreEqual(PushReliability.Decision.Gap, rel.Observe(3));
            Assert.AreEqual(PushReliability.Decision.Apply, rel.Observe(2));
            rel.MarkApplied(2);
            Assert.AreEqual(2ul, rel.LastAppliedServerSeq);
        }

        [Test]
        public void BadProtobuf_FailClosed()
        {
            using (var server = new FakeGatewayServer())
            {
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None).GetAwaiter().GetResult();
                    Assert.AreEqual(ConnectionState.Connected, conn.State);
                    server.SendRawAsync(new byte[] { 0xff, 0x00, 0xab, 0xcd, 0x12, 0x34 }).GetAwaiter().GetResult();
                    SpinPump(dispatcher, () => conn.State == ConnectionState.Disconnected, 3000);
                    Assert.AreEqual(ConnectionState.Disconnected, conn.State);
                }
                finally
                {
                    conn.DisposeAsync().GetAwaiter().GetResult();
                }
            }
        }

        static void SpinPump(QueueDispatcher dispatcher, Func<bool> done, int timeoutMs)
        {
            var start = DateTime.UtcNow;
            while (!done() && (DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
            {
                dispatcher.Pump(64);
                Thread.Sleep(10);
            }

            dispatcher.Pump(64);
        }
    }
}
