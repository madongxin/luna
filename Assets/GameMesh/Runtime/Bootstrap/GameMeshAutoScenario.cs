using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GameMesh.Network;
using UnityEngine;

namespace GameMesh.Bootstrap
{
    public sealed class GameMeshAutoScenario : MonoBehaviour
    {
        GameMeshClient _client;
        string _resultDir;
        string _coordDir;
        string _role;
        bool _running;
        bool _finished;
        readonly StringBuilder _events = new StringBuilder();
        float _startedAt;

        void Start()
        {
            _client = GameMeshClient.Instance;
            if (_client == null || string.IsNullOrEmpty(_client.LaunchArgs.AutoScenario))
                return;
            _resultDir = string.IsNullOrEmpty(_client.LaunchArgs.ResultDir)
                ? Path.Combine(Application.persistentDataPath, "gamemesh-e2e")
                : _client.LaunchArgs.ResultDir;
            _coordDir = string.IsNullOrEmpty(_client.LaunchArgs.CoordDir)
                ? _resultDir
                : _client.LaunchArgs.CoordDir;
            _role = string.IsNullOrEmpty(_client.LaunchArgs.Role) ? "a" : _client.LaunchArgs.Role;
            Directory.CreateDirectory(_resultDir);
            Directory.CreateDirectory(_coordDir);
            _startedAt = Time.unscaledTime;
            _running = true;
            _ = RunAsync();
        }

        async Task RunAsync()
        {
            var ok = false;
            var reason = "";
            try
            {
                await _client.RegisterAsync().ConfigureAwait(true);
                if (!_client.HelloOk)
                    throw new InvalidOperationException("hello failed: " + _client.LastErrorCode);
                Event("hello_ok", "protocol_version", _client.ProtocolVersion);
                var hbDeadline = Time.unscaledTime + 15f;
                while (!_client.HeartbeatOk && Time.unscaledTime < hbDeadline)
                    await Task.Yield();
                if (_client.HeartbeatOk)
                    Event("heartbeat_ok", "rtt_ms", _client.LastRttMs);
                if (_client.Session.PlayerId == 0)
                    throw new InvalidOperationException("register did not return player_id");
                Event("register_ok", "player_id", _client.Session.PlayerId);
                WriteCoord();
                await _client.LoginAsync().ConfigureAwait(true);
                if (!_client.Session.HasIdentity)
                    throw new InvalidOperationException("login failed: " + _client.LastErrorCode);
                Event("login_ok", "player_id", _client.Session.PlayerId);
                Event("profile_ok", "from_server", _client.Session.Attributes.FromServer);
                var deadline = Time.unscaledTime + 30f;
                while (_client.Connection.State != ConnectionState.InWorld && Time.unscaledTime < deadline)
                    await Task.Yield();
                if (_client.Connection.State != ConnectionState.InWorld)
                    throw new InvalidOperationException("enter map failed: " + _client.LastErrorCode);
                Event("enter_map_ok", "map_instance_id", _client.Session.MapInstanceId);
                WriteCoord();

                var peer = await WaitPeerAsync(45f).ConfigureAwait(true);
                _client.LaunchArgs.PeerPlayerId = peer;
                await WaitAoiAsync(peer, 45f).ConfigureAwait(true);
                Event("aoi_peer_seen", "peer_id", peer);
                var peerX = _client.Aoi.Entities.TryGetValue(peer, out var seen) ? seen.X : 0f;

                var target = new Vector3(
                    _client.LaunchArgs.MoveX + (_role == "a" ? 0f : 1.5f),
                    _client.LaunchArgs.MoveY,
                    _client.LaunchArgs.MoveZ);
                await _client.SendMoveAsync(target, 0f, default).ConfigureAwait(true);
                Event("move_sent", "x", target.x);
                await WaitAoiMoveAsync(peer, peerX, 45f).ConfigureAwait(true);
                Event("aoi_peer_moved", "peer_id", peer);

                if (_role == "a")
                {
                    var err = await _client.Mail.SendAsync(peer, "e2e", "hello-from-a", default)
                        .ConfigureAwait(true);
                    if (!string.IsNullOrEmpty(err))
                        throw new InvalidOperationException(err);
                    Event("mail_sent", "mail_id", _client.Mail.Page.LastSentMailId);
                    Event("mail_title", "title", "e2e");
                }
                else
                {
                    await WaitMailAsync(45f).ConfigureAwait(true);
                    var mail = _client.Mail.Page.Selected;
                    if (mail?.Brief?.Title != "e2e" || (mail.Body ?? "").IndexOf("hello-from-a", StringComparison.Ordinal) < 0)
                        throw new InvalidOperationException("mail content mismatch");
                    Event("mail_received", "mail_id", mail.Brief.MailId);
                    Event("mail_title", "title", mail.Brief.Title);
                }

                await _client.LogoutAsync().ConfigureAwait(true);
                Event("logout_ok", "player_id", 0);
                if (_client.Session.SessionReplaced)
                    Event("session_replaced", "player_id", _client.Session.PlayerId);
                ok = true;
            }
            catch (Exception ex)
            {
                reason = GameMeshLog.Redact(ex.Message);
                Event("fail", "error", reason);
            }
            finally
            {
                WriteResult(ok, reason);
                _finished = true;
                _running = false;
                if (!Application.isEditor)
                    Application.Quit(ok ? 0 : 1);
            }
        }

        async Task<ulong> WaitPeerAsync(float timeoutSec)
        {
            if (_client.LaunchArgs.PeerPlayerId != 0)
                return _client.LaunchArgs.PeerPlayerId;
            var peerRole = _role == "a" ? "b" : "a";
            var path = Path.Combine(_coordDir, peerRole + ".json");
            var deadline = Time.unscaledTime + timeoutSec;
            while (Time.unscaledTime < deadline)
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    var id = ReadJsonUlong(text, "player_id");
                    if (id != 0)
                        return id;
                }

                await Task.Yield();
            }

            throw new TimeoutException("peer identity not published");
        }

        async Task WaitAoiAsync(ulong peerId, float timeoutSec)
        {
            var deadline = Time.unscaledTime + timeoutSec;
            while (Time.unscaledTime < deadline)
            {
                if (_client.Aoi.Entities.ContainsKey(peerId))
                    return;
                await Task.Yield();
            }

            throw new TimeoutException("aoi peer not seen");
        }

        async Task WaitAoiMoveAsync(ulong peerId, float previousX, float timeoutSec)
        {
            var deadline = Time.unscaledTime + timeoutSec;
            while (Time.unscaledTime < deadline)
            {
                if (_client.Aoi.Entities.TryGetValue(peerId, out var state) &&
                    Mathf.Abs(state.X - previousX) > 0.2f)
                    return;
                await Task.Yield();
            }

            throw new TimeoutException("aoi peer move not seen");
        }

        async Task WaitMailAsync(float timeoutSec)
        {
            var deadline = Time.unscaledTime + timeoutSec;
            while (Time.unscaledTime < deadline)
            {
                await _client.Mail.RefreshAsync(default).ConfigureAwait(true);
                if (_client.Mail.Page.Mails.Count > 0)
                {
                    await _client.Mail.GetAsync(_client.Mail.Page.Mails[0].MailId, default).ConfigureAwait(true);
                    return;
                }

                await Task.Delay(400).ConfigureAwait(true);
            }

            throw new TimeoutException("mail not received");
        }

        void WriteCoord()
        {
            var path = Path.Combine(_coordDir, _role + ".json");
            File.WriteAllText(path,
                "{\"player_id\":" + _client.Session.PlayerId +
                ",\"map_instance_id\":" + _client.Session.MapInstanceId + "}");
        }

        void Event(string name, string key, object value)
        {
            var line = "{\"event\":\"" + name + "\",\"" + key + "\":" + JsonValue(value) + "}\n";
            _events.Append(line);
            try
            {
                File.AppendAllText(Path.Combine(_resultDir, "events.jsonl"), line);
            }
            catch
            {
                /* ignore */
            }
        }

        void WriteResult(bool ok, string reason)
        {
            var duration = (int)((Time.unscaledTime - _startedAt) * 1000f);
            var json =
                "{\"result\":\"" + (ok ? "PASS" : "FAIL") +
                "\",\"scenario\":\"" + Escape(_client.LaunchArgs.AutoScenario) +
                "\",\"role\":\"" + Escape(_role) +
                "\",\"player_id\":" + _client.Session.PlayerId +
                ",\"map_instance_id\":" + _client.Session.MapInstanceId +
                ",\"duration_ms\":" + duration +
                ",\"error_code\":\"" + Escape(_client.LastErrorCode) +
                "\",\"client_commit\":\"" + Escape(ReadClientCommit()) +
                "\",\"server_commit\":\"" + Escape(ReadManifestField("source_commit")) +
                "\",\"schema_sha256\":\"" + Escape(_client.ProtocolSchemaSha256) +
                "\",\"map_manifest_version\":" + _client.Config.dataVersion +
                ",\"gateway\":\"" + Escape(_client.Config.host + ":" + _client.Config.port) +
                "\",\"error\":\"" + Escape(reason) + "\"}\n";
            File.WriteAllText(Path.Combine(_resultDir, "result.json"), json);
            File.AppendAllText(Path.Combine(_resultDir, "events.jsonl"), json);
        }

        static string JsonValue(object value)
        {
            if (value is bool b)
                return b ? "true" : "false";
            if (value is string s)
                return "\"" + Escape(s) + "\"";
            if (value is float f)
                return f.ToString("0.###", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static ulong ReadJsonUlong(string json, string key)
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
            return ulong.TryParse(json.Substring(colon + 1, end - colon - 1).Trim(), out var v) ? v : 0UL;
        }

        string ReadManifestField(string key)
        {
            try
            {
                var path = Path.Combine(Application.dataPath, "GameMesh", "Protocol", "protocol_manifest.json");
                if (!File.Exists(path))
                    return "";
                return ReadJsonString(File.ReadAllText(path), key);
            }
            catch
            {
                return "";
            }
        }

        static string ReadClientCommit()
        {
            var env = Environment.GetEnvironmentVariable("GAMEMESH_CLIENT_COMMIT");
            return string.IsNullOrEmpty(env) ? Application.version : env;
        }

        static string ReadJsonString(string json, string key)
        {
            var token = "\"" + key + "\"";
            var i = json.IndexOf(token, StringComparison.Ordinal);
            if (i < 0)
                return "";
            var colon = json.IndexOf(':', i);
            if (colon < 0)
                return "";
            var q = json.IndexOf('"', colon + 1);
            var q2 = q >= 0 ? json.IndexOf('"', q + 1) : -1;
            return q >= 0 && q2 > q ? json.Substring(q + 1, q2 - q - 1) : "";
        }

        public bool IsFinished => _finished;
        public bool IsRunning => _running;
    }
}
