using GameMesh.Bootstrap;
using GameMesh.Network;
using GameMesh.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameMesh.UI
{
    public sealed class GameMeshRuntimeUi : MonoBehaviour
    {
        bool _showMail;
        bool _showDebug;
        string _mailTitle = "hello";
        string _mailBody = "from luna";
        string _peerId = "";
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

        void Start()
        {
            _cursorUnlocked = true;
            ApplyCursor();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.F1))
            {
                _cursorUnlocked = !_cursorUnlocked;
                ApplyCursor();
            }

            if (SceneManager.GetActiveScene().name != "MainScene")
            {
                _cursorUnlocked = true;
                ApplyCursor();
            }
        }

        void OnGUI()
        {
            var client = GameMeshClient.Instance;
            if (client == null)
                return;

            EnsureStyles();
            var width = Mathf.Clamp(Screen.width * 0.36f, 460f, 620f);
            var height = Screen.height - 24f;
            var area = new Rect(16f, 12f, width, height);

            GUI.DrawTexture(area, _bg);
            GUI.DrawTexture(new Rect(area.x, area.y, area.width, 64f), _header);
            GUI.DrawTexture(new Rect(area.x, area.y + 64f, 6f, area.height - 64f), _accent);

            GUILayout.BeginArea(new Rect(area.x + 16f, area.y + 10f, area.width - 28f, area.height - 20f));
            GUILayout.Label("LUNA / GameMesh 联调", _title);
            GUILayout.Label("Tab 或 F1 锁定/解锁鼠标", _hint);

            _panelScroll = GUILayout.BeginScrollView(_panelScroll);
            DrawStatus(client);
            DrawAuth(client);
            DrawWorld(client);
            DrawMail(client);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
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
            GUILayout.Label("连接  " + StateText(state), style);
            if (client.IsBusy)
                GUILayout.Label("阶段  " + client.BusyStage, _statusWarn);
            GUILayout.Label(
                "玩家ID  " + client.Session.PlayerId +
                "    模板  " + client.Session.MapTemplateId +
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
            client.Config.port = IntField("端口", client.Config.port);
            client.LaunchArgs.DeviceId = Field("设备ID", client.LaunchArgs.DeviceId);
            client.LaunchArgs.DisplayName = Field("显示名", client.LaunchArgs.DisplayName);
            GUILayout.BeginHorizontal();
            GUILayout.Label("密码", _label, GUILayout.Width(90));
            client.LaunchArgs.Password = GUILayout.PasswordField(client.LaunchArgs.Password ?? "", '*', 64, _field,
                GUILayout.Height(32));
            GUILayout.EndHorizontal();
            GUILayout.Label(
                "密码至少 6 位。空密码或短于 6 位会被立刻拒绝。登录成功后密码框会清空，再次登录请再填一次（联调可用 demo-local）。关窗口 ≠ 登出。",
                _hint);
            var playerText = Field("玩家ID", client.Session.PlayerId == 0 ? "" : client.Session.PlayerId.ToString());
            if (ulong.TryParse(playerText, out var pid))
                client.Session.PlayerId = pid;

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("注  册", _btnStyle, GUILayout.Height(48)))
                _ = client.RegisterAsync();
            if (GUILayout.Button("登  录", _loginStyle, GUILayout.Height(48)))
                _ = client.LoginAsync();
            if (GUILayout.Button("登  出", _logoutStyle, GUILayout.Height(48)))
                _ = client.LogoutAsync();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("清除本地账号信息", _btnStyle, GUILayout.Height(36)))
                client.ClearLocalAccount();
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
            GUILayout.Label(label, _label, GUILayout.Width(90));
            value = GUILayout.TextField(value ?? "", _field, GUILayout.Height(32));
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
                fontSize = 26,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _section = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.35f) }
            };
            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                wordWrap = true,
                normal = { textColor = new Color(0.92f, 0.94f, 0.98f) }
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = new Color(0.70f, 0.76f, 0.84f) }
            };
            _field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleLeft
            };
            _btnStyle = ButtonStyle(_btn, Color.white);
            _loginStyle = ButtonStyle(_btnLogin, Color.white);
            _logoutStyle = ButtonStyle(_btnLogout, Color.white);
            _statusOk = Banner(new Color(0.10f, 0.38f, 0.20f, 1f), Color.white);
            _statusWarn = Banner(new Color(0.42f, 0.32f, 0.08f, 1f), Color.white);
            _statusErr = Banner(new Color(0.48f, 0.12f, 0.12f, 1f), Color.white);
            _stylesReady = true;
        }

        static GUIStyle ButtonStyle(Texture2D bg, Color text)
        {
            return new GUIStyle(GUI.skin.button)
            {
                fontSize = 18,
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
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 10, 10),
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
}
