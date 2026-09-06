using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Bootstrap;
using GameMesh.Network;
using GameMesh.Protocol;
using UnityEngine;

namespace GameMesh.LoadTest
{
    public sealed class LoadTestBot
    {
        readonly int _index;
        readonly GameConnection _conn;
        readonly CancellationTokenSource _life = new CancellationTokenSource();

        string _host = "";
        string _deviceId;
        string _displayName;
        string _password;
        string _token = "";
        ulong _playerId;
        ulong _mapInstanceId;
        ulong _lastServerSeq;
        int _heartbeatMs = 15000;
        int _loopGen;
        Vector3 _spawn;
        Vector3 _home;
        Vector3 _pos;
        float _yaw;
        bool _helloOk;
        bool _stopMoves;
        Task _heartbeatTask = Task.CompletedTask;
        Task _moveTask = Task.CompletedTask;

        public int Index => _index;
        public int GatewayPort { get; private set; }
        public ulong PlayerId => _playerId;
        public bool InWorld { get; private set; }
        public bool Failed { get; private set; }
        public string Status { get; private set; } = "idle";
        public string LastError { get; private set; } = "";

        public LoadTestBot(int index, IMainThreadDispatcher dispatcher)
        {
            _index = index;
            _conn = new GameConnection(dispatcher ?? new ImmediateDispatcher()) { Quiet = true };
            _conn.PushReceived += OnPush;
        }

        public async Task EnterWorldAsync(string deviceId, string displayName, string password, int gatewayPort,
            CancellationToken ct)
        {
            _deviceId = deviceId;
            _displayName = displayName;
            _password = password ?? "";
            GatewayPort = gatewayPort > 0 ? gatewayPort : 8083;
            await CloseSessionAsync().ConfigureAwait(true);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, ct))
            {
                var token = linked.Token;
                try
                {
                    Status = "连接中 :" + GatewayPort;
                    var client = GameMeshClient.Instance;
                    if (client == null)
                        throw new InvalidOperationException("GameMeshClient missing");
                    _host = client.Config.host ?? "";
                    client.Config.ResolveMapContract();
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connectCts.CancelAfter(15000);
                        await _conn.ConnectAsync(client.Config.host, GatewayPort, connectCts.Token)
                            .ConfigureAwait(true);
                    }

                    await HelloAsync(client, token).ConfigureAwait(true);
                    await RegisterAsync(token).ConfigureAwait(true);
                    await LoginAsync(token).ConfigureAwait(true);
                    await EnterMapAsync(client, token).ConfigureAwait(true);
                    InWorld = true;
                    Status = "在线 :" + GatewayPort;
                    _heartbeatTask = HeartbeatLoopAsync(_life.Token);
                    _moveTask = MoveLoopAsync(_life.Token);
                }
                catch (OperationCanceledException)
                {
                    Status = "已取消";
                    throw;
                }
                catch (Exception ex)
                {
                    Failed = true;
                    LastError = GameMeshLog.Redact(ex.Message);
                    Status = "失败";
                    throw;
                }
            }
        }

        public async Task PrepareRetryAsync()
        {
            Failed = false;
            LastError = "";
            Status = "重试准备";
            await CloseSessionAsync().ConfigureAwait(true);
        }

        public async Task LogoutAsync()
        {
            Status = "下线中";
            try
            {
                await CloseSessionAsync().ConfigureAwait(true);
            }
            finally
            {
                try { _life.Cancel(); }
                catch { /* ignore */ }
                Status = Failed ? "失败" : "已下线";
            }
        }

        public async Task CloseSessionAsync()
        {
            _stopMoves = true;
            InWorld = false;
            _loopGen++;
            var playerId = _playerId;
            var token = _token ?? "";
            var port = GatewayPort > 0 ? GatewayPort : 8083;
            var host = ResolveHost();
            try
            {
                if (playerId != 0 && !string.IsNullOrEmpty(token))
                    await SendLogoutAsync(host, port, playerId, token).ConfigureAwait(true);
            }
            catch
            {
                /* ledger keeps the token for the next sweep */
            }
            finally
            {
                try
                {
                    await _conn.DisconnectAsync(DisconnectReason.UserLogout, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch
                {
                    /* ignore */
                }

                _helloOk = false;
                _token = "";
                _playerId = 0;
                _stopMoves = false;
            }
        }

        async Task SendLogoutAsync(string host, int port, ulong playerId, string token)
        {
            var live = _conn.State != ConnectionState.Disconnected &&
                       _conn.State != ConnectionState.Closing &&
                       _conn.State != ConnectionState.Connecting &&
                       _helloOk;
            if (!live)
            {
                var client = GameMeshClient.Instance;
                if (client == null || string.IsNullOrEmpty(host))
                    return;
                try
                {
                    await _conn.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch
                {
                    /* ignore */
                }

                using (var cts = new CancellationTokenSource(8000))
                {
                    await _conn.ConnectAsync(host, port, cts.Token).ConfigureAwait(true);
                    await HelloAsync(client, cts.Token).ConfigureAwait(true);
                }
            }

            if (ConnectionStateMachine.CanTransition(_conn.State, ConnectionState.LoggingOut))
                _conn.SetLogicalState(ConnectionState.LoggingOut);
            var req = new GameRequest
            {
                SessionToken = token,
                Logout = new LogoutReq { PlayerId = playerId, Token = token }
            };
            var rsp = await _conn.RequestAsync(req, TimeSpan.FromSeconds(3), CancellationToken.None)
                .ConfigureAwait(true);
            if (AuthResponse.FromLogout(rsp, true).AuthorityOk)
                LoadTestSessionLedger.Forget(playerId, token);
        }

        async Task HelloAsync(GameMeshClient client, CancellationToken ct)
        {
            Status = "握手";
            var hello = new ClientHelloReq
            {
                ProtocolVersion = ProtocolHandshake.ProtocolVersion,
                SchemaSha256 = client.ProtocolSchemaSha256 ?? "",
                ClientVersion = Application.version ?? "luna",
                Platform = Application.platform.ToString(),
                BuildChannel = "loadtest"
            };
            foreach (var cap in ProtocolHandshake.ClientCapabilities)
                hello.Capabilities.Add(cap);
            var rsp = await SendAsync(new GameRequest { ClientHello = hello },
                TimeSpan.FromMilliseconds(Math.Max(3000, client.Config.helloTimeoutMs)), ct).ConfigureAwait(true);
            var helloRsp = rsp.ServerHello;
            if (!ProtocolHandshake.TryValidate(helloRsp, client.ProtocolSchemaSha256,
                    ProtocolHandshake.ProtocolVersion, out var code, out var message))
                throw new GameMeshException(code, message);
            if (helloRsp.HeartbeatIntervalMs != 0)
                _heartbeatMs = Math.Max(1000, (int)helloRsp.HeartbeatIntervalMs);
            var maps = new List<MapManifestEntry>();
            if (helloRsp.Maps != null)
            {
                foreach (var map in helloRsp.Maps)
                    maps.Add(map);
            }

            if (!ProtocolHandshake.TryMatchMap(maps, client.Config.mapTemplateId, client.Config.mapDataHash,
                    client.Config.dataVersion, out _, out var mapCode))
                throw new GameMeshException(mapCode, "hello map mismatch");
            _helloOk = true;
            _conn.SetLogicalState(ConnectionState.Connected);
        }

        async Task RegisterAsync(CancellationToken ct)
        {
            Status = "注册";
            _conn.SetLogicalState(ConnectionState.Authenticating);
            var rsp = await SendAsync(new GameRequest
            {
                Register = new RegisterReq
                {
                    DeviceId = _deviceId ?? "",
                    DisplayName = _displayName ?? "",
                    Password = _password ?? ""
                }
            }, TimeSpan.FromSeconds(8), ct).ConfigureAwait(true);
            if (!rsp.Ok || rsp.Register == null || !rsp.Register.Ok || rsp.Register.PlayerId == 0)
            {
                var msg = rsp.Register != null ? rsp.Register.Message : rsp.Message;
                throw new GameMeshException(ProtocolMapper.ExtractErrorCode(rsp),
                    string.IsNullOrEmpty(msg) ? "register failed" : msg);
            }

            _playerId = rsp.Register.PlayerId;
            _conn.SetLogicalState(ConnectionState.Connected);
        }

        async Task LoginAsync(CancellationToken ct)
        {
            Status = "登录";
            _conn.SetLogicalState(ConnectionState.Authenticating);
            var rsp = await SendAsync(new GameRequest
            {
                Login = new LoginReq
                {
                    PlayerId = _playerId,
                    DeviceId = _deviceId ?? "",
                    ServerId = 1,
                    TtlSec = 300,
                    KickOtherDevice = true,
                    Credential = _password ?? ""
                }
            }, TimeSpan.FromSeconds(8), ct).ConfigureAwait(true);
            if (!AuthResponse.TryAcceptLogin(rsp, _playerId, out var playerId, out var login, out var code,
                    out var message))
                throw new GameMeshException(code, message);
            _playerId = playerId;
            _token = login.Token ?? "";
            LoadTestSessionLedger.Remember(ResolveHost(), GatewayPort, _playerId, _token);
            _conn.SetLogicalState(ConnectionState.Authenticated);
        }

        async Task EnterMapAsync(GameMeshClient client, CancellationToken ct)
        {
            Status = "进图";
            _conn.SetLogicalState(ConnectionState.EnteringWorld);
            var req = new GameRequest
            {
                EnterMap = new EnterMapReq
                {
                    PlayerId = _playerId,
                    RealmId = client.Config.realmId,
                    MapTemplateId = client.Config.mapTemplateId,
                    MapInstanceId = 0,
                    MapDataVersion = client.Config.dataVersion,
                    MapDataSha256 = client.Config.mapDataHash ?? "",
                    OperationId = Guid.NewGuid().ToString("N")
                }
            };
            var rsp = await SendAsync(req, TimeSpan.FromSeconds(8), ct).ConfigureAwait(true);
            if (!rsp.Ok || rsp.EnterMap == null || !rsp.EnterMap.Ok)
            {
                var msg = rsp.EnterMap != null ? rsp.EnterMap.Message : rsp.Message;
                throw new GameMeshException(ProtocolMapper.ExtractErrorCode(rsp),
                    string.IsNullOrEmpty(msg) ? "enter map failed" : msg);
            }

            _mapInstanceId = rsp.EnterMap.MapInstanceId;
            _pos = rsp.EnterMap.SpawnPosition != null
                ? ProtocolMapper.ToUnity(rsp.EnterMap.SpawnPosition)
                : new Vector3(-26f, -0.2f, -5f);
            _spawn = _pos;
            var angle = _index * 2.39996323f;
            var ring = 28f + Mathf.Sqrt((_index % 80) / 79f) * 160f;
            _home = _spawn + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * ring;
            _yaw = rsp.EnterMap.SpawnYaw;
            _conn.SetLogicalState(ConnectionState.InWorld);
        }

        async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var interval = Math.Max(1000, _heartbeatMs);
            var gen = _loopGen;
            while (!_stopMoves && !ct.IsCancellationRequested && gen == _loopGen)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(true);
                    if (_stopMoves || ct.IsCancellationRequested)
                        return;
                    var mono = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await SendAsync(new GameRequest
                    {
                        Heartbeat = new HeartbeatReq
                        {
                            ClientMonotonicMs = mono,
                            LastServerSeq = _lastServerSeq,
                            EchoMs = mono
                        }
                    }, TimeSpan.FromSeconds(4), ct).ConfigureAwait(true);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch
                {
                    /* keep wandering until logout */
                }
            }
        }

        async Task MoveLoopAsync(CancellationToken ct)
        {
            var rnd = new System.Random(_index * 9176 + Environment.TickCount);
            var dir = RandomDir(rnd);
            var gen = _loopGen;
            while (!_stopMoves && !ct.IsCancellationRequested && InWorld && gen == _loopGen)
            {
                var waitMs = 140 + rnd.Next(0, 70);
                try
                {
                    await Task.Delay(waitMs, ct).ConfigureAwait(true);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (_stopMoves || !InWorld)
                    return;
                var toHome = _home - _pos;
                toHome.y = 0f;
                var far = toHome.magnitude > 10f;
                if (far && rnd.NextDouble() < 0.78)
                    dir = toHome.normalized;
                else if (rnd.NextDouble() < 0.16)
                    dir = RandomDir(rnd);
                var step = far
                    ? 0.65f + (float)rnd.NextDouble() * 0.35f
                    : 0.38f + (float)rnd.NextDouble() * 0.22f;
                var next = _pos + dir * step;
                if (Vector3.Distance(new Vector3(next.x, _home.y, next.z),
                        new Vector3(_home.x, _home.y, _home.z)) > 55f)
                {
                    dir = (_home - _pos);
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 0.01f)
                        dir = RandomDir(rnd);
                    else
                        dir.Normalize();
                    next = _pos + dir * step;
                }

                next.y = _spawn.y;
                _pos = next;
                _yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                try
                {
                    var rsp = await SendAsync(new GameRequest
                    {
                        Move = new MoveReq
                        {
                            PlayerId = _playerId,
                            MapInstanceId = _mapInstanceId,
                            Position = ProtocolMapper.ToVec3(_pos),
                            Yaw = _yaw,
                            ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }
                    }, TimeSpan.FromSeconds(4), ct).ConfigureAwait(true);
                    if (rsp.Move != null && rsp.Move.Position != null)
                        _pos = ProtocolMapper.ToUnity(rsp.Move.Position);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch
                {
                    /* ignore single move failure */
                }
            }
        }

        async Task<GameResponse> SendAsync(GameRequest request, TimeSpan timeout, CancellationToken ct)
        {
            if (!_helloOk && request.BodyCase != GameRequest.BodyOneofCase.ClientHello)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, "hello required");
            if (!string.IsNullOrEmpty(_token) && request.BodyCase != GameRequest.BodyOneofCase.ClientHello)
                request.SessionToken = _token;
            return await _conn.RequestAsync(request, timeout, ct).ConfigureAwait(true);
        }

        void OnPush(GameResponse rsp)
        {
            if (rsp == null)
                return;
            if (rsp.Seq > _lastServerSeq)
                _lastServerSeq = rsp.Seq;
            if (rsp.BodyCase == GameResponse.BodyOneofCase.SessionReplaced)
            {
                Failed = true;
                LastError = "ERR_SESSION_REPLACED";
                Status = "被顶号";
                _stopMoves = true;
                InWorld = false;
                LoadTestSessionLedger.Forget(_playerId, _token);
                _token = "";
            }
        }

        string ResolveHost()
        {
            if (!string.IsNullOrEmpty(_host))
                return _host;
            var client = GameMeshClient.Instance;
            return client != null ? client.Config.host ?? "" : "";
        }

        static Vector3 RandomDir(System.Random rnd)
        {
            var angle = rnd.NextDouble() * Math.PI * 2.0;
            return new Vector3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
        }

        public static async Task LogoutOrphanAsync(IMainThreadDispatcher dispatcher, string host, int port,
            ulong playerId, string token)
        {
            var bot = new LoadTestBot(-1, dispatcher);
            bot._host = host ?? "";
            bot.GatewayPort = port > 0 ? port : 8083;
            bot._playerId = playerId;
            bot._token = token ?? "";
            await bot.CloseSessionAsync().ConfigureAwait(true);
        }
    }

    static class LoadTestSessionLedger
    {
        [Serializable]
        public class Entry
        {
            public string host;
            public int port;
            public string playerId;
            public string token;
        }

        [Serializable]
        class FileBox
        {
            public Entry[] items;
        }

        static readonly object Gate = new object();

        static string FilePath =>
            Path.Combine(Application.persistentDataPath, "gamemesh-loadtest-sessions.json");

        public static void Remember(string host, int port, ulong playerId, string token)
        {
            if (playerId == 0 || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(host))
                return;
            lock (Gate)
            {
                var list = new List<Entry>(LoadUnlocked());
                var id = playerId.ToString();
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] != null && list[i].playerId == id)
                        list.RemoveAt(i);
                }

                list.Add(new Entry
                {
                    host = host,
                    port = port,
                    playerId = id,
                    token = token
                });
                SaveUnlocked(list);
            }
        }

        public static void Forget(ulong playerId, string token)
        {
            lock (Gate)
            {
                var list = new List<Entry>(LoadUnlocked());
                var id = playerId.ToString();
                var changed = false;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var entry = list[i];
                    if (entry == null)
                    {
                        list.RemoveAt(i);
                        changed = true;
                        continue;
                    }

                    if (entry.playerId == id ||
                        (!string.IsNullOrEmpty(token) && entry.token == token))
                    {
                        list.RemoveAt(i);
                        changed = true;
                    }
                }

                if (changed)
                    SaveUnlocked(list);
            }
        }

        public static Entry[] Snapshot()
        {
            lock (Gate)
                return LoadUnlocked();
        }

        static Entry[] LoadUnlocked()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return Array.Empty<Entry>();
                var box = JsonUtility.FromJson<FileBox>(File.ReadAllText(FilePath));
                return box != null && box.items != null ? box.items : Array.Empty<Entry>();
            }
            catch
            {
                return Array.Empty<Entry>();
            }
        }

        static void SaveUnlocked(List<Entry> list)
        {
            var box = new FileBox { items = list != null ? list.ToArray() : Array.Empty<Entry>() };
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonUtility.ToJson(box));
        }
    }
}
