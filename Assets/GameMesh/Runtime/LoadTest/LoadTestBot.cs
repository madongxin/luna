using System;
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
        Vector3 _home;
        Vector3 _pos;
        float _yaw;
        bool _helloOk;
        bool _stopMoves;
        int _heartbeatMisses;
        int _resumeBusy;
        static int _lineRefreshTick;
        static string _cachedClientVersion = "luna";
        static string _cachedPlatform = "Unknown";
        static bool _unityInfoCached;
        Task _heartbeatTask = Task.CompletedTask;
        Task _moveTask = Task.CompletedTask;

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
            CacheUnityInfoOnMainThread();
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
                    if (attempt == 2 || !IsRetryableEnter(ex))
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
            _spawn = rsp.EnterMap.SpawnPosition != null
                ? ProtocolMapper.ToUnity(rsp.EnterMap.SpawnPosition)
                : new Vector3(-26f, -0.2f, -5f);
            // This map's AOI is 32m. Keep the crowd inside it; a 400m ring never gets pushed.
            var slot = _index % 80;
            var angle = slot * 2.39996323f;
            var ring = 3f + Mathf.Sqrt(slot) * 1.7f;
            _home = _spawn + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * ring;
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
            if (unchecked(now - _lineRefreshTick) < 2000)
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
                            // Gateway was slow. The old session may still be valid.
                            // Another Reconnect on the same live socket only piles up CLIENT_TIMEOUT.
                            if (!IsLinkDown() && _mapInstanceId != 0)
                            {
                                InWorld = true;
                                Status = "重连超时，保持原会话 :" + GatewayPort;
                                _heartbeatMisses = 0;
                                return;
                            }

                            lastStage = "reconnect";
                            throw new GameMeshException(GameMeshErrorCode.ClientTimeout,
                                "reconnect timeout, skip login");
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
                }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(true);
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
                    }, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
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
                    GameMeshLog.Warn("bot#" + _index + " heartbeat failed " +
                                     GameMeshLog.Redact(ex.Message));
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
                }
            }
        }

        async Task MoveLoopAsync(CancellationToken ct)
        {
            // Local speed must stay ≤ server move_speed (default 10). Step = speed * dt.
            const float speed = 5.5f;
            const float maxSpeed = 10f;
            const int tickMs = 100;
            const float turnRadPerTick = 1.1f;
            var walkSpeed = Mathf.Min(speed, maxSpeed);
            var rnd = new System.Random(_index * 9176 + Environment.TickCount);
            var dir = RandomDir(rnd);
            var desired = dir;
            var gen = _loopGen;
            Task inflight = Task.CompletedTask;
            while (!_stopMoves && !ct.IsCancellationRequested && gen == _loopGen)
            {
                var tickStart = Environment.TickCount;
                if (InWorld && Volatile.Read(ref _resumeBusy) == 0 && inflight.IsCompleted)
                {
                    var toHome = _home - _pos;
                    toHome.y = 0f;
                    var distHome = toHome.magnitude;
                    if (distHome > 28f)
                        desired = toHome.normalized;
                    else if (rnd.NextDouble() < 0.035)
                        desired = RandomDir(rnd);

                    dir = Vector3.RotateTowards(dir, desired, turnRadPerTick, 0f);
                    if (dir.sqrMagnitude < 0.01f)
                        dir = desired.sqrMagnitude > 0.01f ? desired : RandomDir(rnd);
                    else
                        dir.Normalize();

                    var step = walkSpeed * (tickMs / 1000f);
                    var next = _pos + dir * step;
                    if (Vector3.Distance(new Vector3(next.x, _home.y, next.z),
                            new Vector3(_home.x, _home.y, _home.z)) > 8f)
                    {
                        desired = toHome.sqrMagnitude > 0.01f ? toHome.normalized : RandomDir(rnd);
                        dir = Vector3.RotateTowards(dir, desired, turnRadPerTick * 1.5f, 0f);
                        if (dir.sqrMagnitude < 0.01f)
                            dir = desired;
                        else
                            dir.Normalize();
                        next = _pos + dir * step;
                    }

                    next = SnapWalkable(next, _spawn.y);
                    _yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    // Do not commit local pose until MoveRsp; keep intent only for the request.
                    inflight = SendMoveStepAsync(next, ct);
                }

                var elapsed = unchecked(Environment.TickCount - tickStart);
                var remain = tickMs - elapsed;
                if (remain < 1)
                    remain = 1;
                try
                {
                    await Task.Delay(remain, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        async Task SendMoveStepAsync(Vector3 next, CancellationToken ct)
        {
            try
            {
                var rsp = await SendAsync(new GameRequest
                {
                    Move = new MoveReq
                    {
                        PlayerId = _playerId,
                        MapInstanceId = _mapInstanceId,
                        Position = ProtocolMapper.ToVec3(next),
                        Yaw = _yaw,
                        ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }
                }, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                var move = rsp != null ? rsp.Move : null;
                // Always adopt MoveRsp pose (ok or not). Never keep a rejected local fly-away.
                if (move != null && move.Position != null)
                {
                    _pos = ProtocolMapper.ToUnity(move.Position);
                    _yaw = move.Yaw;
                }
                else if (!rsp.Ok && GameErrorCatalog.IsNotOnMap(ProtocolMapper.ExtractErrorCode(rsp)) &&
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
            catch
            {
                /* next free tick resends from the last accepted point */
            }
        }

        static Vector3 SnapWalkable(Vector3 pos, float fallbackY)
        {
            // Keep intended XZ. NavMesh planar snap was yanking bots back and caused twitching.
            try
            {
                if (UnityEngine.AI.NavMesh.SamplePosition(pos, out var hit, 2.5f,
                        UnityEngine.AI.NavMesh.AllAreas))
                {
                    var drift = Vector2.Distance(new Vector2(pos.x, pos.z),
                        new Vector2(hit.position.x, hit.position.z));
                    if (drift < 0.75f)
                        return hit.position;
                    return new Vector3(pos.x, hit.position.y, pos.z);
                }
            }
            catch
            {
                /* NavMesh may be missing in some scenes */
            }

            pos.y = fallbackY;
            return pos;
        }

        async Task<GameResponse> SendAsync(GameRequest request, TimeSpan timeout, CancellationToken ct)
        {
            if (!_helloOk && request.BodyCase != GameRequest.BodyOneofCase.ClientHello)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, "hello required");
            if (!string.IsNullOrEmpty(_token) && request.BodyCase != GameRequest.BodyOneofCase.ClientHello)
                request.SessionToken = _token;
            return await _conn.RequestAsync(request, timeout, ct).ConfigureAwait(false);
        }

        void OnPush(GameResponse rsp)
        {
            if (rsp == null)
                return;
            if (rsp.Seq > _lastServerSeq)
                _lastServerSeq = rsp.Seq;
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

        static Vector3 RandomDir(System.Random rnd)
        {
            var angle = rnd.NextDouble() * Math.PI * 2.0;
            return new Vector3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
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
}
