using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Protocol;
using Google.Protobuf;

namespace GameMesh.Network
{
    public sealed class GameConnection : IGameConnection
    {
        const int SendQueueLimit = 128;
        const int PendingLimit = 256;
        const int ReadBufferSize = 8192;

        readonly IMainThreadDispatcher _dispatcher;
        readonly ConcurrentDictionary<ulong, PendingCall> _pending = new ConcurrentDictionary<ulong, PendingCall>();
        readonly ConcurrentQueue<SendItem> _sendQueue = new ConcurrentQueue<SendItem>();
        readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        readonly object _stateGate = new object();

        TcpClient _tcp;
        NetworkStream _stream;
        CancellationTokenSource _loopCts;
        Task _recvTask = Task.CompletedTask;
        Task _sendTask = Task.CompletedTask;
        long _seq;
        int _queued;
        int _generation;
        ConnectionState _state = ConnectionState.Disconnected;
        volatile bool _failClosed;

        public ConnectionState State
        {
            get { lock (_stateGate) return _state; }
        }

        public int Generation => _generation;

        public ulong LastClientSeq => (ulong)Interlocked.Read(ref _seq);

        public event Action<GameResponse> PushReceived;
        public event Action<ConnectionState> StateChanged;

        public GameConnection(IMainThreadDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? new ImmediateDispatcher();
        }

        public void SetLogicalState(ConnectionState next)
        {
            lock (_stateGate)
            {
                if (_state == next)
                    return;
                if (!ConnectionStateMachine.CanTransition(_state, next))
                {
                    GameMeshLog.Warn($"skip illegal {_state}->{next}");
                    return;
                }
            }

            SetState(next);
        }

        public async Task ConnectAsync(string host, int port, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new GameMeshException(GameMeshErrorCode.ClientProtocol, "host is empty");
            if (port <= 0 || port > 65535)
                throw new GameMeshException(GameMeshErrorCode.ClientProtocol, "invalid port");

            var current = State;
            if (current != ConnectionState.Disconnected && current != ConnectionState.Reconnecting)
                throw new GameMeshException(GameMeshErrorCode.ClientIllegalState, $"ConnectAsync from {current}");

            SetState(ConnectionState.Connecting);
            TcpClient tcp = null;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                using (ct.Register(() => { try { tcp.Close(); } catch { /* ignore */ } }))
                {
                    var connect = tcp.ConnectAsync(host, port);
                    var completed = await Task.WhenAny(connect, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                    if (completed != connect)
                    {
                        try { tcp.Close(); } catch { /* ignore */ }
                        throw new GameMeshException(GameMeshErrorCode.ClientCancelled, "connect cancelled");
                    }

                    await connect.ConfigureAwait(false);
                }

                if (!tcp.Connected)
                    throw new GameMeshException(GameMeshErrorCode.ClientDisconnected, "connect failed");

                lock (_stateGate)
                {
                    _tcp = tcp;
                    _stream = tcp.GetStream();
                    _failClosed = false;
                    _loopCts = new CancellationTokenSource();
                    _generation++;
                    var loopToken = _loopCts.Token;
                    _recvTask = Task.Run(() => ReceiveLoopAsync(loopToken), loopToken);
                    _sendTask = Task.Run(() => SendLoopAsync(loopToken), loopToken);
                }

                SetState(ConnectionState.Handshaking);
                GameMeshLog.Info($"connected {host}:{port} lastSeq={LastClientSeq} gen={_generation}");
            }
            catch (GameMeshException)
            {
                SafeCloseSocket(tcp);
                SetState(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                SafeCloseSocket(tcp);
                SetState(ConnectionState.Disconnected);
                throw new GameMeshException(GameMeshErrorCode.ClientDisconnected, ex.Message, ex);
            }
        }

        public async Task<GameResponse> RequestAsync(GameRequest request, TimeSpan timeout, CancellationToken ct)
        {
            if (request == null)
                throw new GameMeshException(GameMeshErrorCode.ClientProtocol, "request is null");

            var state = State;
            if (state == ConnectionState.Disconnected || state == ConnectionState.Closing ||
                state == ConnectionState.Connecting)
            {
                throw new GameMeshException(GameMeshErrorCode.ClientNotConnected, $"cannot send in {state}");
            }

            if (request.Seq == 0)
                request.Seq = NextSeq();

            if (_pending.Count >= PendingLimit)
                throw new GameMeshException(GameMeshErrorCode.ClientQueueFull, "too many pending requests");

            var tcs = new TaskCompletionSource<GameResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingCall(tcs);
            if (!_pending.TryAdd(request.Seq, pending))
                throw new GameMeshException(GameMeshErrorCode.ClientProtocol, $"duplicate seq {request.Seq}");

            byte[] frame;
            try
            {
                frame = FrameCodec.Encode(request.ToByteArray());
            }
            catch
            {
                _pending.TryRemove(request.Seq, out _);
                throw;
            }

            if (Interlocked.Increment(ref _queued) > SendQueueLimit)
            {
                Interlocked.Decrement(ref _queued);
                _pending.TryRemove(request.Seq, out _);
                throw new GameMeshException(GameMeshErrorCode.ClientQueueFull, "send queue full");
            }

            _sendQueue.Enqueue(new SendItem(frame, request.Seq, BodyName(request)));
            _sendSignal.Release();

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(timeout);
                using (timeoutCts.Token.Register(() =>
                       {
                           if (_pending.TryRemove(request.Seq, out var call))
                           {
                               var code = ct.IsCancellationRequested
                                   ? GameMeshErrorCode.ClientCancelled
                                   : GameMeshErrorCode.ClientTimeout;
                               call.TrySetException(new GameMeshException(code, $"seq={request.Seq} {code}"));
                           }
                       }))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
        }

        public async Task DisconnectAsync(DisconnectReason reason, CancellationToken ct)
        {
            SetState(ConnectionState.Closing);
            FailPending(new GameMeshException(GameMeshErrorCode.ClientDisconnected, reason.ToString()));
            var cts = _loopCts;
            try { cts?.Cancel(); } catch { /* ignore */ }

            try { _stream?.Close(); } catch { /* ignore */ }
            try { _tcp?.Close(); } catch { /* ignore */ }

            try
            {
                await Task.WhenAll(IgnoreFault(_recvTask), IgnoreFault(_sendTask)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            lock (_stateGate)
            {
                _stream = null;
                _tcp = null;
                _loopCts = null;
            }

            DrainSendQueue();
            SetState(ConnectionState.Disconnected);
            GameMeshLog.Info($"disconnected reason={reason} lastSeq={LastClientSeq}");
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync(DisconnectReason.Dispose, CancellationToken.None).ConfigureAwait(false);
            _sendSignal.Dispose();
            _loopCts?.Dispose();
        }

        ulong NextSeq()
        {
            return (ulong)Interlocked.Increment(ref _seq);
        }

        async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _sendSignal.WaitAsync(ct).ConfigureAwait(false);
                    while (_sendQueue.TryDequeue(out var item))
                    {
                        Interlocked.Decrement(ref _queued);
                        var stream = _stream;
                        if (stream == null)
                            break;
                        await WriteExactAsync(stream, item.Frame, ct).ConfigureAwait(false);
                        GameMeshLog.Info($"send seq={item.Seq} type={item.Type}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                FailClosed(ex);
            }
        }

        async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new List<byte>(4096);
            var read = new byte[ReadBufferSize];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var stream = _stream;
                    if (stream == null)
                        break;
                    var n = await stream.ReadAsync(read, 0, read.Length, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        FailClosed(new GameMeshException(GameMeshErrorCode.ClientDisconnected, "remote closed"));
                        return;
                    }

                    for (var i = 0; i < n; i++)
                        buffer.Add(read[i]);

                    while (true)
                    {
                        var status = FrameCodec.TryDecode(buffer, out var payload, out var length);
                        if (status == FrameDecodeStatus.NeedMore)
                            break;
                        if (status == FrameDecodeStatus.InvalidLength)
                        {
                            FailClosed(new GameMeshException(GameMeshErrorCode.ClientProtocol, $"bad frame length {length}"));
                            return;
                        }

                        GameResponse response;
                        try
                        {
                            response = GameResponse.Parser.ParseFrom(payload);
                        }
                        catch (Exception ex)
                        {
                            FailClosed(new GameMeshException(GameMeshErrorCode.ClientProtocol, "bad protobuf", ex));
                            return;
                        }

                        DispatchResponse(response);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                FailClosed(ex);
            }
        }

        void DispatchResponse(GameResponse response)
        {
            var isPush = response.Seq == 0 || response.BodyCase == GameResponse.BodyOneofCase.ServerPush;
            if (isPush)
            {
                _dispatcher.Enqueue(() => PushReceived?.Invoke(response));
                return;
            }

            if (_pending.TryRemove(response.Seq, out var call))
            {
                _dispatcher.Enqueue(() => call.TrySetResult(response));
                return;
            }

            GameMeshLog.Info($"orphan response seq={response.Seq} type={response.BodyCase}");
        }

        void FailClosed(Exception ex)
        {
            if (_failClosed)
                return;
            _failClosed = true;
            GameMeshLog.Info($"fail-closed {Redact(ex.Message)}");
            FailPending(ex is GameMeshException ge
                ? ge
                : new GameMeshException(GameMeshErrorCode.ClientDisconnected, ex.Message, ex));
            try { _loopCts?.Cancel(); } catch { /* ignore */ }
            try { _stream?.Close(); } catch { /* ignore */ }
            try { _tcp?.Close(); } catch { /* ignore */ }
            _dispatcher.Enqueue(() =>
            {
                if (State != ConnectionState.Disconnected && State != ConnectionState.Closing)
                    SetState(ConnectionState.Disconnected);
            });
        }

        void FailPending(Exception ex)
        {
            foreach (var kv in _pending)
            {
                if (_pending.TryRemove(kv.Key, out var call))
                    call.TrySetException(ex);
            }
        }

        void DrainSendQueue()
        {
            while (_sendQueue.TryDequeue(out _))
                Interlocked.Decrement(ref _queued);
        }

        void SetState(ConnectionState next)
        {
            ConnectionState prev;
            lock (_stateGate)
            {
                prev = _state;
                if (prev == next)
                    return;
                _state = ConnectionStateMachine.Transition(prev, next);
            }

            _dispatcher.Enqueue(() => StateChanged?.Invoke(next));
        }

        static async Task WriteExactAsync(NetworkStream stream, byte[] frame, CancellationToken ct)
        {
            var offset = 0;
            while (offset < frame.Length)
            {
                await stream.WriteAsync(frame, offset, frame.Length - offset, ct).ConfigureAwait(false);
                offset = frame.Length;
            }
        }

        static Task IgnoreFault(Task task)
        {
            return task ?? Task.CompletedTask;
        }

        static void SafeCloseSocket(TcpClient tcp)
        {
            try { tcp?.Close(); } catch { /* ignore */ }
        }

        static string BodyName(GameRequest request) => request.BodyCase.ToString();

        static string Redact(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;
            return message
                .Replace("token", "t***")
                .Replace("password", "p***")
                .Replace("credential", "c***");
        }

        sealed class PendingCall
        {
            readonly TaskCompletionSource<GameResponse> _tcs;

            public PendingCall(TaskCompletionSource<GameResponse> tcs) => _tcs = tcs;

            public bool TrySetResult(GameResponse response) => _tcs.TrySetResult(response);

            public bool TrySetException(Exception ex) => _tcs.TrySetException(ex);
        }

        readonly struct SendItem
        {
            public readonly byte[] Frame;
            public readonly ulong Seq;
            public readonly string Type;

            public SendItem(byte[] frame, ulong seq, string type)
            {
                Frame = frame;
                Seq = seq;
                Type = type;
            }
        }
    }
}
