using System;
using System.Collections.Concurrent;
using GameMesh.Network;
using UnityEngine;

namespace GameMesh.Bootstrap
{
    public sealed class GameMeshMainThreadDispatcher : MonoBehaviour, IMainThreadDispatcher
    {
        readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        int _backlog;

        public int Backlog => System.Threading.Interlocked.CompareExchange(ref _backlog, 0, 0);

        public static GameMeshMainThreadDispatcher Ensure(GameObject host)
        {
            var existing = host.GetComponent<GameMeshMainThreadDispatcher>();
            return existing != null ? existing : host.AddComponent<GameMeshMainThreadDispatcher>();
        }

        public void Enqueue(Action action)
        {
            if (action == null)
                return;
            _queue.Enqueue(action);
            System.Threading.Interlocked.Increment(ref _backlog);
        }

        public int Pump(int max = 256)
        {
            var n = 0;
            while (n < max && _queue.TryDequeue(out var action))
            {
                System.Threading.Interlocked.Decrement(ref _backlog);
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
            Pump(2048);
        }
    }
}
