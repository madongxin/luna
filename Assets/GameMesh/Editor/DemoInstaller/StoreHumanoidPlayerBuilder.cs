using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameMesh.Editor
{
    public static class StoreHumanoidPlayerBuilder
    {
        public const string PrefabPath = "Assets/GameMesh/Prefabs/PlayerHumanoid.prefab";
        const string ControllerPath = "Assets/GameMesh/Prefabs/PlayerHumanoid.controller";
        const string StarterController =
            "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";

        static readonly string[] SearchRoots =
        {
            "Assets/ExplosiveLLC",
            "Assets/RPG Character Mecanim Animation Pack FREE",
            "Assets/RPGCharacterMecanimAnimationPackFREE",
            "Assets/Real Human",
            "Assets/RealHuman",
            "Assets/Pyramis Arts",
            "Assets/PyramisArts"
        };

        [MenuItem("GameMesh/Build Store Humanoid Player")]
        public static void BindAfterImport()
        {
            var model = FindCharacterModel();
            if (string.IsNullOrEmpty(model))
            {
                Debug.LogWarning("[StoreHumanoid] no Humanoid character mesh found yet");
                return;
            }

            var controller = BuildLocomotionController(Path.GetDirectoryName(model)?.Replace('\\', '/'));
            var prefab = MixamoPlayerBuilder.BuildFromPath(
                model,
                PrefabPath,
                "PlayerHumanoid",
                Path.GetDirectoryName(model)?.Replace('\\', '/'),
                controller);
            if (prefab != null)
                Debug.Log("[StoreHumanoid] player ready " + PrefabPath + " from " + model);
        }

        public static string FindCharacterModel()
        {
            var folders = ExistingRoots();
            if (folders.Length == 0)
                return null;

            var guids = AssetDatabase.FindAssets("t:Model", folders);
            string best = null;
            var bestScore = -1;
            for (var i = 0; i < (guids?.Length ?? 0); i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path))
                    continue;
                if (path.IndexOf("/StarterAssets/", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (path.IndexOf("/Mixamo/", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null || go.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                    continue;
                var file = Path.GetFileNameWithoutExtension(path);
                if (MixamoPlayerBuilder.LooksLikeAnimationOnly(file))
                    continue;
                var score = ScoreCharacter(path);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = path;
                }
            }

            return best;
        }

        static int ScoreCharacter(string path)
        {
            var n = path.Replace('\\', '/').ToLowerInvariant();
            var score = 1;
            if (n.Contains("real human") || n.Contains("realhuman") || n.Contains("pyramis"))
                score += 50;
            if (n.Contains("dummy") || n.Contains("rpg-character") || n.Contains("rpgcharacter"))
                score += 20;
            if (n.Contains("character") || n.Contains("human") || n.Contains("man"))
                score += 8;
            if (n.Contains("explosive"))
                score += 10;
            if (n.Contains("/animation") || n.Contains("/animations/"))
                score -= 15;
            return score;
        }

        static string[] ExistingRoots()
        {
            var list = new List<string>();
            for (var i = 0; i < SearchRoots.Length; i++)
            {
                if (AssetDatabase.IsValidFolder(SearchRoots[i]))
                    list.Add(SearchRoots[i]);
            }

            if (list.Count > 0)
                return list.ToArray();

            var assets = Application.dataPath;
            try
            {
                foreach (var dir in Directory.GetDirectories(assets))
                {
                    var name = Path.GetFileName(dir);
                    if (name.IndexOf("Explosive", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("RPG Character", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Real Human", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Pyramis", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        list.Add("Assets/" + name);
                }
            }
            catch
            {
                // ignored
            }

            return list.ToArray();
        }

        static RuntimeAnimatorController BuildLocomotionController(string clipFolder)
        {
            var source = AssetDatabase.LoadAssetAtPath<AnimatorController>(StarterController);
            if (source == null)
                return null;

            var destDir = Path.GetDirectoryName(ControllerPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
            {
                if (!AssetDatabase.IsValidFolder("Assets/GameMesh/Prefabs"))
                {
                    if (!AssetDatabase.IsValidFolder("Assets/GameMesh"))
                        AssetDatabase.CreateFolder("Assets", "GameMesh");
                    AssetDatabase.CreateFolder("Assets/GameMesh", "Prefabs");
                }
            }

            if (File.Exists(Path.Combine(Application.dataPath, "GameMesh", "Prefabs", "PlayerHumanoid.controller")) ||
                AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
                AssetDatabase.DeleteAsset(ControllerPath);
            AssetDatabase.CopyAsset(StarterController, ControllerPath);
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ctrl == null)
                return source;

            var clips = LoadClips(clipFolder);
            var idle = PickClip(clips, true, "idle") ?? PickClip(clips, false, "idle");
            var walk = PickClip(clips, true, "walk", "forward") ?? PickClip(clips, true, "walk") ??
                       PickClip(clips, false, "walk");
            var run = PickClip(clips, true, "run", "forward") ?? PickClip(clips, true, "run") ??
                      PickClip(clips, false, "run") ?? PickClip(clips, false, "sprint");
            var jump = PickClip(clips, false, "jump");
            var fall = PickClip(clips, false, "fall") ?? PickClip(clips, false, "inair") ??
                       PickClip(clips, false, "airborne");
            var land = PickClip(clips, false, "land");
            if (idle == null && walk == null && run == null)
                return ctrl;

            for (var i = 0; i < ctrl.layers.Length; i++)
                PatchMachine(ctrl.layers[i].stateMachine, idle, walk, run, jump, fall, land);

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            Debug.Log("[StoreHumanoid] locomotion clips idle=" + ClipName(idle) +
                      " walk=" + ClipName(walk) + " run=" + ClipName(run));
            return ctrl;
        }

        static void PatchMachine(
            AnimatorStateMachine machine,
            AnimationClip idle,
            AnimationClip walk,
            AnimationClip run,
            AnimationClip jump,
            AnimationClip fall,
            AnimationClip land)
        {
            if (machine == null)
                return;
            var states = machine.states;
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i].state;
                if (state == null)
                    continue;
                var name = state.name ?? "";
                if (name.IndexOf("Idle Walk Run", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var tree = state.motion as BlendTree;
                    if (tree == null)
                        continue;
                    var children = tree.children;
                    if (children.Length > 0 && idle != null)
                        children[0].motion = idle;
                    if (children.Length > 1 && walk != null)
                        children[1].motion = walk;
                    if (children.Length > 2 && run != null)
                        children[2].motion = run;
                    tree.children = children;
                    continue;
                }

                if (name.IndexOf("JumpLand", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Land", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (land != null)
                        state.motion = land;
                    continue;
                }

                if (name.IndexOf("InAir", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Fall", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (fall != null)
                        state.motion = fall;
                    continue;
                }

                if (name.IndexOf("Jump", System.StringComparison.OrdinalIgnoreCase) >= 0 && jump != null)
                    state.motion = jump;
            }

            var subs = machine.stateMachines;
            for (var i = 0; i < subs.Length; i++)
                PatchMachine(subs[i].stateMachine, idle, walk, run, jump, fall, land);
        }

        static List<AnimationClip> LoadClips(string folder)
        {
            var list = new List<AnimationClip>();
            string[] search = !string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder)
                ? new[] { folder }
                : ExistingRoots();
            if (search == null || search.Length == 0)
                search = new[] { "Assets" };
            var guids = AssetDatabase.FindAssets("t:AnimationClip", search);
            for (var i = 0; i < (guids?.Length ?? 0); i++)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (clip != null && !clip.name.StartsWith("__preview"))
                    list.Add(clip);
            }

            return list;
        }

        static AnimationClip PickClip(List<AnimationClip> clips, bool preferUnarmed, params string[] needles)
        {
            AnimationClip fallback = null;
            for (var i = 0; i < clips.Count; i++)
            {
                var n = clips[i].name.ToLowerInvariant();
                if (n.Contains("injured") || n.Contains("strafe") || n.Contains("left") || n.Contains("right") ||
                    n.Contains("back") || n.Contains("combat"))
                    continue;
                var ok = true;
                for (var k = 0; k < needles.Length; k++)
                {
                    if (n.IndexOf(needles[k], System.StringComparison.Ordinal) < 0)
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                    continue;
                if (preferUnarmed && (n.Contains("unarmed") || n.Contains("neutral")))
                    return clips[i];
                if (fallback == null)
                    fallback = clips[i];
            }

            return fallback;
        }

        static string ClipName(AnimationClip clip)
        {
            return clip != null ? clip.name : "-";
        }
    }
}
