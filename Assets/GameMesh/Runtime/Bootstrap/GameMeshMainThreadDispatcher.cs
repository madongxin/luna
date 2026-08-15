using System;
using System.Collections.Concurrent;
using GameMesh.Network;
using UnityEngine;

namespace GameMesh.Bootstrap
{
    public sealed class GameMeshMainThreadDispatcher : MonoBehaviour, IMainThreadDispatcher
    {
        readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public static GameMeshMainThreadDispatcher Ensure(GameObject host)
        {
            var existing = host.GetComponent<GameMeshMainThreadDispatcher>();
            return existing != null ? existing : host.AddComponent<GameMeshMainThreadDispatcher>();
        }

        public void Enqueue(Action action)
        {
            if (action != null)
                _queue.Enqueue(action);
        }

        public int Pump(int max = 256)
        {
            var n = 0;
            while (n < max && _queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    GameMeshLog.Error(ex.ToString());
                }

                n++;
            }

            return n;
        }

        void Update()
        {
            Pump(512);
        }
    }
}
