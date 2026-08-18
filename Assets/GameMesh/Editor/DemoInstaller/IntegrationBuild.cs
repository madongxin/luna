using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameMesh.Editor
{
    public static class IntegrationBuild
    {
        public static void BuildWindows()
        {
            try
            {
                var outDir = Path.Combine(Directory.GetCurrentDirectory(), "Builds", "GameMeshClient");
                Directory.CreateDirectory(outDir);
                var scenes = new[]
                {
                    "Assets/FPS/Scenes/IntroMenu.unity",
                    "Assets/FPS/Scenes/MainScene.unity"
                };
                var opts = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = Path.Combine(outDir, "GameMeshClient.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                };
                var report = BuildPipeline.BuildPlayer(opts);
                if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                    throw new Exception("build failed: " + report.summary.result);
                var manifest = Path.Combine("Assets", "GameMesh", "Protocol", "protocol_manifest.json");
                var dataDirs = new[]
                {
                    Path.Combine(outDir, "GameMeshClient_Data", "GameMesh", "Protocol"),
                    Path.Combine(outDir, "GameMeshClient_Data", "StreamingAssets", "GameMesh")
                };
                foreach (var dir in dataDirs)
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(manifest, Path.Combine(dir, "protocol_manifest.json"), true);
                }
                Debug.Log("[GameMesh] build ok " + opts.locationPathName);
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
                EditorApplication.Exit(1);
            }
        }
    }
}
