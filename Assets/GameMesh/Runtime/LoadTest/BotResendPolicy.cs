using System;
using UnityEngine;

namespace GameMesh.LoadTest
{
    /// <summary>
    /// Same-target MOVE resend after the 2s authority miss.
    /// Backoff off (default): latch arms → next poll resends immediately (E1 evidence path).
    /// Backoff on (aoi_bot_backoff): first resend immediate, then 2/4/8…s up to CapSec;
    /// after MaxSameTargetResends, abandon the waypoint.
    /// </summary>
    public sealed class BotResendPolicy
    {
        public const int DefaultMaxSameTargetResends = 3;
        public const int DefaultBackoffCapSec = 30;

        Vector3 _target;
        bool _armed;
        int _sameTargetResends;
        long _nextResendAtMs;
        int _nextWaitSec = 2;

        public bool BackoffEnabled { get; set; }
        public int MaxSameTargetResends { get; set; } = DefaultMaxSameTargetResends;
        public int BackoffCapSec { get; set; } = DefaultBackoffCapSec;
        public int ResendCount { get; private set; }
        public int AbandonCount { get; private set; }
        public bool Armed => _armed;
        public int SameTargetResends => _sameTargetResends;

        public void Arm(Vector3 target, long nowMs)
        {
            _target = target;
            _armed = true;
            if (!BackoffEnabled || _sameTargetResends == 0)
                _nextResendAtMs = nowMs;
            else
                _nextResendAtMs = nowMs + _nextWaitSec * 1000L;
        }

        public void Clear()
        {
            _armed = false;
            _sameTargetResends = 0;
            _nextResendAtMs = 0;
            _nextWaitSec = 2;
        }

        /// <summary>
        /// When armed and still short of the target, take one resend.
        /// Returns false if waiting on backoff, not armed, or should abandon instead.
        /// </summary>
        public bool TryTakeResend(long nowMs, float confirmedX, float confirmedZ,
            out Vector3 target, out bool abandon)
        {
            target = default;
            abandon = false;
            if (!_armed)
                return false;
            if (!MoveRecovery.ShouldResendSamePoint(confirmedX, confirmedZ, _target.x, _target.z, true))
            {
                Clear();
                return false;
            }

            if (BackoffEnabled &&
                MaxSameTargetResends > 0 &&
                _sameTargetResends >= MaxSameTargetResends)
            {
                abandon = true;
                AbandonCount++;
                Clear();
                return false;
            }

            if (BackoffEnabled && nowMs < _nextResendAtMs)
                return false;

            target = _target;
            _armed = false;
            _sameTargetResends++;
            ResendCount++;
            // After 1st resend wait 2s; then 4, 8, … up to CapSec.
            if (BackoffEnabled && _sameTargetResends >= 2)
                _nextWaitSec = NextBackoffSec(_nextWaitSec, BackoffCapSec);

            return true;
        }

        /// <summary>Backoff wait remaining; 0 when ready or disabled.</summary>
        public int WaitMsUntilResend(long nowMs)
        {
            if (!_armed || !BackoffEnabled)
                return 0;
            var left = _nextResendAtMs - nowMs;
            if (left <= 0)
                return 0;
            if (left > int.MaxValue)
                return int.MaxValue;
            return (int)left;
        }

        public static int NextBackoffSec(int previousWaitSec, int capSec)
        {
            if (previousWaitSec < 2)
                previousWaitSec = 2;
            if (capSec < 2)
                capSec = 2;
            var next = previousWaitSec * 2;
            return next > capSec ? capSec : next;
        }
    }

    /// <summary>
    /// Shared 10s timeout ratio for load-test bots. When aoi_bot_backoff is on and the
    /// recent timeout share exceeds the threshold, segment durations scale up (max 4x).
    /// </summary>
    public static class BotCongestionGate
    {
        public const float DefaultTimeoutRatio = 0.30f;
        public const float DefaultScaleMax = 4f;
        public const int WindowMs = 10000;

        static readonly object Gate = new object();
        static long _windowStartMs;
        static int _sends;
        static int _timeouts;
        static float _scale = 1f;
        static bool _enabled;

        public static bool Enabled
        {
            get { lock (Gate) return _enabled; }
            set { lock (Gate) { _enabled = value; if (!value) ResetUnlocked(); } }
        }

        public static float TimeoutRatioThreshold { get; set; } = DefaultTimeoutRatio;
        public static float ScaleMax { get; set; } = DefaultScaleMax;
        public static float CurrentScale { get { lock (Gate) return _scale; } }
        public static int WindowSends { get { lock (Gate) return _sends; } }
        public static int WindowTimeouts { get { lock (Gate) return _timeouts; } }

        public static void Reset()
        {
            lock (Gate) ResetUnlocked();
        }

        static void ResetUnlocked()
        {
            _windowStartMs = 0;
            _sends = 0;
            _timeouts = 0;
            _scale = 1f;
        }

        public static void NoteSend(long nowMs)
        {
            lock (Gate)
            {
                if (!_enabled)
                    return;
                RollUnlocked(nowMs);
                _sends++;
            }
        }

        public static void NoteTimeout(long nowMs)
        {
            lock (Gate)
            {
                if (!_enabled)
                    return;
                RollUnlocked(nowMs);
                _timeouts++;
                UpdateScaleUnlocked();
            }
        }

        public static void NoteSuccess(long nowMs)
        {
            lock (Gate)
            {
                if (!_enabled)
                    return;
                RollUnlocked(nowMs);
                UpdateScaleUnlocked();
            }
        }

        public static float SegmentScale(long nowMs)
        {
            lock (Gate)
            {
                if (!_enabled)
                    return 1f;
                RollUnlocked(nowMs);
                UpdateScaleUnlocked();
                return _scale;
            }
        }

        static void RollUnlocked(long nowMs)
        {
            if (_windowStartMs == 0)
            {
                _windowStartMs = nowMs;
                return;
            }

            if (nowMs - _windowStartMs < WindowMs)
                return;
            _windowStartMs = nowMs;
            _sends = 0;
            _timeouts = 0;
        }

        static void UpdateScaleUnlocked()
        {
            if (_sends < 8)
                return;
            var ratio = _timeouts / (float)_sends;
            var half = TimeoutRatioThreshold * 0.5f;
            if (ratio >= TimeoutRatioThreshold)
            {
                var next = _scale * 2f;
                if (next > ScaleMax)
                    next = ScaleMax;
                _scale = next;
            }
            else if (ratio < half && _scale > 1f)
            {
                _scale = 1f;
            }
        }
    }
}
