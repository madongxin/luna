using GameMesh.Map;
using GameMesh.UI;
using Unity.FPS.Game;
using Unity.FPS.Gameplay;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace GameMesh.Player
{
    /// <summary>
    /// Spawns a playable character into imported environment scenes.
    /// Prefers store Humanoid PlayerHumanoid, then Mixamo, then Starter Assets armature.
    /// Input is driven with UnityEngine.Input so GameMesh.Runtime does not reference Input System.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class PlayableScenePlayer : MonoBehaviour
    {
        const string FpsPlayerPath = "Assets/FPS/Prefabs/Player.prefab";
        const float HintSeconds = 8f;
        static readonly string[] ExploreScenes = { "TerrainDemoScene", "demoScene_free" };
        static readonly string[] ArmaturePaths =
        {
            "Assets/GameMesh/Prefabs/PlayerHumanoid.prefab",
            "Assets/Mixamo/Prefabs/PlayerMixamo.prefab",
            "Assets/StarterAssets/ThirdPersonController/Prefabs/PlayerArmature.prefab",
            "Assets/StarterAssets/ThirdPersonController/Prefabs/NestedParent/PlayerArmature.prefab",
            "Assets/Starter Assets/ThirdPersonController/Prefabs/PlayerArmature.prefab"
        };
        static readonly string[] FollowCameraPaths =
        {
            "Assets/StarterAssets/ThirdPersonController/Prefabs/PlayerFollowCamera.prefab",
            "Assets/Starter Assets/ThirdPersonController/Prefabs/PlayerFollowCamera.prefab"
        };
        static readonly string[] MainCameraPaths =
        {
            "Assets/StarterAssets/ThirdPersonController/Prefabs/MainCamera.prefab",
            "Assets/StarterAssets/ThirdPersonController/Prefabs/NestedParent/MainCamera.prefab",
            "Assets/Starter Assets/ThirdPersonController/Prefabs/MainCamera.prefab"
        };

        float _hintUntil;
        bool _thirdPerson;
        Component _starterInput;
        Behaviour _playerInput;
        string _modeHint = "第一人称 FPS";
        readonly System.Collections.Generic.List<GameObject> _keep = new System.Collections.Generic.List<GameObject>();

        public static bool IsExploreScene => IsExploreSceneName(SceneManager.GetActiveScene().name);

        public static bool UsesHoldToLook => IsExploreScene;

        public static bool SceneUsesFpsLook
        {
            get
            {
                if (UsesHoldToLook)
                    return false;
                return SceneManager.GetActiveScene().name == "MainScene";
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryAttach(SceneManager.GetActiveScene());
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryAttach(scene);
        }

        static void TryAttach(Scene scene)
        {
            if (!IsExploreSceneName(scene.name))
                return;
            if (FindObjectOfType<PlayableScenePlayer>() != null)
                return;
            if (FindObjectOfType<PlayerCharacterController>() != null)
                return;

            var go = new GameObject("PlayableScenePlayer");
            go.AddComponent<PlayableScenePlayer>();
        }

        public static bool IsExploreSceneName(string name)
        {
            for (var i = 0; i < ExploreScenes.Length; i++)
            {
                if (ExploreScenes[i] == name)
                    return true;
            }

            return false;
        }

        void Start()
        {
            if (FindObjectOfType<GameFlowManager>() == null)
                new GameObject("GameFlowManager").AddComponent<GameFlowManager>();

            var spawn = ResolveSpawn();
            _thirdPerson = TrySpawnThirdPerson(spawn);
            if (!_thirdPerson)
                SpawnFpsPlayer(spawn);

            DisableSceneCameras();
            StopCinematics();

            var ui = FindObjectOfType<GameMeshRuntimeUi>();
            ui?.PrepareExploreGameplay();

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _hintUntil = Time.unscaledTime + HintSeconds;
        }

        void Update()
        {
            DriveStarterMove();
            SyncLookCursor();
        }

        void SyncLookCursor()
        {
            if (!_thirdPerson)
                return;
            if (CursorCapture.BlocksGameInput || Input.GetKey(KeyCode.Escape))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            var holdLook = Input.GetMouseButton(1);
            Cursor.lockState = holdLook ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !holdLook;
        }

        void OnGUI()
        {
            if (Time.unscaledTime > _hintUntil)
                return;
            var text = _modeHint + "    WASD 移动    Shift 冲刺    空格 跳跃    按住右键转视角（松开即停）\nF2 联调面板";
            GUI.Label(new Rect(18f, Screen.height - 64f, 780f, 48f), text);
        }

        bool TrySpawnThirdPerson(Pose spawn)
        {
            var armaturePrefab = LoadFromCatalog(c => c.thirdPersonArmature) ?? LoadEditorPrefab(ArmaturePaths);
            var followPrefab = LoadFromCatalog(c => c.thirdPersonFollowCamera) ?? LoadEditorPrefab(FollowCameraPaths);
            var mainPrefab = LoadFromCatalog(c => c.thirdPersonMainCamera) ?? LoadEditorPrefab(MainCameraPaths);
            if (armaturePrefab == null)
                return false;

            var tagged = GameObject.FindGameObjectsWithTag("MainCamera");
            for (var i = 0; i < tagged.Length; i++)
            {
                if (tagged[i] != null)
                    tagged[i].tag = "Untagged";
            }

            if (mainPrefab != null)
            {
                var main = Instantiate(mainPrefab);
                main.name = "PlayerMainCamera";
                main.tag = "MainCamera";
                _keep.Add(main);
                var cam = main.GetComponentInChildren<Camera>();
                if (cam != null)
                    cam.farClipPlane = 8000f;
            }

            GameObject follow = null;
            if (followPrefab != null)
            {
                follow = Instantiate(followPrefab);
                follow.name = "PlayerFollowCamera";
                _keep.Add(follow);
                SetFarClip(follow, 8000f);
            }

            var player = Instantiate(armaturePrefab, spawn.position, spawn.rotation);
            player.name = armaturePrefab.name;
            _keep.Add(player);
            _starterInput = player.GetComponent("StarterAssetsInputs");
            SetInputField("cursorInputForLook", false);
            SetInputField("cursorLocked", false);
            _modeHint = armaturePrefab.name.IndexOf("Humanoid", System.StringComparison.OrdinalIgnoreCase) >= 0
                ? "第三人称 写实 Humanoid"
                : armaturePrefab.name.IndexOf("Mixamo", System.StringComparison.OrdinalIgnoreCase) >= 0
                    ? "第三人称 Mixamo"
                    : "第三人称 Armature";

            var controller = player.GetComponent<CharacterController>();
            if (controller != null)
            {
                controller.enabled = false;
                player.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
                controller.enabled = true;
            }

            var tp = player.GetComponent("ThirdPersonController") as MonoBehaviour;
            if (tp != null)
            {
                var layers = tp.GetType().GetField("GroundLayers");
                if (layers != null)
                    layers.SetValue(tp, (LayerMask)(-1));
            }

            var playerInput = player.GetComponent("PlayerInput") as Behaviour;
            _playerInput = playerInput;
            if (playerInput != null)
            {
                playerInput.enabled = true;
                var activate = playerInput.GetType().GetMethod("ActivateInput");
                activate?.Invoke(playerInput, null);
            }

            if (follow != null)
            {
                var camRoot = FindNamed(player.transform, "PlayerCameraRoot");
                BindCinemachineFollow(follow, camRoot != null ? camRoot : player.transform);
            }

            Debug.Log("[PlayableScenePlayer] third-person armature at " + spawn.position);
            return true;
        }

        void DriveStarterMove()
        {
            if (_starterInput == null)
                return;
            if (CursorCapture.BlocksGameInput)
            {
                CallInput("MoveInput", Vector2.zero);
                CallInput("LookInput", Vector2.zero);
                return;
            }

            var x = 0f;
            var y = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
                x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
                x += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
                y -= 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
                y += 1f;

            CallInput("MoveInput", new Vector2(x, y));
            CallInput("JumpInput", Input.GetKey(KeyCode.Space));
            CallInput("SprintInput", Input.GetKey(KeyCode.LeftShift));
            DriveStarterLook();
        }

        void DriveStarterLook()
        {
            if (!Input.GetMouseButton(1))
            {
                CallInput("LookInput", Vector2.zero);
                return;
            }

            var dx = Input.GetAxis("Mouse X");
            var dy = Input.GetAxis("Mouse Y");
            if (Mathf.Abs(dx) < 0.0005f && Mathf.Abs(dy) < 0.0005f)
            {
                CallInput("LookInput", Vector2.zero);
                return;
            }

            var look = new Vector2(dx, -dy) * 12f;
            if (!IsKeyboardMouseScheme())
                look /= Mathf.Max(Time.deltaTime, 0.0001f);
            CallInput("LookInput", look);
        }

        bool IsKeyboardMouseScheme()
        {
            if (_playerInput == null)
                return false;
            var prop = _playerInput.GetType().GetProperty("currentControlScheme");
            var scheme = prop != null ? prop.GetValue(_playerInput, null) as string : null;
            return scheme == "KeyboardMouse";
        }

        void CallInput(string method, object arg)
        {
            var type = _starterInput.GetType();
            var mi = type.GetMethod(method);
            if (mi != null)
                mi.Invoke(_starterInput, new[] { arg });
        }

        void SetInputField(string name, object value)
        {
            if (_starterInput == null)
                return;
            var field = _starterInput.GetType().GetField(name);
            if (field != null)
                field.SetValue(_starterInput, value);
        }

        void SpawnFpsPlayer(Pose spawn)
        {
            var prefab = LoadFromCatalog(c => c.playerPrefab) ?? LoadEditorPrefab(new[] { FpsPlayerPath });
            if (prefab == null)
            {
                Debug.LogError("[PlayableScenePlayer] missing player prefab");
                return;
            }

            var instance = Instantiate(prefab, spawn.position, spawn.rotation);
            instance.name = "Player";
            _keep.Add(instance);
            var player = instance.GetComponent<PlayerCharacterController>();
            if (player != null)
            {
                player.KillHeight = -10000f;
                player.RecievesFallDamage = false;
                if (player.PlayerCamera != null)
                    player.PlayerCamera.farClipPlane = 8000f;
            }

            var health = instance.GetComponent<Health>();
            if (health != null)
                health.Invincible = true;

            var controller = instance.GetComponent<CharacterController>();
            if (controller != null)
            {
                controller.enabled = false;
                instance.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
                controller.enabled = true;
            }

            Debug.Log("[PlayableScenePlayer] FPS player at " + spawn.position + " (Starter Assets not imported yet)");
        }

        static PlayablePlayerCatalog Catalog => Resources.Load<PlayablePlayerCatalog>("PlayablePlayerCatalog");

        static GameObject LoadFromCatalog(System.Func<PlayablePlayerCatalog, GameObject> pick)
        {
            var catalog = Catalog;
            return catalog != null ? pick(catalog) : null;
        }

        static GameObject LoadEditorPrefab(string[] paths)
        {
#if UNITY_EDITOR
            for (var i = 0; i < paths.Length; i++)
            {
                var loaded = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                if (loaded != null)
                    return loaded;
            }
#endif
            return null;
        }

        static Transform FindNamed(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindNamed(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        static void BindCinemachineFollow(GameObject followCamera, Transform target)
        {
            if (followCamera == null || target == null)
                return;
            var behaviours = followCamera.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                var type = behaviour.GetType();
                var typeName = type.Name;
                if (typeName != "CinemachineVirtualCamera" &&
                    typeName != "CinemachineCamera" &&
                    typeName != "CinemachineFreeLook")
                    continue;

                var follow = type.GetProperty("Follow");
                if (follow != null && follow.CanWrite)
                    follow.SetValue(behaviour, target, null);

                var lookAt = type.GetProperty("LookAt");
                if (lookAt != null && lookAt.CanWrite)
                    lookAt.SetValue(behaviour, target, null);

                var targetProp = type.GetProperty("Target");
                if (targetProp != null)
                {
                    var current = targetProp.GetValue(behaviour, null);
                    var trackingProp = targetProp.PropertyType.GetProperty("TrackingTarget");
                    var trackingField = targetProp.PropertyType.GetField("TrackingTarget");
                    if (trackingProp != null && trackingProp.CanWrite)
                    {
                        trackingProp.SetValue(current, target, null);
                        if (targetProp.CanWrite)
                            targetProp.SetValue(behaviour, current, null);
                    }
                    else if (trackingField != null)
                    {
                        trackingField.SetValue(current, target);
                        if (targetProp.CanWrite)
                            targetProp.SetValue(behaviour, current, null);
                    }
                }
            }
        }

        static void SetFarClip(GameObject root, float far)
        {
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                var lensField = behaviour.GetType().GetField("m_Lens");
                if (lensField == null)
                    continue;
                var lens = lensField.GetValue(behaviour);
                if (lens == null)
                    continue;
                var farField = lens.GetType().GetField("FarClipPlane");
                if (farField == null)
                    continue;
                farField.SetValue(lens, far);
                lensField.SetValue(behaviour, lens);
            }
        }

        void DisableSceneCameras()
        {
            var cameras = FindObjectsOfType<Camera>();
            for (var i = 0; i < cameras.Length; i++)
            {
                var camera = cameras[i];
                if (camera == null || ShouldKeep(camera.transform))
                    continue;
                camera.enabled = false;
                var listener = camera.GetComponent<AudioListener>();
                if (listener != null)
                    listener.enabled = false;
            }
        }

        void StopCinematics()
        {
            var directors = FindObjectsOfType<PlayableDirector>();
            for (var i = 0; i < directors.Length; i++)
            {
                if (ShouldKeep(directors[i].transform))
                    continue;
                directors[i].Stop();
                directors[i].enabled = false;
            }

            var cameras = FindObjectsOfType<Camera>();
            for (var i = 0; i < cameras.Length; i++)
            {
                if (ShouldKeep(cameras[i].transform))
                    continue;
                var brain = cameras[i].GetComponent("CinemachineBrain") as Behaviour;
                if (brain != null)
                    brain.enabled = false;
            }

            var virtualCameras = GameObject.Find("VirtualCameras");
            if (virtualCameras != null && !ShouldKeep(virtualCameras.transform))
                virtualCameras.SetActive(false);
        }

        bool ShouldKeep(Transform t)
        {
            for (var i = 0; i < _keep.Count; i++)
            {
                if (_keep[i] != null && t.IsChildOf(_keep[i].transform))
                    return true;
            }

            return false;
        }

        static Pose ResolveSpawn()
        {
            var marker = FindObjectOfType<GameMeshSpawnPoint>();
            if (marker != null)
                return new Pose(marker.transform.position, Quaternion.Euler(0f, marker.yaw, 0f));

            var yaw = 0f;
            var xz = Vector3.zero;
            var cam = Camera.main;
            if (cam == null)
            {
                var cameras = FindObjectsOfType<Camera>();
                if (cameras.Length > 0)
                    cam = cameras[0];
            }

            if (cam != null)
            {
                xz = cam.transform.position;
                yaw = cam.transform.eulerAngles.y;
            }

            var terrain = Terrain.activeTerrain;
            if (terrain != null)
            {
                var origin = terrain.GetPosition();
                var size = terrain.terrainData != null ? terrain.terrainData.size : new Vector3(32f, 0f, 32f);
                if (cam == null)
                    xz = origin + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);

                var ground = new Vector3(
                    Mathf.Clamp(xz.x, origin.x + 1f, origin.x + size.x - 1f),
                    0f,
                    Mathf.Clamp(xz.z, origin.z + 1f, origin.z + size.z - 1f));
                var y = terrain.SampleHeight(ground) + origin.y + 0.08f;
                return new Pose(new Vector3(ground.x, y, ground.z), Quaternion.Euler(0f, yaw, 0f));
            }

            var rayOrigin = cam != null ? cam.transform.position + Vector3.up * 50f : new Vector3(0f, 80f, 0f);
            if (Physics.Raycast(rayOrigin, Vector3.down, out var hit, 400f))
                return new Pose(hit.point + Vector3.up * 0.08f, Quaternion.Euler(0f, yaw, 0f));

            return new Pose(new Vector3(xz.x, xz.y, xz.z), Quaternion.Euler(0f, yaw, 0f));
        }
    }
}
