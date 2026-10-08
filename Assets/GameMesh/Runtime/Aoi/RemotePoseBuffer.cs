using System;

namespace GameMesh.Aoi
{
    public sealed class RemotePoseBuffer
    {
        public const int Depth = 3;

        struct Sample
        {
            public float X, Y, Z, Yaw, Time;
            public ulong Seq;
        }

        readonly Sample[] _samples = new Sample[Depth];
        int _count;
        ulong _lastSeq;

        public int Count => _count;

        public bool Push(float x, float y, float z, float yaw, float time, ulong stateSeq)
        {
            if (stateSeq != 0 && _lastSeq != 0 && stateSeq <= _lastSeq)
                return false;
            if (stateSeq != 0)
                _lastSeq = stateSeq;
            if (_count == Depth)
            {
                for (var i = 1; i < Depth; i++)
                    _samples[i - 1] = _samples[i];
                _count = Depth - 1;
            }

            _samples[_count++] = new Sample
            {
                X = x, Y = y, Z = z, Yaw = yaw, Time = time, Seq = stateSeq
            };
            return true;
        }

        public void Snap(float x, float y, float z, float yaw, float time, ulong stateSeq)
        {
            _count = 0;
            _lastSeq = 0;
            Push(x, y, z, yaw, time, stateSeq);
        }

        public bool TrySample(float now, float delaySec, out float x, out float y, out float z, out float yaw)
        {
            x = y = z = yaw = 0f;
            if (_count <= 0)
                return false;
            if (_count == 1)
            {
                Copy(_samples[0], out x, out y, out z, out yaw);
                return true;
            }

            var t = now - Math.Max(0f, delaySec);
            if (t <= _samples[0].Time)
            {
                Copy(_samples[0], out x, out y, out z, out yaw);
                return true;
            }

            var last = _count - 1;
            if (t >= _samples[last].Time)
            {
                Copy(_samples[last], out x, out y, out z, out yaw);
                return true;
            }

            for (var i = 0; i < last; i++)
            {
                var a = _samples[i];
                var b = _samples[i + 1];
                if (t > b.Time)
                    continue;
                var span = b.Time - a.Time;
                var u = span <= 0.0001f ? 1f : (t - a.Time) / span;
                x = a.X + (b.X - a.X) * u;
                y = a.Y + (b.Y - a.Y) * u;
                z = a.Z + (b.Z - a.Z) * u;
                yaw = LerpAngle(a.Yaw, b.Yaw, u);
                return true;
            }

            Copy(_samples[last], out x, out y, out z, out yaw);
            return true;
        }

        static void Copy(Sample s, out float x, out float y, out float z, out float yaw)
        {
            x = s.X;
            y = s.Y;
            z = s.Z;
            yaw = s.Yaw;
        }

        static float LerpAngle(float a, float b, float t)
        {
            var delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return a + delta * t;
        }
    }
}
