using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameMesh.Editor
{
    public static class GameMeshDemoInstaller
    {
        [MenuItem("GameMesh/Demo/Install Into Built Scenes")]
        public static void InstallMenu()
        {
            Install();
            Debug.Log("[GameMesh] demo components installed. Runtime also auto-boots via RuntimeInitializeOnLoadMethod.");
        }

        public static void Install()
        {
            EnsureConfig();
            EnsureSceneHook("Assets/FPS/Scenes/IntroMenu.unity");
            EnsureSceneHook("Assets/FPS/Scenes/MainScene.unity");
            AssetDatabase.SaveAssets();
        }

        static void EnsureConfig()
        {
            const string dir = "Assets/GameMesh/Resources";
            const string path = dir + "/GameMeshClientConfig.asset";
            Directory.CreateDirectory(dir);
            var cfg = AssetDatabase.LoadAssetAtPath<Bootstrap.GameMeshClientConfig>(path);
            if (cfg == null)
            {
                cfg = ScriptableObject.CreateInstance<Bootstrap.GameMeshClientConfig>();
                AssetDatabase.CreateAsset(cfg, path);
            }
        }

        static void EnsureSceneHook(string scenePath)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var found = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.GetComponent<Bootstrap.GameMeshClient>() != null)
                    found = true;
            }

            if (!found)
            {
                var go = new GameObject("GameMeshClient");
                go.AddComponent<Bootstrap.GameMeshClient>();
                EditorSceneManager.MarkSceneDirty(scene);
            }

            EditorSceneManager.SaveScene(scene);
        }
    }
}
