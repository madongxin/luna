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
            Assert.AreEqual(ConnectionState.Handshaking,
                ConnectionStateMachine.Transition(ConnectionState.Connecting, ConnectionState.Handshaking));
            Assert.AreEqual(ConnectionState.Connected,
                ConnectionStateMachine.Transition(ConnectionState.Handshaking, ConnectionState.Connected));
            Assert.AreEqual(ConnectionState.Authenticating,
                ConnectionStateMachine.Transition(ConnectionState.Authenticated, ConnectionState.Authenticating));
            Assert.AreEqual(ConnectionState.Authenticating,
                ConnectionStateMachine.Transition(ConnectionState.EnteringWorld, ConnectionState.Authenticating));
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
                    Assert.AreEqual(ConnectionState.Handshaking, conn.State);
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
        public void RequestAsync_ReportsExactFrameWriteCompletion()
        {
            using (var server = new FakeGatewayServer())
            {
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                ulong writtenSeq = 0;
                string writtenType = null;
                long writtenAtMs = 0;
                conn.FrameWriteCompleted += (seq, type, atMs) =>
                {
                    writtenSeq = seq;
                    writtenType = type;
                    writtenAtMs = atMs;
                };
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var request = new GameRequest { MapPing = new MapPingReq() };
                    var pending = conn.RequestAsync(request, TimeSpan.FromSeconds(3),
                        CancellationToken.None);
                    SpinPump(dispatcher,
                        () => pending.IsCompleted && Volatile.Read(ref writtenAtMs) > 0, 3000);
                    pending.GetAwaiter().GetResult();
                    Assert.AreEqual(request.Seq, writtenSeq);
                    Assert.AreEqual(GameRequest.BodyOneofCase.MapPing.ToString(), writtenType);
                    Assert.Greater(writtenAtMs, 0);
                }
                finally
                {
                    conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None)
                        .GetAwaiter().GetResult();
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
        public void FakeGateway_HelloIncludesMap1001_AndLoginLogoutFailBranches()
        {
            using (var server = new FakeGatewayServer())
            {
                var helloReq = new GameRequest { Seq = 1, ClientHello = new ClientHelloReq { SchemaSha256 = "abc" } };
                var hello = FakeGatewayServer.DefaultHandler(helloReq);
                Assert.IsTrue(hello.ServerHello.Ok);
                Assert.AreEqual(3, hello.ServerHello.Maps.Count);
                Assert.AreEqual(1001ul, hello.ServerHello.Maps[0].MapTemplateId);
                Assert.AreEqual(1002ul, hello.ServerHello.Maps[1].MapTemplateId);
                Assert.AreEqual(2102ul, hello.ServerHello.Maps[2].MapTemplateId);

                server.Handler = req =>
                {
                    if (req.BodyCase == GameRequest.BodyOneofCase.Login)
                    {
                        return new GameResponse
                        {
                            Seq = req.Seq,
                            Ok = true,
                            ErrorCode = "ERR_UNAUTHENTICATED",
                            Login = new LoginRsp { Ok = false, Message = "no" }
                        };
                    }

                    if (req.BodyCase == GameRequest.BodyOneofCase.Logout)
                    {
                        return new GameResponse
                        {
                            Seq = req.Seq,
                            Ok = true,
                            Logout = new LogoutRsp { Ok = false, Message = "no" }
                        };
                    }

                    return FakeGatewayServer.DefaultHandler(req);
                };
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None).GetAwaiter().GetResult();
                    var loginTask = conn.RequestAsync(new GameRequest { Login = new LoginReq { PlayerId = 1, Credential = "x" } },
                        TimeSpan.FromSeconds(3), CancellationToken.None);
                    SpinPump(dispatcher, () => loginTask.IsCompleted, 3000);
                    var login = loginTask.GetAwaiter().GetResult();
                    Assert.IsFalse(AuthResponse.TryAcceptLogin(login, 1, out _, out _, out var code, out _));
                    Assert.AreEqual("ERR_UNAUTHENTICATED", code);

                    var logoutTask = conn.RequestAsync(new GameRequest { Logout = new LogoutReq { PlayerId = 1 } },
                        TimeSpan.FromSeconds(3), CancellationToken.None);
                    SpinPump(dispatcher, () => logoutTask.IsCompleted, 3000);
                    var logout = AuthResponse.FromLogout(logoutTask.GetAwaiter().GetResult(), true);
                    Assert.IsFalse(logout.AuthorityOk);
                }
                finally
                {
                    conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None).GetAwaiter().GetResult();
                    conn.DisposeAsync().GetAwaiter().GetResult();
                }
            }
        }

        [Test]
        public void SessionReplacedNotify_ParsesExplicitBody()
        {
            var inner = new GameResponse
            {
                SessionReplaced = new SessionReplacedNotify
                {
                    ReasonCode = "ERR_SESSION_REPLACED",
                    Message = "kicked",
                    ServerTimeMs = 9
                }
            };
            Assert.AreEqual(GameResponse.BodyOneofCase.SessionReplaced, inner.BodyCase);
            Assert.AreEqual("ERR_SESSION_REPLACED", inner.SessionReplaced.ReasonCode);
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
                    Assert.AreEqual(ConnectionState.Handshaking, conn.State);
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

        [Test]
        public void RpcResult_DoesNotNeedUnityDispatcherPump()
        {
            using (var server = new FakeGatewayServer())
            {
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var pending = conn.RequestAsync(
                        new GameRequest { MapPing = new MapPingReq() },
                        TimeSpan.FromSeconds(2), CancellationToken.None);
                    Assert.IsTrue(pending.Wait(TimeSpan.FromMilliseconds(1500)),
                        "Matched RPC must complete without Pump()");
                    Assert.IsTrue(pending.Result.Ok);
                }
                finally
                {
                    conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    conn.DisposeAsync().GetAwaiter().GetResult();
                }
            }
        }

        [Test]
        public void LateRpc_DoesNotBecomeServerPush()
        {
            using (var server = new FakeGatewayServer())
            {
                server.Handler = req =>
                {
                    Thread.Sleep(200);
                    return FakeGatewayServer.DefaultHandler(req);
                };
                var dispatcher = new QueueDispatcher();
                var conn = new GameConnection(dispatcher);
                var pushes = 0;
                conn.PushReceived += _ => Interlocked.Increment(ref pushes);
                try
                {
                    conn.ConnectAsync("127.0.0.1", server.Port, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var pending = conn.RequestAsync(
                        new GameRequest { MapPing = new MapPingReq() },
                        TimeSpan.FromMilliseconds(40), CancellationToken.None);
                    Assert.Throws<AggregateException>(() => pending.Wait());
                    Thread.Sleep(350);
                    dispatcher.Pump();
                    Assert.AreEqual(0, pushes);
                    Assert.GreaterOrEqual(conn.PipeMetrics.Late, 1);
                }
                finally
                {
                    conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None)
                        .GetAwaiter().GetResult();
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
