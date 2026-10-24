using GameMesh.Aoi;
using GameMesh.Bootstrap;
using GameMesh.LoadTest;
using GameMesh.Map;
using GameMesh.Network;
using GameMesh.Protocol;
using Unity.FPS.Game;
using Unity.FPS.Gameplay;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        readonly System.Collections.Generic.HashSet<ulong> _seenRemoteIds =
            new System.Collections.Generic.HashSet<ulong>();
        readonly System.Collections.Generic.List<ulong> _deadRemoteIds =
            new System.Collections.Generic.List<ulong>();
        float _nextAoiSnapshotAt;
        int _modelBudget;
        int _holdSpawnFrames;
        Vector3 _goodPose;
        bool _hasGoodPose;
        bool _loggedSink;
        // maps/1002.grid.json bounds_min.y is -38. Terrain origin Y is -40, so the
        // lowest mesh sits under the server box. A pawn that falls there is rejected.
        const float MapFloorY = -37.5f;

        public int RemoteViewCount => _views.Count;

        public bool TryLocalPosition(out Vector3 position)
        {
            if (_local == null)
            {
                position = Vector3.zero;
                return false;
            }

            position = _local.position;
            return true;
        }

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

        void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            foreach (var kv in _views)
            {
                if (kv.Value != null)
                    Destroy(kv.Value.gameObject);
            }

            _views.Clear();
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _local = null;
            _controller = null;
            _fps = null;
            _health = null;
            _holdSpawnFrames = 12;
        }

        void LateUpdate()
        {
            var client = GameMeshClient.Instance;
            if (client == null)
                return;
            BindLocal(client);
            MaybeRefreshAoi(client);
            SyncRemotes(client);
            PlaceLocal(client);
            HoldStable(client);
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

            // Skin 0.001 lets the capsule tunnel through the heightfield in one step.
            if (_controller != null && _controller.skinWidth < 0.05f)
                _controller.skinWidth = 0.08f;
        }

        void PlaceLocal(GameMeshClient client)
        {
            if (_local == null)
                return;
            if (!string.IsNullOrEmpty(client.LaunchArgs.AutoScenario))
            {
                PlaceScripted(client);
                return;
            }

            if (client.HasPendingSpawn && client.PendingSpawnFromServer)
            {
                if (_local == null || _local.gameObject.scene != SceneManager.GetActiveScene())
                {
                    _local = null;
                    _fps = null;
                    _controller = null;
                    FindLocalPawn();
                }

                if (_local == null)
                    return;

                var seated = SeatOnTerrain(client.PendingSpawn);
                ApplyPose(seated, client.PendingSpawnYaw);
                NoteGoodPose(seated);
                if (_fps != null)
                    _fps.CharacterVelocity = Vector3.zero;
                client.HasPendingSpawn = false;
                client.PendingSpawnFromServer = false;
                client.HasPendingCorrection = false;
                return;
            }

            // Adopt MoveRsp authority (including rejected TooFast). Do not keep a rejected local pose.
            if (client.HasPendingCorrection)
            {
                var back = SeatOnTerrain(client.PendingCorrection);
                ApplyPose(back, client.PendingCorrectionYaw);
                NoteGoodPose(back);
                if (_fps != null)
                    _fps.CharacterVelocity = Vector3.zero;
                client.HasPendingCorrection = false;
            }

            if (!client.PendingSpawnFromServer)
                client.HasPendingSpawn = false;
        }

        void PlaceScripted(GameMeshClient client)
        {
            if (client.HasPendingSpawn)
            {
                if (_holdSpawnFrames <= 0)
                    _holdSpawnFrames = 12;
                var pos = SnapToWalkable(client.PendingSpawn);
                var yaw = client.PendingSpawnYaw;
                if (!client.PendingSpawnFromServer)
                {
                    var marker = FindObjectOfType<GameMeshSpawnPoint>();
                    if (marker != null)
                    {
                        pos = SnapToWalkable(marker.transform.position);
                        yaw = marker.yaw;
                    }
                }

                ApplyPose(pos, yaw);
                _holdSpawnFrames--;
                if (_holdSpawnFrames > 0)
                    return;

                client.HasPendingSpawn = false;
                client.PendingSpawnFromServer = false;
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
            Physics.SyncTransforms();
            if (_controller != null)
                _controller.enabled = true;
        }

        void NoteGoodPose(Vector3 pos)
        {
            _goodPose = pos;
            _hasGoodPose = true;
        }

        static bool UsesTerrainHold(GameMeshClient client)
        {
            if (client.Session.MapTemplateId == 1001)
                return false;
            return Terrain.activeTerrain != null ||
                   (Terrain.activeTerrains != null && Terrain.activeTerrains.Length > 0);
        }

        static bool IsPoseStable(Vector3 pos)
        {
            if (pos.y < MapFloorY)
                return false;
            if (!TryTerrainSurface(pos, out var surface))
                return false;
            if (surface < MapFloorY)
                return false;
            if (pos.y < surface - 0.35f)
                return false;
            return true;
        }

        // Spawn Y is about -18.7. The capsule then falls through the heightfield and
        // the request Y keeps decreasing. Put the feet back on the sampled surface
        // before that height is sent. Do not adopt a lower server ack as the stance.
        public static bool TryLiftAboveSurface(Vector3 pos, out Vector3 lifted)
        {
            lifted = pos;
            if (!TryTerrainSurface(pos, out var surface) || surface < MapFloorY)
                return false;
            if (pos.y >= surface - 0.35f)
                return false;
            lifted.y = surface + 0.12f;
            return true;
        }

        static bool SameSpotDrop(Vector3 pos, Vector3 stance)
        {
            var dx = pos.x - stance.x;
            var dz = pos.z - stance.z;
            return dx * dx + dz * dz < 0.64f && stance.y - pos.y > 1.5f;
        }

        void HoldStable(GameMeshClient client)
        {
            if (_local == null || !UsesTerrainHold(client))
                return;
            if (client.Connection == null || client.Connection.State != ConnectionState.InWorld)
                return;

            var pos = _local.position;
            var yaw = _local.eulerAngles.y;
            Vector3 held;
            if (TryLiftAboveSurface(pos, out var lifted))
                held = lifted;
            else if (_hasGoodPose && SameSpotDrop(pos, _goodPose))
                held = new Vector3(pos.x, _goodPose.y, pos.z);
            else if (!IsPoseStable(pos) && _hasGoodPose)
                held = _goodPose;
            else
            {
                if (IsPoseStable(pos))
                    NoteGoodPose(pos);
                return;
            }

            if (!_loggedSink)
            {
                _loggedSink = true;
                GameMeshLog.Warn("feet through terrain y=" + pos.y.ToString("F2") +
                                 " put=" + held.y.ToString("F2") +
                                 " xz=" + pos.x.ToString("F1") + "," + pos.z.ToString("F1"));
            }

            ApplyPose(held, yaw);
            if (_fps != null)
            {
                var velocity = _fps.CharacterVelocity;
                if (velocity.y < 0f)
                    _fps.CharacterVelocity = new Vector3(velocity.x, 0f, velocity.z);
            }

            NoteGoodPose(held);
        }

        static Vector3 SeatOnTerrain(Vector3 pos)
        {
            return TryLiftAboveSurface(pos, out var lifted) ? lifted : pos;
        }

        static bool TryTerrainSurface(Vector3 pos, out float surfaceY)
        {
            surfaceY = pos.y;
            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0)
            {
                var only = Terrain.activeTerrain;
                terrains = only != null ? new[] { only } : null;
            }

            if (terrains == null)
                return false;
            for (var i = 0; i < terrains.Length; i++)
            {
                var terrain = terrains[i];
                if (terrain == null || terrain.terrainData == null)
                    continue;
                var origin = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (pos.x < origin.x || pos.z < origin.z ||
                    pos.x > origin.x + size.x || pos.z > origin.z + size.z)
                    continue;
                surfaceY = terrain.SampleHeight(pos) + origin.y;
                return true;
            }

            return false;
        }

        static Vector3 SnapToWalkable(Vector3 pos)
        {
            var terrain = Terrain.activeTerrain;
            if (terrain != null)
            {
                var origin = terrain.GetPosition();
                var size = terrain.terrainData != null ? terrain.terrainData.size : new Vector3(32f, 600f, 32f);
                var x = Mathf.Clamp(pos.x, origin.x + 2f, origin.x + size.x - 2f);
                var z = Mathf.Clamp(pos.z, origin.z + 2f, origin.z + size.z - 2f);
                var y = terrain.SampleHeight(new Vector3(x, 0f, z)) + origin.y + 0.35f;
                return new Vector3(x, y, z);
            }

            var rayOrigin = pos + Vector3.up * 80f;
            if (Physics.Raycast(rayOrigin, Vector3.down, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                return new Vector3(pos.x, hit.point.y + 0.35f, pos.z);
            return pos + Vector3.up * 0.5f;
        }

        void MaybeReportMove(GameMeshClient client)
        {
            if (_local == null || client.Connection == null)
                return;
            if (client.MovesFrozen || client.IsBusy)
                return;
            if (!string.IsNullOrEmpty(client.LaunchArgs.AutoScenario))
                return;
            if (client.MoveCorrector.ShouldSuppress(Time.unscaledTime))
                return;
            var pos = _local.position;
            var yaw = _local.eulerAngles.y;
            if (UsesTerrainHold(client) && !IsPoseStable(pos))
                return;
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

            var localId = client.Aoi.LocalPlayerId != 0
                ? client.Aoi.LocalPlayerId
                : client.Session.PlayerId;
            var remoteCount = 0;
            foreach (var kv in client.Aoi.Entities)
            {
                if (localId != 0 && kv.Key == localId)
                    continue;
                remoteCount++;
            }

            // While bots are online, a missing MOVE is usually a full write buffer, not a leave.
            // PruneSilent would delete the whole crowd about 25s after the last push.
            var runner = GameMeshLoadTestRunner.Instance;
            var loadActive = runner != null && (runner.InWorldCount > 0 || runner.HoldOnline || runner.Busy);
            var silenceMs = client.LastRttMs > 3000 ? 45000 : 25000;
            if (!loadActive && client.LastRttMs < 8000)
            {
                var pruned = client.Aoi.PruneSilent(AoiWorld.LocalNowMs, silenceMs);
                if (pruned > 0)
                {
                    GameMeshLog.Warn("prune silent AOI n=" + pruned + " silence_ms=" + silenceMs);
                    remoteCount = System.Math.Max(0, remoteCount - pruned);
                }
            }

            var afterLoadGhosts = runner != null && !loadActive && remoteCount > 0;
            var far = remoteCount > 0 && _local != null &&
                      TryNearestRemote(out _, out var dist) && dist > 40f;
            // Empty AOI with a full line is normal: this map only pushes entities inside 32m.
            // Polling WorldSnapshot here does not create interest, and it times out under load.
            var needRefresh = far || afterLoadGhosts;
            if (!needRefresh || client.LastRttMs > 5000)
                return;
            if (Time.unscaledTime < _nextAoiSnapshotAt)
                return;
            _nextAoiSnapshotAt = Time.unscaledTime + 8f;
            _ = client.RequestWorldSnapshotAsync(background: true);
        }

        void SyncRemotes(GameMeshClient client)
        {
            var localId = client.Aoi.LocalPlayerId != 0
                ? client.Aoi.LocalPlayerId
                : client.Session.PlayerId;
            var seen = _seenRemoteIds;
            seen.Clear();
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

            var dead = _deadRemoteIds;
            dead.Clear();
            foreach (var kv in _views)
            {
                if (!seen.Contains(kv.Key))
                    dead.Add(kv.Key);
            }

            // Bulk leave (压测下线 / AOI Clear): destroy immediately, skip fade.
            var instant = seen.Count == 0 && dead.Count > 0;
            var fadeSeconds = instant
                ? 0f
                : (client.Config != null && client.Config.aoiLeaveFadeSeconds > 0f
                    ? client.Config.aoiLeaveFadeSeconds
                    : 0.2f);
            foreach (var id in dead)
            {
                if (!_views.TryGetValue(id, out var view) || view == null)
                {
                    _views.Remove(id);
                    continue;
                }

                if (instant || fadeSeconds <= 0f)
                {
                    Destroy(view.gameObject);
                    _views.Remove(id);
                    continue;
                }

                if (!view.Fading)
                    view.BeginFade(fadeSeconds);
                if (!view.FadeFinished)
                    continue;
                Destroy(view.gameObject);
                _views.Remove(id);
            }
        }

        /// <summary>Called after load-test LogoutAll so leftover Bot models do not linger.</summary>
        public void PurgeAllRemoteViews(string reason = "")
        {
            var n = _views.Count;
            foreach (var kv in _views)
            {
                if (kv.Value != null)
                    Destroy(kv.Value.gameObject);
            }

            _views.Clear();
            if (n > 0)
                GameMeshLog.Info("purged remote views n=" + n +
                                 (string.IsNullOrEmpty(reason) ? "" : " " + reason));
        }
    }

    public sealed class RemotePlayerView : MonoBehaviour
    {
        public ulong EntityId;
        public bool HasModel { get; private set; }
        public bool Fading { get; private set; }
        public bool FadeFinished => Fading && Time.unscaledTime >= _fadeUntil;
        Vector3 _target;
        Vector3 _velocity;
        float _animSpeed;
        readonly RemotePoseBuffer _poses = new RemotePoseBuffer();
        float _fadeUntil;
        float _fadeSeconds = 0.2f;
        float _targetYaw;
        float _delay;
        long _lastServerTimeMs;
        double _clockOffset = double.NaN;
        float _lateSec;
        float _gapSec;
        float _lastSampleTime;
        ulong _lastSampleSeq;
        string _labelName;
        float _labelHp = -1f;
        float _labelMaxHp = -1f;
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
                // ThirdPersonController [RequireComponent(PlayerInput)]. Destroy the
                // dependent first, or Unity refuses to remove PlayerInput.
                if (name == "ThirdPersonController" || name == "StarterAssetsInputs" ||
                    name == "BasicRigidBodyPush")
                    Object.DestroyImmediate(behaviour);
            }

            behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                if (behaviour.GetType().Name == "PlayerInput")
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

        public void BeginFade(float seconds)
        {
            if (Fading)
                return;
            Fading = true;
            _fadeSeconds = Mathf.Max(0.05f, seconds);
            _fadeUntil = Time.unscaledTime + _fadeSeconds;
        }

        public void CancelFade()
        {
            if (!Fading)
                return;
            Fading = false;
            SetVisualAlpha(1f);
        }

        public void Apply(RemoteEntityState state)
        {
            CancelFade();
            // Order by server clock when present. Playback uses one local timeline; server times are
            // mapped onto it with a single offset (SampleTime), never mixed raw.
            if (state.ServerTimeMs != 0 && _lastServerTimeMs != 0 &&
                state.ServerTimeMs + 5 < _lastServerTimeMs)
                return;
            if (state.ServerTimeMs != 0)
                _lastServerTimeMs = state.ServerTimeMs;

            var next = new Vector3(state.X, state.Y, state.Z);
            next.y = GroundY(next.x, next.z, next.y);
            var err = Vector3.Distance(new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(next.x, 0f, next.z));
            var now = Time.unscaledTime;
            var sampleTime = SampleTime(state, now);
            var accepted = false;
            if (err > 30f)
            {
                transform.position = next;
                _velocity = Vector3.zero;
                _poses.Snap(next.x, next.y, next.z, state.Yaw, sampleTime, state.StateSeq);
                accepted = true;
            }
            else
                accepted = _poses.Push(next.x, next.y, next.z, state.Yaw, sampleTime, state.StateSeq);

            // Only move the follow target when a new sample is accepted; SyncRemotes runs every frame.
            if (accepted)
            {
                _target = next;
                _targetYaw = state.Yaw;
            }

            if (_label != null && (state.Name != _labelName || state.Hp != _labelHp || state.MaxHp != _labelMaxHp))
            {
                _labelName = state.Name;
                _labelHp = state.Hp;
                _labelMaxHp = state.MaxHp;
                _label.text = $"{state.Name} {state.Hp:0}/{state.MaxHp:0}";
            }
        }

        // Places a sample on the local timeline by its server move time, so watcher-side
        // throttling and bursty delivery do not change the apparent walking speed.
        float SampleTime(RemoteEntityState state, float now)
        {
            if (state.StateSeq != 0 && state.StateSeq <= _lastSampleSeq)
                return _lastSampleTime;
            var t = now;
            if (state.ServerTimeMs > 0)
            {
                var serverSec = state.ServerTimeMs / 1000.0;
                var offset = now - serverSec;
                if (double.IsNaN(_clockOffset) || offset < _clockOffset)
                    _clockOffset = offset;
                else
                    _clockOffset += System.Math.Min(offset - _clockOffset, 0.002);
                t = Mathf.Min(now, (float)(serverSec + _clockOffset));
            }

            if (_lastSampleSeq != 0 || _lastSampleTime > 0f)
            {
                if (t <= _lastSampleTime)
                    t = _lastSampleTime + 0.001f;
                _gapSec = Mathf.Max(_gapSec * 0.97f, Mathf.Min(1.5f, t - _lastSampleTime));
            }

            _lateSec = Mathf.Max(_lateSec * 0.97f, now - t);
            _lastSampleTime = t;
            _lastSampleSeq = state.StateSeq;
            return t;
        }

        // Interpolation needs the next sample before playback reaches the current one:
        // delay >= update gap + delivery lateness.
        float PlaybackDelay()
        {
            return Mathf.Clamp(_gapSec + _lateSec + 0.05f, _delay, 1.2f);
        }

        void SetVisualAlpha(float alpha)
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                    continue;
                var color = renderer.material.color;
                color.a = alpha;
                renderer.material.color = color;
            }

            if (_label == null)
                return;
            var label = _label.color;
            label.a = alpha;
            _label.color = label;
        }

        void Update()
        {
            if (Fading)
            {
                var remain = _fadeUntil - Time.unscaledTime;
                SetVisualAlpha(Mathf.Clamp01(remain / _fadeSeconds));
                return;
            }

            var pos = transform.position;
            if (_poses.TrySample(Time.unscaledTime, PlaybackDelay(), out var sx, out var sy, out var sz, out var syaw))
            {
                _target = new Vector3(sx, sy, sz);
                _targetYaw = syaw;
            }

            var desiredY = GroundY(_target.x, _target.z, _target.y);
            var desired = new Vector3(_target.x, desiredY, _target.z);
            var planar = Vector3.Distance(new Vector3(pos.x, 0f, pos.z),
                new Vector3(desired.x, 0f, desired.z));

            Vector3 next;
            if (planar > 30f)
            {
                next = desired;
                _velocity = Vector3.zero;
            }
            else
            {
                // SmoothDamp kills packet-to-packet jitter; keep under walk speed.
                next = Vector3.SmoothDamp(pos, desired, ref _velocity, 0.14f, 7.5f, Time.deltaTime);
                next.y = Mathf.Lerp(pos.y, desiredY, 1f - Mathf.Exp(-Time.deltaTime * 10f));
            }

            transform.position = next;
            if (planar > 0.03f || _velocity.sqrMagnitude > 0.01f)
            {
                // Same rule as the local third-person controller: a walking body faces where it walks.
                var flat = new Vector3(_velocity.x, 0f, _velocity.z);
                var faceYaw = flat.sqrMagnitude > 0.25f
                    ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg
                    : _targetYaw;
                var look = Quaternion.Euler(0f, faceYaw, 0f);
                transform.rotation = Quaternion.Slerp(transform.rotation, look,
                    1f - Mathf.Exp(-Time.deltaTime * 8f));
            }

            var speed = new Vector3(_velocity.x, 0f, _velocity.z).magnitude;
            var moving = speed > 0.15f;
            _animSpeed = Mathf.MoveTowards(_animSpeed, moving ? Mathf.Clamp(speed, 2f, 5.5f) : 0f,
                Time.deltaTime * 8f);

            if (_anim != null)
            {
                _anim.SetBool(_groundedId, true);
                _anim.SetFloat(_speedId, _animSpeed);
                _anim.SetFloat(_motionSpeedId, _animSpeed > 0.2f ? 1f : 0f);
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
