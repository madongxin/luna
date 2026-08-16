using System.Collections.Generic;
using GameMesh.Aoi;
using GameMesh.Player;
using GameMesh.Protocol;
using UnityEngine;

namespace GameMesh.Protocol
{
    public static class ProtocolMapper
    {
        public static PlayerAttributeSnapshot ToAttributes(PlayerAttributes src)
        {
            if (src == null)
                return null;
            return new PlayerAttributeSnapshot
            {
                PlayerId = src.PlayerId,
                Name = src.PlayerName ?? "",
                Hp = src.Hp,
                MaxHp = src.MaxHp,
                Mp = src.Mp,
                MaxMp = src.MaxMp,
                Attack = src.Attack,
                SpellPower = src.SpellPower,
                Defense = src.Defense,
                MagicResist = src.MagicResistance,
                CritRate = src.CritChance,
                CritDamage = src.CritDamage,
                MoveSpeed = src.MoveSpeed,
                AttackSpeed = src.AttackSpeed,
                StatsVersion = src.StatsVersion,
                LifeState = string.IsNullOrEmpty(src.LifeState) ? "ALIVE" : src.LifeState,
                FromServer = true
            };
        }

        public static EntitySnapshotDto ToEntityDto(EntitySnapshot src, ulong mapInstanceId = 0)
        {
            if (src == null)
                return null;
            var pos = src.Position;
            return new EntitySnapshotDto
            {
                EntityId = src.PlayerId,
                PlayerId = src.PlayerId,
                Name = src.PlayerName ?? "",
                X = pos != null ? pos.X : 0f,
                Y = pos != null ? pos.Y : 0f,
                Z = pos != null ? pos.Z : 0f,
                Yaw = src.Yaw,
                Hp = src.Hp,
                MaxHp = src.MaxHp,
                StateSeq = src.StateSeq,
                MapInstanceId = mapInstanceId
            };
        }

        public static bool ApplyAoiDelta(AoiWorld world, AoiDelta delta)
        {
            if (world == null || delta == null)
                return false;
            var applied = true;
            foreach (var ev in delta.Events)
            {
                if (ev?.Entity == null)
                {
                    applied = false;
                    continue;
                }

                var ok = world.ApplyDelta(new AoiDeltaDto
                {
                    Op = (AoiOp)ev.Op,
                    MapInstanceId = delta.MapInstanceId,
                    Entity = ToEntityDto(ev.Entity, delta.MapInstanceId)
                });
                if (!ok)
                    applied = false;
            }

            return applied;
        }

        public static void ApplySnapshot(AoiWorld world, IEnumerable<EntitySnapshot> entities, ulong mapInstanceId)
        {
            if (world == null)
                return;
            var dtos = new List<EntitySnapshotDto>();
            if (entities != null)
            {
                foreach (var e in entities)
                {
                    var dto = ToEntityDto(e, mapInstanceId);
                    if (dto != null)
                        dtos.Add(dto);
                }
            }

            world.ApplySnapshot(dtos);
        }

        public static Vec3 ToVec3(Vector3 v)
        {
            return new Vec3 { X = v.x, Y = v.y, Z = v.z };
        }

        public static Vector3 ToUnity(Vec3 v)
        {
            return v == null ? Vector3.zero : new Vector3(v.X, v.Y, v.Z);
        }

        public static string ExtractErrorCode(GameResponse rsp)
        {
            if (rsp == null)
                return "";
            if (!string.IsNullOrEmpty(rsp.ErrorCode))
                return rsp.ErrorCode;
            switch (rsp.BodyCase)
            {
                case GameResponse.BodyOneofCase.ServerHello:
                    return rsp.ServerHello?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.Heartbeat:
                    return rsp.Heartbeat?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.FullSnapshot:
                    return rsp.FullSnapshot?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.Respawn:
                    return rsp.Respawn?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.Move:
                    return rsp.Move?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.GetSelfProfile:
                    return rsp.GetSelfProfile?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.PlayerMailSend:
                    return rsp.PlayerMailSend?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.MailboxSummary:
                    return rsp.MailboxSummary?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.MailList:
                    return rsp.MailList?.ErrorCode ?? "";
                case GameResponse.BodyOneofCase.MailGet:
                    return rsp.MailGet?.ErrorCode ?? "";
                default:
                    return "";
            }
        }

        public static string ShortTraceId(GameResponse rsp)
        {
            var id = rsp?.TraceId ?? "";
            if (id.Length <= 8)
                return id;
            return id.Substring(id.Length - 8);
        }
    }
}
