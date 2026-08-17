using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GameMesh.Auth;
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
        ulong _playerIdBeforeLogout;
        ulong _mapInstanceBeforeLogout;
        bool _helloOk;
        bool _loginOk;
        bool _peerSeen;
        bool _peerMoveSeen;
        bool _logoutRspOk;
        bool _peerLeaveSeen;
        bool _remoteGoSeen;

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
                var scenario = _client.LaunchArgs.AutoScenario ?? "";
                if (scenario == "extended-mail")
                    await RunExtendedMailAsync().ConfigureAwait(true);
                else
                    await RunPresenceMoveLogoutAsync().ConfigureAwait(true);
                ok = true;
            }
            catch (Exception ex)
            {
                reason = GameMeshLog.Redact(ex.Message);
                Event("fail", "error", reason, "error_code", _client.LastErrorCode);
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

        async Task RunPresenceMoveLogoutAsync()
        {
            await BootstrapSessionAsync().ConfigureAwait(true);
            var peer = await WaitPeerAsync(45f).ConfigureAwait(true);
            _client.LaunchArgs.PeerPlayerId = peer;
            await WaitAoiAsync(peer, 45f).ConfigureAwait(true);
            _peerSeen = true;
            Event("aoi_peer_seen", "peer_id", peer, "map_instance_id", _client.Session.MapInstanceId);
            float baselineX;
            ulong baselineSeq;
            CapturePeer(peer, out baselineX, out baselineSeq);
            await WaitRemoteGoAsync(peer, true, 10f).ConfigureAwait(true);
            _remoteGoSeen = true;
            Event("remote_go_seen", "peer_id", peer, "name", "RemotePlayer_" + peer);

            if (_role == "a")
            {
                var target = MoveTarget();
                await _client.SendMoveAsync(target, 0f, default).ConfigureAwait(true);
                Event("move_sent", "x", target.x, "y", target.y, "z", target.z);
                float peerX;
                ulong peerSeq;
                CapturePeer(peer, out peerX, out peerSeq);
                float newX;
                ulong newSeq;
                await WaitAoiMoveAsync(peer, peerX, peerSeq, 45f).ConfigureAwait(true);
                CapturePeer(peer, out newX, out newSeq);
                _peerMoveSeen = true;
                Event("aoi_peer_moved", "peer_id", peer, "old_x", peerX, "new_x", newX,
                    "old_state_seq", peerSeq, "new_state_seq", newSeq);
                SnapshotBeforeLogout();
                var logout = await _client.LogoutAsync().ConfigureAwait(true);
                RecordLogout(logout);
                if (!logout.AuthorityOk)
                    throw new InvalidOperationException("logout_failed:" + logout.ErrorCode + " " + logout.Message);
            }
            else
            {
                float newX;
                ulong newSeq;
                await WaitAoiMoveAsync(peer, baselineX, baselineSeq, 45f).ConfigureAwait(true);
                CapturePeer(peer, out newX, out newSeq);
                _peerMoveSeen = true;
                Event("aoi_peer_moved", "peer_id", peer, "old_x", baselineX, "new_x", newX,
                    "old_state_seq", baselineSeq, "new_state_seq", newSeq);
                var target = MoveTarget();
                await _client.SendMoveAsync(target, 0f, default).ConfigureAwait(true);
                Event("move_sent", "x", target.x, "y", target.y, "z", target.z);
                SnapshotBeforeLogout();
                await WaitAoiLeaveAsync(peer, 45f).ConfigureAwait(true);
                _peerLeaveSeen = true;
                Event("aoi_peer_left", "peer_id", peer, "map_instance_id", _mapInstanceBeforeLogout);
                await WaitRemoteGoAsync(peer, false, 10f).ConfigureAwait(true);
                Event("remote_go_removed", "peer_id", peer, "name", "RemotePlayer_" + peer);
                var logout = await _client.LogoutAsync().ConfigureAwait(true);
                RecordLogout(logout);
                if (!logout.AuthorityOk)
                    throw new InvalidOperationException("logout_failed:" + logout.ErrorCode + " " + logout.Message);
            }
        }

        async Task RunExtendedMailAsync()
        {
            await BootstrapSessionAsync().ConfigureAwait(true);
            var peer = await WaitPeerAsync(45f).ConfigureAwait(true);
            _client.LaunchArgs.PeerPlayerId = peer;
            await WaitAoiAsync(peer, 45f).ConfigureAwait(true);
            _peerSeen = true;
            Event("aoi_peer_seen", "peer_id", peer, "map_instance_id", _client.Session.MapInstanceId);
            if (_role == "a")
            {
                var err = await _client.Mail.SendAsync(peer, "e2e", "hello-from-a", default)
                    .ConfigureAwait(true);
                if (!string.IsNullOrEmpty(err))
                    throw new InvalidOperationException(err);
                Event("mail_sent", "mail_id", _client.Mail.Page.LastSentMailId, "peer_id", peer);
            }
            else
            {
                await WaitMailAsync(45f).ConfigureAwait(true);
                var mail = _client.Mail.Page.Selected;
                if (mail?.Brief?.Title != "e2e" ||
                    (mail.Body ?? "").IndexOf("hello-from-a", StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException("mail content mismatch");
                Event("mail_received", "mail_id", mail.Brief.MailId, "title", mail.Brief.Title);
            }

            SnapshotBeforeLogout();
            var logout = await _client.LogoutAsync().ConfigureAwait(true);
            RecordLogout(logout);
            if (!logout.AuthorityOk)
                throw new InvalidOperationException("logout_failed:" + logout.ErrorCode + " " + logout.Message);
        }

        async Task BootstrapSessionAsync()
        {
            await _client.RegisterThenLoginAsync().ConfigureAwait(true);
            if (!_client.HelloOk)
                throw new InvalidOperationException("hello failed: " + _client.LastErrorCode);
            _helloOk = true;
            Event("hello_ok", "protocol_version", _client.ProtocolVersion,
                "schema_sha256", _client.ProtocolSchemaSha256);
            var hbDeadline = Time.unscaledTime + 15f;
            while (!_client.HeartbeatOk && Time.unscaledTime < hbDeadline)
                await Task.Yield();
            if (_client.HeartbeatOk)
                Event("heartbeat_ok", "rtt_ms", _client.LastRttMs);
            if (_client.Session.PlayerId == 0)
                throw new InvalidOperationException("register/login did not return player_id");
            Event("register_ok", "player_id", _client.Session.PlayerId);
            if (!_client.Session.HasIdentity)
                throw new InvalidOperationException("login failed: " + _client.LastErrorCode);
            Event("login_ok", "player_id", _client.Session.PlayerId);
            WriteCoord();
            var deadline = Time.unscaledTime + 30f;
            while (_client.Connection.State != ConnectionState.InWorld && Time.unscaledTime < deadline)
                await Task.Yield();
            if (_client.Connection.State != ConnectionState.InWorld)
                throw new InvalidOperationException("enter map failed: " + _client.LastErrorCode);
            _loginOk = true;
            Event("enter_map_ok", "map_instance_id", _client.Session.MapInstanceId,
                "player_id", _client.Session.PlayerId);
            WriteCoord();
        }

        void SnapshotBeforeLogout()
        {
            _playerIdBeforeLogout = _client.Session.PlayerId;
            _mapInstanceBeforeLogout = _client.Session.MapInstanceId;
            Event("pre_logout_snapshot", "player_id", _playerIdBeforeLogout,
                "map_instance_id", _mapInstanceBeforeLogout);
        }

        void RecordLogout(LogoutResult logout)
        {
            _logoutRspOk = logout != null && logout.AuthorityOk;
            Event("logout",
                "ok", _logoutRspOk,
                "request_sent", logout != null && logout.RequestSent,
                "top_level_ok", logout != null && logout.TopLevelOk,
                "body_ok", logout != null && logout.BodyOk,
                "transport_disconnected", logout != null && logout.TransportDisconnected,
                "error_code", logout != null ? logout.ErrorCode : "",
                "player_id", _playerIdBeforeLogout);
            if (_logoutRspOk)
                Event("logout_ok", "player_id", _playerIdBeforeLogout, "error_code", "");
            else
                Event("logout_failed", "player_id", _playerIdBeforeLogout,
                    "error_code", logout != null ? logout.ErrorCode : "SERVER_ERROR");
        }

        Vector3 MoveTarget()
        {
            return new Vector3(
                _client.LaunchArgs.MoveX + (_role == "a" ? 0f : 1.5f),
                _client.LaunchArgs.MoveY,
                _client.LaunchArgs.MoveZ);
        }

        void CapturePeer(ulong peerId, out float x, out ulong stateSeq)
        {
            if (_client.Aoi.Entities.TryGetValue(peerId, out var state))
            {
                x = state.X;
                stateSeq = state.StateSeq;
                return;
            }

            x = 0f;
            stateSeq = 0;
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

        async Task WaitAoiMoveAsync(ulong peerId, float previousX, ulong previousSeq, float timeoutSec)
        {
            var deadline = Time.unscaledTime + timeoutSec;
            while (Time.unscaledTime < deadline)
            {
                if (_client.Aoi.Entities.TryGetValue(peerId, out var state) &&
                    Mathf.Abs(state.X - previousX) > 0.2f &&
                    (previousSeq == 0 || state.StateSeq > previousSeq))
                    return;
                await Task.Yield();
            }

            throw new TimeoutException("aoi peer move not seen");
        }

        async Task WaitAoiLeaveAsync(ulong peerId, float timeoutSec)
        {
            var deadline = Time.unscaledTime + timeoutSec;
            while (Time.unscaledTime < deadline)
            {
                if (!_client.Aoi.Entities.ContainsKey(peerId))
                    return;
                await Task.Yield();
            }

            throw new TimeoutException("aoi peer leave not seen");
        }

        async Task WaitRemoteGoAsync(ulong peerId, bool shouldExist, float timeoutSec)
        {
            var name = "RemotePlayer_" + peerId;
            var deadline = Time.unscaledTime + timeoutSec;
            while (Time.unscaledTime < deadline)
            {
                var go = GameObject.Find(name);
                if (shouldExist && go != null)
                    return;
                if (!shouldExist && go == null)
                    return;
                await Task.Yield();
            }

            throw new TimeoutException(shouldExist ? "remote GameObject missing" : "remote GameObject still present");
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

        void Event(string name, params object[] pairs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"event\":\"").Append(name).Append("\"");
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                sb.Append(",\"");
                sb.Append(Convert.ToString(pairs[i], CultureInfo.InvariantCulture));
                sb.Append("\":");
                sb.Append(JsonValue(pairs[i + 1]));
            }

            sb.Append("}\n");
            var line = sb.ToString();
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
            var playerId = _playerIdBeforeLogout != 0 ? _playerIdBeforeLogout : _client.Session.PlayerId;
            var mapId = _mapInstanceBeforeLogout != 0 ? _mapInstanceBeforeLogout : _client.Session.MapInstanceId;
            var json =
                "{\"result\":\"" + (ok ? "PASS" : "FAIL") +
                "\",\"scenario\":\"" + Escape(_client.LaunchArgs.AutoScenario) +
                "\",\"role\":\"" + Escape(_role) +
                "\",\"player_id_before_logout\":" + playerId +
                ",\"map_instance_id_before_logout\":" + mapId +
                ",\"hello_ok\":" + (_helloOk ? "true" : "false") +
                ",\"login_ok\":" + (_loginOk ? "true" : "false") +
                ",\"peer_seen\":" + (_peerSeen ? "true" : "false") +
                ",\"peer_move_seen\":" + (_peerMoveSeen ? "true" : "false") +
                ",\"remote_go_seen\":" + (_remoteGoSeen ? "true" : "false") +
                ",\"logout_rsp_ok\":" + (_logoutRspOk ? "true" : "false") +
                ",\"peer_leave_seen\":" + (_peerLeaveSeen ? "true" : "false") +
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
            if (value == null)
                return "\"\"";
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
