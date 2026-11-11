using System;

namespace GameMesh.Aoi
{
    /// <summary>
    /// Render-only pose trail for remote players. Does not read vx, vz, or move_start_ms.
    /// Samples are the received MOVE/ENTER positions. Playback is at now - delay.
    /// Past the newest sample it holds, or coasts along the last segment when asked.
    /// </summary>
    public sealed class RemotePoseBuffer
    {
        public const int Depth = 12;

        public struct Sample
        {
            public float X, Y, Z, Yaw, Time;
            public ulong Seq;
        }

        public struct Hit
        {
            public float X, Y, Z, Yaw;
            public bool Underrun;
            public bool Coasting;
            public float CoastSec;
            public float TipTime;
        }

        readonly Sample[] _samples;
        int _start;
        int _count;
        ulong _lastSeq;
        uint _generation;

        public RemotePoseBuffer() : this(Depth)
        {
        }

        public RemotePoseBuffer(int capacity)
        {
            if (capacity < 2)
                capacity = Depth;
            _samples = new Sample[capacity];
        }

        public int Count => _count;
        public int Capacity => _samples.Length;
        public ulong LastSeq => _lastSeq;
        public uint Generation => _generation;

        public Sample At(int logical)
        {
            return _samples[Index(logical)];
        }

        /// <summary>
        /// Same state_seq with a new position is the next sample of this walk.
        /// Only a strictly older seq is dropped. vx, vz, and move_start_ms are not inputs.
        /// </summary>
        public static bool IsAlongPathUpdate(ulong incomingSeq, ulong acceptedSeq)
        {
            return incomingSeq == 0 || acceptedSeq == 0 || incomingSeq >= acceptedSeq;
        }

        public void Clear()
        {
            _count = 0;
            _start = 0;
            _lastSeq = 0;
        }

        public uint BumpGeneration()
        {
            _generation++;
            Clear();
            return _generation;
        }

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
            if (!IsAlongPathUpdate(stateSeq, _lastSeq))
                return false;
            if (stateSeq != 0)
                _lastSeq = stateSeq;
            if (_count >= _samples.Length)
                DropOldest();
            var idx = Index(_count);
            _samples[idx] = new Sample
            {
                X = x, Y = y, Z = z, Yaw = yaw, Time = time, Seq = stateSeq
            };
            _count++;
            return true;
        }

        public void Snap(float x, float y, float z, float yaw, float time, ulong stateSeq)
        {
            _count = 0;
            _start = 0;
            if (stateSeq != 0)
                _lastSeq = stateSeq;
            else
                _lastSeq = 0;
            _samples[Index(0)] = new Sample
            {
                X = x, Y = y, Z = z, Yaw = yaw, Time = time, Seq = stateSeq
            };
            _count = 1;
        }

        public bool TryLastSegment(out float dx, out float dz, out float span)
        {
            dx = dz = span = 0f;
            if (_count < 2)
                return false;
            var from = At(_count - 2);
            var to = At(_count - 1);
            dx = to.X - from.X;
            dz = to.Z - from.Z;
            span = to.Time - from.Time;
            return true;
        }

        public int CopyPath(AoiInterpDebugDraw.Point[] dest)
        {
            if (dest == null)
                return 0;
            var n = _count < dest.Length ? _count : dest.Length;
            for (var i = 0; i < n; i++)
            {
                var s = At(i);
                dest[i] = new AoiInterpDebugDraw.Point { X = s.X, Y = s.Y, Z = s.Z };
            }

            return n;
        }

        public bool TrySample(float now, float delaySec, out float x, out float y, out float z, out float yaw)
        {
            var ok = TrySample(now, delaySec, 0f, 10f, out var hit);
            x = hit.X;
            y = hit.Y;
            z = hit.Z;
            yaw = hit.Yaw;
            return ok;
        }

        public bool TrySample(float now, float delaySec, float coastMaxSec, float moveSpeed, out Hit hit)
        {
            hit = default;
            if (_count <= 0)
                return false;
            if (delaySec < 0f)
                delaySec = 0f;
            var t = now - delaySec;
            Expire(t);
            if (_count <= 0)
                return false;

            var first = At(0);
            if (_count == 1 || t <= first.Time)
            {
                Copy(first, ref hit);
                hit.TipTime = At(_count - 1).Time;
                return true;
            }

            var last = At(_count - 1);
            hit.TipTime = last.Time;
            if (t >= last.Time)
            {
                Copy(last, ref hit);
                ApplyCoast(ref hit, last, t, coastMaxSec, moveSpeed);
                return true;
            }

            for (var i = 0; i < _count - 1; i++)
            {
                var to = At(i + 1);
                if (t > to.Time)
                    continue;
                var from = At(i);
                var span = to.Time - from.Time;
                var u = span <= 0.0001f ? 1f : (t - from.Time) / span;
                if (u < 0f)
                    u = 0f;
                if (u > 1f)
                    u = 1f;
                hit.X = from.X + (to.X - from.X) * u;
                hit.Y = from.Y + (to.Y - from.Y) * u;
                hit.Z = from.Z + (to.Z - from.Z) * u;
                hit.Yaw = LerpAngle(from.Yaw, to.Yaw, u);
                return true;
            }

            Copy(last, ref hit);
            return true;
        }

        void ApplyCoast(ref Hit hit, Sample last, float t, float coastMaxSec, float moveSpeed)
        {
            var ahead = t - last.Time;
            if (ahead <= 0.0001f || _count < 2 || coastMaxSec <= 0f)
            {
                if (ahead > 0.0001f)
                    hit.Underrun = true;
                return;
            }

            var prev = At(_count - 2);
            var stepX = last.X - prev.X;
            var stepZ = last.Z - prev.Z;
            var dist2 = stepX * stepX + stepZ * stepZ;
            if (dist2 <= 0.0001f)
            {
                hit.Underrun = true;
                return;
            }

            var span = last.Time - prev.Time;
            if (span < 0.05f)
                span = 0.05f;
            var dist = (float)Math.Sqrt(dist2);
            var speed = dist / span;
            if (moveSpeed > 0.01f && speed > moveSpeed)
                speed = moveSpeed;
            var coast = ahead < coastMaxSec ? ahead : coastMaxSec;
            var inv = 1f / dist;
            hit.X = last.X + stepX * inv * speed * coast;
            hit.Z = last.Z + stepZ * inv * speed * coast;
            hit.Y = last.Y;
            hit.Yaw = last.Yaw;
            hit.Underrun = true;
            hit.CoastSec = coast;
            hit.Coasting = ahead <= coastMaxSec + 0.0001f;
        }

        void Expire(float tRender)
        {
            var expireAt = tRender - 0.05f;
            while (_count > 1)
            {
                var oldest = At(0);
                var next = At(1);
                if (oldest.Time >= expireAt)
                    break;
                // Keep the left endpoint of the segment that still contains t_render.
                if (next.Time > tRender)
                    break;
                DropOldest();
            }
        }

        void DropOldest()
        {
            if (_count <= 0)
                return;
            _start = (_start + 1) % _samples.Length;
            _count--;
        }

        int Index(int logical)
        {
            return (_start + logical) % _samples.Length;
        }

        static void Copy(Sample s, ref Hit hit)
        {
            hit.X = s.X;
            hit.Y = s.Y;
            hit.Z = s.Z;
            hit.Yaw = s.Yaw;
        }

        static float LerpAngle(float a, float b, float t)
        {
            var delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return a + delta * t;
        }
    }

    /// <summary>
    /// MOVE's server_time_ms is when that displacement was confirmed, so a later body
    /// position can carry an earlier clock. That update still belongs on the path.
    /// </summary>
    public static class AoiSampleClock
    {
        public const float MaxGapSec = 3.2f;

        public static bool IsRewound(long incomingMs, long acceptedMs)
        {
            return incomingMs != 0 && acceptedMs != 0 && incomingMs + 5 < acceptedMs;
        }

        public static float PlaceRewound(float now, float lastTime, float distance, float metersPerSec)
        {
            var travel = metersPerSec > 0.01f ? distance / metersPerSec : 0.05f;
            if (travel < 0.05f)
                travel = 0.05f;
            if (travel > MaxGapSec)
                travel = MaxGapSec;
            var t = lastTime + travel;
            if (t > now)
                t = now;
            if (t <= lastTime)
                t = lastTime + 0.001f;
            return t;
        }
    }
}
