using GameMesh.Aoi;
using GameMesh.Bootstrap;
using GameMesh.Network;
using GameMesh.Protocol;
using Unity.FPS.Game;
using Unity.FPS.Gameplay;
using UnityEngine;

namespace GameMesh.Player
{
    public sealed class GameMeshWorldBinder : MonoBehaviour
    {
        PlayerCharacterController _local;
        Health _health;
        readonly System.Collections.Generic.Dictionary<ulong, RemotePlayerView> _views =
            new System.Collections.Generic.Dictionary<ulong, RemotePlayerView>();

        void LateUpdate()
        {
            var client = GameMeshClient.Instance;
            if (client == null)
                return;
            BindLocal(client);
            SyncRemotes(client);
            MaybeReportMove(client);
        }

        void BindLocal(GameMeshClient client)
        {
            if (_local == null)
                _local = FindObjectOfType<PlayerCharacterController>();
            if (_local == null)
                return;
            if (_health == null)
                _health = _local.GetComponent<Health>();

            var attrs = client.Session.Attributes;
            if (attrs.FromServer)
            {
                if (_health != null)
                {
                    _health.MaxHealth = attrs.MaxHp;
                    _health.CurrentHealth = attrs.Hp;
                }

                if (attrs.MoveSpeed > 0f)
                    _local.MaxSpeedOnGround = attrs.MoveSpeed;
            }

            if (client.Config.disableSprint)
                _local.SprintSpeedModifier = 1f;
            if (client.HasPendingSpawn)
            {
                _local.transform.position = client.PendingSpawn;
                _local.transform.rotation = Quaternion.Euler(0f, client.PendingSpawnYaw, 0f);
                client.HasPendingSpawn = false;
            }

            if (client.HasPendingCorrection)
            {
                _local.transform.position = client.PendingCorrection;
                _local.transform.rotation = Quaternion.Euler(0f, client.PendingCorrectionYaw, 0f);
                client.HasPendingCorrection = false;
            }
        }

        void MaybeReportMove(GameMeshClient client)
        {
            if (_local == null || client.Connection == null)
                return;
            if (client.MovesFrozen)
                return;
            if (client.MoveCorrector.ShouldSuppress(Time.unscaledTime))
                return;
            var pos = _local.transform.position;
            var yaw = _local.transform.eulerAngles.y;
            if (!client.MoveSampler.ShouldSend(pos, yaw, Time.unscaledTime, out var reject))
            {
                if (reject != null)
                    GameMeshLog.Warn("reject local move " + reject);
                return;
            }

            _ = client.SendMoveAsync(pos, yaw, default);
        }

        void SyncRemotes(GameMeshClient client)
        {
            var seen = new System.Collections.Generic.HashSet<ulong>();
            foreach (var kv in client.Aoi.Entities)
            {
                seen.Add(kv.Key);
                if (!_views.TryGetValue(kv.Key, out var view) || view == null)
                {
                    view = RemotePlayerView.Spawn(kv.Value, client.Config.interpolationDelayMs);
                    _views[kv.Key] = view;
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
        Vector3 _target;
        float _targetYaw;
        float _delay;
        TextMesh _label;

        public static RemotePlayerView Spawn(RemoteEntityState state, int delayMs)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "RemotePlayer_" + state.EntityId;
            Destroy(go.GetComponent<Collider>());
            var view = go.AddComponent<RemotePlayerView>();
            view.EntityId = state.EntityId;
            view._delay = delayMs / 1000f;
            view._target = new Vector3(state.X, state.Y, state.Z);
            go.transform.position = view._target;
            var labelGo = new GameObject("Name");
            labelGo.transform.SetParent(go.transform);
            labelGo.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            view._label = labelGo.AddComponent<TextMesh>();
            view._label.characterSize = 0.08f;
            view._label.anchor = TextAnchor.LowerCenter;
            view._label.alignment = TextAlignment.Center;
            view._label.fontSize = 48;
            view.Apply(state);
            return view;
        }

        public void Apply(RemoteEntityState state)
        {
            var next = new Vector3(state.X, state.Y, state.Z);
            if (Vector3.Distance(transform.position, next) > 8f)
                transform.position = next;
            _target = next;
            _targetYaw = state.Yaw;
            if (_label != null)
                _label.text = $"{state.Name} {state.Hp:0}/{state.MaxHp:0}";
        }

        void Update()
        {
            var t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.04f, _delay));
            transform.position = Vector3.Lerp(transform.position, _target, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, _targetYaw, 0f), t);
        }
    }
}
