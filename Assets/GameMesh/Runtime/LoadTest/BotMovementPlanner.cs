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
}
