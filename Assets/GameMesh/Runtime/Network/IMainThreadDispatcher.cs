using System;

namespace GameMesh.Network
{
    public interface IMainThreadDispatcher
    {
        void Enqueue(Action action);
        int Pump(int max = 256);
        int Backlog { get; }
    }

    public sealed class ImmediateDispatcher : IMainThreadDispatcher
    {
        public void Enqueue(Action action) => action?.Invoke();
        public int Pump(int max = 256) => 0;
        public int Backlog => 0;
    }

    public sealed class QueueDispatcher : IMainThreadDispatcher
    {
        readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();
        int _backlog;

        public int Backlog => System.Threading.Interlocked.CompareExchange(ref _backlog, 0, 0);

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
                action();
                n++;
            }

            return n;
        }
    }
}
