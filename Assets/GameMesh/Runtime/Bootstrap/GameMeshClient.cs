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
using Google.Protobuf;
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
        public string LastError { get; private set; } = "";
        public string LastErrorCode { get; private set; } = "";
        public bool MapBlocked { get; private set; }
        public string MapBlockReason { get; private set; } = "";
        public uint LastPlayerCount { get; private set; }
        public ulong ExpectedMapHashVersion { get; set; }

        GameMeshMainThreadDispatcher _dispatcher;
        CancellationTokenSource _lifetime;
        bool _logoutRequested;
        int _reconnectAttempts;
        float _nextReconnectAt;
        float _lastMailPoll;
        bool _busy;

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
            Session.DeviceId = LaunchArgs.DeviceId;
            Session.DisplayName = LaunchArgs.DisplayName;
            _dispatcher = GameMeshMainThreadDispatcher.Ensure(gameObject);
            Connection = new GameConnection(_dispatcher);
            Connection.PushReceived += OnPush;
            Connection.StateChanged += OnTransportState;
            Mail = new MailClient(Session, req => RequestAsync(req));
            MoveSampler.SendHz = Config.moveSendHz;
            if (GetComponent<GameMeshRuntimeUi>() == null)
                gameObject.AddComponent<GameMeshRuntimeUi>();
            if (GetComponent<GameMeshWorldBinder>() == null)
                gameObject.AddComponent<GameMeshWorldBinder>();
            _lifetime = new CancellationTokenSource();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Instance == this)
                Instance = null;
            _lifetime?.Cancel();
            try { Connection?.DisconnectAsync(DisconnectReason.Dispose, CancellationToken.None).GetAwaiter().GetResult(); }
            catch { /* ignore */ }
        }

        void Update()
        {
            if (_logoutRequested)
                return;
            if (Connection != null &&
                Connection.State == ConnectionState.Disconnected &&
                Session.AutoReconnect &&
                Session.HasIdentity &&
                Time.unscaledTime >= _nextReconnectAt &&
                _reconnectAttempts < Config.reconnectMaxAttempts)
            {
                _ = ReconnectAsync();
            }

            if (Mail != null && Session.HasIdentity &&
                Mail.ShouldPoll(Time.unscaledTime, _lastMailPoll, true))
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
                    SetError(GameMeshErrorCode.ServerError, rsp.Register?.Message ?? rsp.Message);
                    Connection.SetLogicalState(ConnectionState.Connected);
                    return;
                }

                Session.PlayerId = rsp.Register.PlayerId;
                Session.DisplayName = LaunchArgs.DisplayName;
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
            }
        }

        public async Task LoginAsync()
        {
            if (_busy)
                return;
            _busy = true;
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
                    SetError(GameMeshErrorCode.ServerError, rsp.Login?.Message ?? rsp.Message);
                    Connection.SetLogicalState(ConnectionState.Connected);
                    return;
                }

                Session.ApplyLogin(Session.PlayerId != 0 ? Session.PlayerId : 0, rsp.Login.SessionId, rsp.Login.Token,
                    rsp.Login.Generation, LaunchArgs.DisplayName);
                if (Session.PlayerId == 0)
                    GameMeshLog.Warn("login ok but player_id was 0; enter it from register result");
                Session.Attributes.PlayerId = Session.PlayerId;
                Session.Attributes.Name = Session.DisplayName;
                Session.AutoReconnect = true;
                _logoutRequested = false;
                _reconnectAttempts = 0;
                Push.Reset(0);
                Aoi.LocalPlayerId = Session.PlayerId;
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
            }
        }

        public async Task LogoutAsync()
        {
            _logoutRequested = true;
            Session.AutoReconnect = false;
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
                await Connection.DisconnectAsync(DisconnectReason.UserLogout, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }

        public async Task EnterMapAsync()
        {
            if (!Session.HasIdentity)
            {
                SetError(GameMeshErrorCode.ClientIllegalState, "not logged in");
                return;
            }

            try
            {
                Connection.SetLogicalState(ConnectionState.EnteringWorld);
                var req = new GameRequest
                {
                    EnterMap = new EnterMapReq
                    {
                        PlayerId = Session.PlayerId,
                        RealmId = Config.realmId,
                        MapTemplateId = Config.mapTemplateId,
                        MapInstanceId = 0
                    }
                };
                var rsp = await RequestAsync(req).ConfigureAwait(true);
                if (!rsp.Ok || rsp.EnterMap == null || !rsp.EnterMap.Ok)
                {
                    SetError(GameMeshErrorCode.ServerError, rsp.EnterMap?.Message ?? rsp.Message);
                    Connection.SetLogicalState(ConnectionState.Authenticated);
                    return;
                }

                var enter = rsp.EnterMap;
                Session.ApplyMap(enter.MapTemplateId, enter.MapInstanceId, enter.OwnerEpoch, enter.RouteVersion);
                Aoi.SetMapInstance(enter.MapInstanceId);
                Aoi.Clear();
                MapBlocked = false;
                MapBlockReason = "";
                if (!string.IsNullOrEmpty(Config.mapDataHash) && Config.mapDataHash.StartsWith("FORCE_MISMATCH"))
                {
                    MapBlocked = true;
                    MapBlockReason =
                        $"map hash mismatch local={Config.mapDataHash} server=unspecified";
                    LastErrorCode = GameMeshErrorCode.MapHashMismatch;
                    LastError = MapBlockReason;
                    Connection.SetLogicalState(ConnectionState.Authenticated);
                    return;
                }

                Connection.SetLogicalState(ConnectionState.InWorld);
                _ = PingMapAsync();
            }
            catch (Exception ex)
            {
                SetError(ex);
            }
        }

        public async Task<GameResponse> RequestAsync(GameRequest request, TimeSpan? timeout = null)
        {
            if (request == null)
                throw new GameMeshException(GameMeshErrorCode.ClientProtocol, "null request");
            if (!string.IsNullOrEmpty(Session.Token))
                request.SessionToken = Session.Token;
            var rsp = await Connection.RequestAsync(
                request,
                timeout ?? TimeSpan.FromMilliseconds(Config.requestTimeoutMs),
                _lifetime.Token).ConfigureAwait(true);
            if (!rsp.Ok)
            {
                LastErrorCode = GameMeshErrorCode.ServerError;
                LastError = rsp.Message;
            }

            return rsp;
        }

        async Task EnsureConnectedAsync()
        {
            if (Connection.State == ConnectionState.Connected ||
                Connection.State == ConnectionState.Authenticating ||
                Connection.State == ConnectionState.Authenticated ||
                Connection.State == ConnectionState.EnteringWorld ||
                Connection.State == ConnectionState.InWorld)
                return;
            using (var cts = new CancellationTokenSource(Config.connectTimeoutMs))
            {
                await Connection.ConnectAsync(Config.host, Config.port, cts.Token).ConfigureAwait(true);
            }
        }

        async Task ReconnectAsync()
        {
            _reconnectAttempts++;
            var backoff = Mathf.Min(8f, 0.4f * Mathf.Pow(2f, _reconnectAttempts - 1));
            backoff += UnityEngine.Random.Range(0f, 0.3f);
            _nextReconnectAt = Time.unscaledTime + backoff;
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
                    SetError(GameMeshErrorCode.ServerError, rsp.Reconnect?.Message ?? rsp.Message);
                    return;
                }

                Session.ApplyReconnect(rsp.Reconnect.SessionId, rsp.Reconnect.Token, rsp.Reconnect.Generation);
                Aoi.Clear();
                if (rsp.Reconnect.NeedFullSnapshot)
                    GameMeshLog.Info("reconnect needs full snapshot; waiting for server push");
                _reconnectAttempts = 0;
                Connection.SetLogicalState(Session.MapInstanceId != 0
                    ? ConnectionState.InWorld
                    : ConnectionState.Authenticated);
                if (Session.MapInstanceId != 0)
                    await EnterMapAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetError(ex);
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
                var inner = response;
                ulong serverSeq = 0;
                if (response.BodyCase == GameResponse.BodyOneofCase.ServerPush && response.ServerPush != null)
                {
                    serverSeq = response.ServerPush.ServerSeq;
                    var decision = Push.Observe(serverSeq);
                    if (decision == PushReliability.Decision.Duplicate)
                    {
                        if (response.ServerPush.Reliable)
                            _ = AckPushAsync(serverSeq);
                        return;
                    }

                    if (decision == PushReliability.Decision.Gap)
                        GameMeshLog.Warn($"push gap expected={Push.ExpectedNext} got={serverSeq}");

                    try
                    {
                        inner = GameResponse.Parser.ParseFrom(response.ServerPush.Payload);
                    }
                    catch (Exception ex)
                    {
                        SetError(new GameMeshException(GameMeshErrorCode.ClientProtocol, "inner push parse", ex));
                        return;
                    }

                    ApplyInnerPush(inner);
                    Push.MarkApplied(serverSeq);
                    Session.LastServerSeq = Push.LastAppliedServerSeq;
                    if (response.ServerPush.Reliable)
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

        void ApplyInnerPush(GameResponse inner)
        {
            if (inner == null)
                return;
            if (inner.MailboxSummary != null || inner.MailList != null)
                Mail.NotifyMailboxChanged(Time.unscaledTime);
            if (inner.FullSnapshot != null)
            {
                Aoi.Clear();
                GameMeshLog.Info($"full snapshot player={inner.FullSnapshot.PlayerId} seq={inner.FullSnapshot.BaselineServerSeq}");
            }
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

        void SetError(Exception ex)
        {
            var code = ex is GameMeshException ge ? ge.ErrorCode : GameMeshErrorCode.ServerError;
            SetError(code, ex.Message);
        }

        void SetError(string code, string message)
        {
            LastErrorCode = code ?? "";
            LastError = GameMeshLog.Redact(message ?? "");
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
