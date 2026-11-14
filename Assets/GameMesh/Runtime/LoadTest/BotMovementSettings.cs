namespace GameMesh.LoadTest
{
    public enum BotMovementProfile
    {
        Idle = 0,
        LocalWander = 1,
        AoiBoundaryShuttle = 2,
        HotspotGather = 3,
        BidirectionalPatrol = 4
    }

    /// <summary>
    /// Load-test movement knobs. Weights are percents and must sum to 100.
    /// An empty SingleProfile means the mixed weights. This is not a YAML file.
    /// </summary>
    public sealed class BotMovementSettings
    {
        public int Seed = 20261021;
        public int TargetHz = 5;
        public int JitterPct = 10;
        public int MaxInflight = 1;
        public string SingleProfile = "";
        public int IdlePct = 10;
        public int WanderPct = 35;
        public int ShuttlePct = 20;
        public int HotspotPct = 20;
        public int PatrolPct = 15;
        public float WalkSpeed = 5.5f;
        public float ServerMoveSpeed = 10f;
        // Sparse KeepInside disk around EnterMap spawn. Was 32→64; enlarge again for longer legs.
        public float AoiRadius = 96f;
        public float CellSize = 32f;
        // Spawn neighborhood that the live 1002 grid treats as walkable.
        // Do not wander out to the map AABB.
        public float SafeRadius = 72f;
        public float OutboundMeters = 50f;
        public float HomeRadiusMeters = 100f;
        public int BotCount;
        public float WanderMin = 10f;
        public float WanderMax = 24f;
        public float PauseMaxSec = 2f;
        public int AoiVisibleCapExpected = 200;
        public bool SyncWave;

        // Sparse waypoint patrol (default on). Mid-segment MOVE is omitted.
        public bool SparsePatrol = true;
        public float SegmentMinSec = 10f;
        public float SegmentMaxSec = 30f;
        // Legacy weight field for dense profiles. Sparse uses SparseDutyPct instead.
        public int MoveDutyPct = 100;
        /// <summary>sparse_duty_pct. 100 = continuous (existing). &lt;100 adds dwell after each leg.</summary>
        public int SparseDutyPct = 100;
        public float SparseDwellMinSec = 2f;
        public float SparseDwellMaxSec = 20f;
        /// <summary>sparse_arrive_wait. Default off = existing arrive-on-clock behavior.</summary>
        public bool SparseArriveWait;
        // 0 = legacy BotHomeLayout.Pick. Otherwise pack bots into clusters of this size.
        public int ClusterSize = 60;
        public float ClusterSpacingMeters = 96f;
        public float ClusterRadiusMeters = 12f;

        /// <summary>aoi_bot_backoff. Default off. Only load-test bots.</summary>
        public bool AoiBotBackoff;
        public int AoiBotBackoffMaxSameTargetResends = BotResendPolicy.DefaultMaxSameTargetResends;
        public int AoiBotBackoffCapSec = BotResendPolicy.DefaultBackoffCapSec;
        public float AoiBotBackoffTimeoutRatio = BotCongestionGate.DefaultTimeoutRatio;

        public BotMovementSettings Clone()
        {
            return new BotMovementSettings
            {
                Seed = Seed,
                TargetHz = TargetHz,
                JitterPct = JitterPct,
                MaxInflight = MaxInflight,
                SingleProfile = SingleProfile ?? "",
                IdlePct = IdlePct,
                WanderPct = WanderPct,
                ShuttlePct = ShuttlePct,
                HotspotPct = HotspotPct,
                PatrolPct = PatrolPct,
                WalkSpeed = WalkSpeed,
                ServerMoveSpeed = ServerMoveSpeed,
                AoiRadius = AoiRadius,
                CellSize = CellSize,
                SafeRadius = SafeRadius,
                OutboundMeters = OutboundMeters,
                HomeRadiusMeters = HomeRadiusMeters,
                BotCount = BotCount,
                WanderMin = WanderMin,
                WanderMax = WanderMax,
                PauseMaxSec = PauseMaxSec,
                AoiVisibleCapExpected = AoiVisibleCapExpected,
                SyncWave = SyncWave,
                SparsePatrol = SparsePatrol,
                SegmentMinSec = SegmentMinSec,
                SegmentMaxSec = SegmentMaxSec,
                MoveDutyPct = MoveDutyPct,
                SparseDutyPct = SparseDutyPct,
                SparseDwellMinSec = SparseDwellMinSec,
                SparseDwellMaxSec = SparseDwellMaxSec,
                SparseArriveWait = SparseArriveWait,
                ClusterSize = ClusterSize,
                ClusterSpacingMeters = ClusterSpacingMeters,
                ClusterRadiusMeters = ClusterRadiusMeters,
                AoiBotBackoff = AoiBotBackoff,
                AoiBotBackoffMaxSameTargetResends = AoiBotBackoffMaxSameTargetResends,
                AoiBotBackoffCapSec = AoiBotBackoffCapSec,
                AoiBotBackoffTimeoutRatio = AoiBotBackoffTimeoutRatio
            };
        }

        public float MaxStepMeters
        {
            get
            {
                var hz = TargetHz < 1 ? 1 : TargetHz;
                return GameMesh.Map.MoveStepLimit.PlannedMeters(1f / hz, WalkSpeed);
            }
        }

        public string Validate()
        {
            if (TargetHz != 2 && TargetHz != 5 && TargetHz != 10)
                return "client send hz must be 2, 5, or 10";
            if (JitterPct < 0 || JitterPct > 50)
                return "move_jitter_pct must be 0..50";
            if (MaxInflight != 1)
                return "max_inflight_move_per_bot must stay 1";
            if (!string.IsNullOrEmpty(SingleProfile) && !TryParseProfile(SingleProfile, out _))
                return "unknown single_strategy_mode";
            if (string.IsNullOrEmpty(SingleProfile) &&
                IdlePct + WanderPct + ShuttlePct + HotspotPct + PatrolPct != 100)
                return "strategy weights must sum to 100";
            if (IdlePct < 0 || WanderPct < 0 || ShuttlePct < 0 || HotspotPct < 0 || PatrolPct < 0)
                return "strategy weights must be >= 0";
            if (SafeRadius < 4f || AoiRadius < 1f || CellSize < 1f)
                return "map radii must be positive";
            if (OutboundMeters < 1f || OutboundMeters > 200f)
                return "outbound leg must be 1..200 meters";
            if (HomeRadiusMeters < 1f || HomeRadiusMeters > 200f)
                return "home radius must be 1..200 meters";
            if (BotCount < 0)
                return "bot count must be >= 0";
            if (WanderMin < 0f || WanderMax < WanderMin || WanderMax > SafeRadius)
                return "wander range must sit inside the safe radius";
            if (PauseMaxSec < 0f || PauseMaxSec > 5f)
                return "pause must be 0..5 seconds";
            if (AoiVisibleCapExpected != GameMesh.Map.MoveStepLimit.VisibleOthers)
                return "aoi cap is 200 other players, self excluded, and is not sent to the server";
            if (SegmentMinSec < 1f || SegmentMaxSec < SegmentMinSec || SegmentMaxSec > 120f)
                return "segment duration must be 1..120 seconds with min <= max";
            if (MoveDutyPct < 1 || MoveDutyPct > 100)
                return "move_duty_pct must be 1..100";
            if (SparseDutyPct < 1 || SparseDutyPct > 100)
                return "sparse_duty_pct must be 1..100";
            if (SparseDwellMinSec < 0f || SparseDwellMaxSec < SparseDwellMinSec || SparseDwellMaxSec > 120f)
                return "sparse dwell must be 0..120 seconds with min <= max";
            if (ClusterSize != 0 && (ClusterSize < 40 || ClusterSize > 80))
                return "cluster_size must be 0 or 40..80";
            if (ClusterSpacingMeters < 1f || ClusterRadiusMeters < 1f)
                return "cluster spacing and radius must be positive";
            if (AoiBotBackoffMaxSameTargetResends < 1 || AoiBotBackoffMaxSameTargetResends > 20)
                return "aoi_bot_backoff max same-target resends must be 1..20";
            if (AoiBotBackoffCapSec < 2 || AoiBotBackoffCapSec > 120)
                return "aoi_bot_backoff cap must be 2..120 seconds";
            if (AoiBotBackoffTimeoutRatio < 0.05f || AoiBotBackoffTimeoutRatio > 0.9f)
                return "aoi_bot_backoff timeout ratio must be 0.05..0.9";
            return null;
        }

        public static bool TryParseProfile(string text, out BotMovementProfile profile)
        {
            profile = BotMovementProfile.Idle;
            if (string.IsNullOrEmpty(text))
                return false;
            switch (text.Trim())
            {
                case "Idle": profile = BotMovementProfile.Idle; return true;
                case "LocalWander": profile = BotMovementProfile.LocalWander; return true;
                case "AoiBoundaryShuttle": profile = BotMovementProfile.AoiBoundaryShuttle; return true;
                case "HotspotGather": profile = BotMovementProfile.HotspotGather; return true;
                case "BidirectionalPatrol": profile = BotMovementProfile.BidirectionalPatrol; return true;
                default: return false;
            }
        }

        public BotMovementProfile ProfileFor(int botIndex)
        {
            if (!string.IsNullOrEmpty(SingleProfile) && TryParseProfile(SingleProfile, out var single))
                return single;
            var roll = StablePercent(Seed, botIndex);
            var cursor = IdlePct;
            if (roll < cursor)
                return BotMovementProfile.Idle;
            cursor += WanderPct;
            if (roll < cursor)
                return BotMovementProfile.LocalWander;
            cursor += ShuttlePct;
            if (roll < cursor)
                return BotMovementProfile.AoiBoundaryShuttle;
            cursor += HotspotPct;
            if (roll < cursor)
                return BotMovementProfile.HotspotGather;
            return BotMovementProfile.BidirectionalPatrol;
        }

        public static int StablePercent(int seed, int botIndex)
        {
            unchecked
            {
                uint x = (uint)seed * 2246822519u ^ (uint)botIndex * 3266489917u;
                x ^= x >> 16;
                x *= 2246822519u;
                x ^= x >> 13;
                return (int)(x % 100u);
            }
        }

        public static bool SameMapInstance(ulong instanceA, ulong instanceB)
        {
            return instanceA != 0 && instanceA == instanceB;
        }
    }
}
