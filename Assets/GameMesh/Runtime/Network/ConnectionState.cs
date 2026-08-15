using System;
using System.Collections.Generic;

namespace GameMesh.Network
{
    public enum ConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Authenticating = 3,
        Authenticated = 4,
        EnteringWorld = 5,
        InWorld = 6,
        Reconnecting = 7,
        Closing = 8,
        Resyncing = 9
    }

    public enum DisconnectReason
    {
        None = 0,
        UserLogout = 1,
        ClientRequest = 2,
        ProtocolError = 3,
        Timeout = 4,
        RemoteClose = 5,
        Reconnect = 6,
        Dispose = 7,
        Cancelled = 8
    }

    public static class ConnectionStateMachine
    {
        static readonly Dictionary<ConnectionState, HashSet<ConnectionState>> Allowed =
            new Dictionary<ConnectionState, HashSet<ConnectionState>>
            {
                [ConnectionState.Disconnected] = new HashSet<ConnectionState>
                {
                    ConnectionState.Connecting, ConnectionState.Closing
                },
                [ConnectionState.Connecting] = new HashSet<ConnectionState>
                {
                    ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Closing
                },
                [ConnectionState.Connected] = new HashSet<ConnectionState>
                {
                    ConnectionState.Authenticating, ConnectionState.Closing,
                    ConnectionState.Disconnected, ConnectionState.Reconnecting
                },
                [ConnectionState.Authenticating] = new HashSet<ConnectionState>
                {
                    ConnectionState.Authenticated, ConnectionState.Closing,
                    ConnectionState.Disconnected, ConnectionState.Reconnecting
                },
                [ConnectionState.Authenticated] = new HashSet<ConnectionState>
                {
                    ConnectionState.EnteringWorld, ConnectionState.Closing,
                    ConnectionState.Disconnected, ConnectionState.Reconnecting,
                    ConnectionState.Resyncing
                },
                [ConnectionState.EnteringWorld] = new HashSet<ConnectionState>
                {
                    ConnectionState.InWorld, ConnectionState.Authenticated,
                    ConnectionState.Closing, ConnectionState.Disconnected, ConnectionState.Reconnecting
                },
                [ConnectionState.InWorld] = new HashSet<ConnectionState>
                {
                    ConnectionState.Authenticated, ConnectionState.Reconnecting,
                    ConnectionState.Resyncing, ConnectionState.Closing, ConnectionState.Disconnected
                },
                [ConnectionState.Resyncing] = new HashSet<ConnectionState>
                {
                    ConnectionState.InWorld, ConnectionState.Authenticated,
                    ConnectionState.Reconnecting, ConnectionState.Closing, ConnectionState.Disconnected
                },
                [ConnectionState.Reconnecting] = new HashSet<ConnectionState>
                {
                    ConnectionState.Connecting, ConnectionState.Connected,
                    ConnectionState.Authenticated, ConnectionState.InWorld,
                    ConnectionState.Resyncing, ConnectionState.Closing, ConnectionState.Disconnected
                },
                [ConnectionState.Closing] = new HashSet<ConnectionState>
                {
                    ConnectionState.Disconnected
                }
            };

        public static bool CanTransition(ConnectionState from, ConnectionState to)
        {
            if (from == to)
                return true;
            return Allowed.TryGetValue(from, out var set) && set.Contains(to);
        }

        public static ConnectionState Transition(ConnectionState from, ConnectionState to)
        {
            if (!CanTransition(from, to))
            {
                throw new GameMeshException(
                    GameMeshErrorCode.ClientIllegalState,
                    $"illegal ConnectionState {from} -> {to}");
            }

            return to;
        }
    }

    public static class GameMeshErrorCode
    {
        public const string ClientNotConnected = "CLIENT_NOT_CONNECTED";
        public const string ClientIllegalState = "CLIENT_ILLEGAL_STATE";
        public const string ClientTimeout = "CLIENT_TIMEOUT";
        public const string ClientCancelled = "CLIENT_CANCELLED";
        public const string ClientProtocol = "CLIENT_PROTOCOL";
        public const string ClientQueueFull = "CLIENT_QUEUE_FULL";
        public const string ClientDisconnected = "CLIENT_DISCONNECTED";
        public const string ClientInvalidCoord = "CLIENT_INVALID_COORD";
        public const string MapHashMismatch = "MAP_HASH_MISMATCH";
        public const string ProtocolMissing = "PROTOCOL_MISSING_TYPE";
        public const string ServerError = "SERVER_ERROR";
        public const string HelloBlocked = "BLOCKED_BY_SERVER_HELLO";
        public const string SnapshotBlocked = "BLOCKED_BY_SERVER_SNAPSHOT";
    }

    public sealed class GameMeshException : Exception
    {
        public string ErrorCode { get; }

        public GameMeshException(string errorCode, string message, Exception inner = null)
            : base(message, inner)
        {
            ErrorCode = errorCode ?? GameMeshErrorCode.ServerError;
        }
    }
}
