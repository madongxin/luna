using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GameMesh.Map;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace GameMesh.Editor
{
    public static class BakeAndExportTerrain1002
    {
        public const ulong TemplateId = 1002;
        public const float NavSampleStep = 8f;
        public const float AoiCellSize = 32f;
        public const float VoxelSize = 4f; // tighter sample radius + spawn on covering terrain
        const string ScenePath = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        const string StatusAbs = @"C:\Users\dongx\FirstFPS\Logs\export-1002-status.txt";
        const string PrefsDone = "FirstFPS.ExportTerrain1002.Done";
        const string SessionKey = "FirstFPS.ExportTerrain1002.Running";

        [InitializeOnLoadMethod]
        static void AutoRunIfMissing()
        {
            if (File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "maps", "1002.grid.json"))))
            {
                EditorPrefs.SetBool(PrefsDone, true);
                return;
            }

            EditorPrefs.SetBool(PrefsDone, false);
            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool(SessionKey, false))
                    return;
                Run();
            };
        }

        [MenuItem("GameMesh/Map/Bake TerrainDemoScene And Export 1002")]
        public static void RunMenu()
        {
            EditorPrefs.SetBool(PrefsDone, false);
            SessionState.SetBool(SessionKey, false);
            Run();
        }

        public static void Run()
        {
            if (SessionState.GetBool(SessionKey, false))
                return;
            SessionState.SetBool(SessionKey, true);
            try
            {
                WriteStatus("START export-before-save");
                EnsureTerrainSceneOpen();

                var spawnXz = new Vector3(179.3f, 0f, 324.2f);
                var terrain = TerrainForPoint(spawnXz) ?? LargestTerrain();
                if (terrain == null)
                    throw new InvalidOperationException("no terrain");

                PlaceSpawn(terrain, spawnXz);
                var bakeTerrain = LargestTerrain() ?? terrain;
                BakeNavMesh(bakeTerrain);
                BindNavMeshForExport();

                var jsonPath = GameMeshMapExporter.ExportCurrentScene(
                    GameMeshMapExporter.DefaultOutDir, null, TemplateId, 1, AoiCellSize, NavSampleStep);
                var hash = File.ReadAllText(jsonPath + ".sha256").Trim();
                WriteHelloSnippet(hash);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                EditorPrefs.SetBool(PrefsDone, true);
                WriteStatus("OK hash=" + hash + " file=" + jsonPath);
            }
            catch (Exception ex)
            {
                WriteStatus("FAIL " + ex.GetType().Name + ": " + ex.Message);
                Debug.LogError("[Export1002] " + ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                SessionState.SetBool(SessionKey, false);
            }
        }

        static void EnsureTerrainSceneOpen()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                if (loaded.path != ScenePath)
                    continue;
                EditorSceneManager.SetActiveScene(loaded);
                return;
            }

            var terrainScene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            EditorSceneManager.SetActiveScene(terrainScene);
            for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var other = SceneManager.GetSceneAt(i);
                if (other != terrainScene)
                    EditorSceneManager.CloseScene(other, true);
            }
        }

        static Terrain[] AllTerrains()
        {
            return UnityEngine.Object.FindObjectsOfType<Terrain>();
        }

        static Terrain TerrainForPoint(Vector3 xz)
        {
            var terrains = AllTerrains();
            Terrain best = null;
            var bestArea = -1f;
            for (var i = 0; i < (terrains?.Length ?? 0); i++)
            {
                var t = terrains[i];
                if (t == null || t.terrainData == null)
                    continue;
                var origin = t.GetPosition();
                var size = t.terrainData.size;
                if (xz.x < origin.x || xz.x > origin.x + size.x ||
                    xz.z < origin.z || xz.z > origin.z + size.z)
                    continue;
                var area = size.x * size.z;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = t;
                }
            }

            return best;
        }

        static Terrain LargestTerrain()
        {
            var terrains = AllTerrains();
            Terrain best = Terrain.activeTerrain;
            var bestArea = -1f;
            for (var i = 0; i < (terrains?.Length ?? 0); i++)
            {
                var t = terrains[i];
                if (t == null || t.terrainData == null)
                    continue;
                var area = t.terrainData.size.x * t.terrainData.size.z;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = t;
                }
            }

            return best;
        }

        static void PlaceSpawn(Terrain terrain, Vector3 xz)
        {
            var existing = UnityEngine.Object.FindObjectOfType<GameMeshSpawnPoint>();
            var go = existing != null ? existing.gameObject : new GameObject("GameMeshSpawnPoint");
            var spawn = existing != null ? existing : go.AddComponent<GameMeshSpawnPoint>();
            spawn.id = "default";
            spawn.isDefault = true;

            var origin = terrain.GetPosition();
            var size = terrain.terrainData != null ? terrain.terrainData.size : new Vector3(4000f, 600f, 4000f);
            WriteStatus("TERRAIN name=" + terrain.name + " origin=" + origin + " size=" + size);
            xz.x = Mathf.Clamp(xz.x, origin.x + 16f, origin.x + size.x - 16f);
            xz.z = Mathf.Clamp(xz.z, origin.z + 16f, origin.z + size.z - 16f);
            var y = terrain.SampleHeight(xz) + origin.y + 0.12f;
            go.transform.SetPositionAndRotation(new Vector3(xz.x, y, xz.z), Quaternion.Euler(0f, 180f, 0f));
            spawn.yaw = 180f;
            WriteStatus("SPAWN " + go.transform.position);
        }

        static void BakeNavMesh(Terrain terrain)
        {
            EditorUtility.DisplayProgressBar("GameMesh 1002", "Baking NavMesh…", 0.2f);
            var surfaceType = FindType("Unity.AI.Navigation.NavMeshSurface");
            if (surfaceType == null)
                throw new InvalidOperationException("Unity.AI.Navigation.NavMeshSurface missing");

            var bakeGo = GameObject.Find("GameMeshNavBake");
            if (bakeGo == null)
                bakeGo = new GameObject("GameMeshNavBake");

            var surface = bakeGo.GetComponent(surfaceType) as Component;
            if (surface == null)
                surface = bakeGo.AddComponent(surfaceType);

            var origin = terrain.GetPosition();
            var size = terrain.terrainData != null ? terrain.terrainData.size : new Vector3(4000f, 600f, 4000f);
            bakeGo.transform.position = Vector3.zero;

            var assetPath = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.navmesh.asset";
            var existingData = AssetDatabase.LoadAssetAtPath<NavMeshData>(assetPath);
            if (existingData != null)
                SetProp(surface, "navMeshData", existingData);

            var collectType = FindType("Unity.AI.Navigation.CollectObjects");
            SetProp(surface, "collectObjects", Enum.ToObject(collectType ?? surfaceType, 0));
            SetProp(surface, "center", origin + new Vector3(size.x * 0.5f, size.y * 0.5f, size.z * 0.5f));
            SetProp(surface, "size", size + new Vector3(16f, 80f, 16f));
            SetProp(surface, "useGeometry", NavMeshCollectGeometry.PhysicsColliders);
            SetProp(surface, "layerMask", (LayerMask)(-1));
            SetProp(surface, "overrideVoxelSize", true);
            SetProp(surface, "voxelSize", VoxelSize);
            SetProp(surface, "overrideTileSize", true);
            SetProp(surface, "tileSize", 256);

            var disabled = new List<Collider>();
            var colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
            for (var i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null || colliders[i] is TerrainCollider || !colliders[i].enabled)
                    continue;
                colliders[i].enabled = false;
                disabled.Add(colliders[i]);
            }

            try
            {
                surfaceType.GetMethod("BuildNavMesh", BindingFlags.Instance | BindingFlags.Public)
                    ?.Invoke(surface, null);
            }
            finally
            {
                for (var i = 0; i < disabled.Count; i++)
                {
                    if (disabled[i] != null)
                        disabled[i].enabled = true;
                }
            }

            var navData = GetProp(surface, "navMeshData") as NavMeshData;
            if (navData == null)
                throw new InvalidOperationException("NavMesh bake produced no data");

            if (!AssetDatabase.Contains(navData))
            {
                if (existingData != null)
                    EditorUtility.CopySerialized(navData, existingData);
                else
                    AssetDatabase.CreateAsset(navData, assetPath);
                SetProp(surface, "navMeshData", AssetDatabase.LoadAssetAtPath<NavMeshData>(assetPath));
            }

            AssetDatabase.SaveAssets();
            BindNavMeshForExport();
            var tri = NavMesh.CalculateTriangulation();
            WriteStatus("NAVMESH verts=" + (tri.vertices?.Length ?? 0) + " voxel=" + VoxelSize);
            if (tri.vertices == null || tri.vertices.Length == 0)
                throw new InvalidOperationException("NavMesh triangulation empty after bake");
        }

        static void BindNavMeshForExport()
        {
            try
            {
                var surfaceType = FindType("Unity.AI.Navigation.NavMeshSurface");
                var bakeGo = GameObject.Find("GameMeshNavBake");
                var surface = bakeGo != null && surfaceType != null ? bakeGo.GetComponent(surfaceType) : null;
                if (surface != null)
                    surfaceType.GetMethod("AddData", BindingFlags.Instance | BindingFlags.Public)?.Invoke(surface, null);

                var assetPath = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.navmesh.asset";
                var navData = AssetDatabase.LoadAssetAtPath<NavMeshData>(assetPath);
                if (navData != null)
                    NavMesh.AddNavMeshData(navData);
            }
            catch (Exception ex)
            {
                WriteStatus("BIND " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void WriteHelloSnippet(string hash)
        {
            var keep1001 = "ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317";
            var existing = Path.Combine(Application.dataPath, "..", "maps", "1001.grid.json.sha256");
            if (File.Exists(existing))
                keep1001 = File.ReadAllText(existing).Trim();

            var body = new StringBuilder();
            body.Append("{\n");
            body.Append("  \"keep_1001\": { \"map_template_id\": 1001, \"data_version\": 1, \"sha256\": \"")
                .Append(keep1001).Append("\" },\n");
            body.Append("  \"add_1002\": { \"map_template_id\": 1002, \"data_version\": 1, \"sha256\": \"")
                .Append(hash).Append("\", \"scene_name\": \"TerrainDemoScene\", \"aoi_cell_size\": ")
                .Append(AoiCellSize.ToString("0.0")).Append(", \"nav_sample_step\": ")
                .Append(NavSampleStep.ToString("0.0")).Append(" }\n");
            body.Append("}\n");
            var dest = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "maps", "1002.server-hello.json"));
            File.WriteAllText(dest, body.ToString(), new UTF8Encoding(false));
            WriteStatus("HELLO " + dest);
        }

        static void SetProp(object target, string name, object value)
        {
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p != null && p.CanWrite)
                p.SetValue(target, value, null);
        }

        static object GetProp(object target, string name)
        {
            return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target, null);
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullName);
                if (type != null)
                    return type;
            }

            return null;
        }

        static void WriteStatus(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatusAbs));
                File.AppendAllText(StatusAbs, DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
                Debug.Log("[Export1002] " + line);
            }
            catch
            {
                // ignored
            }
        }
    }
}
