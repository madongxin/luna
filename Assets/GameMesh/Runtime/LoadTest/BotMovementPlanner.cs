using System;
using UnityEngine;

namespace GameMesh.LoadTest
{
    public struct MovePlan
    {
        public bool Send;
        public Vector3 Position;
        public float Yaw;
        public string Reason;
        public int WaypointIndex;
        public bool CrossesCell;
        public bool CrossesInterest;

        public static MovePlan None(string reason, int waypoint)
        {
            return new MovePlan { Send = false, Reason = reason ?? "", WaypointIndex = waypoint };
        }
    }

    /// <summary>
    /// Next intent from the last server-confirmed pose. Does not send.
    /// </summary>
    public sealed class BotMovementPlanner
    {
        readonly BotMovementProfile _profile;
        readonly int _index;
        readonly BotMovementSettings _settings;
        readonly Vector3 _spawn;
        readonly System.Random _rng;
        Vector3 _goal;
        bool _hasGoal;
        int _waypoint;
        int _pauseLeft;
        int _phase;
        bool _rejected;
        bool _returning;
        int _scatterNudge;

        public int WaypointIndex => _waypoint;

        public BotMovementPlanner(BotMovementProfile profile, int botIndex, BotMovementSettings settings,
            Vector3 spawn)
        {
            _profile = profile;
            _index = botIndex;
            _settings = settings ?? new BotMovementSettings();
            _spawn = spawn;
            _rng = new System.Random(BotMovementSettings.StablePercent(settings.Seed, botIndex) + botIndex * 17);
        }

        public void NoteRejected()
        {
            _rejected = true;
            _hasGoal = false;
            _returning = false;
            // Skip the rest of this stage so the next point is not the rejected one.
            _waypoint += 6;
        }

        /// <summary>
        /// Walk from spawn to a random point OutboundMeters away, then walk back.
        /// A finished return picks a new direction. The 24m safe disk does not apply.
        /// </summary>
        public MovePlan NextOutAndBack(Vector3 confirmed, float maxStepMeters = -1f)
        {
            var maxStep = maxStepMeters >= 0f ? maxStepMeters : _settings.MaxStepMeters;
            var leg = _settings.OutboundMeters > 0f ? _settings.OutboundMeters : 50f;
            if (_rejected || !_hasGoal)
            {
                _rejected = false;
                _returning = false;
                _goal = PointOnCircle(_spawn, leg);
                _hasGoal = true;
                _waypoint++;
            }

            var goal = _returning ? _spawn : _goal;
            if (Flat(confirmed, goal) <= 0.35f)
            {
                if (!_returning)
                {
                    _returning = true;
                    goal = _spawn;
                }
                else
                {
                    _returning = false;
                    _goal = PointOnCircle(_spawn, leg);
                    goal = _goal;
                }

                _waypoint++;
            }

            var next = Step(confirmed, goal, maxStep);
            next.y = confirmed.y;
            if (Flat(next, confirmed) < 0.02f)
            {
                _hasGoal = false;
                _returning = false;
                return MovePlan.None("path_rejected", _waypoint);
            }

            var yaw = Mathf.Atan2(next.x - confirmed.x, next.z - confirmed.z) * Mathf.Rad2Deg;
            return new MovePlan
            {
                Send = true,
                Position = next,
                Yaw = yaw,
                Reason = _returning ? "return" : "outbound",
                WaypointIndex = _waypoint,
                CrossesCell = CrossesCell(confirmed, next),
                CrossesInterest = CrossesInterest(confirmed, next)
            };
        }

        /// <summary>
        /// Walk to this bot's own home, then pick random points inside HomeRadiusMeters.
        /// The disk is convex, so a step toward an interior goal stays inside it.
        /// </summary>
        public MovePlan NextAroundHome(Vector3 confirmed, float maxStepMeters = -1f)
        {
            var maxStep = maxStepMeters >= 0f ? maxStepMeters : _settings.MaxStepMeters;
            var radius = _settings.HomeRadiusMeters > 0f ? _settings.HomeRadiusMeters : 100f;
            var outside = Flat(confirmed, _spawn) > radius + 0.5f;
            if (_rejected || !_hasGoal)
            {
                _rejected = false;
                _goal = outside ? ScatterAim(confirmed) : PointInsideDisk(radius);
                _hasGoal = true;
                _waypoint++;
            }

            if (Flat(confirmed, _goal) <= 0.35f)
            {
                _goal = PointInsideDisk(radius);
                _waypoint++;
            }

            var next = Step(confirmed, _goal, maxStep);
            next.y = confirmed.y;
            if (!outside)
                next = ClampDisk(next, _spawn, radius);
            if (Flat(next, confirmed) < 0.02f)
            {
                _hasGoal = false;
                return MovePlan.None("path_rejected", _waypoint);
            }

            var yaw = Mathf.Atan2(next.x - confirmed.x, next.z - confirmed.z) * Mathf.Rad2Deg;
            return new MovePlan
            {
                Send = true,
                Position = next,
                Yaw = yaw,
                Reason = outside ? "scatter" : "wander",
                WaypointIndex = _waypoint,
                CrossesCell = CrossesCell(confirmed, next),
                CrossesInterest = CrossesInterest(confirmed, next)
            };
        }

        Vector3 ScatterAim(Vector3 from)
        {
            var dx = _spawn.x - from.x;
            var dz = _spawn.z - from.z;
            var len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 0.01f)
                return _spawn;
            var ahead = Mathf.Min(80f, len);
            var side = _scatterNudge * 16f;
            _scatterNudge = _scatterNudge > 0 ? -_scatterNudge : -_scatterNudge + 1;
            if (_scatterNudge > 6)
                _scatterNudge = 0;
            var px = -dz / len;
            var pz = dx / len;
            return new Vector3(
                from.x + dx / len * ahead + px * side,
                from.y,
                from.z + dz / len * ahead + pz * side);
        }

        Vector3 PointInsideDisk(float radius)
        {
            var angle = (float)(_rng.NextDouble() * Mathf.PI * 2.0);
            var dist = Mathf.Sqrt((float)_rng.NextDouble()) * radius;
            if (radius > 8f && dist < 8f)
                dist = 8f;
            return new Vector3(
                _spawn.x + Mathf.Cos(angle) * dist,
                _spawn.y,
                _spawn.z + Mathf.Sin(angle) * dist);
        }

        Vector3 PointOnCircle(Vector3 center, float radius)
        {
            var angle = (float)(_rng.NextDouble() * Mathf.PI * 2.0);
            return new Vector3(
                center.x + Mathf.Cos(angle) * radius,
                center.y,
                center.z + Mathf.Sin(angle) * radius);
        }

        public MovePlan Next(Vector3 confirmed, float maxStepMeters = -1f)
        {
            var maxStep = maxStepMeters >= 0f ? maxStepMeters : _settings.MaxStepMeters;
            if (_profile == BotMovementProfile.Idle)
                return MovePlan.None("idle", _waypoint);
            if (_pauseLeft > 0)
            {
                _pauseLeft--;
                return MovePlan.None("pause", _waypoint);
            }

            if (_rejected)
            {
                _rejected = false;
                _waypoint++;
            }

            if (!_hasGoal || Flat(confirmed, _goal) <= 0.8f)
            {
                _goal = PickGoal();
                _hasGoal = true;
                _waypoint++;
                if (_profile == BotMovementProfile.LocalWander)
                {
                    var pauseTicks = Mathf.CeilToInt(_settings.PauseMaxSec * _settings.TargetHz);
                    if (pauseTicks < 0)
                        pauseTicks = 0;
                    _pauseLeft = _rng.Next(0, pauseTicks + 1);
                }
                if (Flat(confirmed, _goal) <= 0.05f)
                    return MovePlan.None("path_rejected", _waypoint);
            }

            var next = Step(confirmed, _goal, maxStep);
            next = ClampDisk(next, _spawn, _settings.SafeRadius);
            next.y = confirmed.y;
            if (Flat(next, confirmed) < 0.02f)
            {
                _hasGoal = false;
                return MovePlan.None("path_rejected", _waypoint);
            }

            var yaw = Mathf.Atan2(next.x - confirmed.x, next.z - confirmed.z) * Mathf.Rad2Deg;
            var cell = CrossesCell(confirmed, next);
            var interest = CrossesInterest(confirmed, next);
            return new MovePlan
            {
                Send = true,
                Position = next,
                Yaw = yaw,
                Reason = "",
                WaypointIndex = _waypoint,
                CrossesCell = cell,
                CrossesInterest = interest
            };
        }

        Vector3 PickGoal()
        {
            switch (_profile)
            {
                case BotMovementProfile.AoiBoundaryShuttle:
                    return ShuttleGoal();
                case BotMovementProfile.HotspotGather:
                    return HotspotGoal();
                case BotMovementProfile.BidirectionalPatrol:
                    return PatrolGoal();
                default:
                    return WanderGoal();
            }
        }

        Vector3 WanderGoal()
        {
            var min = _settings.WanderMin;
            var max = _settings.WanderMax;
            if (max > _settings.SafeRadius - 1f)
                max = _settings.SafeRadius - 1f;
            if (min > max)
                min = max;
            var radius = min + (float)_rng.NextDouble() * (max - min);
            var angle = (float)(_rng.NextDouble() * Mathf.PI * 2.0);
            return new Vector3(
                _spawn.x + Mathf.Cos(angle) * radius,
                _spawn.y,
                _spawn.z + Mathf.Sin(angle) * radius);
        }

        Vector3 ShuttleGoal()
        {
            // Cell edge near the spawn. Both ends stay inside the safe disk.
            var edge = Mathf.Floor(_spawn.x / _settings.CellSize) * _settings.CellSize;
            var left = edge - 6f;
            var right = edge + 6f;
            _phase ^= 1;
            var x = _phase == 0 ? left : right;
            return ClampDisk(new Vector3(x, _spawn.y, _spawn.z), _spawn, _settings.SafeRadius - 1f);
        }

        Vector3 HotspotGoal()
        {
            var slot = _index % 20;
            var angle = slot * 0.314f;
            var hotspot = new Vector3(
                _spawn.x + Mathf.Cos(angle) * 2f,
                _spawn.y,
                _spawn.z + Mathf.Sin(angle) * 2f);
            var stage = (_waypoint / 8) % 3;
            if (stage == 0)
                return hotspot;
            if (stage == 1)
            {
                var orbit = angle + _waypoint * 0.4f;
                return new Vector3(
                    hotspot.x + Mathf.Cos(orbit) * 3f,
                    _spawn.y,
                    hotspot.z + Mathf.Sin(orbit) * 3f);
            }

            var homeAngle = BotMovementSettings.StablePercent(_settings.Seed, _index) * 0.0628f;
            return new Vector3(
                _spawn.x + Mathf.Cos(homeAngle) * 16f,
                _spawn.y,
                _spawn.z + Mathf.Sin(homeAngle) * 16f);
        }

        Vector3 PatrolGoal()
        {
            var group = _index % 2;
            var stagger = _settings.SyncWave ? 0 : (_index % 5);
            var flip = ((_waypoint + stagger) / 6) % 2;
            if (group == 1)
                flip ^= 1;
            var x = flip == 0 ? _spawn.x - 12f : _spawn.x + 12f;
            return ClampDisk(new Vector3(x, _spawn.y, _spawn.z + group * 2f), _spawn,
                _settings.SafeRadius - 1f);
        }

        bool CrossesCell(Vector3 a, Vector3 b)
        {
            var size = _settings.CellSize;
            var ca = Mathf.Floor(a.x / size);
            var cb = Mathf.Floor(b.x / size);
            return ca != cb;
        }

        bool CrossesInterest(Vector3 a, Vector3 b)
        {
            var ra = Flat(a, _spawn);
            var rb = Flat(b, _spawn);
            var limit = _settings.AoiRadius;
            return (ra <= limit && rb > limit) || (rb <= limit && ra > limit);
        }

        static Vector3 Step(Vector3 from, Vector3 to, float maxStep)
        {
            var delta = to - from;
            delta.y = 0f;
            var dist = delta.magnitude;
            if (dist <= maxStep || dist <= 0.0001f)
                return new Vector3(to.x, from.y, to.z);
            delta /= dist;
            return from + delta * maxStep;
        }

        static Vector3 ClampDisk(Vector3 pos, Vector3 center, float radius)
        {
            var dx = pos.x - center.x;
            var dz = pos.z - center.z;
            var dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist <= radius || dist <= 0.0001f)
                return pos;
            var scale = radius / dist;
            return new Vector3(center.x + dx * scale, pos.y, center.z + dz * scale);
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>
    /// Sparse waypoint patrol: one MOVE at depart (segment goal), optional arrive MOVE
    /// only when the server shortened the first packet. No mid-segment MOVE. No velocity
    /// extrapolation — vx/vz/move_start_ms are not in the protocol yet.
    /// </summary>
    public sealed class SparsePatrolController
    {
        public const float TurnDegrees = 15f;
        public const float ArriveEpsilon = 0.75f;

        enum Phase
        {
            NeedDepart = 0,
            Traveling = 1,
            NeedArrive = 2,
            Dwelling = 3,
            StartWait = 4
        }

        readonly BotMovementSettings _settings;
        readonly System.Random _rng;
        readonly int _botIndex;
        Phase _phase = Phase.NeedDepart;
        long _segmentEndMs;
        float _segmentSec;
        Vector3 _goal;
        bool _hasGoal;
        bool _firstSegment = true;
        bool _rejected;
        float _lastSentYaw;
        bool _hasLastYaw;
        long _lastAccountMs = -1;
        int _spin;
        Vector3 _home;
        float _keepRadius;
        bool _keepInside;
        float _lastSegmentSec;
        float _lastSegmentMeters;
        long _dwellUntilMs;
        long _startReadyMs;
        bool _departAcked;
        bool _arriveSentThisSegment;
        long _departSentAtMs;
        public long StartReadyMs => _startReadyMs;
        public bool DepartAcked => _departAcked;
        public bool ArriveSentThisSegment => _arriveSentThisSegment;

        public float LastSegmentSec => _lastSegmentSec;
        public float LastSegmentMeters => _lastSegmentMeters;

        public SparsePatrolController(int botIndex, BotMovementSettings settings)
        {
            _botIndex = botIndex;
            _settings = settings ?? new BotMovementSettings();
            _rng = new System.Random(
                BotMovementSettings.StablePercent(_settings.Seed, botIndex) + botIndex * 31);
        }

        public void Arm(long nowMs)
        {
            _hasGoal = false;
            _lastAccountMs = nowMs;
            _dwellUntilMs = 0;
            // sparse_duty_pct&lt;100 and SyncWave off: stagger first depart across one segment length.
            // Duty 100 keeps the previous immediate start (default behavior).
            if (!_settings.SyncWave && _settings.SparseDutyPct < 100)
            {
                var maxWaitMs = (long)(Mathf.Max(1f, _settings.SegmentMaxSec) * 1000f);
                var wait = (long)(_rng.NextDouble() * maxWaitMs);
                _startReadyMs = nowMs + wait;
                _phase = wait > 0 ? Phase.StartWait : Phase.NeedDepart;
            }
            else
            {
                _startReadyMs = nowMs;
                _phase = Phase.NeedDepart;
            }
        }

        /// <summary>
        /// Keep every segment inside the AOI disk around the EnterMap spawn.
        /// The observer stands on that same spawn; a 100–300m leg leaves the 32m window.
        /// </summary>
        public void KeepInside(Vector3 home, float radius)
        {
            _home = home;
            _keepRadius = radius;
            _keepInside = radius > 1f;
        }

        public void NoteRejected()
        {
            _rejected = true;
            _hasGoal = false;
            _phase = Phase.NeedDepart;
        }

        /// <summary>
        /// Decide the next sparse action. WaitMs is how long the move loop may sleep.
        /// </summary>
        public MovePlan Poll(Vector3 confirmed, long nowMs, BotMovementMetrics metrics, out int waitMs)
        {
            AccountTime(nowMs, metrics);
            waitMs = 50;
            if (_phase == Phase.StartWait)
            {
                if (nowMs < _startReadyMs)
                {
                    waitMs = ClampWait(_startReadyMs - nowMs);
                    return MovePlan.None("start_wait", 0);
                }

                _phase = Phase.NeedDepart;
            }

            if (_phase == Phase.Dwelling)
            {
                if (nowMs < _dwellUntilMs)
                {
                    waitMs = ClampWait(_dwellUntilMs - nowMs);
                    return MovePlan.None("dwell", 0);
                }

                _phase = Phase.NeedDepart;
            }

            if (_phase == Phase.Traveling)
            {
                // The server can reach the goal before this segment's clock ends. Standing
                // there until the clock expires looks like the bot stopped.
                if (Flat(confirmed, _goal) <= ArriveEpsilon)
                    CompleteSegment(nowMs, metrics);
                else if (nowMs < _segmentEndMs)
                {
                    waitMs = ClampWait(_segmentEndMs - nowMs);
                    return MovePlan.None("travel", 0);
                }
                else
                    _phase = Phase.NeedArrive;
            }

            if (_phase == Phase.NeedArrive)
            {
                if (Flat(confirmed, _goal) <= ArriveEpsilon)
                    CompleteSegment(nowMs, metrics);
                else if (_settings.SparseArriveWait && _arriveSentThisSegment)
                    CompleteSegment(nowMs, metrics);
                else if (_settings.SparseArriveWait && !MaySendArrive(nowMs, metrics, out waitMs))
                    return MovePlan.None("arrive_wait", 0);
                else
                    return BuildSend(confirmed, "arrive", metrics, out waitMs);
            }

            if (_phase == Phase.Dwelling)
            {
                waitMs = ClampWait(_dwellUntilMs - nowMs);
                return MovePlan.None("dwell", 0);
            }

            // NeedDepart. With SparseDutyPct=100, completion falls through to the next depart
            // in the same poll (existing continuous behavior).
            if (_rejected || !_hasGoal)
            {
                _rejected = false;
                PickSegment(confirmed);
            }

            return BuildSend(confirmed, "depart", metrics, out waitMs);
        }

        public void NoteSent(float yaw, long nowMs, string reason, BotMovementMetrics metrics)
        {
            if (_hasLastYaw && Mathf.Abs(Mathf.DeltaAngle(_lastSentYaw, yaw)) > TurnDegrees &&
                metrics != null)
                metrics.Turns++;
            _lastSentYaw = yaw;
            _hasLastYaw = true;
            if (metrics != null)
                metrics.NoteSendReason(reason);
            if (reason == "depart")
            {
                _phase = Phase.Traveling;
                _segmentEndMs = nowMs + (long)(_segmentSec * 1000f);
                _departSentAtMs = nowMs;
                _departAcked = false;
                _arriveSentThisSegment = false;
                if (metrics != null)
                    metrics.NoteSegment(_lastSegmentSec, _lastSegmentMeters);
            }
            else if (reason == "arrive")
            {
                _arriveSentThisSegment = true;
                CompleteSegment(nowMs, metrics);
            }
        }

        public void NoteAuthorityOk()
        {
            _departAcked = true;
        }

        public static int ArriveWaitMs(int rttP95Ms)
        {
            var triple = rttP95Ms > 0 ? rttP95Ms * 3 : 0;
            return Math.Max(3000, triple);
        }

        bool MaySendArrive(long nowMs, BotMovementMetrics metrics, out int waitMs)
        {
            waitMs = 50;
            if (_departAcked)
                return true;
            var p95 = metrics != null ? metrics.LatencyPercentile(95) : 0;
            var need = ArriveWaitMs(p95);
            var elapsed = nowMs - _departSentAtMs;
            if (elapsed >= need)
                return true;
            waitMs = ClampWait(need - elapsed);
            return false;
        }

        void CompleteSegment(long nowMs, BotMovementMetrics metrics)
        {
            // Flush travel time before dwell or the next depart.
            AccountTime(nowMs, metrics);
            _hasGoal = false;
            var duty = _settings.SparseDutyPct;
            if (duty < 100 && _lastSegmentSec > 0.01f)
            {
                var dwell = ComputeDwellSec(_lastSegmentSec, duty,
                    _settings.SparseDwellMinSec, _settings.SparseDwellMaxSec);
                if (_settings.AoiBotBackoff)
                {
                    var scale = BotCongestionGate.SegmentScale(nowMs);
                    if (scale > 1f)
                        dwell *= scale;
                }

                if (dwell > 0.01f)
                {
                    _dwellUntilMs = nowMs + (long)(dwell * 1000f);
                    _phase = Phase.Dwelling;
                    return;
                }
            }

            _phase = Phase.NeedDepart;
        }

        /// <summary>
        /// Choose dwell so moving/(moving+dwell) ≈ dutyPct/100, clamped to [min,max].
        /// </summary>
        public static float ComputeDwellSec(float movingSec, int dutyPct, float minSec, float maxSec)
        {
            if (dutyPct >= 100 || movingSec <= 0f)
                return 0f;
            if (dutyPct < 1)
                dutyPct = 1;
            var dwell = movingSec * (100 - dutyPct) / (float)dutyPct;
            if (dwell < minSec)
                dwell = minSec;
            if (dwell > maxSec)
                dwell = maxSec;
            return dwell;
        }

        /// <summary>Drop the current goal so the next Poll picks a new waypoint.</summary>
        public void AbandonTarget()
        {
            NoteRejected();
        }

        void PickSegment(Vector3 from)
        {
            var scale = 1f;
            if (_settings.AoiBotBackoff)
                scale = BotCongestionGate.SegmentScale(
                    System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (scale < 1f)
                scale = 1f;
            var min = _settings.SegmentMinSec * scale;
            var max = _settings.SegmentMaxSec * scale;
            if (max < min)
                max = min;
            // The server advances an accepted route at ServerMoveSpeed. Using WalkSpeed for
            // the distance but waiting the full segment left the body parked for roughly 45%
            // of every leg (5.5 / 10). Cover the whole interval at the authority speed.
            // The first request only owns SegmentMinSec of server-side time budget.
            var requestedSec = _firstSegment
                ? min
                : min + (float)_rng.NextDouble() * (max - min);
            _firstSegment = false;
            var serverSpeed = _settings.ServerMoveSpeed > 0.1f
                ? _settings.ServerMoveSpeed
                : 10f;
            var dist = serverSpeed * requestedSec;
            if (dist < 2f)
                dist = 2f;
            var angle = (float)(_rng.NextDouble() * Mathf.PI * 2.0) + _spin * 0.7f;
            _goal = new Vector3(
                from.x + Mathf.Cos(angle) * dist,
                from.y,
                from.z + Mathf.Sin(angle) * dist);
            ClampToHome();
            _goal = BotHomeLayout.SnapNearestWalkable(_goal, from.y);
            ClampToHome();
            if (Flat(from, _goal) < 2f)
            {
                _spin++;
                angle += 1.2f * _spin;
                _goal = new Vector3(
                    from.x + Mathf.Cos(angle) * dist,
                    from.y,
                    from.z + Mathf.Sin(angle) * dist);
                ClampToHome();
                _goal = BotHomeLayout.SnapNearestWalkable(_goal, from.y);
                ClampToHome();
            }
            // Grid snapping changes the actual leg length. Schedule the next turn from that
            // final distance so it cannot create another hidden dwell at the endpoint.
            // KeepInside often shrinks the leg below SegmentMinSec×speed; _segmentSec follows
            // the clamped distance, not the configured 10–30s request.
            _lastSegmentMeters = Flat(from, _goal);
            _segmentSec = _lastSegmentMeters / serverSpeed;
            if (_segmentSec < 0.05f)
                _segmentSec = 0.05f;
            _lastSegmentSec = _segmentSec;
            _hasGoal = true;
        }

        void ClampToHome()
        {
            if (!_keepInside)
                return;
            var limit = _keepRadius - 1f;
            if (limit < 1f)
                limit = 1f;
            var dx = _goal.x - _home.x;
            var dz = _goal.z - _home.z;
            var dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist <= limit || dist < 0.001f)
                return;
            var scale = limit / dist;
            _goal.x = _home.x + dx * scale;
            _goal.z = _home.z + dz * scale;
        }

        MovePlan BuildSend(Vector3 confirmed, string reason, BotMovementMetrics metrics, out int waitMs)
        {
            waitMs = 50;
            if (metrics != null)
                metrics.Planned++;
            var yaw = Mathf.Atan2(_goal.x - confirmed.x, _goal.z - confirmed.z) * Mathf.Rad2Deg;
            if (Flat(confirmed, _goal) < 0.05f)
            {
                _hasGoal = false;
                _phase = Phase.NeedDepart;
                return MovePlan.None("path_rejected", 0);
            }

            return new MovePlan
            {
                Send = true,
                Position = new Vector3(_goal.x, confirmed.y, _goal.z),
                Yaw = yaw,
                Reason = reason,
                WaypointIndex = 0,
                CrossesCell = false,
                CrossesInterest = false
            };
        }

        void AccountTime(long nowMs, BotMovementMetrics metrics)
        {
            if (metrics == null)
                return;
            if (_lastAccountMs < 0)
            {
                _lastAccountMs = nowMs;
                return;
            }

            var dt = nowMs - _lastAccountMs;
            if (dt <= 0)
                return;
            _lastAccountMs = nowMs;
            if (_phase == Phase.Traveling || _phase == Phase.NeedArrive)
                metrics.MovingMs += dt;
            else if (_phase == Phase.Dwelling || _phase == Phase.StartWait)
                metrics.DwellingMs += dt;
            metrics.SampleEndedMs = nowMs;
        }

        static int ClampWait(long ms)
        {
            if (ms < 20)
                return 20;
            if (ms > 1000)
                return 1000;
            return (int)ms;
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
