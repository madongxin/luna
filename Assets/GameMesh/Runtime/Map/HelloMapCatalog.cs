using System.Collections.Generic;
using GameMesh.Protocol;

namespace GameMesh.Map
{
    public sealed class HelloMapCatalog
    {
        public const ulong HubTemplateId = 1001;
        public const ulong LineTemplateId = 1002;
        public const ulong DungeonTemplateId = 2102;
        public const string HubHash = "ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317";
        public const string LineHash = "46d5bb506de2f0418a85fce8d8e285dfec664023122b2692cecf818a26be75d9";
        public const string HubScene = "MainScene";
        public const string LineScene = "TerrainDemoScene";
        public const float SpawnX = -28.5f;
        public const float SpawnY = -0.244f;
        public const float SpawnZ = -7.25f;
        public const float PortalX = -22.5f;
        public const float PortalY = -0.244f;
        public const float PortalZ = -7.25f;
        public const float PortalYaw = 76.022f;
        public const float PortalRadius = 3f;
        public const string SpawnToDungeon = "spawn_to_dungeon";
        public const string DungeonToSpawn = "dungeon_to_spawn";

        public static bool InSpawnToDungeonRadius(float x, float z)
        {
            var dx = x - PortalX;
            var dz = z - PortalZ;
            return dx * dx + dz * dz <= PortalRadius * PortalRadius;
        }

        readonly Dictionary<ulong, MapManifestEntry> _byId = new Dictionary<ulong, MapManifestEntry>();

        public IEnumerable<MapManifestEntry> All => _byId.Values;

        public void Replace(IEnumerable<MapManifestEntry> maps)
        {
            _byId.Clear();
            if (maps != null)
            {
                foreach (var map in maps)
                {
                    if (map == null || map.MapTemplateId == 0)
                        continue;
                    _byId[map.MapTemplateId] = map;
                }
            }

            EnsureFrozenFallbacks();
        }

        public MapManifestEntry Find(ulong templateId)
        {
            if (templateId == 0)
                return null;
            return _byId.TryGetValue(templateId, out var entry) ? entry : null;
        }

        public bool TryContract(ulong templateId, out uint version, out string hash, out string errorCode)
        {
            version = 1;
            hash = "";
            errorCode = "";
            var entry = Find(templateId);
            if (entry == null)
            {
                errorCode = "ERR_MAP_DATA_MISMATCH";
                return false;
            }

            version = entry.DataVersion != 0 ? (uint)entry.DataVersion : 1u;
            hash = entry.Sha256 ?? "";
            return true;
        }

        public void UpdateHash(ulong templateId, string hash, ulong version)
        {
            if (templateId == 0 || string.IsNullOrEmpty(hash))
                return;
            if (!_byId.TryGetValue(templateId, out var entry) || entry == null)
            {
                entry = new MapManifestEntry { MapTemplateId = templateId };
                _byId[templateId] = entry;
            }

            entry.Sha256 = hash;
            if (version != 0)
                entry.DataVersion = version;
        }

        public string VisualSceneName(ulong templateId, string fallback)
        {
            var entry = Find(templateId);
            if (entry != null && entry.VisualMapTemplateId != 0)
            {
                var visual = Find(entry.VisualMapTemplateId);
                if (visual != null && !string.IsNullOrEmpty(visual.SceneName))
                    return visual.SceneName;
                if (entry.VisualMapTemplateId == HubTemplateId)
                    return HubScene;
            }

            if (entry != null && !string.IsNullOrEmpty(entry.SceneName))
                return entry.SceneName;
            if (templateId == HubTemplateId || templateId == DungeonTemplateId)
                return HubScene;
            if (templateId == LineTemplateId)
                return LineScene;
            return fallback ?? "";
        }

        public string KindOf(ulong templateId)
        {
            var entry = Find(templateId);
            return entry != null ? entry.Kind ?? "" : "";
        }

        public IList<PortalDef> PortalsFor(ulong templateId)
        {
            var entry = Find(templateId);
            if (entry != null && entry.Portals.Count > 0)
                return entry.Portals;
            return FrozenPortals(templateId);
        }

        public PortalDef FindPortal(ulong templateId, string portalId)
        {
            if (string.IsNullOrEmpty(portalId))
                return null;
            var portals = PortalsFor(templateId);
            if (portals == null)
                return null;
            for (var i = 0; i < portals.Count; i++)
            {
                if (portals[i] != null && portals[i].PortalId == portalId)
                    return portals[i];
            }

            return null;
        }

        void EnsureFrozenFallbacks()
        {
            if (!_byId.TryGetValue(HubTemplateId, out var hub) || hub == null)
            {
                hub = FrozenHub();
                _byId[HubTemplateId] = hub;
            }
            else
            {
                if (string.IsNullOrEmpty(hub.SceneName))
                    hub.SceneName = HubScene;
                if (string.IsNullOrEmpty(hub.Kind))
                    hub.Kind = "LEGACY_POOL";
                if (string.IsNullOrEmpty(hub.Sha256))
                    hub.Sha256 = HubHash;
                if (hub.DataVersion == 0)
                    hub.DataVersion = 1;
                if (hub.Portals.Count == 0)
                    hub.Portals.Add(FrozenPortal(SpawnToDungeon, HubTemplateId, DungeonTemplateId));
            }

            if (!_byId.TryGetValue(DungeonTemplateId, out var dungeon) || dungeon == null)
            {
                dungeon = FrozenDungeon();
                _byId[DungeonTemplateId] = dungeon;
            }
            else
            {
                if (string.IsNullOrEmpty(dungeon.SceneName))
                    dungeon.SceneName = HubScene;
                if (string.IsNullOrEmpty(dungeon.Kind))
                    dungeon.Kind = "DUNGEON";
                if (dungeon.VisualMapTemplateId == 0)
                    dungeon.VisualMapTemplateId = HubTemplateId;
                if (string.IsNullOrEmpty(dungeon.Sha256))
                    dungeon.Sha256 = string.IsNullOrEmpty(hub.Sha256) ? HubHash : hub.Sha256;
                if (dungeon.DataVersion == 0)
                    dungeon.DataVersion = hub.DataVersion != 0 ? hub.DataVersion : 1;
                if (dungeon.Portals.Count == 0)
                    dungeon.Portals.Add(FrozenPortal(DungeonToSpawn, DungeonTemplateId, HubTemplateId));
            }

            if (!_byId.TryGetValue(LineTemplateId, out var line) || line == null)
            {
                _byId[LineTemplateId] = FrozenLine();
            }
            else
            {
                if (string.IsNullOrEmpty(line.SceneName))
                    line.SceneName = LineScene;
                if (string.IsNullOrEmpty(line.Kind))
                    line.Kind = "LINE";
                if (string.IsNullOrEmpty(line.Sha256))
                    line.Sha256 = LineHash;
                if (line.DataVersion == 0)
                    line.DataVersion = 1;
            }
        }

        static MapManifestEntry FrozenHub()
        {
            var entry = new MapManifestEntry
            {
                MapTemplateId = HubTemplateId,
                DataVersion = 1,
                Sha256 = HubHash,
                SceneName = HubScene,
                Kind = "LEGACY_POOL"
            };
            entry.Portals.Add(FrozenPortal(SpawnToDungeon, HubTemplateId, DungeonTemplateId));
            return entry;
        }

        static MapManifestEntry FrozenDungeon()
        {
            var entry = new MapManifestEntry
            {
                MapTemplateId = DungeonTemplateId,
                DataVersion = 1,
                Sha256 = HubHash,
                SceneName = HubScene,
                Kind = "DUNGEON",
                VisualMapTemplateId = HubTemplateId
            };
            entry.Portals.Add(FrozenPortal(DungeonToSpawn, DungeonTemplateId, HubTemplateId));
            return entry;
        }

        static MapManifestEntry FrozenLine()
        {
            return new MapManifestEntry
            {
                MapTemplateId = LineTemplateId,
                DataVersion = 1,
                Sha256 = LineHash,
                SceneName = LineScene,
                Kind = "LINE"
            };
        }

        static PortalDef FrozenPortal(string portalId, ulong fromTemplate, ulong toTemplate)
        {
            return new PortalDef
            {
                PortalId = portalId,
                FromMapTemplateId = fromTemplate,
                ToMapTemplateId = toTemplate,
                Position = new Vec3 { X = PortalX, Y = PortalY, Z = PortalZ },
                Yaw = PortalYaw,
                TriggerRadius = PortalRadius
            };
        }

        static IList<PortalDef> FrozenPortals(ulong templateId)
        {
            if (templateId == HubTemplateId)
                return new[] { FrozenPortal(SpawnToDungeon, HubTemplateId, DungeonTemplateId) };
            if (templateId == DungeonTemplateId)
                return new[] { FrozenPortal(DungeonToSpawn, DungeonTemplateId, HubTemplateId) };
            return new PortalDef[0];
        }
    }
}
