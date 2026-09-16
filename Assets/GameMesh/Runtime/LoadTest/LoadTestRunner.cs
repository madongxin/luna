using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Bootstrap;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Protocol;
using UnityEngine;

namespace GameMesh.LoadTest
{
    public sealed class GameMeshLoadTestRunner : MonoBehaviour
    {
        public static GameMeshLoadTestRunner Instance { get; private set; }

        readonly List<LoadTestBot> _bots = new List<LoadTestBot>();
        CancellationTokenSource _waveCts;
        CancellationTokenSource _sweepCts;
        bool _busy;
        bool _logoutInFlight;
        bool _holdOnline;
        float _holdUntil;
        float _durationSec = 300f;
        float _nextLineRefresh;
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

        void AbortSweep()
        {
            try { _sweepCts?.Cancel(); }
            catch { /* ignore */ }
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
            if (InWorldCount > 0 && Time.unscaledTime >= _nextLineRefresh)
            {
                _nextLineRefresh = Time.unscaledTime + 4f;
                var live = GameMeshClient.Instance;
                if (live != null && live.Session != null && live.Session.HasIdentity)
                    _ = live.QueryMapLinesAsync(true);
            }
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
                AbortSweep();
                var ct = _waveCts.Token;
                var client = GameMeshClient.Instance;
                if (client != null && client.Session != null && client.Session.HasIdentity)
                {
                    try { await client.QueryMapLinesAsync(true).ConfigureAwait(true); }
                    catch { /* occupancy may be stale */ }
                    await EnsureLocalPlayerOnLineAsync(client).ConfigureAwait(true);
                    try { await client.QueryMapLinesAsync(true).ConfigureAwait(true); }
                    catch { /* ignore */ }
                    client.Lines.BindPresence(client.Session.MapInstanceId);
                }

                TargetLineNo = ResolvePreferredLine(GameMeshClient.Instance);
                if (client != null && client.Config.port > 0)
                {
                    portA = client.Config.port;
                    portB = client.Config.port;
                }

                _phase = "集体登录 " + count + "  端口 " + portA + "/" + portB;

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
                {
                    _ = live.RequestWorldSnapshotAsync();
                    _ = live.QueryMapLinesAsync(true);
                }
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
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        await bot.CloseSessionAsync().ConfigureAwait(true);
                        return;
                    }

                    if (attempt > 0)
                    {
                        await bot.PrepareRetryAsync().ConfigureAwait(true);
                        await Task.Delay(500 * attempt, ct).ConfigureAwait(true);
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
                        if (attempt == 4)
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
            if (client == null || client.Lines == null)
                return 0;
            if (client.Lines.LineNo != 0)
                return client.Lines.LineNo;
            if (client.IsOnMap)
                return client.Lines.PickLoadTestLine(0, 0);
            return 0;
        }

        async Task EnsureLocalPlayerOnLineAsync(GameMeshClient client)
        {
            if (client == null || client.Session == null || !client.Session.HasIdentity)
                throw new InvalidOperationException("请先登录本号，再集体登录");
            if (client.Connection == null ||
                client.Connection.State == ConnectionState.Disconnected ||
                client.Connection.State == ConnectionState.Closing ||
                client.Connection.State == ConnectionState.LoggingOut)
                throw new InvalidOperationException("本号还没连上 Gateway");

            if (!client.IsOnMap)
            {
                _phase = "本号进线";
                await client.TryEnterWorldAfterLoginAsync().ConfigureAwait(true);
            }

            if (!client.IsOnMap &&
                GameErrorCatalog.IsSessionMissing(client.LastErrorCode, client.LastError))
            {
                _phase = "本号重新登录";
                await client.LoginAsync().ConfigureAwait(true);
            }

            if (client.IsOnMap && client.Lines.LineNo == 0)
            {
                try { await client.QueryMapLinesAsync(true).ConfigureAwait(true); }
                catch { /* ignore */ }
                client.Lines.BindPresence(client.Session.MapInstanceId);
            }

            if (!client.IsOnMap)
            {
                var why = client.LastErrorUi;
                if (string.IsNullOrEmpty(why))
                    why = client.LastError;
                if (string.IsNullOrEmpty(why))
                    why = "本号未能进线，周围看不到机器人。请先点登录，再点「系统选线进图」。";
                throw new InvalidOperationException(why);
            }

            var now = client.Lines.LineNo;
            client.SetNotice(now != 0
                ? "本号已在 " + now + " 线，机器人会进同一条线"
                : "本号已进图，正在拉视野");
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
                AbortSweep();

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
                    await SweepLedgerAsync(CancellationToken.None, 2000).ConfigureAwait(true);
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

        async Task SweepLedgerAsync(CancellationToken ct, int budgetMs = 2000)
        {
            AbortSweep();
            _sweepCts = new CancellationTokenSource();
            var entries = LoadTestSessionLedger.Snapshot();
            if (entries == null || entries.Length == 0)
                return;
            var client = GameMeshClient.Instance;
            if (client == null)
            {
                LoadTestSessionLedger.ForgetMany(entries);
                return;
            }

            var dispatcher = GameMeshMainThreadDispatcher.Ensure(client.gameObject);
            var gate = new SemaphoreSlim(8);
            var tasks = new List<Task>(entries.Length);
            var budgetMsClamped = Mathf.Clamp(budgetMs, 250, 5000);
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, _sweepCts.Token))
            {
                budget.CancelAfter(budgetMsClamped);
                var token = budget.Token;
                for (var i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.token))
                        continue;
                    if (!ulong.TryParse(entry.playerId, out var playerId) || playerId == 0)
                        continue;
                    var host = string.IsNullOrEmpty(entry.host) ? client.Config.host : entry.host;
                    var port = entry.port > 0 ? entry.port : 8083;
                    tasks.Add(SweepOneAsync(dispatcher, host, port, playerId, entry.token, gate, token));
                }

                if (tasks.Count > 0)
                {
                    var all = Task.WhenAll(tasks);
                    var cap = Task.Delay(budgetMsClamped);
                    try
                    {
                        await Task.WhenAny(all, cap).ConfigureAwait(true);
                    }
                    catch
                    {
                        /* per-entry */
                    }

                    try { budget.Cancel(); }
                    catch { /* ignore */ }
                }
            }

            LoadTestSessionLedger.ForgetMany(entries);
        }

        static async Task SweepOneAsync(IMainThreadDispatcher dispatcher, string host, int port, ulong playerId,
            string token, SemaphoreSlim gate, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                await LoadTestBot.LogoutOrphanAsync(dispatcher, host, port, playerId, token, ct)
                    .ConfigureAwait(true);
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
            var counts = new Dictionary<string, int>();
            var first = "";
            for (var i = 0; i < _bots.Count; i++)
            {
                var err = _bots[i] != null ? _bots[i].LastError : "";
                if (string.IsNullOrEmpty(err))
                    continue;
                if (string.IsNullOrEmpty(first))
                    first = err;
                int n;
                counts.TryGetValue(err, out n);
                counts[err] = n + 1;
            }

            if (counts.Count == 0)
                return "";
            var top = first;
            var topN = 0;
            foreach (var pair in counts)
            {
                if (pair.Value > topN)
                {
                    top = pair.Key;
                    topN = pair.Value;
                }
            }

            return topN > 1 ? top + " ×" + topN : top;
        }
    }
}
