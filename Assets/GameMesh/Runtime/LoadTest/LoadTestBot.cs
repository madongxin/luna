using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Auth;
using GameMesh.Bootstrap;
using GameMesh.Map;
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
        uint _preferredLine;
        string _deviceId;
        string _displayName;
        string _password;
        string _token = "";
        string _sessionId = "";
        ulong _playerId;
        ulong _mapInstanceId;
        ulong _lastServerSeq;
        int _heartbeatMs = 15000;
        int _loopGen;
        Vector3 _spawn;
        Vector3 _pos;
        long _lastClientTimeMs;
        float _yaw;
        bool _helloOk;
        bool _stopMoves;
        int _heartbeatMisses;
        int _resumeBusy;
        int _rpcRttMs;
        int _moveTimeoutStreak;
        int _unknownStreak;
        ulong _authorityStateSeq;
        int _poseUncertain;
        int _moveRecoveryBusy;
        long _moveHoldUntilMs;
        long _nextAuthorityAtMs;
        long _uncertainRequestSeq;
        long _lastAuthorityRequestSeq;
        long _lastResolvedMoveRequestSeq;
        long _currentAuthorityRequestSeq;
        long _currentAuthoritySentAtMs;
        Vector3 _uncertainTarget;
        readonly ConcurrentDictionary<ulong, long> _moveWrittenAtMs =
            new ConcurrentDictionary<ulong, long>();
        readonly ConcurrentDictionary<ulong, Vector3> _timedOutBeforeWriteCompleted =
            new ConcurrentDictionary<ulong, Vector3>();
        static int _lineRefreshTick;
        static string _cachedClientVersion = "luna";
        static string _cachedPlatform = "Unknown";
        static bool _unityInfoCached;
        Task _heartbeatTask = Task.CompletedTask;
        Task _moveTask = Task.CompletedTask;
        Task _recoveryTask = Task.CompletedTask;
        BotMovementClock _clock;
        readonly BotPushTracker _push = new BotPushTracker();

        public BotMovementSettings Movement { get; set; } = new BotMovementSettings();
        public BotMovementProfile Profile { get; private set; }
        public BotMovementMetrics Metrics => _clock != null ? _clock.Metrics : null;
        public ulong MapInstanceId => _mapInstanceId;
        public uint RealmId { get; private set; }
        public ulong MapTemplateId { get; private set; }
        public uint LineNo => _lastEnterLine;
        public string GameLogicInstanceId { get; private set; } = "";
        public bool PoseUncertain => Volatile.Read(ref _poseUncertain) != 0;
        public int CurrentAuthorityWaitMs
        {
            get
            {
                var since = Interlocked.Read(ref _currentAuthoritySentAtMs);
                if (since <= 0)
                    return 0;
                var elapsed = MonotonicMs() - since;
                return elapsed <= 0 ? 0 : (int)Math.Min(int.MaxValue, elapsed);
            }
        }
        public bool AwaitingMoveAuthority => Interlocked.Read(ref _currentAuthoritySentAtMs) > 0;
        public int ClientSendQueueDepth => _conn.SendQueueDepth;
        public long ClientSendQueueOldestMs => _conn.OldestQueueAgeMs;
        public RpcPipeMetrics PipeMetrics => _conn.PipeMetrics;
        public int VisibleEntities => _push.VisibleCount;
        public bool PushHealthy => _push.Healthy;

        public string Presence
        {
            get
            {
                if (Volatile.Read(ref _resumeBusy) != 0)
                    return "migrating";
                if (Failed)
                    return "disconnected";
                if (InWorld)
                    return "in_world";
                if (Status == "idle")
                    return "idle";
                return "entering";
            }
        }

        public int Index => _index;
        /// <summary>0 means server default (7200s). Heartbeat does not renew TTL.</summary>
        public int SessionTtlSec { get; set; }
        public int GatewayPort { get; private set; }
        public ulong PlayerId => _playerId;
        public bool InWorld { get; private set; }
        public bool Failed { get; private set; }
        public string Status { get; private set; } = "idle";
        /// <summary>Full diag for UI/server核对. Bucket key is the part before " | ".</summary>
        public string LastError { get; private set; } = "";
        /// <summary>stage=… code=… — used to aggregate identical failure classes.</summary>
        public string LastErrorBucket { get; private set; } = "";
        string _lastRejectDetail = "";
        string _lastTrace = "";
        uint _lastEnterLine;

        public LoadTestBot(int index, IMainThreadDispatcher dispatcher)
        {
            _index = index;
            _conn = new GameConnection(dispatcher ?? new ImmediateDispatcher()) { Quiet = true };
            _conn.PushReceived += OnPush;
            _conn.LateResponse += OnLateMove;
            _conn.FrameWriteCompleted += OnFrameWriteCompleted;
            CacheUnityInfoOnMainThread();
        }

        static long MonotonicMs()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000L /
                   System.Diagnostics.Stopwatch.Frequency;
        }

        static void AtomicMax(ref long target, long value)
        {
            long previous;
            do
            {
                previous = Interlocked.Read(ref target);
                if (previous >= value)
                    return;
            }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }

        static void CacheUnityInfoOnMainThread()
        {
            if (_unityInfoCached)
                return;
            try
            {
                _cachedClientVersion = Application.version ?? "luna";
                _cachedPlatform = Application.platform.ToString();
                LoadTestSessionLedger.CachePathOnMainThread();
                _unityInfoCached = true;
            }
            catch
            {
                /* constructor may already be off-main; Hello falls back to cached defaults */
            }
        }

        public async Task EnterWorldAsync(string deviceId, string displayName, string password, int gatewayPort,
            CancellationToken ct, uint preferredLine = 0)
        {
            CacheUnityInfoOnMainThread();
            _deviceId = deviceId;
            _displayName = displayName;
            _password = password ?? "";
            GatewayPort = gatewayPort > 0 ? gatewayPort : 8083;
            _preferredLine = preferredLine;
            await CloseSessionAsync().ConfigureAwait(true);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, ct))
            {
                var token = linked.Token;
                try
                {
                    var client = GameMeshClient.Instance;
                    if (client == null)
                        throw new InvalidOperationException("GameMeshClient missing");
                    _host = client.Config.host ?? "";
                    client.Config.ResolveMapContract();
                    await ConnectHelloWithFailoverAsync(client, GatewayPort, token).ConfigureAwait(true);
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
                    MarkFailed(GuessStage(ex), ex);
                    Status = "失败";
                    throw;
                }
            }
        }

        public async Task PrepareRetryAsync()
        {
            Failed = false;
            LastError = "";
            LastErrorBucket = "";
            _lastRejectDetail = "";
            _lastTrace = "";
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

        public Task CloseSessionAsync()
        {
            return CloseSessionAsync(CancellationToken.None, 1200, 800);
        }

        public async Task CloseSessionAsync(CancellationToken ct, int connectMs, int rpcMs)
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
                    await SendLogoutAsync(host, port, playerId, token, ct, connectMs, rpcMs)
                        .ConfigureAwait(true);
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
                _sessionId = "";
                _playerId = 0;
                _stopMoves = false;
            }
        }

        async Task SendLogoutAsync(string host, int port, ulong playerId, string token,
            CancellationToken ct, int connectMs, int rpcMs)
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

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    linked.CancelAfter(Math.Max(250, connectMs));
                    await _conn.ConnectAsync(host, port, linked.Token).ConfigureAwait(true);
                    await HelloAsync(client, linked.Token, Math.Max(250, rpcMs)).ConfigureAwait(true);
                }
            }

            if (ct.IsCancellationRequested)
                return;
            if (ConnectionStateMachine.CanTransition(_conn.State, ConnectionState.LoggingOut))
                _conn.SetLogicalState(ConnectionState.LoggingOut);
            var req = new GameRequest
            {
                SessionToken = token,
                Logout = new LogoutReq { PlayerId = playerId, Token = token }
            };
            var rsp = await _conn.RequestAsync(req, TimeSpan.FromMilliseconds(Math.Max(250, rpcMs)), ct)
                .ConfigureAwait(true);
            if (AuthResponse.FromLogout(rsp, true).AuthorityOk)
                LoadTestSessionLedger.Forget(playerId, token);
        }

        async Task HelloAsync(GameMeshClient client, CancellationToken ct, int timeoutMs = 0)
        {
            Status = "握手";
            var hello = new ClientHelloReq
            {
                ProtocolVersion = ProtocolHandshake.ProtocolVersion,
                SchemaSha256 = client.ProtocolSchemaSha256 ?? "",
                // Application.* is main-thread only; bots Hello/reconnect on the thread pool.
                ClientVersion = _cachedClientVersion,
                Platform = _cachedPlatform,
                BuildChannel = "loadtest"
            };
            foreach (var cap in ProtocolHandshake.ClientCapabilities)
                hello.Capabilities.Add(cap);
            var helloMs = timeoutMs > 0
                ? timeoutMs
                : Math.Max(3000, client.Config.helloTimeoutMs);
            var rsp = await SendAsync(new GameRequest { ClientHello = hello },
                TimeSpan.FromMilliseconds(helloMs), ct).ConfigureAwait(true);
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
            }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(true);
            if (!rsp.Ok || rsp.Register == null || !rsp.Register.Ok || rsp.Register.PlayerId == 0)
            {
                var msg = rsp.Register != null ? rsp.Register.Message : rsp.Message;
                throw new GameMeshException(ProtocolMapper.ExtractErrorCode(rsp),
                    string.IsNullOrEmpty(msg) ? "register failed" : msg);
            }

            _playerId = rsp.Register.PlayerId;
            _conn.SetLogicalState(ConnectionState.Connected);
        }

        async Task LoginAsync(CancellationToken ct, bool kickOtherDevice = true)
        {
            Status = "登录";
            if (_conn.State == ConnectionState.Connected)
                _conn.SetLogicalState(ConnectionState.Authenticating);
            var rsp = await SendAsync(new GameRequest
            {
                Login = new LoginReq
                {
                    PlayerId = _playerId,
                    DeviceId = _deviceId ?? "",
                    ServerId = 1,
                    TtlSec = SessionTtlSec <= 0 ? 0u : (uint)SessionTtlSec,
                    KickOtherDevice = kickOtherDevice,
                    Credential = _password ?? ""
                }
            }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(true);
            if (!AuthResponse.TryAcceptLogin(rsp, _playerId, out var playerId, out var login, out var code,
                    out var message))
                throw new GameMeshException(code, message);
            _playerId = playerId;
            _token = login.Token ?? "";
            _sessionId = login.SessionId ?? "";
            LoadTestSessionLedger.Remember(ResolveHost(), GatewayPort, _playerId, _token);
            _conn.SetLogicalState(ConnectionState.Authenticated);
        }

        async Task EnterMapAsync(GameMeshClient client, CancellationToken ct, bool systemPickLine = false)
        {
            Exception last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        await RefreshSharedLinesAsync().ConfigureAwait(true);
                        await Task.Delay(400 * attempt, ct).ConfigureAwait(true);
                        // Timeout alone must not tear down a live socket; only reconnect when down.
                        if (IsLinkDown())
                            await EnsureLinkedAsync(ct).ConfigureAwait(true);
                    }

                    await EnterMapOnceAsync(client, ct, systemPickLine || attempt > 0).ConfigureAwait(true);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // A slow EnterMap on a live socket is still in the gateway queue.
                    // Sending it again is what piled 605 logins into multi-second replies.
                    var slowOnly = IsLinkFailure(ex) && !IsTransportDead(ex) && !IsLinkDown();
                    if (attempt == 2 || !IsRetryableEnter(ex) || slowOnly)
                        throw;
                }
            }

            if (last != null)
                throw last;
        }

        async Task EnterMapOnceAsync(GameMeshClient client, CancellationToken ct, bool systemPickLine = false)
        {
            Status = "进图";
            _conn.SetLogicalState(ConnectionState.EnteringWorld);
            // Post-reconnect / overflow: line_no=0 lets the server open or assign a line.
            var line = systemPickLine ? 0u : ResolveEnterLine(client, _preferredLine, _index);
            _lastEnterLine = line;
            var rsp = await SendEnterMapAsync(client, line, ct).ConfigureAwait(true);
            if (!EnterMapAccepted(rsp))
            {
                var code = ProtocolMapper.ExtractErrorCode(rsp);
                RememberRspMeta(rsp);
                if (GameErrorCatalog.IsSessionMissing(code))
                {
                    await LoginAsync(ct).ConfigureAwait(true);
                    rsp = await SendEnterMapAsync(client, line, ct).ConfigureAwait(true);
                    RememberRspMeta(rsp);
                }
                else
                {
                    var retry = NextEnterLine(client, line, code);
                    if (retry != null)
                    {
                        _lastEnterLine = retry.Value;
                        rsp = await SendEnterMapAsync(client, retry.Value, ct).ConfigureAwait(true);
                        RememberRspMeta(rsp);
                    }
                }
            }
            if (!EnterMapAccepted(rsp))
            {
                var failCode = ProtocolMapper.ExtractErrorCode(rsp);
                RememberRspMeta(rsp);
                if (string.IsNullOrEmpty(failCode))
                    failCode = GameMeshErrorCode.ServerError;
                var msg = rsp.EnterMap != null ? rsp.EnterMap.Message : rsp.Message;
                throw new GameMeshException(failCode,
                    "enter_fail line=" + _lastEnterLine +
                    " trace=" + (_lastTrace.Length > 0 ? _lastTrace : "-") +
                    (string.IsNullOrEmpty(msg) ? "" : " msg=" + GameMeshLog.Redact(msg)));
            }

            _mapInstanceId = rsp.EnterMap.MapInstanceId;
            _lastEnterLine = rsp.EnterMap.LineNo;
            MapTemplateId = rsp.EnterMap.MapTemplateId;
            GameLogicInstanceId = rsp.EnterMap.GamelogicInstanceId ?? "";
            if (client != null && client.Config != null)
                RealmId = client.Config.realmId;
            _push.BindInstance(_mapInstanceId, rsp.EnterMap.OwnerEpoch);
            _lastServerSeq = _push.LastAppliedServerSeq;
            _spawn = rsp.EnterMap.SpawnPosition != null
                ? ProtocolMapper.ToUnity(rsp.EnterMap.SpawnPosition)
                : new Vector3(-26f, -0.2f, -5f);
            _pos = _spawn;
            _yaw = rsp.EnterMap.SpawnYaw;
            _conn.SetLogicalState(ConnectionState.InWorld);
        }

        async Task<GameResponse> SendEnterMapAsync(GameMeshClient client, uint lineNo, CancellationToken ct)
        {
            return await SendAsync(new GameRequest
            {
                EnterMap = new EnterMapReq
                {
                    PlayerId = _playerId,
                    RealmId = client.Config.realmId,
                    MapTemplateId = client.Config.mapTemplateId,
                    MapInstanceId = 0,
                    MapDataVersion = client.Config.dataVersion,
                    MapDataSha256 = client.Config.mapDataHash ?? "",
                    OperationId = Guid.NewGuid().ToString("N"),
                    LineNo = lineNo
                }
            }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(true);
        }

        static bool EnterMapAccepted(GameResponse rsp)
        {
            return rsp != null && rsp.Ok && rsp.EnterMap != null && rsp.EnterMap.Ok;
        }

        static uint ResolveEnterLine(GameMeshClient client, uint preferred, int index)
        {
            if (client != null && client.Lines != null)
                return client.Lines.PickLoadTestLine(preferred, index);
            return 0;
        }

        static uint? NextEnterLine(GameMeshClient client, uint failedLine, string code)
        {
            if (code == GameMeshErrorCode.MapHashMismatch || code == "ERR_MAP_DATA_MISMATCH")
                return null;
            if (GameErrorCatalog.IsSessionMissing(code))
                return null;
            if (client == null || client.Lines == null)
                return null;
            if (GameErrorCatalog.IsMapNoLine(code))
                return client.Lines.ResolveConcreteLine(0);
            if (GameErrorCatalog.IsMapLineFull(code) || GameErrorCatalog.IsMapDraining(code) ||
                code == "ERR_INTERNAL" || code == "ERR_WRONG_ROUTE")
                return 0;
            if (GameErrorCatalog.IsMapNotReady(code))
                return failedLine;
            return null;
        }

        static bool IsRetryableEnter(Exception ex)
        {
            return IsLinkFailure(ex) || IsLineRetry(ex);
        }

        static bool IsLinkFailure(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is GameMeshException ge &&
                    (ge.ErrorCode == GameMeshErrorCode.ClientTimeout ||
                     ge.ErrorCode == GameMeshErrorCode.ClientDisconnected ||
                     ge.ErrorCode == GameMeshErrorCode.ClientNotConnected))
                    return true;
                if (IsTransportDead(cur))
                    return true;
                var msg = cur.Message ?? "";
                if (msg.IndexOf("CLIENT_TIMEOUT", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        static bool IsTransportDead(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is GameMeshException ge &&
                    (ge.ErrorCode == GameMeshErrorCode.ClientDisconnected ||
                     ge.ErrorCode == GameMeshErrorCode.ClientNotConnected))
                    return true;
                var msg = cur.Message ?? "";
                if (msg.IndexOf("远程主机强迫关闭", StringComparison.Ordinal) >= 0 ||
                    msg.IndexOf("Connection reset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("Unable to read data from the transport connection",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("fail-closed", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        static bool IsLineRetry(Exception ex)
        {
            var ge = ex as GameMeshException;
            if (ge == null)
                return false;
            return GameErrorCatalog.IsMapLineFull(ge.ErrorCode) ||
                   GameErrorCatalog.IsMapNoLine(ge.ErrorCode) ||
                   GameErrorCatalog.IsMapNotReady(ge.ErrorCode) ||
                   GameErrorCatalog.IsMapDraining(ge.ErrorCode) ||
                   ge.ErrorCode == "ERR_INTERNAL" ||
                   ge.ErrorCode == "ERR_WRONG_ROUTE";
        }

        async Task RefreshSharedLinesAsync()
        {
            var now = Environment.TickCount;
            if (unchecked(now - _lineRefreshTick) < 5000)
                return;
            _lineRefreshTick = now;
            var live = GameMeshClient.Instance;
            if (live == null)
                return;
            try
            {
                await live.QueryMapLinesAsync(true).ConfigureAwait(true);
            }
            catch
            {
                /* the next enter still asks the server to choose a line */
            }
        }

        bool IsLinkDown()
        {
            return !_helloOk ||
                   _conn.State == ConnectionState.Disconnected ||
                   _conn.State == ConnectionState.Closing ||
                   _conn.State == ConnectionState.Connecting;
        }

        async Task EnsureLinkedAsync(CancellationToken ct)
        {
            // Hold period: if TCP is still alive and Hello already done, do not re-Hello.
            if (!IsLinkDown())
                return;
            var client = GameMeshClient.Instance;
            if (client == null)
                throw new InvalidOperationException("GameMeshClient missing");
            if (string.IsNullOrEmpty(_host))
                _host = client.Config.host ?? "";
            await ConnectHelloWithFailoverAsync(client, GatewayPort, ct).ConfigureAwait(false);
        }

        async Task ConnectHelloWithFailoverAsync(GameMeshClient client, int preferredPort, CancellationToken ct)
        {
            client.Config.NormalizeGatewayPorts();
            var ports = client.Config.GatewayPortsPreferred(preferredPort > 0 ? preferredPort : client.Config.port);
            Exception last = null;
            for (var i = 0; i < ports.Length; i++)
            {
                var port = ports[i];
                try
                {
                    Status = "连接中 :" + port;
                    try
                    {
                        await _conn.DisconnectAsync(DisconnectReason.Reconnect, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        /* already closed */
                    }

                    _helloOk = false;
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        connectCts.CancelAfter(15000);
                        await _conn.ConnectAsync(_host, port, connectCts.Token).ConfigureAwait(false);
                    }

                    await HelloAsync(client, ct).ConfigureAwait(false);
                    GatewayPort = port;
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _helloOk = false;
                    GameMeshLog.Warn("bot#" + _index + " gateway :" + port + " " +
                                     GameMeshLog.Redact(ex.Message) +
                                     (i + 1 < ports.Length ? " → try :" + ports[i + 1] : ""));
                    try
                    {
                        await _conn.DisconnectAsync(DisconnectReason.Reconnect, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        /* next port */
                    }
                }
            }

            if (last != null)
                throw last;
            throw new GameMeshException(GameMeshErrorCode.ClientNotConnected, "no gateway available");
        }

        enum ResumeOutcome
        {
            /// <summary>Server rejected the session. Login + EnterMap is allowed.</summary>
            Closed = 0,
            /// <summary>On map; safe for Move/Heartbeat.</summary>
            OnMap = 1,
            /// <summary>Session restored; EnterMap still required (do NOT Login).</summary>
            NeedEnter = 2,
            /// <summary>Reconnect RPC timed out. Session may still be alive. Do NOT Login.</summary>
            Unconfirmed = 3
        }

        async Task TryResumeAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _resumeBusy, 1, 0) != 0)
                return;
            var gen = _loopGen;
            InWorld = false;
            Status = "重连";
            try
            {
                await Task.Delay((_index % 40) * 50, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            finally
            {
                if (ct.IsCancellationRequested)
                    Interlocked.Exchange(ref _resumeBusy, 0);
            }
            Exception last = null;
            var lastStage = "resume";
            var lastOutcome = "";
            try
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    if (_stopMoves || gen != _loopGen || ct.IsCancellationRequested)
                        return;
                    try
                    {
                        if (attempt > 0)
                        {
                            var jitter = (_index % 25) * 40;
                            await Task.Delay(1000 * attempt + jitter, ct).ConfigureAwait(true);
                        }
                        lastStage = "connect";
                        await EnsureLinkedAsync(ct).ConfigureAwait(true);
                        lastStage = "reconnect";
                        var outcome = await TryReconnectSessionAsync(ct).ConfigureAwait(true);
                        lastOutcome = outcome.ToString();
                        var live = GameMeshClient.Instance;
                        if (live == null)
                            throw new InvalidOperationException("GameMeshClient missing");

                        if (outcome == ResumeOutcome.Unconfirmed)
                        {
                            // 605-bot run: one slow Reconnect was retried 6 times, then the
                            // retry wave did it again. The session was still on the map.
                            if (_mapInstanceId != 0)
                                InWorld = true;
                            Status = "重连超时，保持原会话 :" + GatewayPort;
                            _heartbeatMisses = 0;
                            return;
                        }

                        if (outcome == ResumeOutcome.NeedEnter)
                        {
                            // Reconnect already issued a new fence_token — Login would replace the session.
                            lastStage = "enter_after_reconnect";
                            await EnterMapAsync(live, ct, systemPickLine: true).ConfigureAwait(true);
                        }
                        else if (outcome == ResumeOutcome.Closed)
                        {
                            lastStage = "login_after_reconnect_closed";
                            await LoginAsync(ct, kickOtherDevice: false).ConfigureAwait(true);
                            lastStage = "enter_after_login";
                            await EnterMapAsync(live, ct, systemPickLine: true).ConfigureAwait(true);
                        }

                        if (_mapInstanceId == 0 || _conn.State != ConnectionState.InWorld)
                            throw new GameMeshException(GameMeshErrorCode.ClientIllegalState,
                                "resume not on map outcome=" + lastOutcome +
                                " detail=" + (_lastRejectDetail.Length > 0 ? _lastRejectDetail : "-"));

                        if (_stopMoves || gen != _loopGen)
                            return;
                        InWorld = true;
                        Failed = false;
                        LastError = "";
                        LastErrorBucket = "";
                        Status = "在线 :" + GatewayPort;
                        _heartbeatMisses = 0;
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Status = "重连失败 " + ErrCode(ex);
                        _lastRejectDetail = "attempt=" + (attempt + 1) + "/6 stage=" + lastStage +
                                            " outcome=" + lastOutcome + " " + ShortErr(ex);
                        /* next attempt opens a new socket */
                    }
                }

                if (_stopMoves || gen != _loopGen)
                    return;
                InWorld = false;
                MarkFailed(lastStage, last, "outcome=" + lastOutcome);
                Status = "掉线";
                _stopMoves = true;
            }
            finally
            {
                Interlocked.Exchange(ref _resumeBusy, 0);
            }
        }

        void MarkFailed(string stage, Exception ex, string extra = "")
        {
            Failed = true;
            InWorld = false;
            var code = ErrCode(ex);
            if (string.IsNullOrEmpty(code))
                code = "FAIL";
            var bucket = "stage=" + stage + " code=" + code;
            var detail = FormatDiag(stage, code, extra);
            LastErrorBucket = bucket;
            LastError = bucket + " | " + detail;
            GameMeshLog.Warn("bot#" + _index + " " + LastError);
        }

        string FormatDiag(string stage, string code, string extra)
        {
            var sess = _sessionId ?? "";
            if (sess.Length > 8)
                sess = sess.Substring(0, 8);
            var parts = new List<string>
            {
                "bot#" + _index,
                "pid=" + _playerId,
                "port=" + GatewayPort,
                "code=" + (string.IsNullOrEmpty(code) ? "-" : code),
                "trace=" + (_lastTrace.Length > 0 ? _lastTrace : "-"),
                "map=" + _mapInstanceId,
                "line=" + _lastEnterLine,
                "sess=" + (sess.Length > 0 ? sess : "-")
            };
            if (!string.IsNullOrEmpty(_lastRejectDetail))
                parts.Add(_lastRejectDetail);
            if (!string.IsNullOrEmpty(extra))
                parts.Add(extra);
            if (!string.IsNullOrEmpty(stage))
                parts.Add("at=" + stage);
            return string.Join(" ", parts);
        }

        void RememberRspMeta(GameResponse rsp)
        {
            if (rsp == null)
                return;
            var t = ProtocolMapper.ShortTraceId(rsp);
            if (!string.IsNullOrEmpty(t))
                _lastTrace = t;
        }

        static string GuessStage(Exception ex)
        {
            var msg = ex?.Message ?? "";
            if (msg.IndexOf("enter", StringComparison.OrdinalIgnoreCase) >= 0)
                return "enter";
            if (msg.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0)
                return "login";
            if (msg.IndexOf("hello", StringComparison.OrdinalIgnoreCase) >= 0)
                return "hello";
            if (msg.IndexOf("register", StringComparison.OrdinalIgnoreCase) >= 0)
                return "register";
            if (ex is GameMeshException ge)
            {
                if (ge.ErrorCode == GameMeshErrorCode.ClientTimeout)
                    return "timeout";
                if (ge.ErrorCode == GameMeshErrorCode.ClientDisconnected ||
                    ge.ErrorCode == GameMeshErrorCode.ClientNotConnected)
                    return "connect";
            }

            return "enter_world";
        }

        static string ErrCode(Exception ex)
        {
            if (ex is GameMeshException ge && !string.IsNullOrEmpty(ge.ErrorCode))
                return ge.ErrorCode;
            return "";
        }

        static string ShortErr(Exception ex)
        {
            var code = ErrCode(ex);
            if (!string.IsNullOrEmpty(code))
                return code;
            var msg = GameMeshLog.Redact(ex?.Message ?? "error");
            if (msg.Length > 80)
                msg = msg.Substring(0, 80);
            return msg;
        }

        async Task<ResumeOutcome> TryReconnectSessionAsync(CancellationToken ct)
        {
            if (_playerId == 0 || string.IsNullOrEmpty(_token))
            {
                _lastRejectDetail = "no_session pid=" + _playerId;
                return ResumeOutcome.Closed;
            }

            GameResponse rsp;
            try
            {
                rsp = await SendAsync(new GameRequest
                {
                    Reconnect = new ReconnectReq
                    {
                        PlayerId = _playerId,
                        SessionId = _sessionId ?? "",
                        ReconnectTicket = _token,
                        LastServerSeq = _lastServerSeq
                    }
                }, RpcBudget(15000), ct).ConfigureAwait(true);
            }
            catch (GameMeshException ex)
            {
                _lastRejectDetail = "reconnect_rpc " + ShortErr(ex);
                _lastTrace = "";
                // Timeout is not a rejected session. Do not fall through to Login.
                return ResumeOutcome.Unconfirmed;
            }

            RememberRspMeta(rsp);
            if (rsp == null || !rsp.Ok || rsp.Reconnect == null || !rsp.Reconnect.Ok)
            {
                var code = ProtocolMapper.ExtractErrorCode(rsp);
                if (string.IsNullOrEmpty(code))
                    code = "RECONNECT_REJECTED";
                var msg = rsp?.Reconnect != null ? rsp.Reconnect.Message : rsp?.Message;
                _lastRejectDetail = "reconnect_rsp code=" + code +
                                    " trace=" + (_lastTrace.Length > 0 ? _lastTrace : "-") +
                                    (string.IsNullOrEmpty(msg) ? "" : " msg=" + GameMeshLog.Redact(msg));
                return ResumeOutcome.Closed;
            }

            var recon = rsp.Reconnect;
            if (!string.IsNullOrEmpty(recon.SessionId))
                _sessionId = recon.SessionId;
            if (!string.IsNullOrEmpty(recon.Token))
            {
                _token = recon.Token;
                LoadTestSessionLedger.Remember(ResolveHost(), GatewayPort, _playerId, _token);
            }

            if (_conn.State == ConnectionState.Connected)
                _conn.SetLogicalState(ConnectionState.Authenticating);
            if (_conn.State == ConnectionState.Authenticating)
                _conn.SetLogicalState(ConnectionState.Authenticated);
            // Already InWorld/EnteringWorld: do not step backward (illegal InWorld->Authenticating).

            // Schema: ok && need_enter_map (or !on_map) → EnterMap before Move/Heartbeat.
            var needEnterMap = recon.NeedEnterMap || !recon.OnMap;
            _lastRejectDetail = "need_enter=" + (needEnterMap ? "1" : "0") +
                                " on_map=" + (recon.OnMap ? "1" : "0") +
                                " map_hint=" + recon.MapInstanceId +
                                " full_snap=" + (recon.NeedFullSnapshot ? "1" : "0");
            if (needEnterMap)
            {
                _mapInstanceId = 0;
                return ResumeOutcome.NeedEnter;
            }

            if (recon.MapInstanceId != 0)
                _mapInstanceId = recon.MapInstanceId;
            if (_mapInstanceId == 0)
                return ResumeOutcome.NeedEnter;
            if (_conn.State == ConnectionState.Authenticated)
                _conn.SetLogicalState(ConnectionState.InWorld);
            return _conn.State == ConnectionState.InWorld
                ? ResumeOutcome.OnMap
                : ResumeOutcome.NeedEnter;
        }

        async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var interval = Math.Max(1000, _heartbeatMs);
            var gen = _loopGen;
            while (!_stopMoves && !ct.IsCancellationRequested && gen == _loopGen)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    if (_stopMoves || ct.IsCancellationRequested)
                        return;
                    // After reconnect need_enter_map: no Heartbeat until EnterMap sets InWorld.
                    if (!InWorld || Volatile.Read(ref _resumeBusy) != 0)
                        continue;
                    var mono = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await SendAsync(new GameRequest
                    {
                        Heartbeat = new HeartbeatReq
                        {
                            ClientMonotonicMs = mono,
                            LastServerSeq = _lastServerSeq,
                            EchoMs = mono
                        }
                    }, RpcBudget(12000), ct).ConfigureAwait(false);
                    _heartbeatMisses = 0;
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (_stopMoves || ct.IsCancellationRequested || gen != _loopGen)
                        return;
                    LoadTestLog.Timeout("heartbeat", "bot#" + _index + " " + GameMeshLog.Redact(ex.Message));
                    // A slow HeartbeatRsp is not a dead session. Only a dropped TCP reconnects immediately.
                    if (IsLinkDown() || IsTransportDead(ex))
                    {
                        _heartbeatMisses = 0;
                        Status = "链路断开，重连";
                        await TryResumeAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    _heartbeatMisses++;
                    Status = "心跳超时 :" + GatewayPort + " x" + _heartbeatMisses;
                    var pause = BotMovementMetrics.HeartbeatPauseMs(_heartbeatMisses) + (_index % 10) * 100;
                    try { await Task.Delay(pause, ct).ConfigureAwait(false); }
                    catch (TaskCanceledException) { return; }
                }
            }
        }

        async Task MoveLoopAsync(CancellationToken ct)
        {
            // Absolute send clock. The Move RPC stays in flight beside the clock.
            // A slow reply skips the next slot; it does not queue a burst.
            var settings = Movement ?? new BotMovementSettings();
            var invalid = settings.Validate();
            Profile = invalid == null
                ? settings.ProfileFor(_index)
                : BotMovementProfile.Idle;
            _clock = new BotMovementClock(settings, _index);
            var population = settings.BotCount > 0 ? settings.BotCount : 1;
            _spawn = BotHomeLayout.Pick(_index, population, _pos.y);
            var planner = new BotMovementPlanner(Profile, _index, settings, _spawn);
            var gen = _loopGen;
            Task inflight = Task.CompletedTask;
            while (!_stopMoves && !ct.IsCancellationRequested && gen == _loopGen)
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (PoseUncertain && nowMs >= Interlocked.Read(ref _nextAuthorityAtMs))
                {
                    // Never switch a timed-out Move to full snapshots. Under pressure snapshots
                    // are larger and were timing out too, leaving the bot permanently frozen.
                    // Repeating the exact target is idempotent and stays on the priority MoveRsp path.
                    if (Volatile.Read(ref _moveRecoveryBusy) == 0)
                        _recoveryTask = RetryUnknownMoveAsync(planner, _clock, ct);
                }
                var queueBlocked = _conn.MoveBackpressured;
                var sessionReady = InWorld && Volatile.Read(ref _resumeBusy) == 0 &&
                                   _conn.State == ConnectionState.InWorld && _mapInstanceId != 0;
                var poseReady = !PoseUncertain;
                var holdReady = nowMs >= Interlocked.Read(ref _moveHoldUntilMs);
                var ready = sessionReady && poseReady && holdReady && !queueBlocked;
                // RequestAsync completion is not the end of this step. A written Move still owns
                // the single send slot until its MoveRsp arrives or its own 2-second budget expires.
                var slotOccupied = !inflight.IsCompleted || !_recoveryTask.IsCompleted ||
                                   AwaitingMoveAuthority
                    ? 1
                    : 0;
                var tick = _clock.Evaluate(ready, slotOccupied);
                if (queueBlocked && tick.Kind == BotMoveTickKind.SkipNotReady && _clock.Metrics != null)
                {
                    _clock.Metrics.SkippedNotReady--;
                    _clock.Metrics.SkippedBusy++;
                }
                else if (tick.Kind == BotMoveTickKind.SkipNotReady && _clock.Metrics != null)
                {
                    if (!sessionReady)
                        _clock.Metrics.SkippedSessionNotReady++;
                    else if (!poseReady)
                        _clock.Metrics.SkippedPoseUncertain++;
                    else if (!holdReady)
                        _clock.Metrics.SkippedRecoveryHold++;
                }
                if (tick.Kind == BotMoveTickKind.Send)
                {
                    var dt = _lastClientTimeMs <= 0
                        ? 1f / settings.TargetHz
                        : (nowMs - _lastClientTimeMs) / 1000f;
                    if (dt < 0f)
                        dt = 0f;
                    // A long stall may advance client_time_ms. The step still stays
                    // within 10 m/s of that delta; one second is enough for one step.
                    var stepDt = dt > 1f ? 1f : dt;
                    var maxStep = MoveStepLimit.PlannedMeters(stepDt, settings.WalkSpeed);
                    var plan = planner.NextAroundHome(_pos, maxStep);
                    if (!plan.Send)
                    {
                        if (plan.Reason == "pause")
                            _clock.Metrics.Paused++;
                        else if (plan.Reason == "path_rejected")
                            _clock.Metrics.PathRejected++;
                    }
                    else
                    {
                        _yaw = plan.Yaw;
                        _lastClientTimeMs = nowMs;
                        // 名额跟着请求本身释放。统计回调若排在线程池后面，不能继续把机器人算成忙。
                        try
                        {
                            var requestTask = StartMoveRequest(plan.Position, nowMs, ct,
                                out var requestSeq);
                            inflight = requestTask;
                            _ = FinishMoveStepAsync(requestTask, requestSeq, plan.Position,
                                planner, _clock, ct);
                        }
                        catch (Exception ex)
                        {
                            NoteMoveSendFailed(ex, planner, _clock);
                        }
                    }
                }

                var wait = tick.WaitMs > 0 ? tick.WaitMs : 20;
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }
            }
        }

        Task<GameResponse> StartMoveRequest(Vector3 next, long clientTimeMs, CancellationToken ct,
            out ulong requestSeq)
        {
            var metrics = _clock != null ? _clock.Metrics : null;
            var request = new GameRequest
            {
                Move = new MoveReq
                {
                    PlayerId = _playerId,
                    MapInstanceId = _mapInstanceId,
                    Position = ProtocolMapper.ToVec3(next),
                    Yaw = _yaw,
                    ClientTimeMs = clientTimeMs
                }
            };
            if (!_helloOk)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, "hello required");
            if (!string.IsNullOrEmpty(_token))
                request.SessionToken = _token;
            var task = _conn.RequestAsync(request, TimeSpan.FromSeconds(2), ct);
            requestSeq = request.Seq;
            if (metrics != null)
                Interlocked.Increment(ref metrics.QueueAccepted);
            return task;
        }

        async Task FinishMoveStepAsync(Task<GameResponse> requestTask, ulong requestSeq, Vector3 sent,
            BotMovementPlanner planner, BotMovementClock clock, CancellationToken ct)
        {
            var metrics = clock.Metrics;
            try
            {
                var rsp = await requestTask.ConfigureAwait(false);
                NoteMoveResponse(requestSeq, rsp != null && rsp.Ok, metrics);
                var move = rsp != null ? rsp.Move : null;
                var adopted = false;
                var fresh = move != null &&
                            (move.StateSeq == 0 || _authorityStateSeq == 0 ||
                             move.StateSeq >= _authorityStateSeq);
                if (fresh && move.Position != null)
                {
                    var authority = ProtocolMapper.ToUnity(move.Position);
                    var dx = authority.x - sent.x;
                    var dz = authority.z - sent.z;
                    var dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist > 0.5f)
                    {
                        metrics.Corrections++;
                        metrics.CorrectionDistanceSum += dist;
                    }

                    _pos = authority;
                    _yaw = move.Yaw;
                    adopted = true;
                    if (PoseUncertain)
                    {
                        _unknownStreak = 0;
                        Volatile.Write(ref _poseUncertain, 0);
                    }
                }

                metrics.RspMatched++;
                if (fresh && move.StateSeq > _authorityStateSeq)
                    _authorityStateSeq = move.StateSeq;
                if (rsp != null && rsp.Ok)
                {
                    // ok means the authority pose was stored. It does not mean
                    // anyone else can see it yet.
                    metrics.AckOk++;
                    _moveTimeoutStreak = 0;
                    _unknownStreak = 0;
                    if (adopted)
                        Volatile.Write(ref _poseUncertain, 0);
                    return;
                }

                var code = ProtocolMapper.ExtractErrorCode(rsp);
                if (BotMovementMetrics.IsBusy(code))
                {
                    metrics.Overloaded++;
                    _moveHoldUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;
                    Status = "服务器忙，暂停移动";
                    return;
                }

                metrics.Rejected++;
                if (code == "ERR_MOVE_TOO_FAST")
                    metrics.RejectTooFast++;
                else if (code == "ERR_OUT_OF_BOUNDS")
                    metrics.RejectBounds++;
                else if (code == "ERR_UNWALKABLE")
                    metrics.RejectUnwalkable++;
                else
                    metrics.RejectOther++;
                planner.NoteRejected();
                if (!adopted && GameErrorCatalog.IsNotOnMap(code) &&
                    Volatile.Read(ref _resumeBusy) == 0)
                {
                    var live = GameMeshClient.Instance;
                    if (live != null)
                        await EnterMapAsync(live, ct).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
                /* wave ending */
            }
            catch (Exception ex)
            {
                var mesh = ex as GameMeshException;
                if (mesh != null && mesh.ErrorCode == GameMeshErrorCode.ClientCancelled)
                {
                    metrics.Cancelled++;
                    return;
                }

                if (mesh != null &&
                    (mesh.ErrorCode == GameMeshErrorCode.ClientQueueExpired ||
                     (mesh.ErrorCode == GameMeshErrorCode.ClientTimeout && mesh.WritePhase == 0)))
                {
                    metrics.ExpiredBeforeWrite++;
                    metrics.Timeout++;
                    ForgetMoveRequest(requestSeq);
                    return;
                }

                if (mesh != null && mesh.ErrorCode == GameMeshErrorCode.ClientTimeout)
                {
                    metrics.Timeout++;
                    if (mesh.WritePhase >= RpcPendingTable.Slot.PhaseWriteCompleted)
                        WaitForMoveAuthorityBudget(requestSeq, sent, metrics);
                    else
                    {
                        // WriteAsync has started but has not completed. Do not report this as a
                        // server reply loss. If it later completes, the write observer starts the
                        // authority wait and marks this exact move uncertain.
                        metrics.TimeoutBeforeWrite++;
                        _timedOutBeforeWriteCompleted[requestSeq] = sent;
                        // The write observer may have won the race just before this continuation.
                        if (_moveWrittenAtMs.ContainsKey(requestSeq) &&
                            _timedOutBeforeWriteCompleted.TryRemove(requestSeq, out var writtenTarget))
                            WaitForMoveAuthorityBudget(requestSeq, writtenTarget, metrics);
                    }
                    return;
                }

                metrics.Rejected++;
                ForgetMoveRequest(requestSeq);
                planner.NoteRejected();
            }
        }

        void OnFrameWriteCompleted(ulong requestSeq, string type, long writtenAtMs)
        {
            if (!string.Equals(type, GameRequest.BodyOneofCase.Move.ToString(),
                    StringComparison.Ordinal))
                return;

            var metrics = _clock != null ? _clock.Metrics : null;
            if (metrics != null)
            {
                Interlocked.Increment(ref metrics.Sent);
                Interlocked.Increment(ref metrics.WriteCompleted);
            }

            // Never resurrect a wait that a very fast receive already confirmed.
            if ((ulong)Math.Max(0, Interlocked.Read(ref _lastResolvedMoveRequestSeq)) >= requestSeq)
            {
                ForgetMoveRequest(requestSeq);
                return;
            }

            _moveWrittenAtMs[requestSeq] = writtenAtMs;
            Interlocked.Exchange(ref _currentAuthorityRequestSeq, (long)requestSeq);
            Interlocked.Exchange(ref _currentAuthoritySentAtMs, writtenAtMs);

            if (_timedOutBeforeWriteCompleted.TryRemove(requestSeq, out var target) && metrics != null)
                WaitForMoveAuthorityBudget(requestSeq, target, metrics);
        }

        void NoteMoveResponse(ulong requestSeq, bool authorityConfirmed, BotMovementMetrics metrics,
            bool recordLatency = true)
        {
            AtomicMax(ref _lastResolvedMoveRequestSeq, (long)requestSeq);
            if (_moveWrittenAtMs.TryRemove(requestSeq, out var writtenAtMs) &&
                recordLatency && authorityConfirmed && metrics != null)
            {
                var elapsed = MonotonicMs() - writtenAtMs;
                if (elapsed >= 0 && elapsed < 60000)
                    metrics.NoteLatency((int)elapsed);
            }

            _timedOutBeforeWriteCompleted.TryRemove(requestSeq, out _);
            if (Interlocked.Read(ref _currentAuthorityRequestSeq) == (long)requestSeq)
            {
                Interlocked.Exchange(ref _currentAuthoritySentAtMs, 0);
                Interlocked.CompareExchange(ref _currentAuthorityRequestSeq, 0, (long)requestSeq);
            }

            if (!authorityConfirmed)
                return;

            AtomicMax(ref _lastAuthorityRequestSeq, (long)requestSeq);

            var uncertainSeq = Interlocked.Read(ref _uncertainRequestSeq);
            if (uncertainSeq != 0 && (long)requestSeq >= uncertainSeq)
            {
                Interlocked.Exchange(ref _uncertainRequestSeq, 0);
                Volatile.Write(ref _poseUncertain, 0);
                _unknownStreak = 0;
            }
        }

        void ForgetMoveRequest(ulong requestSeq)
        {
            _moveWrittenAtMs.TryRemove(requestSeq, out _);
            _timedOutBeforeWriteCompleted.TryRemove(requestSeq, out _);
            if (Interlocked.Read(ref _currentAuthorityRequestSeq) != (long)requestSeq)
                return;
            Interlocked.Exchange(ref _currentAuthoritySentAtMs, 0);
            Interlocked.CompareExchange(ref _currentAuthorityRequestSeq, 0, (long)requestSeq);
        }

        void MarkMoveAuthorityUncertain(ulong requestSeq, Vector3 sent,
            BotMovementMetrics metrics)
        {
            if ((ulong)Math.Max(0, Interlocked.Read(ref _lastAuthorityRequestSeq)) >= requestSeq)
                return;

            Interlocked.Increment(ref metrics.UnknownOutcome);
            _unknownStreak++;
            _uncertainTarget = sent;
            // The request has now left RpcPendingTable. It is a timed-out outcome, not a
            // centuries-old "currently waiting" sample. A retry gets its own fresh timer.
            ForgetMoveRequest(requestSeq);
            Interlocked.Exchange(ref _uncertainRequestSeq, (long)requestSeq);
            Volatile.Write(ref _poseUncertain, 1);

            // Close the timeout/late-response race. Whichever side observes the matching
            // authority last leaves the bot ready, never permanently uncertain.
            if ((ulong)Math.Max(0, Interlocked.Read(ref _lastAuthorityRequestSeq)) >= requestSeq)
            {
                Interlocked.Exchange(ref _uncertainRequestSeq, 0);
                Volatile.Write(ref _poseUncertain, 0);
                _unknownStreak = 0;
                return;
            }

            var retryShift = Math.Min(3, Math.Max(0, _unknownStreak - 1));
            var retryMs = Math.Min(5000, 500 << retryShift);
            Interlocked.Exchange(ref _nextAuthorityAtMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + retryMs + (_index % 20) * 50);
            Status = "Move超过2秒未确认，等待同目标重试 :" + GatewayPort;
        }

        void WaitForMoveAuthorityBudget(ulong requestSeq, Vector3 sent,
            BotMovementMetrics metrics)
        {
            if (!_moveWrittenAtMs.TryGetValue(requestSeq, out var writtenAtMs))
            {
                _timedOutBeforeWriteCompleted[requestSeq] = sent;
                return;
            }

            var remaining = 2000 - (MonotonicMs() - writtenAtMs);
            if (remaining <= 0)
            {
                MarkMoveAuthorityUncertain(requestSeq, sent, metrics);
                return;
            }

            _ = MarkMoveUncertainAfterBudgetAsync(requestSeq, sent, metrics, (int)remaining);
        }

        async Task MarkMoveUncertainAfterBudgetAsync(ulong requestSeq, Vector3 sent,
            BotMovementMetrics metrics, int delayMs)
        {
            try
            {
                await Task.Delay(Math.Max(1, delayMs), _life.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (!_moveWrittenAtMs.ContainsKey(requestSeq))
                return;
            MarkMoveAuthorityUncertain(requestSeq, sent, metrics);
        }

        async Task RetryUnknownMoveAsync(BotMovementPlanner planner, BotMovementClock clock,
            CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _moveRecoveryBusy, 1, 0) != 0)
                return;
            try
            {
                if (!PoseUncertain || !InWorld || _conn.State != ConnectionState.InWorld)
                    return;
                var target = _uncertainTarget;
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var requestTask = StartMoveRequest(target, nowMs, ct, out var requestSeq);
                await FinishMoveStepAsync(requestTask, requestSeq, target, planner, clock, ct)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                NoteMoveSendFailed(ex, planner, clock);
                Interlocked.Exchange(ref _nextAuthorityAtMs,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 750 + (_index % 20) * 100);
            }
            finally
            {
                Interlocked.Exchange(ref _moveRecoveryBusy, 0);
            }
        }

        // RequestAsync throws synchronously only before the frame is queued.
        void NoteMoveSendFailed(Exception ex, BotMovementPlanner planner, BotMovementClock clock)
        {
            var metrics = clock != null ? clock.Metrics : null;
            if (metrics == null || ex is TaskCanceledException)
                return;
            var mesh = ex as GameMeshException;
            if (mesh != null && mesh.ErrorCode == GameMeshErrorCode.ClientCancelled)
            {
                metrics.Cancelled++;
                return;
            }

            if (mesh != null &&
                (mesh.ErrorCode == GameMeshErrorCode.ClientQueueFull ||
                 mesh.ErrorCode == GameMeshErrorCode.ClientNotConnected ||
                 mesh.ErrorCode == GameMeshErrorCode.ClientQueueExpired))
            {
                metrics.ExpiredBeforeWrite++;
                return;
            }

            metrics.Rejected++;
            if (planner != null)
                planner.NoteRejected();
        }

        TimeSpan RpcBudget(int floorMs)
        {
            if (floorMs < 8000)
                return TimeSpan.FromMilliseconds(Math.Max(250, floorMs));
            var scaled = _rpcRttMs > 1000 ? _rpcRttMs * 2 + 1000 : floorMs;
            if (scaled < floorMs)
                scaled = floorMs;
            if (scaled > 20000)
                scaled = 20000;
            return TimeSpan.FromMilliseconds(scaled);
        }

        async Task<GameResponse> SendAsync(GameRequest request, TimeSpan timeout, CancellationToken ct)
        {
            if (!_helloOk && request.BodyCase != GameRequest.BodyOneofCase.ClientHello)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, "hello required");
            if (!string.IsNullOrEmpty(_token) && request.BodyCase != GameRequest.BodyOneofCase.ClientHello)
                request.SessionToken = _token;
            var started = Environment.TickCount;
            var rsp = await _conn.RequestAsync(request, timeout, ct).ConfigureAwait(false);
            var elapsed = unchecked(Environment.TickCount - started);
            if (elapsed > 0 && elapsed < 60000)
                _rpcRttMs = _rpcRttMs == 0 ? elapsed : (_rpcRttMs * 3 + elapsed) / 4;
            return rsp;
        }

        void OnLateMove(GameResponse rsp)
        {
            var move = rsp != null ? rsp.Move : null;
            if (move == null || !rsp.Ok || move.Position == null)
                return;
            NoteMoveResponse(rsp.Seq, true, _clock != null ? _clock.Metrics : null, false);
            if (!MoveRecovery.TryAdoptLate(_authorityStateSeq, move.StateSeq, _conn.Generation, _conn.Generation,
                    _mapInstanceId, _mapInstanceId))
                return;
            _authorityStateSeq = move.StateSeq;
            _pos = ProtocolMapper.ToUnity(move.Position);
            _yaw = move.Yaw;
            _unknownStreak = 0;
            _moveTimeoutStreak = 0;
            Volatile.Write(ref _poseUncertain, 0);
            if (_clock != null && _clock.Metrics != null)
                _clock.Metrics.LateRsp++;
            Status = "迟到坐标 :" + GatewayPort;
        }

        void OnPush(GameResponse rsp)
        {
            if (rsp == null)
                return;
            if (rsp.BodyCase == GameResponse.BodyOneofCase.ServerPush && rsp.ServerPush != null)
            {
                GameResponse inner = null;
                try
                {
                    if (rsp.ServerPush.Payload != null && rsp.ServerPush.Payload.Length > 0)
                        inner = GameResponse.Parser.ParseFrom(rsp.ServerPush.Payload);
                }
                catch (Exception ex)
                {
                    GameMeshLog.Warn("bot#" + _index + " push parse " + GameMeshLog.Redact(ex.Message));
                    return;
                }

                var applied = _push.ObserveServerPush(rsp.ServerPush, inner);
                if (applied)
                    _lastServerSeq = _push.LastAppliedServerSeq;
                // A gap only affects remote AOI rendering. This bot's own authority comes from
                // its MoveRsp; never pause Move or fetch a full snapshot to hide a remote gap.
                return;
            }

            if (rsp.BodyCase == GameResponse.BodyOneofCase.SessionReplaced)
            {
                RememberRspMeta(rsp);
                _lastRejectDetail = "session_replaced";
                MarkFailed("session_replaced",
                    new GameMeshException("ERR_SESSION_REPLACED", "session replaced"));
                Status = "被顶号";
                _stopMoves = true;
                InWorld = false;
                LoadTestSessionLedger.Forget(_playerId, _token);
                _token = "";
                _sessionId = "";
            }
        }

        string ResolveHost()
        {
            if (!string.IsNullOrEmpty(_host))
                return _host;
            var client = GameMeshClient.Instance;
            return client != null ? client.Config.host ?? "" : "";
        }

        public static async Task LogoutOrphanAsync(IMainThreadDispatcher dispatcher, string host, int port,
            ulong playerId, string token, CancellationToken ct)
        {
            var bot = new LoadTestBot(-1, dispatcher);
            bot._host = host ?? "";
            bot.GatewayPort = port > 0 ? port : 8083;
            bot._playerId = playerId;
            bot._token = token ?? "";
            await bot.CloseSessionAsync(ct, 800, 800).ConfigureAwait(true);
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
        static string _cachedPath;

        /// <summary>Unity persistentDataPath is main-thread only. Call before bot tasks.</summary>
        public static void CachePathOnMainThread()
        {
            _cachedPath = Path.Combine(Application.persistentDataPath, "gamemesh-loadtest-sessions.json");
        }

        static string FilePath => _cachedPath;

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

        public static void Clear()
        {
            lock (Gate)
                SaveUnlocked(new List<Entry>());
        }

        public static void ForgetMany(Entry[] entries)
        {
            if (entries == null || entries.Length == 0)
                return;
            lock (Gate)
            {
                var list = new List<Entry>(LoadUnlocked());
                var changed = false;
                for (var s = 0; s < entries.Length; s++)
                {
                    var target = entries[s];
                    if (target == null)
                        continue;
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        var entry = list[i];
                        if (entry == null)
                        {
                            list.RemoveAt(i);
                            changed = true;
                            continue;
                        }

                        if ((!string.IsNullOrEmpty(target.playerId) && entry.playerId == target.playerId) ||
                            (!string.IsNullOrEmpty(target.token) && entry.token == target.token))
                        {
                            list.RemoveAt(i);
                            changed = true;
                        }
                    }
                }

                if (changed)
                    SaveUnlocked(list);
            }
        }

        public static int Count()
        {
            lock (Gate)
                return LoadUnlocked().Length;
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
                if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
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
            if (string.IsNullOrEmpty(FilePath))
                return;
            var box = new FileBox { items = list != null ? list.ToArray() : Array.Empty<Entry>() };
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonUtility.ToJson(box));
        }
    }

    static class LoadTestLog
    {
        static int _heartbeat;
        static int _snapshot;
        static int _samples;
        static int _nextSummary;

        public static void Timeout(string kind, string detail)
        {
            if (kind == "heartbeat")
                Interlocked.Increment(ref _heartbeat);
            else
                Interlocked.Increment(ref _snapshot);
            if (Interlocked.Increment(ref _samples) <= 5)
                GameMeshLog.Warn(kind + " " + detail);
            var now = Environment.TickCount;
            if (_nextSummary != 0 && unchecked(now - _nextSummary) < 0)
                return;
            _nextSummary = unchecked(now + 10000);
            GameMeshLog.Warn("timeout summary heartbeat=" + _heartbeat + " snapshot=" + _snapshot);
        }
    }
}
