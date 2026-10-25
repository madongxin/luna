using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Bootstrap;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Player;
using GameMesh.Protocol;
using UnityEngine;

namespace GameMesh.LoadTest
{
    /// <summary>
    /// Client-side stall meter. A dedicated thread that oversleeps means the whole process
    /// stopped (GC or CPU); a slow pool probe means callbacks wait for a pool thread.
    /// Either one delays every bot RPC regardless of server speed.
    /// </summary>
    static class ProcessStallProbe
    {
        const int TickMs = 20;
        static Thread _thread;
        static long _stallMax;
        static long _poolMax;
        static long _poolQueuedAt;

        public static void EnsureStarted()
        {
            if (_thread != null)
                return;
            _thread = new Thread(Run) { IsBackground = true, Name = "GameMesh stall probe" };
            _thread.Start();
        }

        public static void TakeWindow(out long stallMs, out long poolMs)
        {
            stallMs = Interlocked.Exchange(ref _stallMax, 0);
            poolMs = Interlocked.Exchange(ref _poolMax, 0);
            var queuedAt = Interlocked.Read(ref _poolQueuedAt);
            if (queuedAt != 0)
            {
                var waiting = System.Diagnostics.Stopwatch.GetTimestamp() - queuedAt;
                var ms = waiting * 1000L / System.Diagnostics.Stopwatch.Frequency;
                if (ms > poolMs)
                    poolMs = ms;
            }
        }

        static void Run()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var nextPoolProbe = 0L;
            while (true)
            {
                var before = clock.ElapsedMilliseconds;
                Thread.Sleep(TickMs);
                var over = clock.ElapsedMilliseconds - before - TickMs;
                Raise(ref _stallMax, over);
                var now = clock.ElapsedMilliseconds;
                if (now >= nextPoolProbe && Interlocked.Read(ref _poolQueuedAt) == 0)
                {
                    nextPoolProbe = now + 200;
                    Interlocked.Exchange(ref _poolQueuedAt, System.Diagnostics.Stopwatch.GetTimestamp());
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var queuedAt = Interlocked.Exchange(ref _poolQueuedAt, 0);
                        if (queuedAt == 0)
                            return;
                        var waited = System.Diagnostics.Stopwatch.GetTimestamp() - queuedAt;
                        Raise(ref _poolMax, waited * 1000L / System.Diagnostics.Stopwatch.Frequency);
                    });
                }
            }
        }

        static void Raise(ref long slot, long value)
        {
            long cur;
            while (value > (cur = Interlocked.Read(ref slot)))
            {
                if (Interlocked.CompareExchange(ref slot, value, cur) == cur)
                    return;
            }
        }
    }

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
        public BotMovementSettings Movement { get; } = new BotMovementSettings();
        public string MovementExport { get; private set; } = "";

        void Awake()
        {
            Instance = this;
            ProcessStallProbe.EnsureStarted();
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

        float _nextRateSample = -1f;
        long _rateLate;
        long _rateWritten;
        long _rateMatched;
        long _rateTimeout;
        int _gc0;
        int _gc1;
        int _gc2;
        bool _rateReady;
        long _rateRxBytes;
        long _rateRxFrames;
        float _rateAt;
        public float MaxRxMbps { get; private set; }
        public int MaxStallMs { get; private set; }
        public int MaxPoolDelayMs { get; private set; }
        public string StallText { get; private set; } = "";
        public string GatewayRateText { get; private set; } = "网关每秒 尚无采样";
        public int MaxFrameMs { get; private set; }
        public int MaxDispatcherBacklog { get; private set; }
        public int MaxReadLoopLagMs { get; private set; }

        public long PipeWriteCompleted()
        {
            long n = 0;
            for (var i = 0; i < _bots.Count; i++)
            {
                var pipe = _bots[i] != null ? _bots[i].PipeMetrics : null;
                if (pipe != null)
                    n += pipe.WriteCompleted;
            }

            return n;
        }

        public string GatewayRateLine()
        {
            return GatewayRateText +
                   "    帧峰值 " + MaxFrameMs + "ms" +
                   "    主线程队列 " + MaxDispatcherBacklog +
                   "    读循环峰值 " + MaxReadLoopLagMs + "ms" +
                   (string.IsNullOrEmpty(StallText) ? "" : "\n" + StallText);
        }

        void Update()
        {
            var frameMs = (int)(Time.unscaledDeltaTime * 1000f);
            if (frameMs > MaxFrameMs)
                MaxFrameMs = frameMs;
            var live = GameMeshClient.Instance;
            var dispatcher = live != null ? live.GetComponent<GameMeshMainThreadDispatcher>() : null;
            if (dispatcher != null && dispatcher.Backlog > MaxDispatcherBacklog)
                MaxDispatcherBacklog = dispatcher.Backlog;
            if (_nextRateSample < 0f)
                _nextRateSample = Time.unscaledTime + 1f;
            if (Time.unscaledTime >= _nextRateSample)
            {
                _nextRateSample = Time.unscaledTime + 1f;
                SampleGatewayRates();
            }

            if (_holdOnline && Time.unscaledTime >= _holdUntil)
                _ = LogoutAllAsync();
            if (InWorldCount > 0 && Time.unscaledTime >= _nextLineRefresh)
            {
                _nextLineRefresh = Time.unscaledTime + 5f;
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

        static void RaisePoolForLoad(int bots)
        {
            var need = bots * 4;
            if (need < 64)
                need = 64;
            if (need > 4096)
                need = 4096;
            int workers;
            int io;
            ThreadPool.GetMinThreads(out workers, out io);
            if (workers < need || io < need)
                ThreadPool.SetMinThreads(workers > need ? workers : need, io > need ? io : need);
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
                if (count > MoveStepLimit.SuzhouMaxPlayers)
                {
                    _lastError = "1002 最多 8 条线、每线 400 人，一共 3200。这一波没有启动";
                    _phase = "超出 1002 容量";
                    _busy = false;
                    return;
                }

                count = Mathf.Clamp(count, 1, MoveStepLimit.SuzhouMaxPlayers);
                if (Movement.SyncWave && count > 50)
                {
                    _lastError = "同步波次最多 50 人，这一波没有启动";
                    _busy = false;
                    return;
                }

                staggerMs = Mathf.Clamp(staggerMs, 0, 5000);
                durationMinutes = Mathf.Max(0f, durationMinutes);
                // 每个机器人两条循环再加收发。线程池若按 CPU 数慢慢扩，应答回调会排到几秒之后，
                // 这一步的 Move 就一直占着名额，后面的节拍全部记成忙跳过。
                RaisePoolForLoad(count);
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
                ResolveLoadTestPorts(ref portA, ref portB);

                _phase = "集体登录 " + count + "  端口 " + portA + "/" + portB;

                LoadTestSessionLedger.CachePathOnMainThread();
                // Load bots do not touch Unity objects from push callbacks. Keeping hundreds of
                // bot AOI callbacks on the Unity dispatcher made rendering pay for load generation.
                // The local playable client still uses GameMeshMainThreadDispatcher.
                var dispatcher = new ImmediateDispatcher();
                var stamp = DateTime.Now.ToString("HHmmssfff");
                var gate = new SemaphoreSlim(20);
                var tasks = new List<Task>(count);
                if (staggerMs <= 0)
                    staggerMs = 40;
                for (var i = 0; i < count; i++)
                {
                    if (i > 0 && i % 25 == 0 && client != null && client.LastRttMs < 3000)
                    {
                        try { await client.QueryMapLinesAsync(true).ConfigureAwait(true); }
                        catch { /* later bots still ask the server to choose a line */ }
                    }

                    var bot = new LoadTestBot(i, dispatcher);
                    bot.Movement = Movement.Clone();
                    // 0 = server default TTL (7200s). Heartbeat does not renew the key.
                    bot.SessionTtlSec = 0;
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
                DumpFailureReport("after-wave");
                await RetryFailedAsync(password, portA, portB, gate, ct).ConfigureAwait(true);
                DumpFailureReport("after-retry");

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
                    _ = live.QueryMapLinesAsync(true);
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
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        await bot.CloseSessionAsync().ConfigureAwait(true);
                        return;
                    }

                    if (attempt > 0)
                    {
                        if (bot.InWorld)
                            return;
                        var live = GameMeshClient.Instance;
                        if (live != null)
                        {
                            try { await live.QueryMapLinesAsync(true).ConfigureAwait(true); }
                            catch { /* retry with the last line list */ }
                        }

                        await bot.PrepareRetryAsync().ConfigureAwait(true);
                        await Task.Delay(Math.Min(4000, 500 * attempt), ct).ConfigureAwait(true);
                    }

                    try
                    {
                        await bot.EnterWorldAsync(device, name, password, port, ct, preferredLine)
                            .ConfigureAwait(true);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        if (!bot.InWorld)
                        {
                            try { await bot.CloseSessionAsync().ConfigureAwait(true); }
                            catch { /* ignore */ }
                        }

                        throw;
                    }
                    catch
                    {
                        if (bot.InWorld)
                            return;
                        if (attempt == 7)
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
                GameErrorCatalog.IsSessionMissing(client.LastErrorCode))
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

        static void ResolveLoadTestPorts(ref int portA, ref int portB)
        {
            if (portA <= 0)
                portA = 8081;
            if (portB <= 0)
                portB = 8083;
            if (portA == portB && (portA == 8081 || portA == 8083))
            {
                portA = 8081;
                portB = 8083;
            }
        }

        static int PickPort(int index, int portA, int portB)
        {
            ResolveLoadTestPorts(ref portA, ref portB);
            if (portA == portB)
                return portA;
            return (index & 1) == 0 ? portA : portB;
        }

        int PickBalancedPort(int portA, int portB)
        {
            ResolveLoadTestPorts(ref portA, ref portB);
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
            if (FailedCount > 0)
                DumpFailureReport("before-logout");
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

                // Bots are gone; AOI Leave Push often lags or never arrives → clear ghost models.
                ResyncLocalPresenceAfterBots();
                _phase = "空闲";
            }
            finally
            {
                _busy = false;
                _logoutInFlight = false;
            }
        }

        static void ResyncLocalPresenceAfterBots()
        {
            var client = GameMeshClient.Instance;
            if (client == null)
                return;
            try
            {
                client.Aoi.Clear();
                var binder = client.GetComponent<GameMeshWorldBinder>();
                if (binder != null)
                    binder.PurgeAllRemoteViews("loadtest-logout");
                if (client.IsOnMap)
                {
                    _ = client.QueryMapLinesAsync(true);
                    _ = client.RequestSnapshotAfterBotsLeftAsync();
                }
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn("resync after bots " + GameMeshLog.Redact(ex.Message));
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
                return;

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
            var ranked = RankErrors(3);
            if (ranked.Count == 0)
                return "";
            return string.Join("  ||  ", ranked);
        }

        /// <summary>Top failure classes with one concrete example each (for server核对).</summary>
        public List<string> RankErrors(int maxBuckets)
        {
            var counts = new Dictionary<string, int>();
            var example = new Dictionary<string, string>();
            for (var i = 0; i < _bots.Count; i++)
            {
                var bot = _bots[i];
                if (bot == null || string.IsNullOrEmpty(bot.LastError))
                    continue;
                var bucket = bot.LastErrorBucket;
                if (string.IsNullOrEmpty(bucket))
                {
                    var pipe = bot.LastError.IndexOf(" | ", StringComparison.Ordinal);
                    bucket = pipe > 0 ? bot.LastError.Substring(0, pipe) : bot.LastError;
                }

                int n;
                counts.TryGetValue(bucket, out n);
                counts[bucket] = n + 1;
                if (!example.ContainsKey(bucket))
                    example[bucket] = bot.LastError;
            }

            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var outList = new List<string>();
            var take = Math.Min(maxBuckets, list.Count);
            for (var i = 0; i < take; i++)
            {
                var bucket = list[i].Key;
                var n = list[i].Value;
                string eg;
                example.TryGetValue(bucket, out eg);
                if (string.IsNullOrEmpty(eg))
                    eg = bucket;
                outList.Add(n > 1 ? eg + " ×" + n : eg);
            }

            return outList;
        }

        public string ConfigureMovement(int hz, int jitterPct, int seed, string singleProfile, string weights)
        {
            Movement.TargetHz = hz > 0 ? hz : 5;
            Movement.JitterPct = jitterPct;
            Movement.Seed = seed;
            Movement.SingleProfile = (singleProfile ?? "").Trim();
            if (!string.IsNullOrEmpty(weights))
            {
                var parts = weights.Split(',');
                if (parts.Length == 5 &&
                    int.TryParse(parts[0].Trim(), out var idle) &&
                    int.TryParse(parts[1].Trim(), out var wander) &&
                    int.TryParse(parts[2].Trim(), out var shuttle) &&
                    int.TryParse(parts[3].Trim(), out var hotspot) &&
                    int.TryParse(parts[4].Trim(), out var patrol))
                {
                    Movement.IdlePct = idle;
                    Movement.WanderPct = wander;
                    Movement.ShuttlePct = shuttle;
                    Movement.HotspotPct = hotspot;
                    Movement.PatrolPct = patrol;
                }
            }

            return Movement.Validate();
        }

        public BotMovementMetrics SumMovement()
        {
            var sum = new BotMovementMetrics();
            for (var i = 0; i < _bots.Count; i++)
            {
                var metrics = _bots[i] != null ? _bots[i].Metrics : null;
                if (metrics != null)
                    sum.Add(metrics);
            }

            return sum;
        }

        void SampleGatewayRates()
        {
            long late = 0;
            long written = 0;
            long matched = 0;
            long timeout = 0;
            long lag = 0;
            long rxBytes = 0;
            long rxFrames = 0;
            var ports = new Dictionary<int, int>();
            for (var i = 0; i < _bots.Count; i++)
            {
                var bot = _bots[i];
                if (bot == null)
                    continue;
                var pipe = bot.PipeMetrics;
                if (pipe != null)
                {
                    late += pipe.Late;
                    written += pipe.BytesSent;
                    matched += pipe.PendingHit;
                    timeout += pipe.Timeout;
                    rxBytes += Interlocked.Read(ref pipe.BytesReceived);
                    rxFrames += Interlocked.Read(ref pipe.FramesDecoded);
                    if (pipe.ReadLoopLagMs > lag)
                        lag = pipe.ReadLoopLagMs;
                }

                if (bot.Presence != "in_world")
                    continue;
                var port = bot.GatewayPort;
                if (!ports.ContainsKey(port))
                    ports[port] = 0;
                ports[port]++;
            }

            if (lag > MaxReadLoopLagMs)
                MaxReadLoopLagMs = (int)lag;
            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            var d0 = _gc0 == 0 && gc0 > 0 ? gc0 : gc0 - _gc0;
            var d1 = gc1 - _gc1;
            var d2 = gc2 - _gc2;
            if (!_rateReady)
            {
                _gc0 = gc0;
                _gc1 = gc1;
                _gc2 = gc2;
                _rateLate = late;
                _rateWritten = written;
                _rateMatched = matched;
                _rateTimeout = timeout;
                _rateRxBytes = rxBytes;
                _rateRxFrames = rxFrames;
                _rateAt = Time.unscaledTime;
                _rateReady = true;
                GatewayRateText = "网关每秒 采样中";
                return;
            }

            var elapsed = Mathf.Max(0.001f, Time.unscaledTime - _rateAt);
            var rxKBps = (rxBytes - _rateRxBytes) / 1024f / elapsed;
            var rxFps = (rxFrames - _rateRxFrames) / elapsed;
            var rxMbps = rxKBps * 8f * 1024f / 1000000f;
            _rateRxBytes = rxBytes;
            _rateRxFrames = rxFrames;
            _rateAt = Time.unscaledTime;
            if (rxMbps > MaxRxMbps)
                MaxRxMbps = rxMbps;
            ProcessStallProbe.TakeWindow(out var stallMs, out var poolMs);
            if (stallMs > MaxStallMs)
                MaxStallMs = (int)stallMs;
            if (poolMs > MaxPoolDelayMs)
                MaxPoolDelayMs = (int)poolMs;
            int poolWorkers, poolIo, poolMaxWorkers, poolMaxIo;
            ThreadPool.GetAvailableThreads(out poolWorkers, out poolIo);
            ThreadPool.GetMaxThreads(out poolMaxWorkers, out poolMaxIo);
            StallText = "进程停顿 " + stallMs + "ms(峰值 " + MaxStallMs + ")  线程池排队 " + poolMs +
                        "ms(峰值 " + MaxPoolDelayMs + ")  池占用 " + (poolMaxWorkers - poolWorkers) + "/" +
                        (poolMaxIo - poolIo);

            _gc0 = gc0;
            _gc1 = gc1;
            _gc2 = gc2;
            var parts = new List<string>();
            foreach (var pair in ports)
                parts.Add(":" + pair.Key + " 在线" + pair.Value);
            parts.Sort(StringComparer.Ordinal);
            GatewayRateText =
                "每秒 写出 " + (written - _rateWritten) +
                "  匹配 " + (matched - _rateMatched) +
                "  晚到 " + (late - _rateLate) +
                "  超时 " + (timeout - _rateTimeout) +
                "    下行 " + rxKBps.ToString("F0") + "KB/s (" + rxMbps.ToString("F1") + "Mbps 峰值 " +
                MaxRxMbps.ToString("F1") + ") 帧 " + rxFps.ToString("F0") + "/s" +
                "    GC " + d0 + "/" + d1 + "/" + d2 +
                (parts.Count > 0 ? "    " + string.Join(" ", parts.ToArray()) : "");
            _rateLate = late;
            _rateWritten = written;
            _rateMatched = matched;
            _rateTimeout = timeout;
        }

        public string LineSpread()
        {
            var counts = new Dictionary<string, int>();
            for (var i = 0; i < _bots.Count; i++)
            {
                var bot = _bots[i];
                if (bot == null || bot.Presence != "in_world")
                    continue;
                var key = "实例" + bot.MapInstanceId + " 线" + bot.LineNo + " :" + bot.GatewayPort;
                if (!counts.ContainsKey(key))
                    counts[key] = 0;
                counts[key]++;
            }

            if (counts.Count == 0)
                return "实例分布 无";
            var parts = new List<string>();
            foreach (var pair in counts)
                parts.Add(pair.Key + " " + pair.Value + "人");
            parts.Sort(StringComparer.Ordinal);
            return "实例分布 " + string.Join(" | ", parts.ToArray());
        }

        public string PresenceSummary()
        {
            var inWorld = 0;
            var entering = 0;
            var migrating = 0;
            var disconnected = 0;
            var idle = 0;
            var poseUncertain = 0;
            for (var i = 0; i < _bots.Count; i++)
            {
                var bot = _bots[i];
                if (bot == null)
                    continue;
                if (bot.PoseUncertain)
                    poseUncertain++;
                switch (bot.Presence)
                {
                    case "in_world": inWorld++; break;
                    case "entering": entering++; break;
                    case "migrating": migrating++; break;
                    case "disconnected": disconnected++; break;
                    default: idle++; break;
                }
            }

            return "在线 " + inWorld + "  进入中 " + entering +
                   "  迁移 " + migrating + "  断开 " + disconnected +
                   "  未开始 " + idle + "  权威待定 " + poseUncertain;
        }

        public string ExportMovementMetrics()
        {
            try
            {
                var dir = Path.Combine(Application.dataPath, "..", "Logs");
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var sum = SumMovement();
                var jsonPath = Path.Combine(dir, "movement-metrics-" + stamp + ".json");
                File.WriteAllText(jsonPath, sum.ToJson("client-" + stamp));
                var csvPath = Path.Combine(dir, "movement-metrics-" + stamp + ".csv");
                File.WriteAllText(csvPath,
                    "move_planned,move_sent,move_ack_ok,move_rejected,move_timeout,move_skipped_busy,move_skipped_not_ready,move_skipped_pose_uncertain,move_skipped_session_not_ready,move_skipped_recovery_hold,move_paused,path_rejected,corrections,actual_send_hz,actual_success_hz,p50,p95,p99,scheduler_lag_ms,reject_too_fast,reject_bounds,reject_unwalkable,reject_other\n" +
                    sum.Planned + "," + sum.Sent + "," + sum.AckOk + "," + sum.Rejected + "," +
                    sum.Timeout + "," + sum.SkippedBusy + "," + sum.SkippedNotReady + "," +
                    sum.SkippedPoseUncertain + "," + sum.SkippedSessionNotReady + "," +
                    sum.SkippedRecoveryHold + "," +
                    sum.Paused + "," + sum.PathRejected + "," + sum.Corrections + "," +
                    sum.ActualSendHz.ToString("0.###") + "," + sum.ActualSuccessHz.ToString("0.###") + "," +
                    sum.LatencyPercentile(50) + "," + sum.LatencyPercentile(95) + "," +
                    sum.LatencyPercentile(99) + "," + sum.SchedulerLagMs + "," +
                    sum.RejectTooFast + "," + sum.RejectBounds + "," + sum.RejectUnwalkable + "," +
                    sum.RejectOther + "\n");
                MovementExport = jsonPath;
                return jsonPath;
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn("movement export failed " + ex.Message);
                return "";
            }
        }

        public string DumpFailureReport(string tag)
        {
            if (FailedCount <= 0)
                return "";
            try
            {
                var dir = Path.Combine(Application.dataPath, "..", "Logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir,
                    "loadtest-fail-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + tag + ".txt");
                var sb = new System.Text.StringBuilder(4096);
                sb.AppendLine("tag=" + tag);
                sb.AppendLine("phase=" + _phase);
                sb.AppendLine("target=" + TargetCount + " in_world=" + InWorldCount +
                              " failed=" + FailedCount + " line=" + TargetLineNo);
                sb.AppendLine("--- buckets ---");
                foreach (var line in RankErrors(20))
                    sb.AppendLine(line);
                sb.AppendLine("--- all failed bots ---");
                for (var i = 0; i < _bots.Count; i++)
                {
                    var bot = _bots[i];
                    if (bot == null || !bot.Failed || string.IsNullOrEmpty(bot.LastError))
                        continue;
                    sb.AppendLine(bot.LastError);
                }

                File.WriteAllText(path, sb.ToString());
                GameMeshLog.Warn("loadtest failure report: " + path);
                _lastError = "报告 " + Path.GetFileName(path) + "  " + SampleError();
                return path;
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn("loadtest dump failed " + ex.Message);
                return "";
            }
        }
    }
}
