using UnityEngine;

namespace GameMesh.Player
{
    [CreateAssetMenu(menuName = "GameMesh/Playable Player Catalog", fileName = "PlayablePlayerCatalog")]
    public sealed class PlayablePlayerCatalog : ScriptableObject
    {
        public GameObject playerPrefab;
        public GameObject thirdPersonArmature;
        public GameObject thirdPersonFollowCamera;
        public GameObject thirdPersonMainCamera;
    }
}
