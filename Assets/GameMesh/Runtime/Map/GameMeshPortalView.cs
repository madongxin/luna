using GameMesh.Bootstrap;
using GameMesh.Player;
using GameMesh.Protocol;
using UnityEngine;

namespace GameMesh.Map
{
    public sealed class GameMeshPortalView : MonoBehaviour
    {
        public string PortalId;
        public float TriggerRadius = 3f;
        Transform _swirl;
        bool _inside;

        public static GameObject Spawn(PortalDef def)
        {
            var prefab = Resources.Load<GameObject>("SpawnPortal");
            GameObject go;
            if (prefab != null)
                go = Instantiate(prefab);
            else
            {
                go = new GameObject("SpawnPortal");
                go.AddComponent<GameMeshPortalView>();
            }

            var view = go.GetComponent<GameMeshPortalView>() ?? go.AddComponent<GameMeshPortalView>();
            view.Bind(def);
            return go;
        }

        public void Bind(PortalDef def)
        {
            if (def == null)
                return;
            PortalId = def.PortalId ?? "";
            TriggerRadius = def.TriggerRadius > 0.1f ? def.TriggerRadius : HelloMapCatalog.PortalRadius;
            var pos = def.Position != null
                ? new Vector3(def.Position.X, def.Position.Y, def.Position.Z)
                : new Vector3(HelloMapCatalog.PortalX, HelloMapCatalog.PortalY, HelloMapCatalog.PortalZ);
            transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, def.Yaw, 0f));
            name = "SpawnPortal_" + PortalId;
            EnsureVisual();
            EnsureTrigger();
        }

        void Awake()
        {
            EnsureVisual();
            EnsureTrigger();
        }

        void Update()
        {
            if (_swirl != null)
                _swirl.Rotate(0f, 80f * Time.deltaTime, 0f, Space.Self);
            MaybeInteractByDistance();
        }

        void OnTriggerEnter(Collider other)
        {
            if (IsLocalPlayer(other.transform))
                RequestInteract();
        }

        void MaybeInteractByDistance()
        {
            var pawn = FindLocalPawn();
            if (pawn == null)
                return;
            var dx = pawn.position.x - transform.position.x;
            var dz = pawn.position.z - transform.position.z;
            var inside = dx * dx + dz * dz <= TriggerRadius * TriggerRadius;
            if (inside && !_inside)
                RequestInteract();
            _inside = inside;
        }

        void RequestInteract()
        {
            var client = GameMeshClient.Instance;
            if (client != null)
                client.TryInteractPortal(PortalId);
        }

        void EnsureVisual()
        {
            if (transform.Find("PillarL") != null)
            {
                _swirl = transform.Find("Swirl");
                return;
            }

            var stone = MakeMat(new Color(0.35f, 0.32f, 0.28f, 1f));
            var glow = MakeMat(new Color(0.25f, 0.85f, 1f, 0.55f));
            var core = MakeMat(new Color(0.55f, 0.2f, 1f, 0.7f));

            var left = GameObject.CreatePrimitive(PrimitiveType.Cube);
            left.name = "PillarL";
            left.transform.SetParent(transform, false);
            left.transform.localPosition = new Vector3(-1.2f, 1.6f, 0f);
            left.transform.localScale = new Vector3(0.45f, 3.2f, 0.45f);
            ApplyMat(left, stone);
            StripCollider(left);

            var right = GameObject.CreatePrimitive(PrimitiveType.Cube);
            right.name = "PillarR";
            right.transform.SetParent(transform, false);
            right.transform.localPosition = new Vector3(1.2f, 1.6f, 0f);
            right.transform.localScale = new Vector3(0.45f, 3.2f, 0.45f);
            ApplyMat(right, stone);
            StripCollider(right);

            var arch = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arch.name = "Arch";
            arch.transform.SetParent(transform, false);
            arch.transform.localPosition = new Vector3(0f, 3.15f, 0f);
            arch.transform.localScale = new Vector3(3.1f, 0.4f, 0.5f);
            ApplyMat(arch, stone);
            StripCollider(arch);

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Gate";
            disc.transform.SetParent(transform, false);
            disc.transform.localPosition = new Vector3(0f, 1.55f, 0f);
            disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            disc.transform.localScale = new Vector3(2.2f, 0.08f, 2.2f);
            ApplyMat(disc, glow);
            StripCollider(disc);

            var swirl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            swirl.name = "Swirl";
            swirl.transform.SetParent(transform, false);
            swirl.transform.localPosition = new Vector3(0f, 1.55f, 0.05f);
            swirl.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            swirl.transform.localScale = new Vector3(1.4f, 0.05f, 1.4f);
            ApplyMat(swirl, core);
            StripCollider(swirl);
            _swirl = swirl.transform;
        }

        void EnsureTrigger()
        {
            var sphere = GetComponent<SphereCollider>();
            if (sphere == null)
                sphere = gameObject.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.center = new Vector3(0f, 1.2f, 0f);
            sphere.radius = Mathf.Max(0.5f, TriggerRadius);
            var body = GetComponent<Rigidbody>();
            if (body == null)
                body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }

        static bool IsLocalPlayer(Transform t)
        {
            if (t == null)
                return false;
            if (t.GetComponent<RemotePlayerView>() != null || t.name.StartsWith("RemotePlayer_"))
                return false;
            return t.GetComponent<CharacterController>() != null ||
                   t.GetComponentInParent<CharacterController>() != null;
        }

        static Transform FindLocalPawn()
        {
            var named = GameObject.Find("PlayerHumanoid")
                        ?? GameObject.Find("PlayerMixamo")
                        ?? GameObject.Find("PlayerArmature")
                        ?? GameObject.Find("Player");
            if (named != null)
                return named.transform;
            var fps = FindObjectOfType<Unity.FPS.Gameplay.PlayerCharacterController>();
            return fps != null ? fps.transform : null;
        }

        static void StripCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
        }

        static void ApplyMat(GameObject go, Material mat)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = mat;
        }

        static Material MakeMat(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            return mat;
        }
    }
}
