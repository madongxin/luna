using GameMesh.Aoi;
using GameMesh.Bootstrap;
using GameMesh.LoadTest;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Protocol;
using Unity.FPS.Game;
using Unity.FPS.Gameplay;
using UnityEngine;

namespace GameMesh.Player
{
    public sealed class GameMeshWorldBinder : MonoBehaviour
    {
        Transform _local;
        CharacterController _controller;
        PlayerCharacterController _fps;
        Health _health;
        readonly System.Collections.Generic.Dictionary<ulong, RemotePlayerView> _views =
            new System.Collections.Generic.Dictionary<ulong, RemotePlayerView>();
        float _nextAoiSnapshotAt;
        int _modelBudget;

        public int RemoteViewCount => _views.Count;

        public bool TryNearestRemote(out string name, out float distance)
        {
            name = "";
            distance = 0f;
            if (_local == null || _views.Count == 0)
                return false;
            var best = float.MaxValue;
            foreach (var kv in _views)
            {
                if (kv.Value == null)
                    continue;
                var d = Vector3.Distance(_local.position, kv.Value.transform.position);
                if (d >= best)
                    continue;
                best = d;
                name = kv.Value.name.Replace("RemotePlayer_", "Bot");
                distance = d;
            }

            return best < float.MaxValue;
        }

        static float PlanarDistance(Vector3 local, RemoteEntityState state)
        {
            if (state == null)
                return float.MaxValue;
            var dx = local.x - state.X;
            var dz = local.z - state.Z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        void LateUpdate()
        {
            var client = GameMeshClient.Instance;
            if (client == null)
                return;
            BindLocal(client);
            MaybeRefreshAoi(client);
            SyncRemotes(client);
            MaybeReportMove(client);
        }

        void BindLocal(GameMeshClient client)
        {
            if (_local == null)
                FindLocalPawn();
            if (_local == null)
                return;

            if (_fps != null)
            {
                if (_health == null)
                    _health = _fps.GetComponent<Health>();

                var attrs = client.Session.Attributes;
                if (attrs.FromServer)
                {
                    if (_health != null)
                    {
                        _health.MaxHealth = attrs.MaxHp;
                        _health.CurrentHealth = attrs.Hp;
                    }

                    if (attrs.MoveSpeed > 0f)
                        _fps.MaxSpeedOnGround = attrs.MoveSpeed;
                }

                if (client.Connection != null &&
                    client.Connection.State == ConnectionState.InWorld &&
                    client.Config.disableSprint)
                    _fps.SprintSpeedModifier = 1f;
            }

            if (client.HasPendingSpawn)
            {
                var pos = client.PendingSpawn;
                var yaw = client.PendingSpawnYaw;
                var marker = FindObjectOfType<GameMeshSpawnPoint>();
                if (marker != null)
                {
                    pos = marker.transform.position;
                    yaw = marker.yaw;
                }

                ApplyPose(pos, yaw);
                client.HasPendingSpawn = false;
                if (client.Connection != null &&
                    client.Connection.State == ConnectionState.InWorld)
                    _ = client.SendMoveAsync(pos, yaw, default);
            }

            if (client.HasPendingCorrection)
            {
                var err = Vector3.Distance(_local.position, client.PendingCorrection);
                if (err >= client.Config.snapError)
                    ApplyPose(client.PendingCorrection, client.PendingCorrectionYaw);
                else if (err >= client.Config.smoothError)
                {
                    ApplyPose(Vector3.Lerp(_local.position, client.PendingCorrection, 0.35f),
                        client.PendingCorrectionYaw);
                }

                client.HasPendingCorrection = false;
            }
        }

        void FindLocalPawn()
        {
            _fps = FindObjectOfType<PlayerCharacterController>();
            if (_fps != null)
            {
                _local = _fps.transform;
                _controller = _fps.GetComponent<CharacterController>();
                return;
            }

            var named = GameObject.Find("PlayerHumanoid")
                ?? GameObject.Find("PlayerMixamo")
                ?? GameObject.Find("PlayerArmature")
                ?? GameObject.Find("Player");
            if (named != null)
            {
                _local = named.transform;
                _controller = named.GetComponent<CharacterController>();
                return;
            }

            var controllers = FindObjectsOfType<CharacterController>();
            for (var i = 0; i < controllers.Length; i++)
            {
                var cc = controllers[i];
                if (cc == null || cc.GetComponent<RemotePlayerView>() != null)
                    continue;
                if (cc.name.StartsWith("RemotePlayer_"))
                    continue;
                _controller = cc;
                _local = cc.transform;
                return;
            }
        }

        void ApplyPose(Vector3 pos, float yaw)
        {
            if (_controller != null)
                _controller.enabled = false;
            _local.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            if (_controller != null)
                _controller.enabled = true;
        }

        void MaybeReportMove(GameMeshClient client)
        {
            if (_local == null || client.Connection == null)
                return;
            if (client.MovesFrozen)
                return;
            if (!string.IsNullOrEmpty(client.LaunchArgs.AutoScenario))
                return;
            if (client.MoveCorrector.ShouldSuppress(Time.unscaledTime))
                return;
            var pos = _local.position;
            var yaw = _local.eulerAngles.y;
            if (!client.MoveSampler.ShouldSend(pos, yaw, Time.unscaledTime, out var reject))
            {
                if (reject != null)
                    GameMeshLog.Warn("reject local move " + reject);
                return;
            }

            _ = client.SendMoveAsync(pos, yaw, default);
        }

        void MaybeRefreshAoi(GameMeshClient client)
        {
            if (client.Connection == null || client.Connection.State != ConnectionState.InWorld)
                return;
            var runner = GameMeshLoadTestRunner.Instance;
            if (runner == null || runner.InWorldCount <= 0)
                return;
            var empty = client.Aoi.Entities.Count == 0;
            var far = !empty && _local != null &&
                      TryNearestRemote(out _, out var dist) && dist > 40f;
            if (!empty && !far)
                return;
            if (Time.unscaledTime < _nextAoiSnapshotAt)
                return;
            _nextAoiSnapshotAt = Time.unscaledTime + 3f;
            _ = client.RequestWorldSnapshotAsync();
        }

        void SyncRemotes(GameMeshClient client)
        {
            var localId = client.Aoi.LocalPlayerId != 0
                ? client.Aoi.LocalPlayerId
                : client.Session.PlayerId;
            var seen = new System.Collections.Generic.HashSet<ulong>();
            var localPos = _local != null ? _local.position : Vector3.zero;
            _modelBudget = 32;
            foreach (var kv in client.Aoi.Entities)
            {
                if (localId != 0 && kv.Key == localId)
                    continue;
                seen.Add(kv.Key);
                var near = _local == null || PlanarDistance(localPos, kv.Value) < 48f;
                if (!_views.TryGetValue(kv.Key, out var view) || view == null)
                {
                    try
                    {
                        view = RemotePlayerView.Spawn(kv.Value, client.Config.interpolationDelayMs,
                            attachModel: near && _modelBudget > 0);
                        if (view != null && view.HasModel)
                            _modelBudget--;
                    }
                    catch (System.Exception ex)
                    {
                        GameMeshLog.Warn("remote spawn failed id=" + kv.Key + " " + ex.Message);
                        continue;
                    }

                    _views[kv.Key] = view;
                }
                else if (!view.HasModel && near && _modelBudget > 0)
                {
                    if (view.TryAttachModel())
                        _modelBudget--;
                }

                view.Apply(kv.Value);
            }

            var dead = new System.Collections.Generic.List<ulong>();
            foreach (var kv in _views)
            {
                if (!seen.Contains(kv.Key))
                    dead.Add(kv.Key);
            }

            foreach (var id in dead)
            {
                if (_views.TryGetValue(id, out var view) && view != null)
                    Destroy(view.gameObject);
                _views.Remove(id);
            }
        }

        void OnDisable()
        {
            foreach (var kv in _views)
            {
                if (kv.Value != null)
                    Destroy(kv.Value.gameObject);
            }

            _views.Clear();
        }
    }

    public sealed class RemotePlayerView : MonoBehaviour
    {
        public ulong EntityId;
        public bool HasModel { get; private set; }
        Vector3 _target;
        float _targetYaw;
        float _delay;
        TextMesh _label;
        Animator _anim;
        int _speedId;
        int _groundedId;
        int _motionSpeedId;
        Color _color = Color.white;
        static Material _bodyMaterialTemplate;

        public static RemotePlayerView Spawn(RemoteEntityState state, int delayMs)
        {
            return Spawn(state, delayMs, true);
        }

        public static RemotePlayerView Spawn(RemoteEntityState state, int delayMs, bool attachModel)
        {
            var go = new GameObject("RemotePlayer_" + state.EntityId);
            go.SetActive(false);
            var view = go.AddComponent<RemotePlayerView>();
            view.EntityId = state.EntityId;
            view._delay = delayMs / 1000f;
            view._color = Color.HSVToRGB((state.EntityId % 12) / 12f, 0.72f, 0.95f);
            view._target = new Vector3(state.X, GroundY(state.X, state.Z, state.Y), state.Z);

            var labelGo = new GameObject("Name");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            view._label = labelGo.AddComponent<TextMesh>();
            view._label.characterSize = 0.12f;
            view._label.anchor = TextAnchor.LowerCenter;
            view._label.alignment = TextAlignment.Center;
            view._label.fontSize = 48;
            view._label.color = view._color;
            view.Apply(state);
            if (attachModel)
                view.TryAttachModel();
            if (!view.HasModel)
                view.AddCapsuleFallback();
            go.SetActive(true);
            go.transform.SetPositionAndRotation(view._target, Quaternion.Euler(0f, state.Yaw, 0f));
            return view;
        }

        public bool TryAttachModel()
        {
            if (HasModel)
                return false;
            var prefab = VisualPrefab();
            if (prefab == null)
                return false;
            try
            {
                var visual = Object.Instantiate(prefab, transform, false);
                visual.name = "Model";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;
                StripGameplay(visual);
                visual.AddComponent<RemoteAnimEventSink>();
                BindAnimator(visual.GetComponentInChildren<Animator>(true));
                var skins = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (var i = 0; i < skins.Length; i++)
                {
                    if (skins[i] == null)
                        continue;
                    skins[i].updateWhenOffscreen = true;
                    skins[i].enabled = true;
                }

                TintRenderers(visual, _color);
                RemovePrimitiveVisuals();
                HasModel = visual.GetComponentInChildren<Renderer>(true) != null;
                return HasModel;
            }
            catch (System.Exception ex)
            {
                GameMeshLog.Warn("remote model failed " + ex.Message);
                return false;
            }
        }

        void AddCapsuleFallback()
        {
            var mat = CreateBodyMaterial(_color);
            AddMeshChild(transform, PrimitiveType.Capsule, "Body",
                new Vector3(0f, 0.95f, 0f), new Vector3(0.45f, 0.9f, 0.45f), mat);
            AddMeshChild(transform, PrimitiveType.Sphere, "Head",
                new Vector3(0f, 1.75f, 0f), Vector3.one * 0.36f, mat);
        }

        void RemovePrimitiveVisuals()
        {
            DestroyNamed("Body");
            DestroyNamed("Head");
            DestroyNamed("Marker");
            DestroyNamed("Beacon");
        }

        void DestroyNamed(string childName)
        {
            var child = transform.Find(childName);
            if (child != null)
                Object.Destroy(child.gameObject);
        }

        void BindAnimator(Animator anim)
        {
            _anim = anim;
            if (_anim == null)
                return;
            _anim.applyRootMotion = false;
            _anim.fireEvents = false;
            _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _speedId = Animator.StringToHash("Speed");
            _groundedId = Animator.StringToHash("Grounded");
            _motionSpeedId = Animator.StringToHash("MotionSpeed");
        }

        static GameObject VisualPrefab()
        {
            var catalog = Resources.Load<PlayablePlayerCatalog>("PlayablePlayerCatalog");
            return catalog != null ? catalog.thirdPersonArmature : null;
        }

        static void StripGameplay(GameObject root)
        {
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                var name = behaviour.GetType().Name;
                if (name == "PlayerInput" || name == "ThirdPersonController" ||
                    name == "StarterAssetsInputs" || name == "BasicRigidBodyPush")
                    Object.DestroyImmediate(behaviour);
            }

            var controllers = root.GetComponentsInChildren<CharacterController>(true);
            for (var i = 0; i < controllers.Length; i++)
            {
                if (controllers[i] != null)
                    Object.DestroyImmediate(controllers[i]);
            }

            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    Object.DestroyImmediate(colliders[i]);
            }

            var cameras = root.GetComponentsInChildren<Camera>(true);
            for (var i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null)
                    cameras[i].enabled = false;
            }

            var listeners = root.GetComponentsInChildren<AudioListener>(true);
            for (var i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null)
                    listeners[i].enabled = false;
            }
        }

        public void Apply(RemoteEntityState state)
        {
            var next = new Vector3(state.X, state.Y, state.Z);
            next.y = GroundY(next.x, next.z, next.y);
            var err = Vector3.Distance(new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(next.x, 0f, next.z));
            if (err > 24f)
                transform.position = next;
            _target = next;
            _targetYaw = state.Yaw;
            if (_label != null)
                _label.text = $"{state.Name} {state.Hp:0}/{state.MaxHp:0}";
        }

        void Update()
        {
            _target.y = GroundY(_target.x, _target.z, _target.y);
            var pos = transform.position;
            var planar = Vector3.Distance(new Vector3(pos.x, 0f, pos.z),
                new Vector3(_target.x, 0f, _target.z));
            var speed = planar > 8f ? 7.5f : (planar > 0.08f ? 4.8f : 0f);
            var next = Vector3.MoveTowards(pos, _target, speed * Time.deltaTime);
            next.y = GroundY(next.x, next.z, _target.y);
            var moving = speed > 0.1f;
            transform.position = next;
            if (planar > 0.05f)
            {
                var look = Quaternion.Euler(0f, _targetYaw, 0f);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-Time.deltaTime * 8f));
            }

            if (_anim != null)
            {
                _anim.SetBool(_groundedId, true);
                _anim.SetFloat(_speedId, moving ? Mathf.Clamp(speed, 1.8f, 6f) : 0f);
                _anim.SetFloat(_motionSpeedId, moving ? 1f : 0f);
            }

            AlignModelToGround();

            if (_label == null)
                return;
            var cam = Camera.main;
            if (cam != null)
                _label.transform.rotation = Quaternion.LookRotation(
                    _label.transform.position - cam.transform.position);
        }

        void AlignModelToGround()
        {
            var model = transform.Find("Model");
            if (model == null)
                return;
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return;
            var any = false;
            var bounds = new Bounds();
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }

            if (!any)
                return;
            var ground = transform.position.y;
            var extra = ground - bounds.min.y + 0.04f;
            if (extra > -0.12f && extra < 0.06f)
                return;
            var local = model.localPosition;
            local.y = Mathf.Clamp(local.y + extra * 0.55f, 0f, 1.6f);
            model.localPosition = local;
            if (_label != null)
                _label.transform.localPosition = new Vector3(0f, Mathf.Max(2.15f, local.y + 1.9f), 0f);
        }

        static float GroundY(float x, float z, float fallback)
        {
            var terrains = Terrain.activeTerrains;
            for (var i = 0; i < terrains.Length; i++)
            {
                var terrain = terrains[i];
                if (terrain == null || terrain.terrainData == null)
                    continue;
                var origin = terrain.GetPosition();
                var size = terrain.terrainData.size;
                if (x < origin.x || z < origin.z || x > origin.x + size.x || z > origin.z + size.z)
                    continue;
                return terrain.SampleHeight(new Vector3(x, 0f, z)) + origin.y;
            }

            if (Physics.Raycast(new Vector3(x, fallback + 80f, z), Vector3.down, out var hit, 250f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return fallback;
        }

        static void AddMeshChild(Transform parent, PrimitiveType type, string name, Vector3 localPos,
            Vector3 localScale, Material mat)
        {
            var child = GameObject.CreatePrimitive(type);
            child.name = name;
            child.transform.SetParent(parent, false);
            child.transform.localPosition = localPos;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = localScale;
            var collider = child.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);
            var renderer = child.GetComponent<MeshRenderer>();
            if (renderer != null && mat != null)
                renderer.sharedMaterial = mat;
        }

        static Material CreateBodyMaterial(Color color)
        {
            if (_bodyMaterialTemplate == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit")
                    ?? Shader.Find("Unlit/Color")
                    ?? Shader.Find("Sprites/Default")
                    ?? Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    var sample = Object.FindObjectOfType<MeshRenderer>();
                    if (sample != null && sample.sharedMaterial != null)
                        shader = sample.sharedMaterial.shader;
                }

                if (shader == null)
                    shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader == null)
                    return null;
                _bodyMaterialTemplate = new Material(shader);
            }

            var mat = new Material(_bodyMaterialTemplate);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }

        static void TintRenderers(GameObject root, Color color)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer.GetComponent<TextMesh>() != null)
                    continue;
                var mats = renderer.materials;
                for (var m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null)
                        continue;
                    if (mats[m].HasProperty("_BaseColor"))
                        mats[m].SetColor("_BaseColor", color);
                    if (mats[m].HasProperty("_Color"))
                        mats[m].SetColor("_Color", color);
                    mats[m].color = color;
                    if (mats[m].HasProperty("_EmissionColor"))
                    {
                        mats[m].EnableKeyword("_EMISSION");
                        mats[m].SetColor("_EmissionColor", color * 0.22f);
                    }
                }

                renderer.materials = mats;
            }
        }
    }

    public sealed class RemoteAnimEventSink : MonoBehaviour
    {
        void OnFootstep(AnimationEvent evt) { }
        void OnLand(AnimationEvent evt) { }
    }
}
