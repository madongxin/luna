using System;
using System.Threading;
using System.Threading.Tasks;
using GameMesh.Protocol;

namespace GameMesh.Network
{
    public interface IGameConnection : IAsyncDisposable
    {
        ConnectionState State { get; }
        ulong LastClientSeq { get; }
        Task ConnectAsync(string host, int port, CancellationToken ct);
        Task<GameResponse> RequestAsync(GameRequest request, TimeSpan timeout, CancellationToken ct);
        Task DisconnectAsync(DisconnectReason reason, CancellationToken ct);
        event Action<GameResponse> PushReceived;
        event Action<ConnectionState> StateChanged;
    }
}
