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

        public bool ApplyDelta(AoiDeltaDto delta)
        {
            if (delta?.Entity == null)
                return false;
            if (MapInstanceId != 0 && delta.MapInstanceId != 0 && delta.MapInstanceId != MapInstanceId)
            {
                ProtocolErrors++;
                return false;
            }

            var id = delta.Entity.EntityId != 0 ? delta.Entity.EntityId : delta.Entity.PlayerId;
            if (id == 0 || id == LocalPlayerId || delta.Entity.PlayerId == LocalPlayerId)
                return false;

            if (delta.Op == AoiOp.Leave)
                return _entities.Remove(id);

            if (delta.Op == AoiOp.Move && _entities.TryGetValue(id, out var existing))
            {
                if (delta.Entity.StateSeq != 0 && existing.StateSeq != 0 && delta.Entity.StateSeq <= existing.StateSeq)
                    return false;
                Copy(delta.Entity, existing, delta.ServerTimeMs);
                existing.LastLocalMs = LocalNowMs;
                return true;
            }

            if (!_entities.TryGetValue(id, out var state))
            {
                state = new RemoteEntityState { EntityId = id };
                _entities[id] = state;
            }
            else if (delta.Op == AoiOp.Enter && delta.Entity.StateSeq != 0 &&
                     state.StateSeq != 0 && delta.Entity.StateSeq < state.StateSeq)
            {
                return false;
            }

            Copy(delta.Entity, state, delta.ServerTimeMs);
            state.LastLocalMs = LocalNowMs;
            return true;
        }

        static void Copy(EntitySnapshotDto src, RemoteEntityState dst, long eventServerTimeMs = 0)
        {
            dst.PlayerId = src.PlayerId;
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
