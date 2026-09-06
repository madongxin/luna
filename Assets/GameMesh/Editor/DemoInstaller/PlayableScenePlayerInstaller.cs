using System.IO;
using GameMesh.Player;
using UnityEditor;
using UnityEngine;

namespace GameMesh.Editor
{
    public static class PlayableScenePlayerInstaller
    {
        const string CatalogDir = "Assets/GameMesh/Resources";
        const string CatalogPath = CatalogDir + "/PlayablePlayerCatalog.asset";
        const string PlayerPath = "Assets/FPS/Prefabs/Player.prefab";

        [InitializeOnLoadMethod]
        static void EnsureCatalogOnLoad()
        {
            EditorApplication.delayCall += RefreshCatalog;
        }

        [MenuItem("GameMesh/Ensure Playable Player Catalog")]
        public static void RefreshCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<PlayablePlayerCatalog>(CatalogPath);
            if (catalog == null)
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "GameMesh", "Resources"));
                catalog = ScriptableObject.CreateInstance<PlayablePlayerCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            var fps = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
            if (fps != null)
                catalog.playerPrefab = fps;

            var store = AssetDatabase.LoadAssetAtPath<GameObject>(StoreHumanoidPlayerBuilder.PrefabPath);
            var mixamo = AssetDatabase.LoadAssetAtPath<GameObject>(MixamoPlayerBuilder.PrefabPath);
            catalog.thirdPersonArmature = store != null ? store : (mixamo != null ? mixamo : FindStarterPrefab("PlayerArmature"));
            catalog.thirdPersonFollowCamera = FindStarterPrefab("PlayerFollowCamera");
            catalog.thirdPersonMainCamera = FindStarterPrefab("MainCamera");
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }

        static GameObject FindStarterPrefab(string name)
        {
            var guids = AssetDatabase.FindAssets(name + " t:Prefab");
            GameObject fallback = null;
            for (var i = 0; i < (guids?.Length ?? 0); i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
                    continue;
                if (Path.GetFileNameWithoutExtension(path) != name)
                    continue;
                var starter = path.IndexOf("StarterAssets", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Starter Assets", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!starter)
                    continue;
                if (name != "MainCamera" && path.IndexOf("ThirdPerson", System.StringComparison.OrdinalIgnoreCase) < 0
                    && path.IndexOf("Third Person", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    fallback = fallback != null ? fallback : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    continue;
                }

                if (name == "MainCamera" && path.IndexOf("ThirdPerson", System.StringComparison.OrdinalIgnoreCase) < 0
                    && path.IndexOf("StarterAssets", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            return fallback;
        }
    }
}
