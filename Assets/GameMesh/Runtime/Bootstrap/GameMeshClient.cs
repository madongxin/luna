using System;
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
            Reconnect.InFlight;
        public string SchemaHashShort =>
            string.IsNullOrEmpty(Config?.mapDataHash) ? "" : Config.mapDataHash.Substring(0, 8);
        public string ProtocolSchemaShort { get; private set; } = "";
        public string ServerBlockedNotes { get; private set; } =
            "BLOCKED BY SERVER: ClientHelloReq / Heartbeat / WorldSnapshotReq / RespawnReq";

        GameMeshMainThreadDispatcher _dispatcher;
        CancellationTokenSource _lifetime;
        bool _logoutRequested;
        float _nextReconnectAt;
        float _lastMailPoll;
        bool _busy;
        string _enterOpId;
        ulong _lastMoveStateSeq;

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
            if (Connection != null)
                _ = Connection.DisconnectAsync(DisconnectReason.Dispose, CancellationToken.None);
        }

        void Update()
        {
            if (_logoutRequested)
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

        public async Task RegisterAsync()
        {
            if (_busy)
                return;
            _busy = true;
            BusyStage = "注册中";
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(true);
                Connection.SetLogicalState(ConnectionState.Authenticating);
                var password = LaunchArgs.Password;
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
                LaunchArgs.ClearPassword();
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
            if (_busy)
                return;
            _busy = true;
            BusyStage = "登录中";
            try
            {
                await EnsureConnectedAsync().ConfigureAwait(true);
                Connection.SetLogicalState(ConnectionState.Authenticating);
                var password = LaunchArgs.Password;
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
                LaunchArgs.ClearPassword();
                if (!rsp.Ok || rsp.Login == null || !rsp.Login.Ok)
                {
                    SetError(GameMeshErrorCode.ServerError, rsp.Login?.Message ?? rsp.Message,
                        ProtocolMapper.ExtractErrorCode(rsp));
                    Connection.SetLogicalState(ConnectionState.Connected);
                    return;
                }

                var login = rsp.Login;
                var playerId = Session.PlayerId;
                if (login.Profile != null && login.Profile.PlayerId != 0)
                    playerId = login.Profile.PlayerId;
                Session.ApplyLogin(playerId, login.SessionId, login.Token, login.Generation,
                    LaunchArgs.DisplayName);
                if (!ApplyProfile(login.Profile))
                    await LoadSelfProfileAsync().ConfigureAwait(true);
                if (Session.PlayerId == 0)
                    GameMeshLog.Warn("login ok but player_id was 0; enter it from register result");
                Session.AutoReconnect = true;
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
            }
            finally
            {
                _busy = false;
                BusyStage = "";
            }
        }

        public async Task LogoutAsync()
        {
            _logoutRequested = true;
            Session.AutoReconnect = false;
            Reconnect.Reset();
            try
            {
                if (Connection.State != ConnectionState.Disconnected && Session.HasIdentity)
                {
                    var req = new GameRequest
                    {
                        Logout = new LogoutReq { PlayerId = Session.PlayerId, Token = Session.Token ?? "" }
                    };
                    try
                    {
                        await RequestAsync(req, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        GameMeshLog.Warn("logout request: " + ex.Message);
                    }
                }
            }
            finally
            {
                Mail.Clear();
                Aoi.Clear();
                Session.ClearSensitive();
                LaunchArgs.ClearPassword();
                HasPendingSpawn = false;
                HasPendingCorrection = false;
                _enterOpId = null;
                await Connection.DisconnectAsync(DisconnectReason.UserLogout, CancellationToken.None)
                    .ConfigureAwait(true);
            }
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

            try
            {
                BusyStage = "进图中";
                Connection.SetLogicalState(ConnectionState.EnteringWorld);
                Config.ResolveMapContract();
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
                ProtocolMapper.ApplySnapshot(Aoi, enter.AoiSnapshot, enter.MapInstanceId);
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

        public async Task SendMoveAsync(Vector3 position, float yaw, CancellationToken ct)
        {
            if (MovesFrozen)
                return;
            var req = new GameRequest
            {
                Move = new MoveReq
                {
                    PlayerId = Session.PlayerId,
                    MapInstanceId = Session.MapInstanceId,
                    Position = ProtocolMapper.ToVec3(position),
                    Yaw = yaw,
                    ClientTimeMs = (long)(Time.unscaledTime * 1000f)
                }
            };
            MoveSampler.MarkSent(position, yaw, Time.unscaledTime);
            try
            {
                var rsp = await RequestAsync(req, null, ct).ConfigureAwait(true);
                ApplyMoveRsp(rsp);
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn("move failed " + ex.Message);
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
            if (!string.IsNullOrEmpty(Session.Token))
                request.SessionToken = Session.Token;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct))
            {
                var rsp = await Connection.RequestAsync(
                    request,
                    timeout ?? TimeSpan.FromMilliseconds(Config.requestTimeoutMs),
                    linked.Token).ConfigureAwait(true);
                if (!rsp.Ok)
                {
                    var code = ProtocolMapper.ExtractErrorCode(rsp);
                    SetError(string.IsNullOrEmpty(code) ? GameMeshErrorCode.ServerError : code, rsp.Message, code);
                }

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
            if (Connection.State == ConnectionState.Connected ||
                Connection.State == ConnectionState.Authenticating ||
                Connection.State == ConnectionState.Authenticated ||
                Connection.State == ConnectionState.EnteringWorld ||
                Connection.State == ConnectionState.InWorld ||
                Connection.State == ConnectionState.Resyncing)
                return;
            using (var cts = new CancellationTokenSource(Config.connectTimeoutMs))
            {
                await Connection.ConnectAsync(Config.host, Config.port, cts.Token).ConfigureAwait(true);
            }

            GameMeshLog.Warn(ServerBlockedNotes);
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
            var generation = Session.Generation;
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
                    SetError(GameMeshErrorCode.ServerError, rsp.Reconnect?.Message ?? rsp.Message,
                        ProtocolMapper.ExtractErrorCode(rsp));
                    Reconnect.EndFailure();
                    return;
                }

                Session.ApplyReconnect(rsp.Reconnect.SessionId, rsp.Reconnect.Token, rsp.Reconnect.Generation);
                if (rsp.Reconnect.NeedFullSnapshot)
                {
                    GameMeshLog.Warn("reconnect needs full snapshot; WorldSnapshotReq " +
                                     GameMeshErrorCode.SnapshotBlocked);
                    Connection.SetLogicalState(ConnectionState.Resyncing);
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
                if (generation != Session.Generation)
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

                    if (decision == PushReliability.Decision.Gap)
                    {
                        GameMeshLog.Warn($"push gap expected={Push.ExpectedNext} got={serverSeq}");
                        Connection.SetLogicalState(ConnectionState.Resyncing);
                        GameMeshLog.Warn("WorldSnapshotReq " + GameMeshErrorCode.SnapshotBlocked);
                        return;
                    }

                    try
                    {
                        inner = GameResponse.Parser.ParseFrom(response.ServerPush.Payload);
                    }
                    catch (Exception ex)
                    {
                        SetError(new GameMeshException(GameMeshErrorCode.ClientProtocol, "inner push parse", ex));
                        return;
                    }

                    if (!ApplyInnerPush(inner))
                        return;
                    Push.MarkApplied(serverSeq);
                    Session.LastServerSeq = Push.LastAppliedServerSeq;
                    if (reliable)
                        _ = AckPushAsync(serverSeq);
                    return;
                }

                ApplyInnerPush(inner);
            }
            catch (Exception ex)
            {
                GameMeshLog.Error(ex.ToString());
            }
        }

        bool ApplyInnerPush(GameResponse inner)
        {
            if (inner == null)
                return false;
            if (inner.MailboxChanged != null || inner.MailboxSummary != null || inner.MailList != null)
                Mail.NotifyMailboxChanged(Time.unscaledTime);
            if (inner.AoiDelta != null)
            {
                if (Session.MapInstanceId != 0 && inner.AoiDelta.MapInstanceId != 0 &&
                    inner.AoiDelta.MapInstanceId != Session.MapInstanceId)
                {
                    GameMeshLog.Warn("drop aoi from other map");
                    return false;
                }

                return ProtocolMapper.ApplyAoiDelta(Aoi, inner.AoiDelta);
            }

            if (inner.FullSnapshot != null)
            {
                Aoi.Clear();
                Push.Reset(inner.FullSnapshot.BaselineServerSeq);
                Session.LastServerSeq = inner.FullSnapshot.BaselineServerSeq;
                if (Connection.State == ConnectionState.Resyncing && Session.MapInstanceId != 0)
                    Connection.SetLogicalState(ConnectionState.InWorld);
                GameMeshLog.Info($"full snapshot player={inner.FullSnapshot.PlayerId} seq={inner.FullSnapshot.BaselineServerSeq}");
            }

            return true;
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
            LocalIdentityStore.Save(LaunchArgs.DeviceId, Session.PlayerId, Session.DisplayName, Config.host,
                Config.port);
        }

        void TryReadProtocolHash()
        {
            try
            {
                var path = System.IO.Path.Combine(Application.dataPath, "GameMesh", "Protocol",
                    "protocol_manifest.json");
                if (!System.IO.File.Exists(path))
                    return;
                var json = System.IO.File.ReadAllText(path);
                var key = "\"schema_sha256\"";
                var i = json.IndexOf(key, StringComparison.Ordinal);
                if (i < 0)
                    return;
                var start = json.IndexOf('"', i + key.Length);
                var end = json.IndexOf('"', start + 1);
                if (start >= 0 && end > start)
                {
                    var hash = json.Substring(start + 1, end - start - 1);
                    ProtocolSchemaShort = hash.Length >= 8 ? hash.Substring(0, 8) : hash;
                }
            }
            catch
            {
                /* ignore */
            }
        }

        void SetError(Exception ex)
        {
            var code = ex is GameMeshException ge ? ge.ErrorCode : GameMeshErrorCode.ServerError;
            SetError(code, ex.Message, code);
        }

        void SetError(string code, string message, string serverCode = "")
        {
            var resolved = !string.IsNullOrEmpty(serverCode) ? serverCode : code;
            LastErrorCode = resolved ?? "";
            LastError = GameMeshLog.Redact(message ?? "");
            LastErrorUi = GameErrorCatalog.FormatUi(LastErrorCode, LastError);
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
