using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameMesh.Editor
{
    public sealed class GameMeshMapExportSettings : ScriptableObject
    {
        public ulong mapTemplateId = 1001;
        public uint dataVersion = 1;
        public float aoiCellSize = 12f;
        public float navSampleStep = 1f;

        public static GameMeshMapExportSettings Load()
        {
            const string path = "Assets/GameMesh/Editor/MapExport/GameMeshMapExportSettings.asset";
            var settings = AssetDatabase.LoadAssetAtPath<GameMeshMapExportSettings>(path);
            if (settings != null)
                return settings;
            settings = CreateInstance<GameMeshMapExportSettings>();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Assets/GameMesh/Editor/MapExport");
            AssetDatabase.CreateAsset(settings, path);
            return settings;
        }
    }
}
