using System;
using UnityEngine;

namespace GameMesh.Map
{
    public sealed class MoveSampler
    {
        public float SendHz = 10f;
        public float FastSendHz = 20f;
        public float FastSpeed = 6f;
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
            var hasBaseline = _lastSendTime > 0f;
            var dt = hasBaseline ? now - _lastSendTime : float.MaxValue;
            var dist = hasBaseline ? Vector3.Distance(pos, _lastSent) : float.MaxValue;
            var speed = hasBaseline && dt > 0.0001f ? dist / dt : 0f;
            var hz = speed >= FastSpeed ? Math.Max(SendHz, FastSendHz) : SendHz;
            if (dt < 1f / Math.Max(0.1f, hz))
                return false;
            var moved = dist >= PositionThreshold;
            var rotated = Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw)) >= YawThreshold;
            if (hasBaseline && !moved && !rotated)
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

        public void SuppressFor(float seconds)
        {
            _suppressUntil = Time.unscaledTime + Mathf.Max(0f, seconds);
        }

        public bool ShouldSuppress(float now) => now < _suppressUntil;
    }
}
