using System;

namespace GameMesh.Aoi
{
    /// <summary>
    /// Render-only pose trail for remote players. No vx/vz/move_start_ms extrapolation.
    /// Between two AOI samples, blend along the last displacement; past the tip, hold still
    /// until the next MOVE arrives and replaces the target.
    /// </summary>
    public sealed class RemotePoseBuffer
    {
        public const int Depth = 2;

        struct Sample
        {
            public float X, Y, Z, Yaw, Time;
            public ulong Seq;
        }

        readonly Sample[] _samples = new Sample[Depth];
        int _count;
        ulong _lastSeq;

        public int Count => _count;

        /// <summary>
        /// A busy load-test frame can last seconds. Never spend that whole wall-clock gap in
        /// one rendered movement step, or a remote visibly teleports on the next frame.
        /// </summary>
        public static float ClampRenderDelta(float deltaSec)
        {
            if (deltaSec < 0.001f)
                return 0.001f;
            return deltaSec > 0.05f ? 0.05f : deltaSec;
        }

        public bool Push(float x, float y, float z, float yaw, float time, ulong stateSeq)
        {
            // Strictly older seq only. Equal seq with a new position is an along-path update.
            if (stateSeq != 0 && _lastSeq != 0 && stateSeq < _lastSeq)
                return false;
            if (stateSeq != 0)
                _lastSeq = stateSeq;

            if (_count > 0)
            {
                var tip = _samples[_count - 1];
                // Missing state sequences only mean intermediate body samples were coalesced
                // or lost. Keep the last visible pose as the interpolation origin; snapping
                // here made the next accepted position look like a teleport.
                _samples[0] = tip;
                _count = 1;
            }

            if (_count >= Depth)
            {
                _samples[0] = _samples[_count - 1];
                _count = 1;
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
            if (stateSeq != 0)
                _lastSeq = stateSeq;
            else
                _lastSeq = 0;
            _samples[_count++] = new Sample
            {
                X = x, Y = y, Z = z, Yaw = yaw, Time = time, Seq = stateSeq
            };
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
                // Hold the latest AOI body position. Do not open velocity extrapolation.
                Copy(_samples[last], out x, out y, out z, out yaw);
                return true;
            }

            var from = _samples[0];
            var to = _samples[last];
            var blendSpan = to.Time - from.Time;
            // The server samples a body that is already walking at constant speed.
            // Smoothstep forced the rendered velocity to zero at every AOI sample, so sparse
            // three-second updates looked like stop/start motion. Linear playback preserves
            // the along-path speed and changes direction only when a new sample says so.
            var uBlend = blendSpan <= 0.0001f ? 1f : (t - from.Time) / blendSpan;
            x = from.X + (to.X - from.X) * uBlend;
            y = from.Y + (to.Y - from.Y) * uBlend;
            z = from.Z + (to.Z - from.Z) * uBlend;
            yaw = LerpAngle(from.Yaw, to.Yaw, uBlend);
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
