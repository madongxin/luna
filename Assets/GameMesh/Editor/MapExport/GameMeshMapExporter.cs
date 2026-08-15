using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GameMesh.Map;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace GameMesh.Editor
{
    public static class GameMeshMapExporter
    {
        public const string DefaultOutDir = "maps";
        public const string DefaultFileName = "1001.grid.json";

        [MenuItem("GameMesh/Map/Export Current Scene")]
        public static void ExportCurrentSceneMenu()
        {
            var path = ExportCurrentScene(DefaultOutDir, null);
            Debug.Log("[GameMesh] exported " + path);
        }

        public static string ExportCurrentScene(string outputDir, string copyToDir)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
                throw new InvalidOperationException("no active scene");

            var triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
                throw new InvalidOperationException("empty NavMesh");

            var min = triangulation.vertices[0];
            var max = triangulation.vertices[0];
            foreach (var v in triangulation.vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            var settings = GameMeshMapExportSettings.Load();
            var step = Mathf.Max(0.1f, settings.navSampleStep);
            var minX = Mathf.Floor(min.x / step) * step;
            var minZ = Mathf.Floor(min.z / step) * step;
            var maxX = Mathf.Ceil(max.x / step) * step;
            var maxZ = Mathf.Ceil(max.z / step) * step;
            var width = Mathf.Max(1, Mathf.RoundToInt((maxX - minX) / step));
            var height = Mathf.Max(1, Mathf.RoundToInt((maxZ - minZ) / step));
            var cells = new bool[width * height];
            var probeY = max.y + 1f;
            var sampleRadius = Mathf.Max(step * 1.5f, (max.y - min.y) + 4f);
            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var wx = minX + (col + 0.5f) * step;
                    var wz = minZ + (row + 0.5f) * step;
                    var probe = new Vector3(wx, probeY, wz);
                    cells[MapStaticData.CellIndex(col, row, width)] =
                        NavMesh.SamplePosition(probe, out _, sampleRadius, NavMesh.AllAreas);
                }
            }

            var data = new MapStaticData
            {
                schema_version = 1,
                map_template_id = settings.mapTemplateId,
                scene_name = scene.name,
                data_version = settings.dataVersion,
                bounds_min = new MapVec3(minX, min.y, minZ),
                bounds_max = new MapVec3(maxX, max.y, maxZ),
                aoi_cell_size = settings.aoiCellSize,
                nav_sample_step = step,
                grid_width = width,
                grid_height = height,
                walkable_rle = MapStaticData.EncodeRle(cells)
            };
            data.spawn_points = CollectSpawns(data, cells);

            Directory.CreateDirectory(outputDir);
            var fileName = settings.mapTemplateId + ".grid.json";
            var jsonPath = Path.Combine(outputDir, fileName);
            var json = data.ToDeterministicJson();
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(jsonPath, json, utf8);
            var hash = data.Sha256();
            File.WriteAllText(jsonPath + ".sha256", hash + "\n", utf8);
            if (!string.IsNullOrEmpty(copyToDir))
            {
                Directory.CreateDirectory(copyToDir);
                File.Copy(jsonPath, Path.Combine(copyToDir, fileName), true);
                File.Copy(jsonPath + ".sha256", Path.Combine(copyToDir, fileName + ".sha256"), true);
            }

            AssetDatabase.Refresh();
            Debug.Log("[GameMesh] map hash " + hash + " file=" + jsonPath);
            return jsonPath;
        }

        static List<MapSpawnPoint> CollectSpawns(MapStaticData data, bool[] cells)
        {
            var result = new List<MapSpawnPoint>();
            foreach (var spawn in UnityEngine.Object.FindObjectsOfType<GameMeshSpawnPoint>())
            {
                var p = spawn.transform.position;
                result.Add(new MapSpawnPoint
                {
                    id = string.IsNullOrEmpty(spawn.id) ? "default" : spawn.id,
                    x = p.x,
                    y = p.y,
                    z = p.z,
                    yaw = spawn.yaw
                });
            }

            if (result.Count == 0)
            {
                var player = UnityEngine.Object.FindObjectOfType<Unity.FPS.Gameplay.PlayerCharacterController>();
                if (player != null)
                {
                    var p = player.transform.position;
                    result.Add(new MapSpawnPoint
                    {
                        id = "default",
                        x = p.x,
                        y = p.y,
                        z = p.z,
                        yaw = player.transform.eulerAngles.y
                    });
                }
            }

            for (var i = result.Count - 1; i >= 0; i--)
            {
                var p = result[i];
                if (p.x < data.bounds_min.x || p.x > data.bounds_max.x ||
                    p.z < data.bounds_min.z || p.z > data.bounds_max.z ||
                    !data.TryGetWalkable(p.x, p.z, cells, out _, out _))
                {
                    result.RemoveAt(i);
                }
            }

            if (result.Count == 0)
            {
                for (var row = 0; row < data.grid_height; row++)
                {
                    for (var col = 0; col < data.grid_width; col++)
                    {
                        if (!cells[MapStaticData.CellIndex(col, row, data.grid_width)])
                            continue;
                        result.Add(new MapSpawnPoint
                        {
                            id = "default",
                            x = data.bounds_min.x + (col + 0.5f) * data.nav_sample_step,
                            y = data.bounds_min.y,
                            z = data.bounds_min.z + (row + 0.5f) * data.nav_sample_step,
                            yaw = 0f
                        });
                        return result;
                    }
                }

                throw new InvalidOperationException("no walkable spawn points");
            }

            return result;
        }
    }

    public static class MapExportBatch
    {
        public static void ExportMainScene()
        {
            try
            {
                EditorSceneManager.OpenScene("Assets/FPS/Scenes/MainScene.unity");
                var copy = GetArg("-gamemeshCopyTo");
                var path = GameMeshMapExporter.ExportCurrentScene(GameMeshMapExporter.DefaultOutDir, copy);
                Debug.Log("[GameMesh] batch export " + path);
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
                EditorApplication.Exit(1);
            }
        }

        static string GetArg(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == key)
                    return args[i + 1];
            }

            return null;
        }
    }
}
