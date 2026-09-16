using GameMesh.Bootstrap;
using GameMesh.LoadTest;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Player;
using GameMesh.Protocol;
using Unity.FPS.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameMesh.UI
{
    [DefaultExecutionOrder(-20000)]
    public sealed class GameMeshRuntimeUi : MonoBehaviour
    {
        bool _showMail;
        bool _showDebug;
        bool _panelOpen = true;
        ConnectionState _lastState = ConnectionState.Disconnected;
        string _mailTitle = "hello";
        string _mailBody = "from luna";
        string _peerId = "";
        string _loadCount = "100";
        string _loadMinutes = "5";
        string _loadStaggerMs = "40";
        string _loadPortA = "8081";
        string _loadPortB = "8083";
        string _loadPassword = "loadtest";
        Vector2 _mailScroll;
        Vector2 _panelScroll;
        bool _cursorUnlocked = true;
        bool _stylesReady;
        Texture2D _bg;
        Texture2D _header;
        Texture2D _accent;
        Texture2D _btn;
        Texture2D _btnLogin;
        Texture2D _btnLogout;
        GUIStyle _title;
        GUIStyle _section;
        GUIStyle _label;
        GUIStyle _field;
        GUIStyle _btnStyle;
        GUIStyle _loginStyle;
        GUIStyle _logoutStyle;
        GUIStyle _statusOk;
        GUIStyle _statusWarn;
        GUIStyle _statusErr;
        GUIStyle _hint;
        GUIStyle _hudHint;

        void Start()
        {
            var client = GameMeshClient.Instance;
            if (client != null && !string.IsNullOrEmpty(client.LaunchArgs.AutoScenario))
                _panelOpen = false;
            if (PlayableScenePlayer.IsExploreScene)
                _panelOpen = false;
            _cursorUnlocked = _panelOpen;
            ApplyCursor();
            if (GetComponent<GameMeshCursorApply>() == null)
                gameObject.AddComponent<GameMeshCursorApply>();
        }

        static Rect LauncherRect()
        {
            return new Rect(24f, 20f, 320f, 88f);
        }

        void OnDisable()
        {
            CursorCapture.UiOwnsCursor = false;
        }

        void Update()
        {
            var client = GameMeshClient.Instance;
            if (client?.Connection != null)
            {
                var state = client.Connection.State;
                if (state == ConnectionState.InWorld && _lastState != ConnectionState.InWorld)
                    SetPanelOpen(false);
                _lastState = state;
            }

            if (Input.GetKeyDown(KeyCode.F2))
                SetPanelOpen(!_panelOpen);

            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.F1))
            {
                if (_panelOpen)
                    SetPanelOpen(false);
                else
                    SetPanelOpen(true);
            }

            if (!PlayableScenePlayer.SceneUsesFpsLook && !PlayableScenePlayer.UsesHoldToLook)
            {
                _cursorUnlocked = true;
                ApplyCursor();
            }

            var overLauncher = !_panelOpen && GuiToScreen(LauncherRect()).Contains(Input.mousePosition);
            var altUi = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            CursorCapture.UiOwnsCursor = _panelOpen || altUi;

            if (!_panelOpen && Cursor.lockState != CursorLockMode.Locked &&
                Input.GetMouseButtonDown(0) && overLauncher)
                SetPanelOpen(true);

            if (_panelOpen || altUi)
            {
                _cursorUnlocked = true;
                ApplyCursor();
            }
        }

        void LateUpdate()
        {
            RefreshCursorCapture();
        }

        void RefreshCursorCapture()
        {
            var altUi = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            CursorCapture.UiOwnsCursor = _panelOpen || altUi;
            if (_panelOpen || altUi)
            {
                _cursorUnlocked = true;
                ApplyCursor();
            }
        }

        public void ForceCursorIfUiOwns()
        {
            RefreshCursorCapture();
        }

        void OnGUI()
        {
            var client = GameMeshClient.Instance;
            if (client == null)
                return;

            EnsureStyles();
            if (!_panelOpen)
            {
                DrawLauncher(client);
                return;
            }

            var width = Mathf.Clamp(Screen.width * 0.52f, 860f, 1200f);
            var height = Screen.height - 16f;
            var area = new Rect(16f, 8f, width, height);

            GUI.DrawTexture(area, _bg);
            GUI.DrawTexture(new Rect(area.x, area.y, area.width, 96f), _header);
            GUI.DrawTexture(new Rect(area.x, area.y + 96f, 10f, area.height - 96f), _accent);

            GUILayout.BeginArea(new Rect(area.x + 24f, area.y + 14f, area.width - 44f, area.height - 28f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("LUNA / GameMesh 联调", _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("隐  藏", _logoutStyle, GUILayout.Width(160), GUILayout.Height(60)))
                SetPanelOpen(false);
            GUILayout.EndHorizontal();
            GUILayout.Label("F2 或 Tab 打开/关闭    按住 Alt 再点「F2 联调」    隐藏后面板外点击才射击", _hint);

            _panelScroll = GUILayout.BeginScrollView(_panelScroll);
            DrawStatus(client);
            DrawAuth(client);
            DrawLines(client);
            DrawLoadTest();
            DrawWorld(client);
            DrawMail(client);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            EatMouseOver(area);
        }

        void DrawLauncher(GameMeshClient client)
        {
            var state = client.Connection != null ? client.Connection.State : ConnectionState.Disconnected;
            var label = state == ConnectionState.InWorld ? "F2  联调" : "F2  登录";
            var launcher = LauncherRect();
            if (GUI.Button(launcher, label, _loginStyle))
                SetPanelOpen(true);
            EatMouseOver(launcher);

            string text;
            if (state != ConnectionState.InWorld && client.Session.MapInstanceId == 0)
                text = "还没进服，压测机器人不会出现。进游戏会自动登录，或按 F2。";
            else
            {
                var runner = GameMeshLoadTestRunner.Instance;
                var bots = runner != null ? runner.InWorldCount : 0;
                var binder = client.GetComponent<GameMeshWorldBinder>();
                var near = "";
                if (binder != null && binder.TryNearestRemote(out var botName, out var dist))
                    near = "    最近 " + botName + " " + dist.ToString("0") + "m";
                else if (bots > 0 && client.Aoi.Entities.Count == 0)
                    near = "    同线视野空，正在拉快照";
                text = "已拉回出生点    " +
                       (client.Lines.LineNo != 0 ? client.Lines.LineNo + "线 " +
                           client.Lines.Occupancy + "/" + client.Lines.EffectiveSplitAt + "    " : "") +
                       "AOI " + client.Aoi.Entities.Count +
                       "    压测在线 " + bots +
                       (runner != null && runner.TargetLineNo != 0 ? "→" + runner.TargetLineNo + "线" : "") +
                       near;
                if (!string.IsNullOrEmpty(client.LastNotice))
                    text += "    " + client.LastNotice;
            }

            var hint = new Rect(24f, launcher.yMax + 12f, Mathf.Min(Screen.width - 48f, 1400f), 96f);
            GUI.DrawTexture(hint, _bg);
            GUI.Label(new Rect(hint.x + 16f, hint.y + 10f, hint.width - 32f, hint.height - 20f), text, _hudHint);
            EatMouseOver(hint);
        }

        static Rect GuiToScreen(Rect gui)
        {
            return new Rect(gui.x, Screen.height - gui.y - gui.height, gui.width, gui.height);
        }

        static void EatMouseOver(Rect guiRect)
        {
            var ev = Event.current;
            if (ev == null || !guiRect.Contains(ev.mousePosition))
                return;
            if (ev.type == EventType.MouseDown || ev.type == EventType.MouseUp || ev.type == EventType.ScrollWheel)
                ev.Use();
        }

        public void PrepareExploreGameplay()
        {
            SetPanelOpen(false);
        }

        void SetPanelOpen(bool open)
        {
            _panelOpen = open;
            if (open)
            {
                _cursorUnlocked = true;
                ApplyCursor();
                var client = GameMeshClient.Instance;
                if (client != null && client.Session.HasIdentity &&
                    (client.Lines.IsLineMap || client.Config.mapTemplateId == 1002))
                    _ = client.QueryMapLinesAsync();
                return;
            }

            if (PlayableScenePlayer.UsesHoldToLook)
            {
                _cursorUnlocked = true;
                ApplyCursor();
                return;
            }

            if (PlayableScenePlayer.SceneUsesFpsLook)
            {
                _cursorUnlocked = false;
                ApplyCursor();
            }
        }

        void DrawStatus(GameMeshClient client)
        {
            var state = client.Connection != null ? client.Connection.State : ConnectionState.Disconnected;
            var style = state == ConnectionState.InWorld || state == ConnectionState.Authenticated
                ? _statusOk
                : state == ConnectionState.Disconnected || state == ConnectionState.Closing
                    ? _statusErr
                    : _statusWarn;
            GUILayout.Space(8);
            GUILayout.Label(
                "连接  " + StateText(state) +
                "    " + client.Config.host + ":" + client.Config.port +
                " / " + client.Config.portB, style);
            if (!string.IsNullOrEmpty(client.BusyStage))
                GUILayout.Label("阶段  " + client.BusyStage, _statusWarn);
            GUILayout.Label(
                "玩家ID  " + client.Session.PlayerId +
                "    模板  " + client.Session.MapTemplateId +
                "    线  " + (client.Lines.LineNo != 0
                    ? client.Lines.LineNo.ToString()
                    : (client.Session.MapInstanceId != 0 ? "在图" : "-")) +
                "    地图实例  " + client.Session.MapInstanceId +
                "    AOI  " + client.Aoi.Entities.Count, _label);
            GUILayout.Label(
                "client_seq  " + (client.Connection != null ? client.Connection.LastClientSeq.ToString() : "0") +
                "    server_seq  " + client.Session.LastServerSeq +
                "    RTT  " + client.LastRttMs + "ms", _label);
            GUILayout.Label(
                "协议  v" + client.ProtocolVersion +
                "  " + client.ProtocolSchemaShort +
                "    Hello  " + (client.HelloOk ? "OK" : "未完成") +
                "    心跳  " + (client.HeartbeatOk ? "OK" : "-") +
                "    时差  " + client.ServerTimeOffsetMs + "ms", _hint);
            GUILayout.Label(
                "生命  " + (client.Session.Attributes.LifeState ?? "ALIVE") +
                (client.Session.SessionReplaced ? "    已被顶号" : ""), _hint);

            if (!string.IsNullOrEmpty(client.LastNotice))
                GUILayout.Label(client.LastNotice, _statusOk);
            if (!string.IsNullOrEmpty(client.LastErrorUi))
                GUILayout.Label(client.LastErrorUi, _statusErr);
            if (client.MapBlocked)
                GUILayout.Label("进图被阻止  " + client.MapBlockReason, _statusErr);
        }

        void DrawAuth(GameMeshClient client)
        {
            GUILayout.Space(10);
            GUILayout.Label("账号", _section);
            client.Config.host = Field("服务器", client.Config.host);
            client.Config.port = IntField("Gateway A", client.Config.port);
            client.Config.portB = IntField("Gateway B", client.Config.portB);
            client.LaunchArgs.DeviceId = Field("设备ID", client.LaunchArgs.DeviceId);
            client.LaunchArgs.DisplayName = Field("显示名", client.LaunchArgs.DisplayName);
            client.LaunchArgs.EnsureDefaultPassword();
            GUILayout.BeginHorizontal();
            GUILayout.Label("密码", _label, GUILayout.Width(150));
            client.LaunchArgs.Password = GUILayout.PasswordField(client.LaunchArgs.Password ?? "", '*', 64, _field,
                GUILayout.Height(48));
            GUILayout.EndHorizontal();
            GUILayout.Label(
                "默认  " + GameMeshLaunchArgs.DefaultPassword +
                "。密码至少 6 位；空着会自动填默认密码。关窗口 ≠ 登出。",
                _hint);
            var playerText = Field("玩家ID", client.Session.PlayerId == 0 ? "" : client.Session.PlayerId.ToString());
            if (ulong.TryParse(playerText, out var pid))
                client.Session.PlayerId = pid;

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("注  册", _btnStyle, GUILayout.Height(64)))
                _ = client.RegisterAsync();
            if (GUILayout.Button("登  录", _loginStyle, GUILayout.Height(64)))
                _ = client.LoginAsync();
            if (GUILayout.Button("登  出", _logoutStyle, GUILayout.Height(64)))
                _ = client.LogoutAsync();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("清除本地账号信息", _btnStyle, GUILayout.Height(52)))
                client.ClearLocalAccount();
        }

        void DrawLoadTest()
        {
            var runner = GameMeshLoadTestRunner.Instance;
            var client = GameMeshClient.Instance;
            if (runner == null || client == null)
                return;
            if (!string.IsNullOrEmpty(client.LaunchArgs.AutoScenario))
                return;

            GUILayout.Space(12);
            GUILayout.Label("压测机器人", _section);
            GUILayout.Label(
                "先把你送进一条未满的线，机器人跟你进同一条。满 " + MapLineState.DefaultSplitAt +
                " 人开下一线。活动约 8m（本图 AOI 32m）。你未进线时周围看不到人。8081/8083 各一半，上限 20000。",
                _hint);
            _loadCount = Field("登录人数", _loadCount);
            _loadMinutes = Field("在线分钟", _loadMinutes);
            _loadStaggerMs = Field("登录间隔ms", _loadStaggerMs);
            _loadPortA = Field("Gateway A", _loadPortA);
            _loadPortB = Field("Gateway B", _loadPortB);
            GUILayout.BeginHorizontal();
            GUILayout.Label("机器人密码", _label, GUILayout.Width(150));
            _loadPassword = GUILayout.PasswordField(_loadPassword ?? "", '*', 64, _field, GUILayout.Height(48));
            GUILayout.EndHorizontal();

            var inWorld = runner.InWorldCount;
            var failed = runner.FailedCount;
            var remain = runner.RemainingSec;
            var remainText = remain > 100000f ? "手动下线" : FormatClock(remain);
            GUILayout.Label(
                "阶段  " + runner.Phase +
                "    在线  " + inWorld +
                " / " + Mathf.Max(runner.TargetCount, 0) +
                (runner.TargetLineNo != 0 ? "    目标 " + runner.TargetLineNo + "线" : "    目标 自动分线") +
                "    失败  " + failed, failed > 0 ? _statusWarn : _label);
            int.TryParse(_loadPortA, out var portA);
            int.TryParse(_loadPortB, out var portB);
            if (portA <= 0)
                portA = 8081;
            if (portB <= 0)
                portB = 8083;
            GUILayout.Label(
                "端口 " + portA + "  " + runner.InWorldOnPort(portA) +
                "    端口 " + portB + "  " + runner.InWorldOnPort(portB), _hint);
            GUILayout.Label("剩余在线  " + remainText, _hint);
            var sample = runner.SampleError();
            if (!string.IsNullOrEmpty(sample))
                GUILayout.Label("最近错误  " + sample, _statusErr);

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUI.enabled = !runner.Busy;
            if (GUILayout.Button("集体登录", _loginStyle, GUILayout.Height(64)))
            {
                int.TryParse(_loadCount, out var count);
                float.TryParse(_loadMinutes, out var minutes);
                int.TryParse(_loadStaggerMs, out var stagger);
                if (count <= 0)
                    count = 100;
                if (minutes < 0f)
                    minutes = 5f;
                int.TryParse(_loadPortA, out var gwA);
                int.TryParse(_loadPortB, out var gwB);
                if (gwA <= 0)
                    gwA = 8081;
                if (gwB <= 0)
                    gwB = 8083;
                runner.StartCollectiveLogin(count, minutes, stagger, _loadPassword, gwA, gwB);
            }

            GUI.enabled = runner.Busy || runner.HoldOnline || inWorld > 0;
            if (GUILayout.Button("集体下线", _logoutStyle, GUILayout.Height(64)))
                runner.RequestCollectiveLogout();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        static string FormatClock(float seconds)
        {
            var total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            var m = total / 60;
            var s = total % 60;
            return m.ToString("00") + ":" + s.ToString("00");
        }

        void DrawLines(GameMeshClient client)
        {
            GUILayout.Space(12);
            GUILayout.Label("分线", _section);
            var lineMap = client.Lines.IsLineMap || client.Config.mapTemplateId == 1002;
            if (client.Config.mapTemplateId == 1001 && !lineMap)
            {
                GUILayout.Label("1001 是旧公共池，没有分线。", _hint);
                return;
            }

            if (!lineMap)
            {
                GUILayout.Label("当前模板不是分线图。", _hint);
                return;
            }

            var soft = client.Lines.EffectiveSplitAt;
            var currentLine = client.Lines.LineNo == 0
                ? (client.Session.MapInstanceId != 0 ? "已在图中" : "未进线")
                : client.Lines.LineNo + " 线";
            GUILayout.Label(
                "苏州 1002    当前 " + currentLine +
                "    " + client.Lines.Occupancy + " / " + soft +
                "    " + (string.IsNullOrEmpty(client.Lines.Kind) ? "LINE" : client.Lines.Kind), _label);
            GUILayout.Label(
                "满 " + MapLineState.DefaultSplitAt +
                " 人开新线（以服务器回包为准）。进图后点「切线」换线；没进图点「进入」。不要把已满当进图成功。",
                _hint);
            GUILayout.Label(
                "分线列表  " + client.Lines.Lines.Count +
                " 条。一线满 " + MapLineState.DefaultSplitAt + " 人后应出现下一线，点刷新可看到。",
                _hint);
            if (client.Lines.QueuePosition > 0)
                GUILayout.Label(
                    "排队  第 " + client.Lines.QueuePosition + " 位" +
                    (client.Lines.QueueReady ? "    已可进线" : ""), _statusWarn);
            if (!string.IsNullOrEmpty(client.LastNotice))
                GUILayout.Label(client.LastNotice, _statusOk);

            var canAct = !client.IsBusy && client.Session.HasIdentity;
            GUILayout.BeginHorizontal();
            GUI.enabled = canAct;
            if (GUILayout.Button("刷新线列表", _btnStyle, GUILayout.Height(56)))
                _ = client.QueryMapLinesAsync();
            if (GUILayout.Button("系统选线进图", _loginStyle, GUILayout.Height(56)))
            {
                if (client.IsOnMap)
                    client.SetNotice("已在图中，请点某一线的「切线」");
                else
                    _ = client.EnterMapAsync(0, 0);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (client.Lines.Lines.Count == 0)
                GUILayout.Label("还没有线列表。登录后点刷新，或先系统选线进图。", _hint);

            var onMap = client.IsOnMap;
            for (var i = 0; i < client.Lines.Lines.Count; i++)
            {
                var line = client.Lines.Lines[i];
                if (line == null || line.LineNo == 0)
                    continue;
                var cap = MapLineState.SplitAt(line.SoftCap);
                var current = onMap && line.LineNo == client.Lines.LineNo;
                var full = cap != 0 && line.Occupancy >= cap;
                GUILayout.BeginHorizontal();
                GUILayout.Label(
                    line.LineNo + " 线    " + line.Occupancy + " / " + cap +
                    (full ? "  已满" : "") +
                    (current ? "  当前" : ""),
                    full ? _statusWarn : _label, GUILayout.Height(52));
                GUI.enabled = canAct && !current;
                if (GUILayout.Button(current ? "所在" : (onMap ? "切线" : "进入"), _loginStyle, GUILayout.Width(120),
                        GUILayout.Height(52)))
                {
                    if (onMap)
                        _ = client.SwitchLineAsync(line.LineNo);
                    else
                        _ = client.EnterMapAsync(0, line.LineNo);
                }

                if (full && !current)
                {
                    if (GUILayout.Button("排队", _btnStyle, GUILayout.Width(108), GUILayout.Height(52)))
                        _ = client.EnqueueMapAsync(line.LineNo);
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        void DrawWorld(GameMeshClient client)
        {
            GUILayout.Space(12);
            GUILayout.Label("角色属性", _section);
            var a = client.Session.Attributes;
            GUILayout.Label(a.FromServer ? "来源：服务器权威值  stats=" + a.StatsVersion : "来源：本地默认（尚未收到服务器资料）", _hint);
            GUILayout.Label("ID  " + a.PlayerId + "    名字  " + a.Name, _label);
            GUILayout.Label("HP  " + a.Hp + " / " + a.MaxHp + "    MP  " + a.Mp + " / " + a.MaxMp, _label);
            GUILayout.Label("攻击  " + a.Attack + "    法强  " + a.SpellPower + "    防御  " + a.Defense + "    魔抗  " + a.MagicResist, _label);
            GUILayout.Label("暴击  " + a.CritRate + " / " + a.CritDamage + "    移速  " + a.MoveSpeed + "    攻速  " + a.AttackSpeed, _label);
            if (client.Session.IsDead)
            {
                GUILayout.Label("角色已死亡，镜头和界面仍可用，移动已禁用", _statusWarn);
                if (GUILayout.Button("复  活", _loginStyle, GUILayout.Height(40)))
                    _ = client.RespawnAsync();
            }
        }

        void DrawMail(GameMeshClient client)
        {
            GUILayout.Space(12);
            GUILayout.Label("邮箱", _section);
            _showMail = GUILayout.Toggle(_showMail, _showMail ? "  邮箱面板已打开" : "  点击打开邮箱", _btnStyle, GUILayout.Height(40));
            client.Mail.PanelOpen = _showMail;
            if (!_showMail)
                return;

            var page = client.Mail.Page;
            GUILayout.Label("未读  " + page.UnreadTotal + "    列表  " + page.Mails.Count, _label);
            if (GUILayout.Button("刷新邮箱", _btnStyle, GUILayout.Height(40)))
                _ = client.Mail.RefreshAsync(default);

            _peerId = Field("收件人ID", _peerId);
            _mailTitle = Field("标题", _mailTitle);
            _mailBody = Field("正文", _mailBody);
            if (GUILayout.Button("发送普通邮件（无附件）", _loginStyle, GUILayout.Height(40)))
            {
                ulong.TryParse(_peerId, out var to);
                _ = SendMail(client, to);
            }

            _mailScroll = GUILayout.BeginScrollView(_mailScroll, GUILayout.Height(180));
            if (page.Mails.Count == 0)
                GUILayout.Label("还没有邮件。登录后点「刷新邮箱」。", _hint);
            foreach (var mail in page.Mails)
            {
                if (GUILayout.Button(mail.MailId + "  " + mail.Title + "  ← " + mail.SenderName, _btnStyle,
                        GUILayout.Height(36)))
                    _ = client.Mail.GetAsync(mail.MailId, default);
            }

            GUILayout.EndScrollView();
            if (page.Selected != null)
            {
                GUILayout.Label("正文  " + page.Selected.Brief?.Title, _section);
                GUILayout.Label(page.Selected.Body ?? "", _label);
            }

            if (!string.IsNullOrEmpty(page.LastError))
                GUILayout.Label(page.LastError, _statusErr);

            _showDebug = GUILayout.Toggle(_showDebug, "显示调试信息", _hint);
            if (_showDebug)
            {
                GUILayout.Label("map_hash  " + client.Config.mapDataHash, _hint);
                GUILayout.Label(client.Session.DebugSummary(), _hint);
            }
        }

        async System.Threading.Tasks.Task SendMail(GameMeshClient client, ulong to)
        {
            var err = await client.Mail.SendAsync(to, _mailTitle, _mailBody, default);
            if (!string.IsNullOrEmpty(err))
                client.Mail.Page.LastError = err;
        }

        void ApplyCursor()
        {
            Cursor.lockState = _cursorUnlocked ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = _cursorUnlocked;
        }

        string Field(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label, GUILayout.Width(150));
            value = GUILayout.TextField(value ?? "", _field, GUILayout.Height(48));
            GUILayout.EndHorizontal();
            return value;
        }

        int IntField(string label, int value)
        {
            var text = Field(label, value.ToString());
            return int.TryParse(text, out var n) ? n : value;
        }

        static string StateText(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Disconnected: return "未连接";
                case ConnectionState.Connecting: return "连接中";
                case ConnectionState.Handshaking: return "协议握手中";
                case ConnectionState.Connected: return "已连接";
                case ConnectionState.Authenticating: return "登录中";
                case ConnectionState.Authenticated: return "已登录";
                case ConnectionState.EnteringWorld: return "进图中";
                case ConnectionState.InWorld: return "已在地图中";
                case ConnectionState.Reconnecting: return "重连中";
                case ConnectionState.Resyncing: return "同步世界中";
                case ConnectionState.Closing: return "关闭中";
                default: return state.ToString();
            }
        }

        void EnsureStyles()
        {
            if (_stylesReady)
                return;
            _bg = ColorTex(new Color(0.06f, 0.08f, 0.12f, 0.94f));
            _header = ColorTex(new Color(0.12f, 0.22f, 0.38f, 1f));
            _accent = ColorTex(new Color(1f, 0.78f, 0.16f, 1f));
            _btn = ColorTex(new Color(0.20f, 0.32f, 0.48f, 1f));
            _btnLogin = ColorTex(new Color(0.12f, 0.52f, 0.28f, 1f));
            _btnLogout = ColorTex(new Color(0.62f, 0.20f, 0.18f, 1f));

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 40,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _section = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.88f, 0.40f) }
            };
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                wordWrap = true,
                normal = { textColor = Color.white }
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                wordWrap = true,
                normal = { textColor = new Color(0.92f, 0.94f, 0.98f) }
            };
            _hudHint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(1f, 0.95f, 0.45f) }
            };
            _field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 22,
                alignment = TextAnchor.MiddleLeft
            };
            _btnStyle = ButtonStyle(_btn, Color.white);
            _loginStyle = ButtonStyle(_btnLogin, Color.white);
            _logoutStyle = ButtonStyle(_btnLogout, Color.white);
            _statusOk = Banner(new Color(0.10f, 0.38f, 0.20f, 1f), Color.white);
            _statusWarn = Banner(new Color(0.55f, 0.38f, 0.06f, 1f), new Color(1f, 0.97f, 0.82f));
            _statusErr = Banner(new Color(0.48f, 0.12f, 0.12f, 1f), Color.white);
            _stylesReady = true;
        }

        static GUIStyle ButtonStyle(Texture2D bg, Color text)
        {
            return new GUIStyle(GUI.skin.button)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = bg, textColor = text },
                hover = { background = bg, textColor = Color.yellow },
                active = { background = bg, textColor = Color.white }
            };
        }

        GUIStyle Banner(Color bg, Color text)
        {
            var tex = ColorTex(bg);
            return new GUIStyle(GUI.skin.box)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(14, 14, 12, 12),
                margin = new RectOffset(0, 0, 6, 6),
                wordWrap = true,
                stretchHeight = true,
                normal = { background = tex, textColor = text }
            };
        }

        static Texture2D ColorTex(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }

    [DefaultExecutionOrder(32000)]
    sealed class GameMeshCursorApply : MonoBehaviour
    {
        GameMeshRuntimeUi _ui;

        void Awake()
        {
            _ui = GetComponent<GameMeshRuntimeUi>();
        }

        void LateUpdate()
        {
            _ui?.ForceCursorIfUiOwns();
        }
    }
}
