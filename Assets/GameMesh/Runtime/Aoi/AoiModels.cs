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
    }

    public sealed class AoiDeltaDto
    {
        public AoiOp Op;
        public EntitySnapshotDto Entity;
        public ulong MapInstanceId;
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
    }

    public sealed class AoiWorld
    {
        readonly Dictionary<ulong, RemoteEntityState> _entities = new Dictionary<ulong, RemoteEntityState>();
        public ulong MapInstanceId { get; private set; }
        public ulong LocalPlayerId { get; set; }
        public int ProtocolErrors { get; private set; }
        public IReadOnlyDictionary<ulong, RemoteEntityState> Entities => _entities;

        public void SetMapInstance(ulong mapInstanceId)
        {
            if (MapInstanceId != mapInstanceId)
                Clear();
            MapInstanceId = mapInstanceId;
        }

        public void Clear()
        {
            _entities.Clear();
        }

        public void ApplySnapshot(IEnumerable<EntitySnapshotDto> entities)
        {
            Clear();
            if (entities == null)
                return;
            foreach (var e in entities)
                ApplyDelta(new AoiDeltaDto { Op = AoiOp.Enter, Entity = e, MapInstanceId = e.MapInstanceId });
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
                Copy(delta.Entity, existing);
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

            Copy(delta.Entity, state);
            return true;
        }

        static void Copy(EntitySnapshotDto src, RemoteEntityState dst)
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
        }
    }
}
