using System.Collections.Generic;

namespace GameMesh.Aoi
{
    public enum AoiOp
    {
        Enter = 1,
        Move = 2,
        Leave = 3
    }

    public sealed class EntitySnapshotDto
    {
        public ulong EntityId;
        public ulong PlayerId;
        public string Name = "";
        public float X, Y, Z, Yaw;
        public float Hp, MaxHp;
        public ulong StateSeq;
        public ulong MapInstanceId;
        public long ServerTimeMs;
    }

    public sealed class AoiDeltaDto
    {
        public AoiOp Op;
        public EntitySnapshotDto Entity;
        public ulong MapInstanceId;
        public long ServerTimeMs;
    }

    public sealed class RemoteEntityState
    {
        public ulong EntityId;
        public ulong PlayerId;
        public string Name = "";
        public float X, Y, Z, Yaw;
        public float Hp, MaxHp;
        public ulong StateSeq;
        public ulong MapInstanceId;
        public long ServerTimeMs;
        /// <summary>Local tick of the last Enter or Move. Silence means a dropped LEAVE.</summary>
        public long LastLocalMs;
    }

    public sealed class AoiWorld
    {
        static readonly System.Diagnostics.Stopwatch LocalClock = System.Diagnostics.Stopwatch.StartNew();
        public static long LocalNowMs => LocalClock.ElapsedMilliseconds;
        readonly Dictionary<ulong, RemoteEntityState> _entities = new Dictionary<ulong, RemoteEntityState>();
        public ulong MapInstanceId { get; private set; }
        public ulong LocalPlayerId { get; set; }
        public int ProtocolErrors { get; private set; }
        public IReadOnlyDictionary<ulong, RemoteEntityState> Entities => _entities;

        public void SetMapInstance(ulong mapInstanceId)
        {
            if (MapInstanceId != 0 && MapInstanceId != mapInstanceId)
                Clear();
            MapInstanceId = mapInstanceId;
        }

        public void Clear()
        {
            _entities.Clear();
        }

        /// <summary>Full replace. Ids absent from the snapshot are removed immediately.</summary>
        public void ApplySnapshot(IEnumerable<EntitySnapshotDto> entities, bool replace = true)
        {
            if (replace)
                Clear();
            if (entities == null)
                return;
            var now = LocalNowMs;
            foreach (var e in entities)
                ApplyDelta(new AoiDeltaDto { Op = AoiOp.Enter, Entity = e, MapInstanceId = e != null ? e.MapInstanceId : 0 });

            StampAll(now);
        }

        /// <summary>Drop remotes with no Enter/Move for <paramref name="silenceMs"/>. LEAVE is immediate.</summary>
        public int PruneSilent(long nowMs, int silenceMs)
        {
            if (silenceMs <= 0 || _entities.Count == 0)
                return 0;
            List<ulong> dead = null;
            foreach (var kv in _entities)
            {
                var seen = kv.Value.LastLocalMs;
                if (seen <= 0)
                    seen = nowMs;
                if (nowMs - seen < silenceMs)
                    continue;
                if (dead == null)
                    dead = new List<ulong>();
                dead.Add(kv.Key);
            }

            if (dead == null)
                return 0;
            for (var i = 0; i < dead.Count; i++)
                _entities.Remove(dead[i]);
            return dead.Count;
        }

        void StampAll(long nowMs)
        {
            foreach (var kv in _entities)
            {
                if (kv.Value.LastLocalMs <= 0)
                    kv.Value.LastLocalMs = nowMs;
            }
        }

        /// <summary>PlayerId is the body id. EntityId 0 still means PlayerId.</summary>
        public static ulong BodyId(ulong entityId, ulong playerId)
        {
            return playerId != 0 ? playerId : entityId;
        }

        public bool ApplyDelta(AoiDeltaDto delta)
        {
            if (delta?.Entity == null)
                return false;
            if (MapInstanceId != 0 && delta.MapInstanceId != 0 && delta.MapInstanceId != MapInstanceId)
            {
                ProtocolErrors++;
                return false;
            }

            var playerId = delta.Entity.PlayerId;
            var entityId = delta.Entity.EntityId;
            var id = BodyId(entityId, playerId);
            if (id == 0 || id == LocalPlayerId || (playerId != 0 && playerId == LocalPlayerId))
                return false;

            if (delta.Op == AoiOp.Leave)
                return RemoveBody(entityId, playerId);

            if (TryFindBody(entityId, playerId, out var key, out var existing))
            {
                if (key != id)
                {
                    _entities.Remove(key);
                    _entities[id] = existing;
                }

                // Same state_seq with a new position and a newer server_time_ms is the next
                // sample of this walk. Only a strictly older seq is a stale packet.
                if (!RemotePoseBuffer.IsAlongPathUpdate(delta.Entity.StateSeq, existing.StateSeq))
                    return false;
                if (delta.Op == AoiOp.Enter && delta.Entity.StateSeq != 0 &&
                    existing.StateSeq != 0 && delta.Entity.StateSeq < existing.StateSeq)
                    return false;

                var name = existing.Name;
                var hp = existing.Hp;
                var maxHp = existing.MaxHp;
                Copy(delta.Entity, existing, delta.ServerTimeMs);
                if (string.IsNullOrEmpty(existing.Name))
                    existing.Name = name;
                if (delta.Op == AoiOp.Move && delta.Entity.Hp == 0 && delta.Entity.MaxHp == 0)
                {
                    existing.Hp = hp;
                    existing.MaxHp = maxHp;
                }

                if (entityId != 0)
                    existing.EntityId = entityId;
                else if (existing.EntityId == 0)
                    existing.EntityId = id;
                existing.LastLocalMs = LocalNowMs;
                return true;
            }

            var state = new RemoteEntityState
            {
                EntityId = entityId != 0 ? entityId : id,
                PlayerId = playerId != 0 ? playerId : id
            };
            _entities[id] = state;
            Copy(delta.Entity, state, delta.ServerTimeMs);
            state.LastLocalMs = LocalNowMs;
            return true;
        }

        public event System.Action<ulong> BodyRemoved;

        bool RemoveBody(ulong entityId, ulong playerId)
        {
            var id = BodyId(entityId, playerId);
            if (id == 0)
                return false;
            List<ulong> dead = null;
            foreach (var kv in _entities)
            {
                if (!SameBody(kv.Key, kv.Value, entityId, playerId))
                    continue;
                if (dead == null)
                    dead = new List<ulong>();
                dead.Add(kv.Key);
            }

            if (dead == null)
                return false;
            for (var i = 0; i < dead.Count; i++)
                _entities.Remove(dead[i]);
            var removed = BodyRemoved;
            if (removed != null)
            {
                removed(id);
                if (playerId != 0 && playerId != id)
                    removed(playerId);
                if (entityId != 0 && entityId != id && entityId != playerId)
                    removed(entityId);
                for (var i = 0; i < dead.Count; i++)
                {
                    if (dead[i] != id && dead[i] != playerId && dead[i] != entityId)
                        removed(dead[i]);
                }
            }

            return true;
        }

        bool TryFindBody(ulong entityId, ulong playerId, out ulong key, out RemoteEntityState state)
        {
            var id = BodyId(entityId, playerId);
            if (id != 0 && _entities.TryGetValue(id, out state))
            {
                key = id;
                return true;
            }

            if (entityId != 0 && entityId != id && _entities.TryGetValue(entityId, out state))
            {
                key = entityId;
                return true;
            }

            foreach (var kv in _entities)
            {
                if (!SameBody(kv.Key, kv.Value, entityId, playerId))
                    continue;
                key = kv.Key;
                state = kv.Value;
                return true;
            }

            key = 0;
            state = null;
            return false;
        }

        static bool SameBody(ulong key, RemoteEntityState state, ulong entityId, ulong playerId)
        {
            var id = BodyId(entityId, playerId);
            if (id != 0 && (key == id || state.PlayerId == id || state.EntityId == id))
                return true;
            if (entityId != 0 && (key == entityId || state.EntityId == entityId || state.PlayerId == entityId))
                return true;
            if (playerId != 0 && (key == playerId || state.PlayerId == playerId || state.EntityId == playerId))
                return true;
            return false;
        }

        static void Copy(EntitySnapshotDto src, RemoteEntityState dst, long eventServerTimeMs = 0)
        {
            if (src.PlayerId != 0)
                dst.PlayerId = src.PlayerId;
            else if (dst.PlayerId == 0)
                dst.PlayerId = dst.EntityId;
            dst.Name = src.Name ?? "";
            dst.X = src.X;
            dst.Y = src.Y;
            dst.Z = src.Z;
            dst.Yaw = src.Yaw;
            dst.Hp = src.Hp;
            dst.MaxHp = src.MaxHp;
            if (src.StateSeq != 0)
                dst.StateSeq = src.StateSeq;
            dst.MapInstanceId = src.MapInstanceId;
            var serverTime = eventServerTimeMs != 0 ? eventServerTimeMs : src.ServerTimeMs;
            if (serverTime != 0)
                dst.ServerTimeMs = serverTime;
        }
    }
}
