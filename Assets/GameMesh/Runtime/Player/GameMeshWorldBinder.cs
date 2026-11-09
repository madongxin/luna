using GameMesh.Aoi;
using GameMesh.Bootstrap;
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
        int _holdSpawnFrames;
        Vector3 _goodPose;
        bool _hasGoodPose;
        bool _loggedSink;
        readonly AoiStallTrace _stallTrace = new AoiStallTrace();
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

        AoiWorld _aoiHook;

        void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            HookAoi(GameMeshClient.Instance);
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnhookAoi();
            _stallTrace.Close();
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
            HookAoi(client);
            MaybeRefreshAoi(client);
            SyncRemotes(client);
            TraceAoiStalls();
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

        /// <summary>
        /// Recover a fallen body without pulling XZ back to the previous stance.
        /// Rewinding XZ traps the player: LateUpdate undoes every step out of a hole.
        /// </summary>
        public static bool TryRecoverPose(Vector3 pos, bool hasGood, Vector3 good, bool hasSurface,
            float surface, float floorY, out Vector3 held)
        {
            held = pos;
            if (hasSurface && surface >= floorY && pos.y < surface - 0.35f)
            {
                held.y = surface + 0.12f;
                return true;
            }

            if (hasGood && SameSpotDrop(pos, good))
            {
                held.y = good.y;
                return true;
            }

            // Through the world floor. Keep the horizontal step and restore height.
            if (pos.y < floorY && hasGood)
            {
                held.y = good.y < floorY ? floorY + 0.12f : good.y;
                return true;
            }

            return false;
        }

        void HoldStable(GameMeshClient client)
        {
            if (_local == null || !UsesTerrainHold(client))
                return;
            if (client.Connection == null || client.Connection.State != ConnectionState.InWorld)
                return;

            var pos = _local.position;
            var yaw = _local.eulerAngles.y;
            var hasSurface = TryTerrainSurface(pos, out var surface);
            if (!TryRecoverPose(pos, _hasGoodPose, _goodPose, hasSurface, surface, MapFloorY, out var held))
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

            // Cell exit is op=3 LEAVE, handled in the same frame the event is applied.
            // PruneSilent is not that deletion: a 25s timeout leaves the model at the last
            // coordinate until the watcher walks up to it.

            // Never fetch a full snapshot merely because a remote is far away or an
            // intermediate AOI position was coalesced. Replacing the AOI table made views
            // disappear/reappear at a distant position and looked like teleporting.
            // Real reliable-state gaps are handled by GameMeshClient.OnPush.
        }

        void SyncRemotes(GameMeshClient client)
        {
            var localId = client.Aoi.LocalPlayerId != 0
                ? client.Aoi.LocalPlayerId
                : client.Session.PlayerId;
            var seen = _seenRemoteIds;
            seen.Clear();
            foreach (var kv in client.Aoi.Entities)
            {
                var bodyId = AoiWorld.BodyId(kv.Value.EntityId, kv.Value.PlayerId);
                if (bodyId == 0)
                    bodyId = kv.Key;
                if (localId != 0 && (bodyId == localId || kv.Value.PlayerId == localId))
                    continue;
                seen.Add(bodyId);
                if (!_views.TryGetValue(bodyId, out var view) || view == null)
                {
                    view = FindView(bodyId, kv.Key, kv.Value.EntityId);
                    if (view != null)
                        BindView(bodyId, view);
                }

                if (view == null)
                {
                    try
                    {
                        view = RemotePlayerView.Spawn(kv.Value, client.Config.interpolationDelayMs,
                            attachModel: true);
                    }
                    catch (System.Exception ex)
                    {
                        GameMeshLog.Warn("remote spawn failed id=" + bodyId + " " + ex.Message);
                        continue;
                    }

                    BindView(bodyId, view);
                }
                else if (!view.HasModel)
                {
                    view.TryAttachModel();
                }

                view.Apply(kv.Value);
            }

            var dead = _deadRemoteIds;
            dead.Clear();
            foreach (var kv in _views)
            {
                var viewId = kv.Value != null && kv.Value.PlayerId != 0 ? kv.Value.PlayerId : kv.Key;
                if (!seen.Contains(kv.Key) && !seen.Contains(viewId))
                    dead.Add(kv.Key);
            }

            // op=3 LEAVE already removed the body. Hide the model and nameplate now, then Destroy.
            // Do not fade them in place and do not wait for the next MOVE or the silence timeout.
            foreach (var id in dead)
            {
                if (!_views.TryGetValue(id, out var view) || view == null)
                {
                    _views.Remove(id);
                    continue;
                }

                ReleaseView(view);
            }
        }

        void TraceAoiStalls()
        {
            var origin = _local != null ? _local.position : Vector3.zero;
            _stallTrace.BeginFrame(Time.unscaledTime, Time.frameCount, Time.unscaledDeltaTime,
                origin.x, origin.z);
            foreach (var kv in _views)
            {
                var view = kv.Value;
                if (view == null || view.Fading)
                    continue;
                var sample = view.CaptureStallSample();
                _stallTrace.Observe(sample);
            }

            _stallTrace.EndFrame();
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

        RemotePlayerView FindView(ulong bodyId, ulong worldKey, ulong entityId)
        {
            if (worldKey != 0 && worldKey != bodyId &&
                _views.TryGetValue(worldKey, out var byKey) && byKey != null)
                return byKey;
            if (entityId != 0 && entityId != bodyId &&
                _views.TryGetValue(entityId, out var byEntity) && byEntity != null)
                return byEntity;
            foreach (var kv in _views)
            {
                var view = kv.Value;
                if (view == null)
                    continue;
                if (view.PlayerId == bodyId || view.EntityId == bodyId ||
                    (entityId != 0 && (view.PlayerId == entityId || view.EntityId == entityId)))
                    return view;
            }

            return null;
        }

        void BindView(ulong bodyId, RemotePlayerView view)
        {
            if (view == null || bodyId == 0)
                return;
            System.Collections.Generic.List<ulong> stale = null;
            foreach (var kv in _views)
            {
                if (kv.Value != view || kv.Key == bodyId)
                    continue;
                if (stale == null)
                    stale = new System.Collections.Generic.List<ulong>();
                stale.Add(kv.Key);
            }

            if (stale != null)
            {
                for (var i = 0; i < stale.Count; i++)
                    _views.Remove(stale[i]);
            }

            _views[bodyId] = view;
        }

        void ReleaseView(RemotePlayerView view)
        {
            if (view == null)
                return;
            System.Collections.Generic.List<ulong> keys = null;
            foreach (var kv in _views)
            {
                if (kv.Value != view)
                    continue;
                if (keys == null)
                    keys = new System.Collections.Generic.List<ulong>();
                keys.Add(kv.Key);
            }

            if (keys != null)
            {
                for (var i = 0; i < keys.Count; i++)
                    _views.Remove(keys[i]);
            }

            view.HideNow();
            Destroy(view.gameObject);
        }

        void HookAoi(GameMeshClient client)
        {
            if (client == null || client.Aoi == null || client.Aoi == _aoiHook)
                return;
            UnhookAoi();
            _aoiHook = client.Aoi;
            _aoiHook.BodyRemoved += OnBodyRemoved;
        }

        void UnhookAoi()
        {
            if (_aoiHook == null)
                return;
            _aoiHook.BodyRemoved -= OnBodyRemoved;
            _aoiHook = null;
        }

        void OnBodyRemoved(ulong id)
        {
            if (id == 0)
                return;
            var dead = _deadRemoteIds;
            dead.Clear();
            foreach (var kv in _views)
            {
                var view = kv.Value;
                if (kv.Key == id || (view != null && (view.PlayerId == id || view.EntityId == id)))
                    dead.Add(kv.Key);
            }

            for (var i = 0; i < dead.Count; i++)
            {
                if (!_views.TryGetValue(dead[i], out var view) || view == null)
                {
                    _views.Remove(dead[i]);
                    continue;
                }

                ReleaseView(view);
            }
        }

    }

    public sealed class RemotePlayerView : MonoBehaviour
    {
        public ulong EntityId;
        public ulong PlayerId => _playerId;
        public bool HasModel { get; private set; }
        public bool Fading { get; private set; }
        public bool FadeFinished => Fading && Time.unscaledTime >= _fadeUntil;
        Vector3 _target;
        Vector3 _velocity;
        float _animSpeed;
        float _locomotionSpeed;
        float _locomotionUntil;
        float _chaseMetersPerSec;
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
        float _lastAuthorityX;
        float _lastAuthorityZ;
        float _lastAuthorityYaw;
        bool _hasAuthorityPose;
        ulong _playerId;
        int _dropOlderSeq;
        int _dropServerTime;
        int _dropPose;
        float _nextJumpLogAt;
        string _labelName;
        float _labelHp = -1f;
        float _labelMaxHp = -1f;
        TextMesh _label;
        Animator _anim;
        Transform _model;
        Renderer[] _modelRenderers;
        float _nextGroundAlignAt;
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
            return Spawn(state, delayMs, attachModel, null);
        }

        public static RemotePlayerView Spawn(RemoteEntityState state, int delayMs, bool attachModel,
            Vector3? resumeFrom)
        {
            var go = new GameObject("RemotePlayer_" + state.EntityId);
            go.SetActive(false);
            var view = go.AddComponent<RemotePlayerView>();
            view.EntityId = state.EntityId != 0 ? state.EntityId : AoiWorld.BodyId(state.EntityId, state.PlayerId);
            view._playerId = state.PlayerId != 0 ? state.PlayerId : view.EntityId;
            view._delay = delayMs / 1000f;
            view._color = Color.HSVToRGB((state.EntityId % 12) / 12f, 0.72f, 0.95f);
            view._target = new Vector3(state.X,
                GroundY(state.X, state.Z, state.Y) + FootClearance, state.Z);

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
            // ENTER and MOVE positions are the current body coordinate.
            var place = resumeFrom ?? view._target;
            go.transform.SetPositionAndRotation(place, Quaternion.Euler(0f, state.Yaw, 0f));
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
                _model = visual.transform;
                _modelRenderers = visual.GetComponentsInChildren<Renderer>(true);
                HasModel = _modelRenderers.Length != 0;
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

        public void HideNow()
        {
            Fading = false;
            _hasAuthorityPose = false;
            _chaseMetersPerSec = 0f;
            _locomotionSpeed = 0f;
            _locomotionUntil = 0f;
            _poses.Clear();
            if (_label != null)
                _label.gameObject.SetActive(false);
            gameObject.SetActive(false);
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

        public AoiStallSample CaptureStallSample()
        {
            var now = Time.unscaledTime;
            var pos = transform.position;
            var speed = new Vector3(_velocity.x, 0f, _velocity.z).magnitude;
            return new AoiStallSample
            {
                EntityId = EntityId,
                PlayerId = _playerId,
                Name = _labelName ?? "",
                X = pos.x,
                Y = pos.y,
                Z = pos.z,
                AuthorityX = _lastAuthorityX,
                AuthorityZ = _lastAuthorityZ,
                AuthorityYaw = _lastAuthorityYaw,
                TargetX = _target.x,
                TargetZ = _target.z,
                Speed = speed,
                StateSeq = _lastSampleSeq,
                ServerTimeMs = _lastServerTimeMs,
                SampleAge = _lastSampleTime > 0f ? Mathf.Max(0f, now - _lastSampleTime) : -1f,
                GapSec = _gapSec,
                LateSec = _lateSec,
                DelaySec = PlaybackDelay(),
                PoseCount = _poses.Count,
                DropOlderSeq = _dropOlderSeq,
                DropServerTime = _dropServerTime,
                DropPose = _dropPose,
                HasAuthority = _hasAuthorityPose
            };
        }

        public void Apply(RemoteEntityState state)
        {
            CancelFade();
            UpdateLabel(state);
            if (state.PlayerId != 0)
                _playerId = state.PlayerId;
            // SyncRemotes visits every entity every frame. Aoi MOVE along one path keeps the same
            // state_seq while position changes — those body updates must still move the Transform.
            if (state.StateSeq != 0 && _lastSampleSeq != 0 &&
                !RemotePoseBuffer.IsAlongPathUpdate(state.StateSeq, _lastSampleSeq))
            {
                _dropOlderSeq++;
                return;
            }
            if (state.StateSeq != 0 && state.StateSeq == _lastSampleSeq)
            {
                // _target is delayed playback and changes every rendered frame. Comparing the
                // AOI table with it re-enqueued one server packet every frame and reset blending.
                var unchanged =
                    _hasAuthorityPose &&
                    Mathf.Abs(state.X - _lastAuthorityX) <= 0.01f &&
                    Mathf.Abs(state.Z - _lastAuthorityZ) <= 0.01f &&
                    Mathf.Abs(Mathf.DeltaAngle(state.Yaw, _lastAuthorityYaw)) <= 0.5f;
                if (unchanged)
                    return;
            }
            // MOVE stamps the time the displacement was confirmed, which can be earlier than
            // an ENTER or a previous sample already applied. Dropping it froze the model on
            // the old point until a later timestamp arrived meters away. state_seq still
            // rejects a strictly older update above. Keep the newest clock for mapping.
            var previousServerTimeMs = _lastServerTimeMs;
            var timeRewound = AoiSampleClock.IsRewound(state.ServerTimeMs, _lastServerTimeMs);
            if (timeRewound)
                _dropServerTime++;
            else if (state.ServerTimeMs != 0)
                _lastServerTimeMs = state.ServerTimeMs;

            var next = new Vector3(state.X, state.Y, state.Z);
            next.y = GroundY(next.x, next.z, next.y) + FootClearance;
            var authorityJump = Vector3.Distance(
                _hasAuthorityPose
                    ? new Vector3(_lastAuthorityX, 0f, _lastAuthorityZ)
                    : new Vector3(_target.x, 0f, _target.z),
                new Vector3(next.x, 0f, next.z));
            if (authorityJump > 5f && previousServerTimeMs > 0 && state.ServerTimeMs > previousServerTimeMs)
            {
                var serverDt = (state.ServerTimeMs - previousServerTimeMs) / 1000f;
                var plausible = MoveStepLimit.ServerMetersPerSec * serverDt + 5f;
                if (authorityJump > plausible && Time.unscaledTime >= _nextJumpLogAt)
                {
                    _nextJumpLogAt = Time.unscaledTime + 2f;
                    GameMeshLog.Warn("remote authority jump id=" + EntityId +
                                     " seq=" + state.StateSeq +
                                     " meters=" + authorityJump.ToString("F1") +
                                     " server_dt_ms=" + (state.ServerTimeMs - previousServerTimeMs));
                }
            }
            var now = Time.unscaledTime;
            var previousSampleTime = _lastSampleTime;
            var previousX = _lastAuthorityX;
            var previousZ = _lastAuthorityZ;
            var hadPose = _hasAuthorityPose;
            var sampleTime = SampleTime(state, now, timeRewound);
            // A seq gap or a large network gap means intermediate samples were lost, not that
            // the body teleported. Always retain the last visible pose as the blend origin.
            var accepted = _poses.Push(next.x, next.y, next.z, state.Yaw, sampleTime, state.StateSeq);
            if (!accepted)
                _dropPose++;

            // Only move the follow target when a new sample is accepted; SyncRemotes runs every frame.
            if (accepted)
            {
                if (hadPose)
                    NoteLocomotion(state.X - previousX, state.Z - previousZ,
                        sampleTime - previousSampleTime, now);
                _lastAuthorityX = state.X;
                _lastAuthorityZ = state.Z;
                _lastAuthorityYaw = state.Yaw;
                _hasAuthorityPose = true;
                _target = next;
                _targetYaw = state.Yaw;
                // MOVE.position is the current authoritative destination. Update walks only
                // along the received segment and stops on this point; it never extrapolates.
            }

        }

        void UpdateLabel(RemoteEntityState state)
        {
            if (_label == null)
                return;
            // MOVE often omits player_name to save bandwidth. Keep whatever ENTER gave us,
            // and if the name is still empty fall back to player_id so the model is never
            // anonymous (HUD "最近 BotN" already uses the GameObject id).
            var shown = !string.IsNullOrEmpty(state.Name)
                ? state.Name
                : ("#" + (state.PlayerId != 0 ? state.PlayerId : state.EntityId));
            if (shown == _labelName && state.Hp == _labelHp && state.MaxHp == _labelMaxHp)
                return;
            _labelName = shown;
            _labelHp = state.Hp;
            _labelMaxHp = state.MaxHp;
            if (state.MaxHp > 0f || state.Hp > 0f)
                _label.text = shown + " " + state.Hp.ToString("0") + "/" + state.MaxHp.ToString("0");
            else
                _label.text = shown;
        }

        // Places a sample on the local timeline by its server move time, so watcher-side
        // throttling and bursty delivery do not change the apparent walking speed.
        float SampleTime(RemoteEntityState state, float now, bool timeRewound)
        {
            // Older seq only. Equal seq with a new body pose still advances the local timeline.
            if (state.StateSeq != 0 && state.StateSeq < _lastSampleSeq)
                return _lastSampleTime;
            var t = now;
            if (timeRewound && (_lastSampleSeq != 0 || _lastSampleTime > 0f))
            {
                var dx = state.X - _lastAuthorityX;
                var dz = state.Z - _lastAuthorityZ;
                var distance = Mathf.Sqrt(dx * dx + dz * dz);
                t = AoiSampleClock.PlaceRewound(now, _lastSampleTime, distance,
                    MoveStepLimit.ServerMetersPerSec);
            }
            else if (state.ServerTimeMs > 0)
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
                // The pose buffer keeps only the previous and latest samples. Its delay must
                // track that current pair, not a historical 15s maximum; otherwise playback
                // remains before the previous sample and the model appears permanently frozen.
                _gapSec = Mathf.Min(3.2f, t - _lastSampleTime);
            }

            _lateSec = Mathf.Min(3.2f, Mathf.Max(0f, now - t));
            _lastSampleTime = t;
            _lastSampleSeq = state.StateSeq;
            return t;
        }

        // Interpolation needs the next sample before playback reaches the current one:
        // delay >= update gap + delivery lateness.
        float PlaybackDelay()
        {
            return Mathf.Clamp(_gapSec + _lateSec + 0.05f, _delay, 3.25f);
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
                HideNow();
                Destroy(gameObject);
                return;
            }

            var pos = transform.position;
            if (!_hasAuthorityPose)
                return;
            // Walk only toward the newest received MOVE coordinate. Do not jump to it and do
            // not continue past it while waiting for the next MOVE or LEAVE.
            var renderDt = RemotePoseBuffer.ClampRenderDelta(Time.unscaledDeltaTime);
            var destination = new Vector3(_lastAuthorityX, pos.y, _lastAuthorityZ);
            _target = destination;
            _targetYaw = _lastAuthorityYaw;
            var planar = Vector3.Distance(new Vector3(pos.x, 0f, pos.z),
                new Vector3(destination.x, 0f, destination.z));
            var step = _chaseMetersPerSec * renderDt;
            var next = planar <= 0.02f || step <= 0f
                ? pos
                : Vector3.MoveTowards(pos, destination, step);
            _velocity = (next - pos) / renderDt;
            next.y = GroundY(next.x, next.z, next.y) + FootClearance;
            transform.position = next;
            if (planar > 0.03f || _velocity.sqrMagnitude > 0.01f)
            {
                var flat = new Vector3(_velocity.x, 0f, _velocity.z);
                var faceYaw = flat.sqrMagnitude > 0.25f
                    ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg
                    : _targetYaw;
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.Euler(0f, faceYaw, 0f),
                    1f - Mathf.Exp(-renderDt * 8f));
            }

            // Run while the body is still walking to the MOVE point. After it arrives, keep the
            // clip across the short gap until the next sample so it does not flicker to idle.
            var walking = planar > 0.08f && _chaseMetersPerSec > 0.05f;
            var animTarget = 0f;
            if (walking)
                animTarget = _locomotionSpeed > 0.15f ? _locomotionSpeed : 6f;
            else if (_locomotionSpeed > 0.15f && Time.unscaledTime <= _locomotionUntil)
                animTarget = _locomotionSpeed;
            _animSpeed = Mathf.MoveTowards(_animSpeed, animTarget, renderDt * 8f);

            if (_anim != null)
            {
                _anim.SetBool(_groundedId, true);
                _anim.SetFloat(_speedId, _animSpeed);
                _anim.SetFloat(_motionSpeedId, _animSpeed > 0.2f ? 1f : 0f);
            }

            if (Time.unscaledTime >= _nextGroundAlignAt)
            {
                _nextGroundAlignAt = Time.unscaledTime + 0.5f;
                AlignModelToGround();
            }

            if (_label == null)
                return;
            var cam = MainCameraThisFrame();
            if (cam != null)
                _label.transform.rotation = Quaternion.LookRotation(
                    _label.transform.position - cam.transform.position);
        }

        // Run clip thresholds in the third-person blend tree: walk at 2, run at 6.
        // Speed comes from the XZ step between two AOI samples, never from vx/vz.
        public static void LocomotionFromStep(float distance, float spanSec, out float speed, out float holdSec)
        {
            speed = 0f;
            holdSec = 0f;
            if (distance <= 0.05f)
                return;
            var span = spanSec < 0.05f ? 0.05f : spanSec;
            speed = distance / span;
            if (speed < 2f)
                speed = 2f;
            if (speed > 6f)
                speed = 6f;
            holdSec = spanSec < 0.35f ? 0.35f : spanSec;
            if (holdSec > 0.75f)
                holdSec = 0.75f;
        }

        // Meters per second along the latest AOI step. Capped at the server move speed so a
        // sparse sample cannot cross the whole gap in one frame. Zero when the body did not move.
        public static float ChaseMetersPerSec(float distance, float spanSec)
        {
            if (distance <= 0.05f)
                return 0f;
            var span = spanSec < 0.05f ? 0.05f : spanSec;
            var speed = distance / span;
            if (speed > MoveStepLimit.ServerMetersPerSec)
                speed = MoveStepLimit.ServerMetersPerSec;
            return speed;
        }

        void NoteLocomotion(float dx, float dz, float span, float now)
        {
            var distance = Mathf.Sqrt(dx * dx + dz * dz);
            LocomotionFromStep(distance, span, out _locomotionSpeed, out var hold);
            _locomotionUntil = hold > 0f ? now + hold : 0f;
            var chase = ChaseMetersPerSec(distance, span);
            if (chase > 0f)
                _chaseMetersPerSec = chase;
            if (_locomotionSpeed > 0f)
                _animSpeed = _locomotionSpeed;
        }

        static Camera _frameCamera;
        static int _cameraFrame = -1;

        static Camera MainCameraThisFrame()
        {
            if (_cameraFrame == Time.frameCount)
                return _frameCamera;
            _cameraFrame = Time.frameCount;
            _frameCamera = Camera.main;
            return _frameCamera;
        }

        void AlignModelToGround()
        {
            if (_model == null || _modelRenderers == null || _modelRenderers.Length == 0)
                return;
            var any = false;
            var bounds = new Bounds();
            for (var i = 0; i < _modelRenderers.Length; i++)
            {
                var renderer = _modelRenderers[i];
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
            var surface = GroundY(transform.position.x, transform.position.z, transform.position.y);
            var extra = surface - bounds.min.y + 0.04f;
            if (extra > -0.12f && extra < 0.06f)
                return;
            var local = _model.localPosition;
            // Pull deep sinks up in one step; small errors ease in.
            var step = Mathf.Abs(extra) > 0.4f ? extra : extra * 0.7f;
            local.y = Mathf.Clamp(local.y + step, -0.2f, 2.2f);
            _model.localPosition = local;
            if (_label != null)
                _label.transform.localPosition = new Vector3(0f, Mathf.Max(2.15f, local.y + 1.9f), 0f);
        }

        const float FootClearance = 0.08f;

        static float GroundY(float x, float z, float fallback)
        {
            var terrains = Terrain.activeTerrains;
            var best = float.NegativeInfinity;
            var found = false;
            for (var i = 0; i < terrains.Length; i++)
            {
                var terrain = terrains[i];
                if (terrain == null || terrain.terrainData == null)
                    continue;
                var origin = terrain.GetPosition();
                var size = terrain.terrainData.size;
                if (x < origin.x || z < origin.z || x > origin.x + size.x || z > origin.z + size.z)
                    continue;
                var y = terrain.SampleHeight(new Vector3(x, 0f, z)) + origin.y;
                if (!found || y > best)
                {
                    best = y;
                    found = true;
                }
            }

            if (found)
                return best;

            if (Physics.Raycast(new Vector3(x, fallback + 120f, z), Vector3.down, out var hit, 400f,
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
