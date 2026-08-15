using System;
using UnityEngine;

namespace GameMesh.Map
{
    public sealed class MoveSampler
    {
        public float SendHz = 10f;
        public float PositionThreshold = 0.05f;
        public float YawThreshold = 2f;
        public int MaxInFlight = 3;

        Vector3 _lastSent;
        float _lastYaw;
        float _lastSendTime = -999f;
        public int InFlight;

        public bool ShouldSend(Vector3 pos, float yaw, float now, out string reject)
        {
            reject = null;
            if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) ||
                float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z) ||
                float.IsNaN(yaw) || float.IsInfinity(yaw))
            {
                reject = "NaN/Inf";
                return false;
            }

            if (InFlight >= MaxInFlight)
                return false;
            if (now - _lastSendTime < 1f / Math.Max(0.1f, SendHz))
                return false;
            var moved = Vector3.Distance(pos, _lastSent) >= PositionThreshold;
            var rotated = Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw)) >= YawThreshold;
            if (!moved && !rotated && _lastSendTime > 0f)
                return false;
            return true;
        }

        public void MarkSent(Vector3 pos, float yaw, float now)
        {
            _lastSent = pos;
            _lastYaw = yaw;
            _lastSendTime = now;
            InFlight++;
        }

        public void MarkCompleted()
        {
            if (InFlight > 0)
                InFlight--;
        }

        public void Reset()
        {
            _lastSent = Vector3.zero;
            _lastYaw = 0f;
            _lastSendTime = -999f;
            InFlight = 0;
        }
    }

    public sealed class MoveCorrector
    {
        public float SmoothError = 0.35f;
        public float SnapError = 2.5f;
        public float SuppressSeconds = 0.25f;

        float _suppressUntil;
        public bool SuppressSend => Time.unscaledTime < _suppressUntil;

        public Vector3 Apply(Vector3 local, Vector3 authority, float now, out bool snapped)
        {
            snapped = false;
            var err = Vector3.Distance(local, authority);
            if (err < 0.01f)
                return local;
            if (err >= SnapError)
            {
                snapped = true;
                _suppressUntil = now + SuppressSeconds;
                return authority;
            }

            if (err >= SmoothError)
            {
                _suppressUntil = now + SuppressSeconds * 0.5f;
                return Vector3.Lerp(local, authority, 0.35f);
            }

            return local;
        }

        public bool ShouldSuppress(float now) => now < _suppressUntil;
    }
}
