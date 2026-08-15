using UnityEngine;

namespace GameMesh.Map
{
    public sealed class GameMeshSpawnPoint : MonoBehaviour
    {
        public string id = "default";
        public float yaw;
        public bool isDefault = true;

        void OnValidate()
        {
            yaw = transform.eulerAngles.y;
        }
    }
}
