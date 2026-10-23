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
        public float AoiRadius = 32f;
        public float CellSize = 32f;
        // Spawn neighborhood that the live 1002 grid treats as walkable.
        // Do not wander out to the map AABB.
        public float SafeRadius = 24f;
        public float WanderMin = 10f;
        public float WanderMax = 24f;
        public float PauseMaxSec = 2f;
        public int AoiVisibleCapExpected = 200;
        public bool SyncWave;

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
                WanderMin = WanderMin,
                WanderMax = WanderMax,
                PauseMaxSec = PauseMaxSec,
                AoiVisibleCapExpected = AoiVisibleCapExpected,
                SyncWave = SyncWave
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
            if (WanderMin < 0f || WanderMax < WanderMin || WanderMax > SafeRadius)
                return "wander range must sit inside the safe radius";
            if (PauseMaxSec < 0f || PauseMaxSec > 5f)
                return "pause must be 0..5 seconds";
            if (AoiVisibleCapExpected != GameMesh.Map.MoveStepLimit.VisibleOthers)
                return "aoi cap is 200 other players, self excluded, and is not sent to the server";
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
