using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Aoi;
using GameMesh.Auth;
using GameMesh.Mail;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Player;
using GameMesh.Protocol;
using GameMesh.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameMesh.Bootstrap
{
    public sealed class GameMeshClient : MonoBehaviour
    {
        public static GameMeshClient Instance { get; private set; }

        public GameMeshClientConfig Config { get; private set; }
        public GameMeshLaunchArgs LaunchArgs { get; private set; }
        public GameSession Session { get; } = new GameSession();
        public PushReliability Push { get; } = new PushReliability();
        public AoiWorld Aoi { get; } = new AoiWorld();
        public MailClient Mail { get; private set; }
        public MoveSampler MoveSampler { get; } = new MoveSampler();
        public MoveCorrector MoveCorrector { get; } = new MoveCorrector();
        public GameConnection Connection { get; private set; }
        public ReconnectPolicy Reconnect { get; } = new ReconnectPolicy();
        public string LastError { get; private set; } = "";
        public string LastErrorCode { get; private set; } = "";
        public string LastErrorUi { get; private set; } = "";
        public bool MapBlocked { get; private set; }
        public string MapBlockReason { get; private set; } = "";
        public uint LastPlayerCount { get; private set; }
        public ulong ExpectedMapHashVersion { get; set; }
        public bool IsBusy => _busy;
        public string BusyStage { get; private set; } = "";
        public bool HasPendingSpawn;
        public Vector3 PendingSpawn;
        public float PendingSpawnYaw;
        public bool HasPendingCorrection;
        public Vector3 PendingCorrection;
        public float PendingCorrectionYaw;
        public bool MovesFrozen =>
            Connection == null ||
            Connection.State != ConnectionState.InWorld ||
            Reconnect.InFlight ||
            (AppPaused && string.IsNullOrEmpty(LaunchArgs.AutoScenario)) ||
            Push.HasGap ||
            Session.IsDead ||
            Session.SessionReplaced;
        public string SchemaHashShort =>
            string.IsNullOrEmpty(Config?.mapDataHash) ? "" : Config.mapDataHash.Substring(0, 8);
        public string ProtocolSchemaShort { get; private set; } = "";
        public string ProtocolSchemaSha256 { get; private set; } = "";
        public int LastRttMs { get; private set; }
        public int ProtocolVersion { get; private set; } = (int)ProtocolHandshake.ProtocolVersion;
        public bool HelloOk { get; private set; }
        public bool HeartbeatOk { get; private set; }
        public long ServerTimeOffsetMs => HeartbeatClock.ServerTimeOffsetMs;
        public HeartbeatClock HeartbeatClock { get; } = new HeartbeatClock();
        public bool AppPaused { get; private set; }

        GameMeshMainThreadDispatcher _dispatcher;
        CancellationTokenSource _lifetime;
        CancellationTokenSource _heartbeatCts;
        bool _logoutRequested;
        float _nextReconnectAt;
        float _lastMailPoll;
        bool _busy;
        bool _helloInFlight;
        bool _snapshotInFlight;
        bool _respawnInFlight;
        string _enterOpId;
        string _respawnOpId;
        ulong _lastMoveStateSeq;
        int _heartbeatMisses;
        uint _heartbeatIntervalMs = 5000;
        readonly List<MapManifestEntry> _helloMaps = new List<MapManifestEntry>();
        uint _idleTimeoutMs = 20000;
        readonly PushGapCache _gapCache = new PushGapCache();
        bool Alive => this != null && _lifetime != null && !_lifetime.IsCancellationRequested;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBoot()
        {
            if (Instance != null)
                return;
            var go = new GameObject("GameMeshClient");
            DontDestroyOnLoad(go);
            go.AddComponent<GameMeshClient>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            Config = GameMeshClientConfig.LoadOrCreate();
            Config.ApplyCommandLine(Environment.GetCommandLineArgs());
            LaunchArgs = GameMeshLaunchArgs.Parse(Environment.GetCommandLineArgs());
            ApplyLocalIdentity();
            _dispatcher = GameMeshMainThreadDispatcher.Ensure(gameObject);
            Connection = new GameConnection(_dispatcher);
            Connection.PushReceived += OnPush;
            Connection.StateChanged += OnTransportState;
            Mail = new MailClient(Session, (req, ct) => RequestAsync(req, null, ct));
            MoveSampler.SendHz = Config.moveSendHz;
            MoveCorrector.SmoothError = Config.smoothError;
            MoveCorrector.SnapError = Config.snapError;
            if (GetComponent<GameMeshRuntimeUi>() == null)
                gameObject.AddComponent<GameMeshRuntimeUi>();
            if (GetComponent<GameMeshWorldBinder>() == null)
                gameObject.AddComponent<GameMeshWorldBinder>();
            if (!string.IsNullOrEmpty(LaunchArgs.AutoScenario) &&
                GetComponent<GameMeshAutoScenario>() == null)
                gameObject.AddComponent<GameMeshAutoScenario>();
            _lifetime = new CancellationTokenSource();
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryReadProtocolHash();
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Instance == this)
                Instance = null;
            _lifetime?.Cancel();
            StopHeartbeat();
            if (Connection != null)
                _ = Connection.DisconnectAsync(DisconnectReason.Dispose, CancellationToken.None);
        }

        void OnApplicationPause(bool pause)
        {
            AppPaused = pause;
            if (pause)
                GameMeshLog.Info("app pause; freeze moves");
        }

        void OnApplicationFocus(bool focus)
        {
            AppPaused = !focus;
        }

        void OnApplicationQuit()
        {
            _logoutRequested = true;
            Session.AutoReconnect = false;
            _lifetime?.Cancel();
            StopHeartbeat();
            if (Connection != null)
                _ = Connection.DisconnectAsync(DisconnectReason.Dispose, CancellationToken.None);
        }

        void Update()
        {
            if (_logoutRequested || Session.SessionReplaced)
                return;
            if (Connection != null &&
                Connection.State == ConnectionState.Disconnected &&
                Session.AutoReconnect &&
                Session.HasIdentity &&
                Time.unscaledTime >= _nextReconnectAt)
            {
                _ = ReconnectAsync();
            }

            if (Mail != null && Session.HasIdentity &&
                Mail.ShouldPoll(Time.unscaledTime, _lastMailPoll, Mail.PanelOpen))
            {
                _lastMailPoll = Time.unscaledTime;
                _ = Safe(Mail.RefreshAsync(_lifetime.Token));
            }
        }

        public async Task RegisterThenLoginAsync()
        {
            var password = LaunchArgs.Password ?? "";
            try
            {
                await RegisterAsync(password).ConfigureAwait(true);
                if (Session.PlayerId == 0)
                    return;
                await LoginAsync(password).ConfigureAwait(true);
            }
            finally
            {
                LaunchArgs.ClearPassword();
            }
        }

        public async Task RegisterAsync()
        {
            await RegisterAsync(LaunchArgs.Password).ConfigureAwait(true);
        }

        public async Task RegisterAsync(string password)
        {
            if (_busy || Connection.State == ConnectionState.LoggingOut)
                return;
            _busy = true;
            BusyStage = "注册中";
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(true);
                Connection.SetLogicalState(ConnectionState.Authenticating);
                var req = new GameRequest
                {
                    Register = new RegisterReq
                    {
                        DeviceId = LaunchArgs.DeviceId ?? "",
                        DisplayName = LaunchArgs.DisplayName ?? "",
                        Password = password ?? ""
                    }
                };
                var rsp = await RequestAsync(req).ConfigureAwait(true);
                if (!rsp.Ok || rsp.Register == null || !rsp.Register.Ok)
                {
                    SetError(GameMeshErrorCode.ServerError, rsp.Register?.Message ?? rsp.Message,
                        ProtocolMapper.ExtractErrorCode(rsp));
                    Connection.SetLogicalState(ConnectionState.Connected);
                    return;
                }

                Session.PlayerId = rsp.Register.PlayerId;
                Session.DisplayName = LaunchArgs.DisplayName;
                PersistIdentity();
                SetError("", "");
                Connection.SetLogicalState(ConnectionState.Connected);
                GameMeshLog.Info($"register ok player_id={Session.PlayerId}");
            }
            catch (Exception ex)
            {
                SetError(ex);
            }
            finally
            {
                _busy = false;
                BusyStage = "";
            }
        }

        public async Task LoginAsync()
        {
            try
            {
                await LoginAsync(LaunchArgs.Password).ConfigureAwait(true);
            }
            finally
            {
                LaunchArgs.ClearPassword();
            }
        }

        public async Task LoginAsync(string password)
        {
            if (_busy || Connection.State == ConnectionState.LoggingOut)
                return;
            _busy = true;
            BusyStage = "登录中";
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(true);
                Connection.SetLogicalState(ConnectionState.Authenticating);
                var req = new GameRequest
                {
                    Login = new LoginReq
                    {
                        PlayerId = Session.PlayerId,
                        DeviceId = LaunchArgs.DeviceId ?? "",
                        ServerId = 1,
                        TtlSec = 3600,
                        KickOtherDevice = true,
                        Credential = password ?? ""
                    }
                };
                var rsp = await RequestAsync(req).ConfigureAwait(true);
                if (!AuthResponse.TryAcceptLogin(rsp, Session.PlayerId, out var playerId, out var login,
                        out var errorCode, out var message))
                {
                    SetError(errorCode, message, errorCode);
                    Connection.SetLogicalState(ConnectionState.Connected);
                    return;
                }

                Session.ApplyLogin(playerId, login.SessionId, login.Token, login.Generation,
                    LaunchArgs.DisplayName);
                if (!ApplyProfile(login.Profile))
                    await LoadSelfProfileAsync().ConfigureAwait(true);
                Session.AutoReconnect = true;
                Session.SessionReplaced = false;
                _logoutRequested = false;
                Reconnect.Reset();
                Push.Reset(0);
                Aoi.LocalPlayerId = Session.PlayerId;
                PersistIdentity();
                SetError("", "");
                Connection.SetLogicalState(ConnectionState.Authenticated);
                GameMeshLog.Info($"login ok {Session.DebugSummary()}");
                if (SceneManager.GetActiveScene().name != Config.mainSceneName)
                    SceneManager.LoadScene(Config.mainSceneName);
                else
                    _ = EnterMapAsync();
            }
            catch (Exception ex)
            {
                SetError(ex);
                if (Connection.State == ConnectionState.Authenticating)
                    Connection.SetLogicalState(ConnectionState.Connected);
            }
            finally
            {
                _busy = false;
                BusyStage = "";
            }
        }

        public async Task<LogoutResult> LogoutAsync()
        {
            var result = new LogoutResult();
            _logoutRequested = true;
            Session.AutoReconnect = false;
            Reconnect.Reset();
            StopHeartbeat();
            if (Connection != null &&
                ConnectionStateMachine.CanTransition(Connection.State, ConnectionState.LoggingOut))
                Connection.SetLogicalState(ConnectionState.LoggingOut);

            try
            {
                if (Connection != null &&
                    Connection.State != ConnectionState.Disconnected &&
                    Connection.State != ConnectionState.Closing &&
                    Session.HasIdentity)
                {
                    var req = new GameRequest
                    {
                        Logout = new LogoutReq { PlayerId = Session.PlayerId, Token = Session.Token ?? "" }
                    };
                    result.RequestSent = true;
                    try
                    {
                        var rsp = await RequestAsync(req, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                        var parsed = AuthResponse.FromLogout(rsp, true);
                        result.TopLevelOk = parsed.TopLevelOk;
                        result.BodyOk = parsed.BodyOk;
                        result.ErrorCode = parsed.ErrorCode;
                        result.Message = parsed.Message;
                        if (!result.AuthorityOk)
                            SetError(result.ErrorCode, result.Message, result.ErrorCode);
                    }
                    catch (Exception ex)
                    {
                        result.ErrorCode = ex is GameMeshException ge
                            ? ge.ErrorCode
                            : GameMeshErrorCode.ServerError;
                        result.Message = ex.Message;
                        GameMeshLog.Warn("logout request: " + ex.Message);
                    }
                }
                else
                {
                    result.ErrorCode = GameMeshErrorCode.ClientIllegalState;
                    result.Message = "logout skipped: no live session";
                }
            }
            finally
            {
                Mail.Clear();
                Aoi.Clear();
                _gapCache.Clear();
                StopHeartbeat();
                HelloOk = false;
                HeartbeatOk = false;
                Session.ClearSessionKeepIdentity();
                Session.AutoReconnect = false;
                LaunchArgs.ClearPassword();
                HasPendingSpawn = false;
                HasPendingCorrection = false;
                _enterOpId = null;
                if (Connection != null)
                {
                    await Connection.DisconnectAsync(DisconnectReason.UserLogout, CancellationToken.None)
                        .ConfigureAwait(true);
                    result.TransportDisconnected = Connection.State == ConnectionState.Disconnected;
                }
            }

            return result;
        }

        public void ClearLocalAccount()
        {
            LocalIdentityStore.Clear();
            LaunchArgs.ClearPassword();
            Session.ClearSensitive();
            Session.DeviceId = LaunchArgs.DeviceId;
            Session.DisplayName = LaunchArgs.DisplayName;
        }

        public async Task EnterMapAsync(ulong mapInstanceId = 0)
        {
            if (!Session.HasIdentity)
            {
                SetError(GameMeshErrorCode.ClientIllegalState, "not logged in");
                return;
            }

            if (Connection != null && Connection.State == ConnectionState.LoggingOut)
            {
                SetError(GameMeshErrorCode.ClientIllegalState, "logging out");
                return;
            }

            Config.ResolveMapContract();
            var mapMatched = ProtocolHandshake.TryMatchMap(_helloMaps, Config.mapTemplateId, Config.mapDataHash,
                Config.dataVersion, out _, out var mapCode);
            if (MapBlocked || !mapMatched)
            {
                MapBlocked = true;
                if (string.IsNullOrEmpty(MapBlockReason))
                    MapBlockReason = "map manifest missing or mismatch template=" + Config.mapTemplateId;
                SetError(string.IsNullOrEmpty(mapCode) ? GameMeshErrorCode.MapHashMismatch : mapCode,
                    MapBlockReason, mapCode);
                return;
            }

            try
            {
                BusyStage = "进图中";
                Connection.SetLogicalState(ConnectionState.EnteringWorld);
                if (string.IsNullOrEmpty(_enterOpId))
                    _enterOpId = Guid.NewGuid().ToString("N");
                var req = new GameRequest
                {
                    EnterMap = new EnterMapReq
                    {
                        PlayerId = Session.PlayerId,
                        RealmId = Config.realmId,
                        MapTemplateId = Config.mapTemplateId,
                        MapInstanceId = mapInstanceId,
                        MapDataVersion = Config.dataVersion,
                        MapDataSha256 = Config.mapDataHash ?? "",
                        OperationId = _enterOpId
                    }
                };
                var rsp = await RequestAsync(req).ConfigureAwait(true);
                if (!rsp.Ok || rsp.EnterMap == null || !rsp.EnterMap.Ok)
                {
                    var code = ProtocolMapper.ExtractErrorCode(rsp);
                    if (string.IsNullOrEmpty(code) &&
                        (rsp.EnterMap?.Message ?? rsp.Message ?? "").IndexOf("mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                        code = GameMeshErrorCode.MapHashMismatch;
                    SetError(string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code,
                        rsp.EnterMap?.Message ?? rsp.Message, code);
                    Connection.SetLogicalState(ConnectionState.Authenticated);
                    return;
                }

                var enter = rsp.EnterMap;
                if (!MapHashesMatch(Config.mapDataHash, Config.dataVersion, enter.MapDataSha256, enter.MapDataVersion))
                {
                    MapBlocked = true;
                    MapBlockReason =
                        $"map hash mismatch local={Config.mapDataHash} v={Config.dataVersion} server={enter.MapDataSha256} v={enter.MapDataVersion}";
                    SetError(GameMeshErrorCode.MapHashMismatch, MapBlockReason);
                    Connection.SetLogicalState(ConnectionState.Authenticated);
                    return;
                }

                Session.ApplyMap(enter.MapTemplateId, enter.MapInstanceId, enter.OwnerEpoch, enter.RouteVersion);
                Aoi.SetMapInstance(enter.MapInstanceId);
                ProtocolMapper.ApplySnapshot(Aoi, enter.AoiSnapshot, enter.MapInstanceId, false);
                if (enter.SpawnPosition != null)
                {
                    HasPendingSpawn = true;
                    PendingSpawn = ProtocolMapper.ToUnity(enter.SpawnPosition);
                    PendingSpawnYaw = enter.SpawnYaw;
                }

                if (enter.Self != null && enter.Self.PlayerId != 0)
                {
                    Session.Attributes.Hp = enter.Self.Hp;
                    Session.Attributes.MaxHp = enter.Self.MaxHp;
                    if (!string.IsNullOrEmpty(enter.Self.PlayerName))
                        Session.Attributes.Name = enter.Self.PlayerName;
                }

                MapBlocked = false;
                MapBlockReason = "";
                _enterOpId = null;
                Connection.SetLogicalState(ConnectionState.InWorld);
                if (string.IsNullOrEmpty(LaunchArgs.AutoScenario))
                    _ = PingMapAsync();
            }
            catch (Exception ex)
            {
                SetError(ex);
            }
            finally
            {
                BusyStage = "";
            }
        }

        public async Task<bool> SendMoveAsync(Vector3 position, float yaw, CancellationToken ct)
        {
            if (MovesFrozen)
            {
                GameMeshLog.Warn("move skipped frozen state=" +
                                 (Connection != null ? Connection.State.ToString() : "null") +
                                 " paused=" + AppPaused + " gap=" + Push.HasGap);
                return false;
            }
            var req = new GameRequest
            {
                Move = new MoveReq
                {
                    PlayerId = Session.PlayerId,
                    MapInstanceId = Session.MapInstanceId,
                    Position = ProtocolMapper.ToVec3(position),
                    Yaw = yaw,
                    ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            };
            try
            {
                var pending = RequestAsync(req, null, ct);
                MoveSampler.MarkSent(position, yaw, Time.unscaledTime);
                var rsp = await pending.ConfigureAwait(true);
                if (Alive)
                    ApplyMoveRsp(rsp);
                return rsp != null && rsp.Ok && (rsp.Move == null || rsp.Move.Ok);
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn("move failed " + ex.Message);
                return false;
            }
            finally
            {
                MoveSampler.MarkCompleted();
            }
        }

        public async Task<GameResponse> RequestAsync(GameRequest request, TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            if (request == null)
                throw new GameMeshException(GameMeshErrorCode.ClientProtocol, "null request");
            if (Connection != null && Connection.State == ConnectionState.LoggingOut &&
                request.BodyCase != GameRequest.BodyOneofCase.Logout)
            {
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState,
                    "logging out; business requests rejected");
            }
            if (request.BodyCase != GameRequest.BodyOneofCase.ClientHello && !HelloOk)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, "Hello required before business requests");
            if (!string.IsNullOrEmpty(Session.Token) &&
                request.BodyCase != GameRequest.BodyOneofCase.ClientHello)
                request.SessionToken = Session.Token;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct))
            {
                var started = Time.realtimeSinceStartup;
                var rsp = await Connection.RequestAsync(
                    request,
                    timeout ?? TimeSpan.FromMilliseconds(Config.requestTimeoutMs),
                    linked.Token).ConfigureAwait(true);
                LastRttMs = Mathf.Max(0, (int)((Time.realtimeSinceStartup - started) * 1000f));
                var code = ProtocolMapper.ExtractErrorCode(rsp);
                var trace = ProtocolMapper.ShortTraceId(rsp);
                GameMeshLog.Info($"rsp seq={rsp.Seq} type={rsp.BodyCase} ok={rsp.Ok} code={code} rtt_ms={LastRttMs}");
                if (GameErrorCatalog.IsSessionReplaced(code))
                    HandleSessionReplaced(code);
                else if (!rsp.Ok &&
                         request.BodyCase != GameRequest.BodyOneofCase.Heartbeat &&
                         request.BodyCase != GameRequest.BodyOneofCase.MapPing)
                    SetError(string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code, rsp.Message, code, trace);
                return rsp;
            }
        }

        public static bool MapHashesMatch(string localHash, uint localVersion, string serverHash, ulong serverVersion)
        {
            if (!string.IsNullOrEmpty(localHash) && !string.IsNullOrEmpty(serverHash) &&
                !string.Equals(localHash.Trim(), serverHash.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (localVersion != 0 && serverVersion != 0 && localVersion != serverVersion)
                return false;
            return true;
        }

        async Task EnsureConnectedAsync()
        {
            if (Connection != null && Connection.State == ConnectionState.LoggingOut)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, "logging out");
            if (HelloOk &&
                (Connection.State == ConnectionState.Connected ||
                 Connection.State == ConnectionState.Authenticating ||
                 Connection.State == ConnectionState.Authenticated ||
                 Connection.State == ConnectionState.EnteringWorld ||
                 Connection.State == ConnectionState.InWorld ||
                 Connection.State == ConnectionState.Resyncing))
                return;
            if (Connection.State == ConnectionState.Disconnected ||
                Connection.State == ConnectionState.Reconnecting ||
                Connection.State == ConnectionState.Closing)
            {
                HelloOk = false;
                HeartbeatOk = false;
                using (var cts = new CancellationTokenSource(Config.connectTimeoutMs))
                {
                    await Connection.ConnectAsync(Config.host, Config.port, cts.Token).ConfigureAwait(true);
                }
            }

            await HandshakeAsync().ConfigureAwait(true);
        }

        async Task HandshakeAsync()
        {
            if (HelloOk && Connection.State != ConnectionState.Handshaking)
                return;
            if (_helloInFlight)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, "hello already in flight");
            _helloInFlight = true;
            var generation = Connection.Generation;
            try
            {
                if (Connection.State == ConnectionState.Handshaking ||
                    Connection.State == ConnectionState.Connecting)
                    Connection.SetLogicalState(ConnectionState.Handshaking);
                var hello = new ClientHelloReq
                {
                    ProtocolVersion = ProtocolHandshake.ProtocolVersion,
                    SchemaSha256 = ProtocolSchemaSha256 ?? "",
                    ClientVersion = Application.version ?? "luna",
                    Platform = Application.platform.ToString(),
                    BuildChannel = string.IsNullOrEmpty(LaunchArgs.AutoScenario) ? "dev" : "e2e"
                };
                foreach (var cap in ProtocolHandshake.ClientCapabilities)
                    hello.Capabilities.Add(cap);
                var rsp = await RequestAsync(new GameRequest { ClientHello = hello },
                    TimeSpan.FromMilliseconds(Config.helloTimeoutMs)).ConfigureAwait(true);
                if (generation != Connection.Generation)
                    throw new GameMeshException(GameMeshErrorCode.ClientDisconnected, "hello generation changed");
                var helloRsp = rsp.ServerHello;
                if (!ProtocolHandshake.TryValidate(helloRsp, ProtocolSchemaSha256,
                        ProtocolHandshake.ProtocolVersion, out var code, out var message))
                {
                    SetError(code, message, code, ProtocolMapper.ShortTraceId(rsp));
                    HelloOk = false;
                    await Connection.DisconnectAsync(DisconnectReason.ProtocolError, CancellationToken.None)
                        .ConfigureAwait(true);
                    throw new GameMeshException(code, message);
                }

                HelloOk = true;
                if (helloRsp.HeartbeatIntervalMs != 0)
                    _heartbeatIntervalMs = helloRsp.HeartbeatIntervalMs;
                if (helloRsp.IdleTimeoutMs != 0)
                    _idleTimeoutMs = helloRsp.IdleTimeoutMs;
                HeartbeatClock.OnReply(HeartbeatClock.MonotonicMs, HeartbeatClock.MonotonicMs,
                    helloRsp.ServerTimeMs, 0);
                _helloMaps.Clear();
                if (helloRsp.Maps != null)
                {
                    foreach (var map in helloRsp.Maps)
                        _helloMaps.Add(map);
                }

                Config.ResolveMapContract();
                if (!ProtocolHandshake.TryMatchMap(_helloMaps, Config.mapTemplateId, Config.mapDataHash,
                        Config.dataVersion, out _, out var mapCode))
                {
                    MapBlocked = true;
                    MapBlockReason = "hello map manifest missing or mismatch template=" + Config.mapTemplateId;
                    SetError(mapCode, MapBlockReason, mapCode);
                }
                else
                {
                    MapBlocked = false;
                    MapBlockReason = "";
                }

                Connection.SetLogicalState(ConnectionState.Connected);
                StartHeartbeat(generation);
                GameMeshLog.Info($"hello ok protocol={helloRsp.ProtocolVersion} hb={_heartbeatIntervalMs}ms");
            }
            finally
            {
                _helloInFlight = false;
            }
        }

        async Task ReconnectAsync()
        {
            if (!Reconnect.TryBegin(Time.unscaledTime, Config.reconnectMaxAttempts, Config.reconnectMaxTotalMs,
                    out var fail))
            {
                if (fail == "in-flight")
                    return;
                Session.AutoReconnect = false;
                SetError(GameMeshErrorCode.ClientTimeout, fail);
                return;
            }

            var backoff = Mathf.Min(8f, 0.4f * Mathf.Pow(2f, Reconnect.Attempts - 1));
            backoff += UnityEngine.Random.Range(0f, 0.3f);
            _nextReconnectAt = Time.unscaledTime + backoff;
            var connGen = Connection.Generation;
            try
            {
                Connection.SetLogicalState(ConnectionState.Reconnecting);
                await Connection.DisconnectAsync(DisconnectReason.Reconnect, CancellationToken.None)
                    .ConfigureAwait(true);
                await EnsureConnectedAsync().ConfigureAwait(true);
                var req = new GameRequest
                {
                    Reconnect = new ReconnectReq
                    {
                        PlayerId = Session.PlayerId,
                        SessionId = Session.SessionId ?? "",
                        ReconnectTicket = Session.Token ?? "",
                        LastServerSeq = Session.LastServerSeq
                    }
                };
                var rsp = await RequestAsync(req).ConfigureAwait(true);
                if (!rsp.Ok || rsp.Reconnect == null || !rsp.Reconnect.Ok)
                {
                    var code = ProtocolMapper.ExtractErrorCode(rsp);
                    if (GameErrorCatalog.IsSessionReplaced(code) || code == "ERR_SESSION_EXPIRED")
                    {
                        HandleSessionReplaced(string.IsNullOrEmpty(code)
                            ? GameMeshErrorCode.SessionReplaced
                            : code);
                        Reconnect.EndFailure();
                        return;
                    }

                    SetError(GameMeshErrorCode.ServerError, rsp.Reconnect?.Message ?? rsp.Message, code);
                    Reconnect.EndFailure();
                    return;
                }

                Session.ApplyReconnect(rsp.Reconnect.SessionId, rsp.Reconnect.Token, rsp.Reconnect.Generation);
                if (rsp.Reconnect.NeedFullSnapshot)
                {
                    Connection.SetLogicalState(ConnectionState.Resyncing);
                    if (!await RequestWorldSnapshotAsync().ConfigureAwait(true))
                    {
                        Session.AutoReconnect = false;
                        Connection.SetLogicalState(ConnectionState.Authenticated);
                        Reconnect.EndFailure();
                        return;
                    }
                }
                else if (Session.MapInstanceId != 0)
                {
                    Connection.SetLogicalState(ConnectionState.InWorld);
                }
                else
                {
                    Connection.SetLogicalState(ConnectionState.Authenticated);
                }

                Reconnect.EndSuccess();
            }
            catch (Exception ex)
            {
                if (connGen != Connection.Generation)
                    return;
                SetError(ex);
                Reconnect.EndFailure();
            }
        }

        async Task LoadSelfProfileAsync()
        {
            var rsp = await RequestAsync(new GameRequest
            {
                GetSelfProfile = new GetSelfProfileReq { PlayerId = Session.PlayerId }
            }).ConfigureAwait(true);
            if (!rsp.Ok || rsp.GetSelfProfile == null || !rsp.GetSelfProfile.Ok ||
                rsp.GetSelfProfile.Profile == null)
            {
                SetError(GameMeshErrorCode.ServerError,
                    rsp.GetSelfProfile?.Message ?? rsp.Message ?? "GetSelfProfile failed",
                    ProtocolMapper.ExtractErrorCode(rsp));
                return;
            }

            ApplyProfile(rsp.GetSelfProfile.Profile);
        }

        bool ApplyProfile(PlayerAttributes profile)
        {
            var mapped = ProtocolMapper.ToAttributes(profile);
            if (mapped == null)
                return false;
            if (mapped.PlayerId != 0)
                Session.PlayerId = mapped.PlayerId;
            Session.Attributes = mapped;
            if (!string.IsNullOrEmpty(mapped.Name))
                Session.DisplayName = mapped.Name;
            return true;
        }

        void ApplyMoveRsp(GameResponse rsp)
        {
            var move = rsp?.Move;
            if (move == null)
                return;
            if (move.StateSeq != 0 && _lastMoveStateSeq != 0 && move.StateSeq < _lastMoveStateSeq)
                return;
            if (move.StateSeq != 0)
                _lastMoveStateSeq = move.StateSeq;
            if (move.Position == null)
                return;
            var authority = ProtocolMapper.ToUnity(move.Position);
            var local = HasPendingCorrection ? PendingCorrection : authority;
            var corrected = MoveCorrector.Apply(local, authority, Time.unscaledTime, out _);
            HasPendingCorrection = true;
            PendingCorrection = corrected;
            PendingCorrectionYaw = move.Yaw;
            if (!move.Ok)
            {
                var code = string.IsNullOrEmpty(move.ErrorCode) ? GameMeshErrorCode.ServerError : move.ErrorCode;
                SetError(code, move.Message, code);
            }
        }

        async Task PingMapAsync()
        {
            try
            {
                var rsp = await RequestAsync(new GameRequest
                {
                    MapPing = new MapPingReq
                    {
                        PlayerId = Session.PlayerId,
                        MapInstanceId = Session.MapInstanceId
                    }
                }).ConfigureAwait(true);
                if (rsp.MapPing != null)
                    LastPlayerCount = rsp.MapPing.PlayerCount;
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn(ex.Message);
            }
        }

        void OnPush(GameResponse response)
        {
            try
            {
                if (_lifetime == null || _lifetime.IsCancellationRequested)
                    return;
                var inner = response;
                ulong serverSeq = 0;
                var reliable = false;
                if (response.BodyCase == GameResponse.BodyOneofCase.ServerPush && response.ServerPush != null)
                {
                    serverSeq = response.ServerPush.ServerSeq;
                    reliable = response.ServerPush.Reliable;
                    var decision = Push.Observe(serverSeq);
                    if (decision == PushReliability.Decision.Duplicate)
                    {
                        if (reliable)
                            _ = AckPushAsync(serverSeq);
                        return;
                    }

                    GameResponse parsed;
                    try
                    {
                        parsed = GameResponse.Parser.ParseFrom(response.ServerPush.Payload);
                    }
                    catch (Exception ex)
                    {
                        SetError(new GameMeshException(GameMeshErrorCode.ClientProtocol, "inner push parse", ex));
                        return;
                    }

                    if (decision == PushReliability.Decision.Gap)
                    {
                        GameMeshLog.Warn($"push gap expected={Push.ExpectedNext} got={serverSeq}");
                        Connection.SetLogicalState(ConnectionState.Resyncing);
                        if (!_gapCache.TryBuffer(serverSeq, parsed))
                        {
                            SetError(GameMeshErrorCode.ClientProtocol, "push gap cache overflow");
                            _ = Connection.DisconnectAsync(DisconnectReason.ProtocolError, CancellationToken.None);
                            return;
                        }

                        _ = RequestWorldSnapshotAsync();
                        return;
                    }

                    ApplyInnerPush(parsed);
                    Push.MarkApplied(serverSeq);
                    Session.LastServerSeq = Push.LastAppliedServerSeq;
                    if (reliable)
                        _ = AckPushAsync(serverSeq);
                    DrainGapCache();
                    return;
                }

                ApplyInnerPush(inner);
            }
            catch (Exception ex)
            {
                GameMeshLog.Error(ex.ToString());
            }
        }

        internal bool ApplyInnerPush(GameResponse inner)
        {
            if (inner == null)
                return false;
            if (inner.SessionReplaced != null)
            {
                var notify = inner.SessionReplaced;
                var code = string.IsNullOrEmpty(notify.ReasonCode)
                    ? GameMeshErrorCode.SessionReplaced
                    : notify.ReasonCode;
                HandleSessionReplaced(code, notify.Message);
                return true;
            }
            if (inner.MailboxChanged != null || inner.MailboxSummary != null || inner.MailList != null)
                Mail.NotifyMailboxChanged(Time.unscaledTime);
            if (inner.AoiDelta != null)
            {
                if (Session.MapInstanceId != 0 && inner.AoiDelta.MapInstanceId != 0 &&
                    inner.AoiDelta.MapInstanceId != Session.MapInstanceId)
                {
                    GameMeshLog.Warn("drop aoi from other map");
                    return true;
                }

                ProtocolMapper.ApplyAoiDelta(Aoi, inner.AoiDelta);
                return true;
            }

            if (inner.FullSnapshot != null)
                return ApplyValidatedSnapshot(inner.FullSnapshot);

            return true;
        }

        void DrainGapCache()
        {
            while (_gapCache.TryTake(Push.ExpectedNext, out var buffered))
            {
                ApplyInnerPush(buffered);
                Push.MarkApplied(Push.ExpectedNext);
                Session.LastServerSeq = Push.LastAppliedServerSeq;
            }

            if (!Push.HasGap && Connection.State == ConnectionState.Resyncing && Session.MapInstanceId != 0)
                Connection.SetLogicalState(ConnectionState.InWorld);
        }

        async Task AckPushAsync(ulong serverSeq)
        {
            try
            {
                await RequestAsync(new GameRequest
                {
                    PushAck = new PushAckReq
                    {
                        PlayerId = Session.PlayerId,
                        AckServerSeq = serverSeq,
                        SessionId = Session.SessionId ?? "",
                        FenceToken = Session.Token ?? "",
                        Generation = Session.Generation
                    }
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn("push ack failed " + ex.Message);
            }
        }

        void OnTransportState(ConnectionState state)
        {
            GameMeshLog.Info("state=" + state);
            if (state == ConnectionState.Disconnected || state == ConnectionState.Closing)
            {
                HelloOk = false;
                HeartbeatOk = false;
                StopHeartbeat();
            }
        }

        public async Task RespawnAsync()
        {
            if (!Session.HasIdentity)
            {
                SetError(GameMeshErrorCode.ClientIllegalState, "not logged in");
                return;
            }

            if (_respawnInFlight)
                return;
            _respawnInFlight = true;
            if (string.IsNullOrEmpty(_respawnOpId))
                _respawnOpId = Guid.NewGuid().ToString("N");
            try
            {
                var rsp = await RequestAsync(new GameRequest
                {
                    Respawn = new RespawnReq
                    {
                        PlayerId = Session.PlayerId,
                        MapInstanceId = Session.MapInstanceId,
                        OperationId = _respawnOpId
                    }
                }).ConfigureAwait(true);
                var body = rsp.Respawn;
                if (!rsp.Ok || body == null || !body.Ok)
                {
                    SetError(string.IsNullOrEmpty(ProtocolMapper.ExtractErrorCode(rsp))
                            ? GameMeshErrorCode.ServerError
                            : ProtocolMapper.ExtractErrorCode(rsp),
                        body?.Message ?? rsp.Message, ProtocolMapper.ExtractErrorCode(rsp));
                    return;
                }

                if (body.Self != null && body.Self.PlayerId != 0)
                {
                    Session.Attributes.Hp = body.Self.Hp;
                    Session.Attributes.MaxHp = body.Self.MaxHp;
                    HasPendingSpawn = true;
                    PendingSpawn = ProtocolMapper.ToUnity(body.Self.Position);
                    PendingSpawnYaw = body.Self.Yaw;
                }

                Session.Attributes.LifeState = string.IsNullOrEmpty(body.LifeState) ? "ALIVE" : body.LifeState;
                _respawnOpId = null;
            }
            catch (Exception ex)
            {
                SetError(ex);
            }
            finally
            {
                _respawnInFlight = false;
            }
        }

        public void HandleSessionReplaced(string code, string message = "")
        {
            Session.SessionReplaced = true;
            Session.AutoReconnect = false;
            Reconnect.Reset();
            StopHeartbeat();
            HelloOk = false;
            HeartbeatOk = false;
            Mail.Clear();
            Aoi.Clear();
            _gapCache.Clear();
            HasPendingSpawn = false;
            HasPendingCorrection = false;
            Session.ClearSessionKeepIdentity();
            var text = string.IsNullOrEmpty(message) ? "账号已在其他设备登录" : message;
            SetError(string.IsNullOrEmpty(code) ? GameMeshErrorCode.SessionReplaced : code, text);
            if (Connection != null && Connection.State != ConnectionState.Disconnected)
                _ = Connection.DisconnectAsync(DisconnectReason.ClientRequest, CancellationToken.None);
        }

        public async Task<bool> RequestWorldSnapshotAsync()
        {
            if (_snapshotInFlight)
            {
                var waitUntil = Time.unscaledTime + 8f;
                while (_snapshotInFlight && Time.unscaledTime < waitUntil)
                    await Task.Yield();
                return Session.MapInstanceId != 0 && Connection.State == ConnectionState.InWorld;
            }
            _snapshotInFlight = true;
            var connGen = Connection != null ? Connection.Generation : 0;
            try
            {
                Connection.SetLogicalState(ConnectionState.Resyncing);
                var rsp = await RequestAsync(new GameRequest
                {
                    WorldSnapshot = new WorldSnapshotReq
                    {
                        PlayerId = Session.PlayerId,
                        LastAppliedServerSeq = Push.LastAppliedServerSeq
                    }
                }).ConfigureAwait(true);
                if (connGen != Connection.Generation)
                    return false;
                var snap = rsp.FullSnapshot;
                if (snap == null || !ApplyValidatedSnapshot(snap))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                if (connGen == Connection.Generation)
                    SetError(ex);
                if (Session.MapInstanceId != 0)
                    Connection.SetLogicalState(ConnectionState.InWorld);
                else
                    Connection.SetLogicalState(ConnectionState.Authenticated);
                return false;
            }
            finally
            {
                _snapshotInFlight = false;
            }
        }

        bool ApplyValidatedSnapshot(FullStateSnapshotRsp snap)
        {
            if (!WorldSnapshotApplier.TryBuild(snap, Session.PlayerId, Session.MapInstanceId,
                    Session.SnapshotVersion, out var model, out var code, out var message))
            {
                SetError(code, message, code);
                return false;
            }

            WorldSnapshotApplier.Apply(Session, Aoi, Push, _gapCache, model);
            if (model.Self != null)
            {
                HasPendingSpawn = true;
                PendingSpawn = new Vector3(model.Self.X, model.Self.Y, model.Self.Z);
                PendingSpawnYaw = model.Self.Yaw;
            }

            DrainGapCache();
            if (Session.MapInstanceId != 0)
                Connection.SetLogicalState(ConnectionState.InWorld);
            GameMeshLog.Info($"full snapshot player={Session.PlayerId} seq={Session.LastServerSeq} ver={Session.SnapshotVersion}");
            return true;
        }

        void StartHeartbeat(int connectionGeneration)
        {
            StopHeartbeat();
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var ct = _heartbeatCts.Token;
            _heartbeatMisses = 0;
            _ = HeartbeatLoopAsync(connectionGeneration, ct);
        }

        void StopHeartbeat()
        {
            try { _heartbeatCts?.Cancel(); } catch { /* ignore */ }
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
        }

        async Task HeartbeatLoopAsync(int connectionGeneration, CancellationToken ct)
        {
            var interval = Math.Max(500, (int)_heartbeatIntervalMs);
            while (!ct.IsCancellationRequested && Alive)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(true);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (!Alive || ct.IsCancellationRequested || Connection.Generation != connectionGeneration)
                    return;
                if (HeartbeatClock.IdleTimedOut((int)_idleTimeoutMs))
                {
                    GameMeshLog.Warn("heartbeat idle timeout");
                    _ = BeginReconnectFromHeartbeat();
                    return;
                }

                var sendMono = HeartbeatClock.MonotonicMs;
                try
                {
                    var rsp = await RequestAsync(new GameRequest
                    {
                        Heartbeat = new HeartbeatReq
                        {
                            ClientMonotonicMs = sendMono,
                            LastServerSeq = Session.LastServerSeq,
                            EchoMs = sendMono
                        }
                    }, TimeSpan.FromMilliseconds(Math.Max(1000, Config.heartbeatTimeoutMs)), ct)
                        .ConfigureAwait(true);
                    if (Connection.Generation != connectionGeneration)
                        return;
                    var hb = rsp.Heartbeat;
                    if (rsp.Ok && hb != null && hb.Ok)
                    {
                        HeartbeatClock.OnReply(sendMono, HeartbeatClock.MonotonicMs, hb.ServerTimeMs,
                            (int)hb.JitterHintMs);
                        LastRttMs = HeartbeatClock.SmoothedRttMs;
                        HeartbeatOk = true;
                        _heartbeatMisses = 0;
                    }
                    else
                    {
                        _heartbeatMisses++;
                    }
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || Connection.Generation != connectionGeneration)
                        return;
                    GameMeshLog.Warn("heartbeat failed " + ex.Message);
                    _heartbeatMisses++;
                }

                if (_heartbeatMisses >= 2)
                {
                    _ = BeginReconnectFromHeartbeat();
                    return;
                }
            }
        }

        Task BeginReconnectFromHeartbeat()
        {
            if (Session.SessionReplaced || _logoutRequested)
                return Task.CompletedTask;
            Session.AutoReconnect = true;
            if (Connection.State != ConnectionState.Disconnected &&
                Connection.State != ConnectionState.Reconnecting)
                Connection.SetLogicalState(ConnectionState.Reconnecting);
            return ReconnectAsync();
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == Config.mainSceneName &&
                Connection.State == ConnectionState.Authenticated)
            {
                _ = EnterMapAsync();
            }
        }

        void ApplyLocalIdentity()
        {
            if (!string.IsNullOrEmpty(LaunchArgs.AutoScenario))
            {
                Session.DeviceId = LaunchArgs.DeviceId;
                Session.DisplayName = LaunchArgs.DisplayName;
                return;
            }

            var stored = LocalIdentityStore.Load();
            if (string.IsNullOrEmpty(LaunchArgs.DeviceId) || LaunchArgs.DeviceId == "unity-dev")
            {
                if (!string.IsNullOrEmpty(stored.DeviceId))
                    LaunchArgs.DeviceId = stored.DeviceId;
            }

            Session.DeviceId = LaunchArgs.DeviceId;
            if (string.IsNullOrEmpty(LaunchArgs.DisplayName) || LaunchArgs.DisplayName == "Luna")
            {
                if (!string.IsNullOrEmpty(stored.DisplayName))
                    LaunchArgs.DisplayName = stored.DisplayName;
            }

            Session.DisplayName = LaunchArgs.DisplayName;
            if (Session.PlayerId == 0 && stored.PlayerId != 0)
                Session.PlayerId = stored.PlayerId;
            if (!string.IsNullOrEmpty(stored.Host) && Config.host == "127.0.0.1")
                Config.host = stored.Host;
            if (stored.Port > 0 && Config.port == 8081)
                Config.port = stored.Port;
        }

        void PersistIdentity()
        {
            if (!string.IsNullOrEmpty(LaunchArgs.AutoScenario))
                return;
            LocalIdentityStore.Save(LaunchArgs.DeviceId, Session.PlayerId, Session.DisplayName, Config.host,
                Config.port);
        }

        internal static string FindProtocolManifestPath()
        {
            var candidates = new[]
            {
                System.IO.Path.Combine(Application.dataPath, "GameMesh", "Protocol", "protocol_manifest.json"),
                System.IO.Path.Combine(Application.streamingAssetsPath, "GameMesh", "protocol_manifest.json")
            };
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && System.IO.File.Exists(candidate))
                    return candidate;
            }

            return "";
        }

        void TryReadProtocolHash()
        {
            try
            {
                var path = FindProtocolManifestPath();
                if (string.IsNullOrEmpty(path))
                    return;
                var json = System.IO.File.ReadAllText(path);
                ProtocolSchemaSha256 = ReadJsonString(json, "schema_sha256");
                if (!string.IsNullOrEmpty(ProtocolSchemaSha256))
                    ProtocolSchemaShort = ProtocolSchemaSha256.Length >= 8
                        ? ProtocolSchemaSha256.Substring(0, 8)
                        : ProtocolSchemaSha256;
                var ver = ReadJsonInt(json, "protocol_version");
                if (ver > 0)
                    ProtocolVersion = ver;
            }
            catch
            {
                /* ignore */
            }
        }

        static string ReadJsonString(string json, string key)
        {
            var token = "\"" + key + "\"";
            var i = json.IndexOf(token, StringComparison.Ordinal);
            if (i < 0)
                return "";
            var start = json.IndexOf('"', i + token.Length);
            start = json.IndexOf('"', start + 1);
            var colon = json.IndexOf(':', i);
            if (colon < 0)
                return "";
            var q = json.IndexOf('"', colon + 1);
            var q2 = q >= 0 ? json.IndexOf('"', q + 1) : -1;
            if (q >= 0 && q2 > q)
                return json.Substring(q + 1, q2 - q - 1);
            return "";
        }

        static int ReadJsonInt(string json, string key)
        {
            var token = "\"" + key + "\"";
            var i = json.IndexOf(token, StringComparison.Ordinal);
            if (i < 0)
                return 0;
            var colon = json.IndexOf(':', i);
            if (colon < 0)
                return 0;
            var end = colon + 1;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == ' '))
                end++;
            return int.TryParse(json.Substring(colon + 1, end - colon - 1).Trim(), out var v) ? v : 0;
        }

        void SetError(Exception ex)
        {
            var code = ex is GameMeshException ge ? ge.ErrorCode : GameMeshErrorCode.ServerError;
            SetError(code, ex.Message, code);
        }

        void SetError(string code, string message, string serverCode = "", string traceShort = "")
        {
            if (!Alive)
                return;
            var resolved = !string.IsNullOrEmpty(serverCode) ? serverCode : code;
            LastErrorCode = resolved ?? "";
            LastError = GameMeshLog.Redact(message ?? "");
            LastErrorUi = GameErrorCatalog.FormatUi(LastErrorCode, LastError, traceShort);
            if (!string.IsNullOrEmpty(LastError))
                GameMeshLog.Warn(LastErrorCode + " " + LastError);
        }

        static async Task Safe(Task task)
        {
            try { await task.ConfigureAwait(true); }
            catch (Exception ex) { GameMeshLog.Warn(ex.Message); }
        }
    }
}
