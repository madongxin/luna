using System;
using GameMesh.Network;
using GameMesh.Player;

namespace GameMesh.Auth
{
    public sealed class GameSession
    {
        public ulong PlayerId;
        public string SessionId;
        public string Token;
        public ulong Generation;
        public ulong LastServerSeq;
        public ulong MapTemplateId;
        public ulong MapInstanceId;
        public ulong OwnerEpoch;
        public ulong RouteVersion;
        public string DisplayName;
        public string DeviceId;
        public bool AutoReconnect = true;
        public PlayerAttributeSnapshot Attributes = new PlayerAttributeSnapshot();

        public bool HasIdentity => PlayerId != 0 && !string.IsNullOrEmpty(SessionId);

        public void ApplyLogin(ulong playerId, string sessionId, string token, ulong generation, string displayName)
        {
            PlayerId = playerId;
            SessionId = sessionId ?? "";
            Token = token ?? "";
            Generation = generation;
            DisplayName = displayName ?? DisplayName;
        }

        public void ApplyReconnect(string sessionId, string token, ulong generation)
        {
            if (!string.IsNullOrEmpty(sessionId))
                SessionId = sessionId;
            Token = token ?? "";
            Generation = generation;
        }

        public void ApplyMap(ulong templateId, ulong instanceId, ulong ownerEpoch, ulong routeVersion)
        {
            MapTemplateId = templateId;
            MapInstanceId = instanceId;
            OwnerEpoch = ownerEpoch;
            RouteVersion = routeVersion;
        }

        public void ClearSensitive()
        {
            Token = null;
            SessionId = null;
            Generation = 0;
            LastServerSeq = 0;
            PlayerId = 0;
            MapInstanceId = 0;
            OwnerEpoch = 0;
            RouteVersion = 0;
            AutoReconnect = true;
            Attributes = new PlayerAttributeSnapshot();
        }

        public string DebugSummary()
        {
            return $"player={PlayerId} session={(string.IsNullOrEmpty(SessionId) ? "-" : "set")} gen={Generation} map={MapInstanceId} lastServerSeq={LastServerSeq} stateToken=redacted";
        }
    }

    public sealed class PushReliability
    {
        readonly HashSetCompat _seen = new HashSetCompat();
        public ulong LastAppliedServerSeq { get; private set; }
        public bool HasGap { get; private set; }
        public ulong ExpectedNext => LastAppliedServerSeq + 1;

        public enum Decision
        {
            Apply = 0,
            Duplicate = 1,
            Gap = 2
        }

        public Decision Observe(ulong serverSeq)
        {
            if (serverSeq == 0)
                return Decision.Apply;
            if (_seen.Contains(serverSeq) || (LastAppliedServerSeq != 0 && serverSeq <= LastAppliedServerSeq))
                return Decision.Duplicate;
            if (LastAppliedServerSeq != 0 && serverSeq > LastAppliedServerSeq + 1)
            {
                HasGap = true;
                return Decision.Gap;
            }

            return Decision.Apply;
        }

        public void MarkApplied(ulong serverSeq)
        {
            if (serverSeq == 0)
                return;
            _seen.Add(serverSeq);
            if (serverSeq > LastAppliedServerSeq)
                LastAppliedServerSeq = serverSeq;
            HasGap = false;
        }

        public void Reset(ulong lastServerSeq = 0)
        {
            _seen.Clear();
            LastAppliedServerSeq = lastServerSeq;
            HasGap = false;
        }

        sealed class HashSetCompat
        {
            readonly System.Collections.Generic.HashSet<ulong> _inner = new System.Collections.Generic.HashSet<ulong>();
            public bool Contains(ulong v) => _inner.Contains(v);
            public void Add(ulong v) => _inner.Add(v);
            public void Clear() => _inner.Clear();
        }
    }
}
