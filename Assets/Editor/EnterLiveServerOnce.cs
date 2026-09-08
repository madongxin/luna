using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameMesh.Editor
{
    public static class EnterLiveServerOnce
    {
        const string Flag = @"C:\Users\dongx\FirstFPS\Logs\enter-live-server.flag";
        const string PlayFlag = @"C:\Users\dongx\FirstFPS\Logs\enter-live-server-play.flag";
        const string Status = @"C:\Users\dongx\FirstFPS\Logs\enter-live-server-status.txt";
        const string Intro = "Assets/FPS/Scenes/IntroMenu.unity";
        const string Terrain = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";

        [InitializeOnLoadMethod]
        static void AutoRun()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged -= RestoreTerrainAfterPlay;
            EditorApplication.playModeStateChanged += RestoreTerrainAfterPlay;
            EditorApplication.delayCall += PinTerrainPlayScene;
        }

        static void RestoreTerrainAfterPlay(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
                PinTerrainPlayScene();
        }

        static void PinTerrainPlayScene()
        {
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(Terrain);
            if (scene != null)
                EditorSceneManager.playModeStartScene = scene;
        }

        static void Tick()
        {
            if (!File.Exists(Flag) || EditorApplication.isCompiling)
                return;
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                return;
            }

            EditorApplication.update -= Tick;
            Run();
        }

        [MenuItem("GameMesh/Connect Live Server Now")]
        public static void RunMenu()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Flag));
            File.WriteAllText(Flag, "1");
            Run();
        }

        static void PinLiveGateway()
        {
            const string path = "Assets/GameMesh/Resources/GameMeshClientConfig.asset";
            var cfg = AssetDatabase.LoadAssetAtPath<GameMesh.Bootstrap.GameMeshClientConfig>(path);
            if (cfg == null)
                return;
            cfg.host = "124.222.244.169";
            cfg.port = 8081;
            cfg.portB = 8083;
            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();
            Write("PIN host=" + cfg.host + ":" + cfg.port + "/" + cfg.portB);
        }

        static void Run()
        {
            try
            {
                if (File.Exists(Flag))
                    File.Delete(Flag);

                PinLiveGateway();
                Directory.CreateDirectory(Path.GetDirectoryName(PlayFlag));
                File.WriteAllText(PlayFlag, "1");
                Write("EDITOR play IntroMenu auto-login 1002");

                var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(Intro);
                if (scene == null)
                    throw new InvalidOperationException("missing " + Intro);
                EditorSceneManager.playModeStartScene = scene;

                EditorApplication.playModeStateChanged -= OnPlayMode;
                EditorApplication.playModeStateChanged += OnPlayMode;

                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPlaying = false;
                    EditorApplication.delayCall += () => { EditorApplication.isPlaying = true; };
                }
                else
                {
                    EditorApplication.isPlaying = true;
                }
            }
            catch (Exception ex)
            {
                Write("FAIL " + ex.GetType().Name + ": " + ex.Message);
                Debug.LogError("[EnterLive] " + ex);
            }
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode)
                return;
            PinTerrainPlayScene();
            EditorApplication.playModeStateChanged -= OnPlayMode;
        }

        static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Status));
                File.AppendAllText(Status,
                    DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
            }
            catch
            {
                // ignored
            }
        }
    }
}
