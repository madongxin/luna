using System.Collections.Generic;
using GameMesh.Aoi;
using GameMesh.Auth;
using GameMesh.Player;

namespace GameMesh.Protocol
{
    public sealed class WorldSnapshotModel
    {
        public PlayerAttributeSnapshot Profile;
        public uint RealmId;
        public ulong MapTemplateId;
        public ulong MapInstanceId;
        public string GameLogicInstanceId = "";
        public ulong OwnerEpoch;
        public ulong RouteVersion;
        public EntitySnapshotDto Self;
        public readonly List<EntitySnapshotDto> AoiEntities = new List<EntitySnapshotDto>();
        public ulong BaselineServerSeq;
        public uint SnapshotVersion;
        public string RecoveryReason = "";
        public string LifeState = "ALIVE";
    }

    public static class WorldSnapshotApplier
    {
        public static bool TryBuild(FullStateSnapshotRsp snap, ulong expectedPlayerId,
            ulong currentMapInstanceId, uint currentSnapshotVersion,
            out WorldSnapshotModel model, out string errorCode, out string message)
        {
            model = null;
            errorCode = "";
            message = "";
            if (snap == null)
            {
                errorCode = "ERR_AOI_RESYNC_REQUIRED";
                message = "missing snapshot";
                return false;
            }

            if (!snap.Ok)
            {
                errorCode = string.IsNullOrEmpty(snap.ErrorCode) ? "ERR_AOI_RESYNC_REQUIRED" : snap.ErrorCode;
                message = snap.Message ?? "";
                return false;
            }

            if (snap.Self == null || snap.Self.PlayerId == 0)
            {
                errorCode = "ERR_AOI_RESYNC_REQUIRED";
                message = "snapshot self is empty";
                return false;
            }

            if (expectedPlayerId != 0 && snap.PlayerId != 0 && snap.PlayerId != expectedPlayerId)
            {
                errorCode = "ERR_AOI_RESYNC_REQUIRED";
                message = "snapshot player mismatch";
                return false;
            }

            if (currentMapInstanceId != 0 && snap.MapInstanceId != 0 &&
                snap.MapInstanceId != currentMapInstanceId)
            {
                errorCode = "ERR_MAP_DATA_MISMATCH";
                message = "snapshot map instance mismatch";
                return false;
            }

            if (currentSnapshotVersion != 0 && snap.SnapshotVersion != 0 &&
                snap.SnapshotVersion < currentSnapshotVersion)
            {
                errorCode = "ERR_AOI_RESYNC_REQUIRED";
                message = "snapshot version went backwards";
                return false;
            }

            model = new WorldSnapshotModel
            {
                Profile = ProtocolMapper.ToAttributes(snap.Profile),
                RealmId = snap.RealmId,
                MapTemplateId = snap.MapTemplateId,
                MapInstanceId = snap.MapInstanceId,
                GameLogicInstanceId = snap.GamelogicInstanceId ?? "",
                OwnerEpoch = snap.OwnerEpoch,
                RouteVersion = snap.RouteVersion,
                Self = ProtocolMapper.ToEntityDto(snap.Self, snap.MapInstanceId),
                BaselineServerSeq = snap.BaselineServerSeq,
                SnapshotVersion = snap.SnapshotVersion,
                RecoveryReason = snap.RecoveryReason ?? "",
                LifeState = string.IsNullOrEmpty(snap.LifeState)
                    ? (snap.Profile != null && !string.IsNullOrEmpty(snap.Profile.LifeState)
                        ? snap.Profile.LifeState
                        : "ALIVE")
                    : snap.LifeState
            };
            if (snap.AoiEntities != null)
            {
                foreach (var entity in snap.AoiEntities)
                {
                    var dto = ProtocolMapper.ToEntityDto(entity, snap.MapInstanceId);
                    if (dto != null)
                        model.AoiEntities.Add(dto);
                }
            }

            return true;
        }

        public static void Apply(GameSession session, AoiWorld world, PushReliability push,
            PushGapCache gapCache, WorldSnapshotModel model)
        {
            if (session == null || world == null || model == null)
                return;
            if (model.Profile != null)
            {
                session.Attributes = model.Profile;
                if (model.Profile.PlayerId != 0)
                    session.PlayerId = model.Profile.PlayerId;
                if (!string.IsNullOrEmpty(model.Profile.Name))
                    session.DisplayName = model.Profile.Name;
            }

            session.Attributes.LifeState = model.LifeState ?? "ALIVE";
            session.RealmId = model.RealmId;
            session.ApplyMap(model.MapTemplateId, model.MapInstanceId, model.OwnerEpoch, model.RouteVersion);
            session.GameLogicInstanceId = model.GameLogicInstanceId ?? "";
            session.SnapshotVersion = model.SnapshotVersion;
            session.RecoveryReason = model.RecoveryReason ?? "";
            world.LocalPlayerId = session.PlayerId;
            world.SetMapInstance(model.MapInstanceId);
            world.ApplySnapshot(model.AoiEntities);
            gapCache?.Clear();
            push?.Reset(model.BaselineServerSeq);
            session.LastServerSeq = model.BaselineServerSeq;
        }
    }
}
