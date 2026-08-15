using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Protocol;
using Google.Protobuf;

namespace GameMesh.Network
{
    public sealed class FakeGatewayServer : IDisposable
    {
        readonly TcpListener _listener;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly List<TcpClient> _clients = new List<TcpClient>();
        public int Port { get; }
        public Func<GameRequest, GameResponse> Handler;
        public bool SplitWrites;
        public bool DropConnectionAfterFirstFrame;
        public bool SendIllegalProtobufAfterConnect;

        public FakeGatewayServer(int port = 0)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Handler = DefaultHandler;
            Task.Run(AcceptLoop);
        }

        async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (_clients) _clients.Add(client);
                    if (SendIllegalProtobufAfterConnect)
                    {
                        _ = Task.Run(() => SendIllegalThenClose(client));
                        continue;
                    }
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        async Task Serve(TcpClient client)
        {
            var buffer = new List<byte>();
            var read = new byte[1024];
            var stream = client.GetStream();
            try
            {
                while (!_cts.IsCancellationRequested && client.Connected)
                {
                    var n = await stream.ReadAsync(read, 0, read.Length, _cts.Token).ConfigureAwait(false);
                    if (n <= 0)
                        break;
                    for (var i = 0; i < n; i++)
                        buffer.Add(read[i]);
                    while (true)
                    {
                        var status = FrameCodec.TryDecode(buffer, out var payload, out _);
                        if (status == FrameDecodeStatus.NeedMore)
                            break;
                        if (status != FrameDecodeStatus.Ok)
                            return;
                        var req = GameRequest.Parser.ParseFrom(payload);
                        var rsp = Handler(req);
                        var frame = FrameCodec.Encode(rsp.ToByteArray());
                        if (SplitWrites)
                        {
                            await stream.WriteAsync(frame, 0, 2, _cts.Token).ConfigureAwait(false);
                            await Task.Delay(20, _cts.Token).ConfigureAwait(false);
                            await stream.WriteAsync(frame, 2, frame.Length - 2, _cts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await stream.WriteAsync(frame, 0, frame.Length, _cts.Token).ConfigureAwait(false);
                        }

                        if (DropConnectionAfterFirstFrame)
                        {
                            client.Close();
                            return;
                        }
                    }
                }
            }
            catch
            {
                /* test server */
            }
        }

        async Task SendIllegalThenClose(TcpClient client)
        {
            try
            {
                var garbage = new byte[] { 0xff, 0x00, 0xab, 0xcd, 0x12, 0x34, 0x56, 0x78 };
                var frame = FrameCodec.Encode(garbage);
                await client.GetStream().WriteAsync(frame, 0, frame.Length, _cts.Token).ConfigureAwait(false);
            }
            catch
            {
                /* test server */
            }
        }

        public async Task SendRawAsync(byte[] payload)
        {
            var frame = FrameCodec.Encode(payload ?? Array.Empty<byte>());
            List<TcpClient> snapshot;
            lock (_clients) snapshot = new List<TcpClient>(_clients);
            foreach (var c in snapshot)
            {
                if (!c.Connected)
                    continue;
                await c.GetStream().WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
            }
        }

        public async Task PushAsync(GameResponse response)
        {
            var frame = FrameCodec.Encode(response.ToByteArray());
            List<TcpClient> snapshot;
            lock (_clients) snapshot = new List<TcpClient>(_clients);
            foreach (var c in snapshot)
            {
                if (!c.Connected)
                    continue;
                await c.GetStream().WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
            }
        }

        public static GameResponse DefaultHandler(GameRequest req)
        {
            var rsp = new GameResponse { Seq = req.Seq, Ok = true, Message = "ok" };
            switch (req.BodyCase)
            {
                case GameRequest.BodyOneofCase.Register:
                    rsp.Register = new RegisterRsp { Ok = true, PlayerId = 10001, Message = "ok" };
                    break;
                case GameRequest.BodyOneofCase.Login:
                    rsp.Login = new LoginRsp
                    {
                        Ok = true, Token = "tok", SessionId = "sess-1", Generation = 1, Message = "ok",
                        Profile = SampleProfile(req.Login.PlayerId != 0 ? req.Login.PlayerId : 10001)
                    };
                    break;
                case GameRequest.BodyOneofCase.Logout:
                    rsp.Logout = new LogoutRsp { Ok = true };
                    break;
                case GameRequest.BodyOneofCase.Reconnect:
                    rsp.Reconnect = new ReconnectRsp
                    {
                        Ok = true, Token = "tok2", SessionId = "sess-1", Generation = 2
                    };
                    break;
                case GameRequest.BodyOneofCase.EnterMap:
                    rsp.EnterMap = new EnterMapRsp
                    {
                        Ok = true,
                        MapTemplateId = req.EnterMap.MapTemplateId,
                        MapInstanceId = req.EnterMap.MapInstanceId != 0 ? req.EnterMap.MapInstanceId : 5001UL,
                        OwnerEpoch = 1,
                        RouteVersion = 1,
                        MapDataVersion = req.EnterMap.MapDataVersion,
                        MapDataSha256 = req.EnterMap.MapDataSha256,
                        SpawnPosition = new Vec3 { X = -28.5f, Y = -0.244f, Z = -7.25f },
                        SpawnYaw = 76.022f,
                        Self = new EntitySnapshot
                        {
                            PlayerId = req.EnterMap.PlayerId,
                            PlayerName = "self",
                            Position = new Vec3 { X = -28.5f, Y = -0.244f, Z = -7.25f },
                            Yaw = 76.022f,
                            Hp = 100,
                            MaxHp = 100,
                            StateSeq = 1
                        }
                    };
                    break;
                case GameRequest.BodyOneofCase.Move:
                    rsp.Move = new MoveRsp
                    {
                        Ok = true,
                        Position = req.Move.Position,
                        Yaw = req.Move.Yaw,
                        StateSeq = 1,
                        ServerTimeMs = 1
                    };
                    break;
                case GameRequest.BodyOneofCase.GetSelfProfile:
                    rsp.GetSelfProfile = new GetSelfProfileRsp
                    {
                        Ok = true,
                        Profile = SampleProfile(req.GetSelfProfile.PlayerId)
                    };
                    break;
                case GameRequest.BodyOneofCase.PlayerMailSend:
                    rsp.PlayerMailSend = new PlayerMailSendRsp
                    {
                        Ok = true,
                        MailId = 88,
                        IdempotentHit = req.PlayerMailSend.OperationId == "retry-op"
                    };
                    break;
                case GameRequest.BodyOneofCase.MapPing:
                    rsp.MapPing = new MapPingRsp { Ok = true, PlayerCount = 2, OwnerEpoch = 1 };
                    break;
                case GameRequest.BodyOneofCase.MailboxSummary:
                    rsp.MailboxSummary = new MailboxSummaryRsp { Ok = true, UnreadSocial = 1, CurrentCount = 1 };
                    break;
                case GameRequest.BodyOneofCase.MailList:
                    rsp.MailList = new MailListRsp
                    {
                        Ok = true,
                        Mails = { new MailBrief { MailId = 9, Title = "hi", SenderName = "A" } }
                    };
                    break;
                case GameRequest.BodyOneofCase.MailGet:
                    rsp.MailGet = new MailGetRsp
                    {
                        Ok = true,
                        Mail = new MailDetail
                        {
                            Brief = new MailBrief { MailId = 9, Title = "hi" },
                            Body = "hello"
                        }
                    };
                    break;
                case GameRequest.BodyOneofCase.PushAck:
                    rsp.PushAck = new PushAckRsp { Ok = true, TrimmedToSeq = req.PushAck.AckServerSeq };
                    break;
                default:
                    rsp.Ok = true;
                    break;
            }

            return rsp;
        }

        static PlayerAttributes SampleProfile(ulong playerId)
        {
            return new PlayerAttributes
            {
                PlayerId = playerId,
                PlayerName = "Luna",
                Hp = 80,
                MaxHp = 100,
                Mp = 30,
                MaxMp = 50,
                Attack = 12,
                SpellPower = 8,
                Defense = 6,
                MagicResistance = 4,
                CritChance = 0.1f,
                CritDamage = 1.5f,
                MoveSpeed = 6f,
                AttackSpeed = 1.1f,
                StatsVersion = 3
            };
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            lock (_clients)
            {
                foreach (var c in _clients)
                {
                    try { c.Close(); } catch { /* ignore */ }
                }
            }

            _cts.Dispose();
        }
    }
}
