using System;

namespace GameMesh.Network
{
    public interface IMainThreadDispatcher
    {
        void Enqueue(Action action);
        int Pump(int max = 256);
    }

    public sealed class ImmediateDispatcher : IMainThreadDispatcher
    {
        public void Enqueue(Action action) => action?.Invoke();
        public int Pump(int max = 256) => 0;
    }

    public sealed class QueueDispatcher : IMainThreadDispatcher
    {
        readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();

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
                action();
                n++;
            }

            return n;
        }
    }
}
