using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Bootstrap;
using GameMesh.Network;
using UnityEngine;

namespace GameMesh.LoadTest
{
    public sealed class GameMeshLoadTestRunner : MonoBehaviour
    {
        public static GameMeshLoadTestRunner Instance { get; private set; }

        readonly List<LoadTestBot> _bots = new List<LoadTestBot>();
        CancellationTokenSource _waveCts;
        bool _busy;
        bool _logoutInFlight;
        bool _holdOnline;
        float _holdUntil;
        float _durationSec = 300f;
        string _lastError = "";
        string _phase = "空闲";

        public bool Busy => _busy;
        public bool HoldOnline => _holdOnline;
        public int TargetCount { get; private set; }
        public uint TargetLineNo { get; private set; }
        public int InWorldCount
        {
            get
            {
                var n = 0;
                for (var i = 0; i < _bots.Count; i++)
                {
                    if (_bots[i] != null && _bots[i].InWorld)
                        n++;
                }

                return n;
            }
        }

        public int FailedCount
        {
            get
            {
                var n = 0;
                for (var i = 0; i < _bots.Count; i++)
                {
                    if (_bots[i] != null && _bots[i].Failed)
                        n++;
                }

                return n;
            }
        }

        public float RemainingSec =>
            _holdOnline ? Mathf.Max(0f, _holdUntil - Time.unscaledTime) : 0f;

        public string Phase => _phase;
        public string LastError => _lastError;

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            _ = SweepOrphansOnStartAsync();
        }

        async Task SweepOrphansOnStartAsync()
        {
            if (_busy || _logoutInFlight)
                return;
            _busy = true;
            _phase = "清理残留 Session";
            try
            {
                await SweepLedgerAsync(CancellationToken.None).ConfigureAwait(true);
                _phase = "空闲";
            }
            catch
            {
                _phase = "空闲";
            }
            finally
            {
                _busy = false;
            }
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            _ = LogoutAllAsync();
        }

        void OnApplicationQuit()
        {
            _ = LogoutAllAsync();
        }

        void Update()
        {
            if (_holdOnline && Time.unscaledTime >= _holdUntil)
                _ = LogoutAllAsync();
        }

        public void StartCollectiveLogin(int count, float durationMinutes, int staggerMs, string password,
            int portA, int portB)
        {
            if (_busy || _logoutInFlight)
                return;
            if (portA <= 0)
                portA = 8081;
            if (portB <= 0)
                portB = 8083;
            _ = StartWaveAsync(count, durationMinutes, staggerMs, password, portA, portB);
        }

        public void RequestCollectiveLogout()
        {
            _ = LogoutAllAsync();
        }

        async Task StartWaveAsync(int count, float durationMinutes, int staggerMs, string password,
            int portA, int portB)
        {
            if (_busy || _logoutInFlight)
                return;
            if (_bots.Count > 0)
                await LogoutAllAsync().ConfigureAwait(true);
            if (_logoutInFlight)
                return;
            _busy = true;
            _holdOnline = false;
            _lastError = "";
            try
            {
                count = Mathf.Clamp(count, 1, 20000);
                staggerMs = Mathf.Clamp(staggerMs, 0, 5000);
                durationMinutes = Mathf.Max(0f, durationMinutes);
                _durationSec = durationMinutes * 60f;
                TargetCount = count;
                TargetLineNo = ResolvePreferredLine(GameMeshClient.Instance);
                if (string.IsNullOrEmpty(password) || password.Length < 6)
                    password = "loadtest";
                if (portA <= 0)
                    portA = 8081;
                if (portB <= 0)
                    portB = 8083;

                try { _waveCts?.Cancel(); }
                catch { /* ignore */ }
                _waveCts?.Dispose();
                _waveCts = new CancellationTokenSource();
                var ct = _waveCts.Token;
                _phase = "清理残留 Session";
                await SweepLedgerAsync(ct).ConfigureAwait(true);
                _phase = "集体登录 " + count + "  端口 " + portA + "/" + portB;

                var client = GameMeshClient.Instance;
                var dispatcher = client != null
                    ? GameMeshMainThreadDispatcher.Ensure(client.gameObject)
                    : null;
                var stamp = DateTime.Now.ToString("HHmmssfff");
                var gate = new SemaphoreSlim(20);
                var tasks = new List<Task>(count);
                if (staggerMs <= 0)
                    staggerMs = 40;
                for (var i = 0; i < count; i++)
                {
                    var bot = new LoadTestBot(i, dispatcher);
                    _bots.Add(bot);
                    var device = "lt-" + stamp + "-" + i.ToString("D3");
                    var name = "Bot" + i.ToString("D3");
                    var port = PickPort(i, portA, portB);
                    tasks.Add(EnterWithRetryAsync(bot, device, name, password, port, TargetLineNo, gate, ct));
                    if (i + 1 < count)
                        await Task.Delay(staggerMs, ct).ConfigureAwait(true);
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(true);
                }
                catch
                {
                    /* per-bot errors already recorded */
                }

                if (ct.IsCancellationRequested)
                {
                    _phase = "已取消";
                    return;
                }

                _phase = "补登失败 " + FailedCount;
                await RetryFailedAsync(password, portA, portB, gate, ct).ConfigureAwait(true);

                if (ct.IsCancellationRequested)
                {
                    _phase = "已取消";
                    return;
                }

                _holdOnline = true;
                _holdUntil = _durationSec > 0f
                    ? Time.unscaledTime + _durationSec
                    : float.PositiveInfinity;
                _phase = _durationSec > 0f ? "在线压测" : "在线（手动下线）";
                var live = GameMeshClient.Instance;
                if (live != null && live.Connection != null &&
                    live.Connection.State == ConnectionState.InWorld)
                    _ = live.RequestWorldSnapshotAsync();
            }
            catch (Exception ex)
            {
                _lastError = GameMeshLog.Redact(ex.Message);
                _phase = "失败";
            }
            finally
            {
                _busy = false;
            }
        }

        static async Task EnterWithRetryAsync(LoadTestBot bot, string device, string name, string password, int port,
            uint preferredLine, SemaphoreSlim gate, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        await bot.CloseSessionAsync().ConfigureAwait(true);
                        return;
                    }

                    if (attempt > 0)
                    {
                        await bot.PrepareRetryAsync().ConfigureAwait(true);
                        await Task.Delay(400 * attempt, ct).ConfigureAwait(true);
                    }

                    try
                    {
                        await bot.EnterWorldAsync(device, name, password, port, ct, preferredLine)
                            .ConfigureAwait(true);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        try { await bot.CloseSessionAsync().ConfigureAwait(true); }
                        catch { /* ignore */ }
                        throw;
                    }
                    catch
                    {
                        if (attempt == 2)
                        {
                            try { await bot.CloseSessionAsync().ConfigureAwait(true); }
                            catch { /* ignore */ }
                            return;
                        }
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }

        async Task RetryFailedAsync(string password, int portA, int portB, SemaphoreSlim gate, CancellationToken ct)
        {
            var stamp = DateTime.Now.ToString("HHmmssfff");
            var tasks = new List<Task>();
            for (var i = 0; i < _bots.Count; i++)
            {
                var bot = _bots[i];
                if (bot == null || bot.InWorld || ct.IsCancellationRequested)
                    continue;
                var device = "lt-" + stamp + "-r" + i.ToString("D3");
                var name = "Bot" + i.ToString("D3");
                var port = PickBalancedPort(portA, portB);
                tasks.Add(EnterWithRetryAsync(bot, device, name, password, port, TargetLineNo, gate, ct));
            }

            if (tasks.Count == 0)
                return;
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(true);
            }
            catch
            {
                /* recorded on bot */
            }
        }

        static uint ResolvePreferredLine(GameMeshClient client)
        {
            if (client == null)
                return 1;
            if (client.Lines != null && client.Lines.LineNo != 0)
                return client.Lines.LineNo;
            return client.Config != null && client.Config.mapTemplateId == 1002 ? 1u : 0u;
        }

        static int PickPort(int index, int portA, int portB)
        {
            if (portA <= 0)
                portA = 8081;
            if (portB <= 0)
                portB = 8083;
            if (portA == portB)
                return portA;
            return (index & 1) == 0 ? portA : portB;
        }

        int PickBalancedPort(int portA, int portB)
        {
            if (portA <= 0)
                portA = 8081;
            if (portB <= 0)
                portB = 8083;
            if (portA == portB)
                return portA;
            return InWorldOnPort(portA) <= InWorldOnPort(portB) ? portA : portB;
        }

        public int InWorldOnPort(int port)
        {
            var n = 0;
            for (var i = 0; i < _bots.Count; i++)
            {
                if (_bots[i] != null && _bots[i].InWorld && _bots[i].GatewayPort == port)
                    n++;
            }

            return n;
        }

        async Task LogoutAllAsync()
        {
            if (_logoutInFlight)
                return;
            _logoutInFlight = true;
            _holdOnline = false;
            _busy = true;
            _phase = "集体下线";
            try
            {
                try { _waveCts?.Cancel(); }
                catch { /* ignore */ }

                var snapshot = _bots.ToArray();
                var tasks = new List<Task>(snapshot.Length);
                for (var i = 0; i < snapshot.Length; i++)
                {
                    var bot = snapshot[i];
                    if (bot != null)
                        tasks.Add(LogoutOneAsync(bot));
                }

                try
                {
                    if (tasks.Count > 0)
                        await Task.WhenAll(tasks).ConfigureAwait(true);
                }
                catch
                {
                    /* ignore */
                }

                _bots.Clear();
                TargetCount = 0;
                try
                {
                    await SweepLedgerAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch
                {
                    /* next start will retry */
                }

                _phase = "空闲";
            }
            finally
            {
                _busy = false;
                _logoutInFlight = false;
            }
        }

        async Task SweepLedgerAsync(CancellationToken ct)
        {
            var entries = LoadTestSessionLedger.Snapshot();
            if (entries == null || entries.Length == 0)
                return;
            var client = GameMeshClient.Instance;
            if (client == null)
                return;
            var dispatcher = GameMeshMainThreadDispatcher.Ensure(client.gameObject);
            var gate = new SemaphoreSlim(10);
            var tasks = new List<Task>(entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.token))
                    continue;
                if (!ulong.TryParse(entry.playerId, out var playerId) || playerId == 0)
                    continue;
                var host = string.IsNullOrEmpty(entry.host) ? client.Config.host : entry.host;
                var port = entry.port > 0 ? entry.port : 8083;
                tasks.Add(SweepOneAsync(dispatcher, host, port, playerId, entry.token, gate, ct));
            }

            if (tasks.Count == 0)
                return;
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(true);
            }
            catch
            {
                /* per-entry */
            }
        }

        static async Task SweepOneAsync(IMainThreadDispatcher dispatcher, string host, int port, ulong playerId,
            string token, SemaphoreSlim gate, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                await LoadTestBot.LogoutOrphanAsync(dispatcher, host, port, playerId, token).ConfigureAwait(true);
            }
            catch
            {
                /* keep ledger row */
            }
            finally
            {
                gate.Release();
            }
        }

        static async Task LogoutOneAsync(LoadTestBot bot)
        {
            try
            {
                await bot.LogoutAsync().ConfigureAwait(true);
            }
            catch
            {
                /* ignore */
            }
        }

        public string SampleError()
        {
            if (!string.IsNullOrEmpty(_lastError))
                return _lastError;
            for (var i = 0; i < _bots.Count; i++)
            {
                if (_bots[i] != null && !string.IsNullOrEmpty(_bots[i].LastError))
                    return _bots[i].LastError;
            }

            return "";
        }
    }
}
