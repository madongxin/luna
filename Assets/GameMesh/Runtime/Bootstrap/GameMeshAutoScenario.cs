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
                Event("hello_skipped", "note", _client.ServerBlockedNotes);
                await _client.RegisterAsync().ConfigureAwait(true);
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

                var target = new Vector3(_client.LaunchArgs.MoveX, _client.LaunchArgs.MoveY, _client.LaunchArgs.MoveZ);
                await _client.SendMoveAsync(target, 0f, default).ConfigureAwait(true);
                Event("move_sent", "x", target.x);

                if (_role == "a")
                {
                    var err = await _client.Mail.SendAsync(peer, "e2e", "hello-from-a", default)
                        .ConfigureAwait(true);
                    if (!string.IsNullOrEmpty(err))
                        throw new InvalidOperationException(err);
                    Event("mail_sent", "mail_id", _client.Mail.Page.LastSentMailId);
                }
                else
                {
                    await WaitMailAsync(45f).ConfigureAwait(true);
                    Event("mail_received", "mail_id", _client.Mail.Page.Selected?.Brief?.MailId ?? 0);
                }

                await _client.LogoutAsync().ConfigureAwait(true);
                Event("logout_ok", "player_id", 0);
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
                ",\"error\":\"" + Escape(reason) + "\"}\n";
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

        public bool IsFinished => _finished;
        public bool IsRunning => _running;
    }
}
