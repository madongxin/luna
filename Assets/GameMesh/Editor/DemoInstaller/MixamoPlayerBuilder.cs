using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameMesh.Editor
{
    public static class MixamoPlayerBuilder
    {
        public const string IncomingDir = "Assets/Mixamo/Incoming";
        public const string PrefabDir = "Assets/Mixamo/Prefabs";
        public const string PrefabPath = PrefabDir + "/PlayerMixamo.prefab";
        const string ArmaturePath = "Assets/StarterAssets/ThirdPersonController/Prefabs/PlayerArmature.prefab";
        const string ControllerPath =
            "Assets/StarterAssets/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";

        static bool _building;

        [InitializeOnLoadMethod]
        static void EnsureFolders()
        {
            EditorApplication.delayCall += CreateFolders;
        }

        public static void CreateFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Mixamo"))
                AssetDatabase.CreateFolder("Assets", "Mixamo");
            if (!AssetDatabase.IsValidFolder(IncomingDir))
                AssetDatabase.CreateFolder("Assets/Mixamo", "Incoming");
            if (!AssetDatabase.IsValidFolder(PrefabDir))
                AssetDatabase.CreateFolder("Assets/Mixamo", "Prefabs");
        }

        [MenuItem("GameMesh/Mixamo/Open Mixamo And Show Drop Folder")]
        public static void OpenMixamo()
        {
            CreateFolders();
            Application.OpenURL("https://www.mixamo.com/#/?page=1&type=Character");
            EditorUtility.DisplayDialog(
                "Mixamo",
                "1. 用 Adobe 账号登录 mixamo.com\n" +
                "2. 选一个写实人物，Download → FBX for Unity，Pose 选 T-Pose，With Skin\n" +
                "3. 可选再下 Idle / Walking / Running / Jump（Without Skin）\n" +
                "4. 把 FBX 拖进 Assets/Mixamo/Incoming\n" +
                "导入后会自动生成 Assets/Mixamo/Prefabs/PlayerMixamo.prefab 并接到第三人称控制器。",
                "OK");
            EditorUtility.RevealInFinder(Path.Combine(Application.dataPath, "Mixamo", "Incoming"));
        }

        [MenuItem("GameMesh/Mixamo/Build Player From Incoming")]
        public static void BuildFromIncomingMenu()
        {
            CreateFolders();
            var path = FindCharacterFbx(IncomingDir);
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog("Mixamo",
                    "Assets/Mixamo/Incoming 里没有带皮肤的人物 FBX。先从 mixamo.com 下载 T-Pose + With Skin。",
                    "OK");
                OpenMixamo();
                return;
            }

            if (BuildFromPath(path) != null)
                EditorUtility.DisplayDialog("Mixamo", "已生成 " + PrefabPath + "\nPlay TerrainDemoScene 会用这个写实人物。", "OK");
        }

        public static string FindCharacterFbx(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
                return null;
            var guids = AssetDatabase.FindAssets("t:Model", new[] { folder });
            string fallback = null;
            for (var i = 0; i < (guids?.Length ?? 0); i++)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (go == null)
                    continue;
                if (go.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                    continue;
                if (LooksLikeAnimationOnly(Path.GetFileNameWithoutExtension(assetPath)))
                {
                    fallback = fallback ?? assetPath;
                    continue;
                }

                return assetPath;
            }

            return fallback;
        }

        public static GameObject BuildFromPath(string fbxPath)
        {
            return BuildFromPath(fbxPath, PrefabPath, "PlayerMixamo", "Assets/Mixamo", null);
        }

        public static GameObject BuildFromPath(
            string modelPath,
            string destPrefabPath,
            string objectName,
            string materialFolder,
            RuntimeAnimatorController overrideController)
        {
            if (_building || string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(destPrefabPath))
                return AssetDatabase.LoadAssetAtPath<GameObject>(destPrefabPath);

            _building = true;
            try
            {
                CreateFolders();
                var destDir = Path.GetDirectoryName(destPrefabPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
                {
                    var parts = destDir.Split('/');
                    var current = parts[0];
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var next = current + "/" + parts[i];
                        if (!AssetDatabase.IsValidFolder(next))
                            AssetDatabase.CreateFolder(current, parts[i]);
                        current = next;
                    }
                }

                if (modelPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase) ||
                    modelPath.EndsWith(".dae", System.StringComparison.OrdinalIgnoreCase))
                    ConfigureHumanoid(modelPath, null);

                var mixamo = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                var template = AssetDatabase.LoadAssetAtPath<GameObject>(ArmaturePath);
                var controller = overrideController ??
                                 AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
                if (mixamo == null || template == null)
                {
                    Debug.LogError("[Mixamo] missing model or PlayerArmature template");
                    return null;
                }

                if (!string.IsNullOrEmpty(materialFolder) && AssetDatabase.IsValidFolder(materialFolder))
                    UpgradeMaterialsUnder(materialFolder);

                var root = PrefabUtility.InstantiatePrefab(mixamo) as GameObject;
                if (root == null)
                    root = Object.Instantiate(mixamo);
                if (PrefabUtility.IsPartOfPrefabInstance(root))
                    PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                root.name = objectName;
                root.tag = "Player";
                root.layer = 0;
                StripImportedAnimatorDrive(root);
                StripChildColliders(root);

                CopyGameplay(template, root);
                var avatar = FindAvatar(modelPath) ?? root.GetComponentInChildren<Animator>(true)?.avatar;
                var animator = root.GetComponent<Animator>();
                if (animator == null)
                    animator = root.AddComponent<Animator>();
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                if (avatar != null)
                    animator.avatar = avatar;
                if (controller != null)
                    animator.runtimeAnimatorController = controller;

                FitCapsule(root);
                EnsureCameraRoot(root);
                UpgradeInstanceMaterials(root);

                PrefabUtility.SaveAsPrefabAsset(root, destPrefabPath);
                Object.DestroyImmediate(root);
                AssetDatabase.SaveAssets();
                PlayableScenePlayerInstaller.RefreshCatalog();
                Debug.Log("[HumanoidPlayer] built " + destPrefabPath + " from " + modelPath);
                return AssetDatabase.LoadAssetAtPath<GameObject>(destPrefabPath);
            }
            finally
            {
                _building = false;
            }
        }

        public static void ConfigureHumanoid(string fbxPath, Avatar copyFrom)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
                return;

            var dirty = false;
            if ((int)importer.animationType != 3)
            {
                importer.animationType = (ModelImporterAnimationType)3;
                dirty = true;
            }

            if (copyFrom != null)
            {
                if (importer.avatarSetup != ModelImporterAvatarSetup.CopyFromOther || importer.sourceAvatar != copyFrom)
                {
                    importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                    importer.sourceAvatar = copyFrom;
                    dirty = true;
                }
            }
            else if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                dirty = true;
            }

            if (importer.materialLocation != ModelImporterMaterialLocation.External)
            {
                importer.materialLocation = ModelImporterMaterialLocation.External;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();
        }

        static void CopyGameplay(GameObject template, GameObject root)
        {
            CopyComponent(template.GetComponent<CharacterController>(), root);
            CopyComponent(template.GetComponent("BasicRigidBodyPush") as Component, root);
            CopyComponent(template.GetComponent("StarterAssetsInputs") as Component, root);
            CopyComponent(template.GetComponent("PlayerInput") as Component, root);
            CopyComponent(template.GetComponent("ThirdPersonController") as Component, root);
        }

        static void CopyComponent(Component source, GameObject dest)
        {
            if (source == null)
                return;
            var existing = dest.GetComponent(source.GetType());
            if (existing == null)
                existing = dest.AddComponent(source.GetType());
            EditorUtility.CopySerialized(source, existing);
        }

        static void StripImportedAnimatorDrive(GameObject root)
        {
            var animators = root.GetComponentsInChildren<Animator>(true);
            for (var i = 0; i < animators.Length; i++)
            {
                if (animators[i].gameObject == root)
                    continue;
                Object.DestroyImmediate(animators[i]);
            }
        }

        static void StripChildColliders(GameObject root)
        {
            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null || colliders[i] is CharacterController)
                    continue;
                Object.DestroyImmediate(colliders[i]);
            }
        }

        static Avatar FindAvatar(string fbxPath)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
            for (var i = 0; i < assets.Length; i++)
            {
                if (assets[i] is Avatar avatar)
                    return avatar;
            }

            return null;
        }

        static void FitCapsule(GameObject root)
        {
            var cc = root.GetComponent<CharacterController>();
            if (cc == null)
                return;
            var rends = root.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0)
                return;
            var bounds = rends[0].bounds;
            for (var i = 1; i < rends.Length; i++)
                bounds.Encapsulate(rends[i].bounds);
            var min = root.transform.InverseTransformPoint(bounds.min);
            var max = root.transform.InverseTransformPoint(bounds.max);
            var height = Mathf.Max(1.4f, max.y - min.y);
            var radius = Mathf.Clamp(Mathf.Min(bounds.extents.x, bounds.extents.z), 0.18f, 0.4f);
            cc.height = height;
            cc.radius = radius;
            cc.center = new Vector3(0f, (min.y + max.y) * 0.5f, 0f);
            cc.skinWidth = 0.02f;
            cc.stepOffset = Mathf.Min(0.4f, height * 0.2f);
            cc.slopeLimit = 45f;
        }

        static void EnsureCameraRoot(GameObject root)
        {
            var camRoot = FindNamed(root.transform, "PlayerCameraRoot");
            if (camRoot == null)
            {
                var go = new GameObject("PlayerCameraRoot");
                go.tag = "CinemachineTarget";
                go.transform.SetParent(root.transform, false);
                camRoot = go.transform;
            }

            var cc = root.GetComponent<CharacterController>();
            var y = cc != null ? Mathf.Max(1.2f, cc.height * 0.85f) : 1.5f;
            camRoot.localPosition = new Vector3(0f, y, 0f);
            camRoot.localRotation = Quaternion.identity;

            var tp = root.GetComponent("ThirdPersonController") as MonoBehaviour;
            if (tp == null)
                return;
            var field = tp.GetType().GetField("CinemachineCameraTarget",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (field != null)
                field.SetValue(tp, camRoot.gameObject);
        }

        static Transform FindNamed(Transform t, string name)
        {
            if (t.name == name)
                return t;
            for (var i = 0; i < t.childCount; i++)
            {
                var found = FindNamed(t.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        static void UpgradeMaterialsUnder(string folder)
        {
            var guids = AssetDatabase.FindAssets("t:Material", new[] { folder });
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null || guids == null)
                return;
            for (var i = 0; i < guids.Length; i++)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (mat == null || mat.shader == null)
                    continue;
                if (mat.shader.name.IndexOf("Universal Render Pipeline", System.StringComparison.Ordinal) >= 0)
                    continue;
                mat.shader = lit;
                EditorUtility.SetDirty(mat);
            }
        }

        static void UpgradeInstanceMaterials(GameObject root)
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                return;
            var rends = root.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < rends.Length; i++)
            {
                var mats = rends[i].sharedMaterials;
                for (var m = 0; m < mats.Length; m++)
                {
                    if (mats[m] != null && mats[m].shader != null &&
                        mats[m].shader.name.IndexOf("Universal Render Pipeline", System.StringComparison.Ordinal) < 0)
                        mats[m].shader = lit;
                }
            }
        }

        public static bool LooksLikeAnimationOnly(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("walking") || n.Contains("running") || n.Contains("jump") ||
                   n.Contains("idle") || n.Contains("walk") || n.Contains("run") || n.Contains("falling");
        }
    }

    public sealed class MixamoIncomingPostprocessor : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            var path = assetPath.Replace('\\', '/');
            if (path.IndexOf("/Mixamo/", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;
            var importer = (ModelImporter)assetImporter;
            importer.animationType = (ModelImporterAnimationType)3;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.materialLocation = ModelImporterMaterialLocation.External;
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            for (var i = 0; i < imported.Length; i++)
            {
                var path = imported[i].Replace('\\', '/');
                if (!path.StartsWith(MixamoPlayerBuilder.IncomingDir + "/", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (MixamoPlayerBuilder.LooksLikeAnimationOnly(Path.GetFileNameWithoutExtension(path)))
                    continue;
                var captured = path;
                EditorApplication.delayCall += () =>
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(captured);
                    if (go != null && go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                        MixamoPlayerBuilder.BuildFromPath(captured);
                };
            }
        }
    }
}
