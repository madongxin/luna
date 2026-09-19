using System.Collections.Generic;
using GameMesh.Protocol;
using UnityEngine;

namespace GameMesh.Map
{
    public sealed class GameMeshPortalWorld : MonoBehaviour
    {
        readonly List<GameObject> _spawned = new List<GameObject>();

        public int SpawnedCount => _spawned.Count;

        public void Rebuild(IList<PortalDef> portals)
        {
            Clear();
            if (portals == null)
                return;
            for (var i = 0; i < portals.Count; i++)
            {
                if (portals[i] == null || string.IsNullOrEmpty(portals[i].PortalId))
                    continue;
                _spawned.Add(GameMeshPortalView.Spawn(portals[i]));
            }
        }

        public void Clear()
        {
            for (var i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    Destroy(_spawned[i]);
            }

            _spawned.Clear();
        }

        void OnDestroy()
        {
            Clear();
        }
    }
}
