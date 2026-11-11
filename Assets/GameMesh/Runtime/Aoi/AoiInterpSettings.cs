namespace GameMesh.Aoi
{
    /// <summary>
    /// Observer-side interpolation knobs. Does not read vx, vz, or move_start_ms.
    /// Far-layer delay stays at DelayMax (250ms); coast covers sparse MOVE gaps.
    /// </summary>
    public struct AoiInterpSettings
    {
        public bool Enabled;
        public bool CoastEnabled;
        public bool MetricsEnabled;
        public bool DebugDraw;
        public bool AllowSnap;
        public float DelayDefault;
        public float DelayMin;
        public float DelayMax;
        public float CoastMaxSec;
        public float Alpha;
        public float KJitter;
        public float SlewUp;
        public float SlewDown;
        public float FarGapSec;
        public int BufferSize;
        public float ReenterReuseSec;
        public int DebugMax;
        public float MoveSpeed;

        public static AoiInterpSettings Disabled => new AoiInterpSettings
        {
            DelayDefault = 0.2f,
            DelayMin = 0.15f,
            DelayMax = 0.25f,
            CoastMaxSec = 0.3f,
            Alpha = 0.25f,
            KJitter = 1.5f,
            SlewUp = 0.04f,
            SlewDown = 0.02f,
            FarGapSec = 0.4f,
            BufferSize = RemotePoseBuffer.Depth,
            ReenterReuseSec = 0.1f,
            DebugMax = 8,
            MoveSpeed = 10f
        };

        public static AoiInterpSettings FromConfig(Bootstrap.GameMeshClientConfig cfg)
        {
            var s = Disabled;
            if (cfg == null)
                return s;
            s.Enabled = cfg.aoiInterpEnabled;
            s.CoastEnabled = cfg.aoiInterpCoastEnabled;
            s.MetricsEnabled = cfg.aoiInterpMetricsEnabled;
            s.DebugDraw = cfg.aoiInterpDebugDraw;
            s.AllowSnap = cfg.aoiInterpAllowSnap;
            s.DelayDefault = Ms(cfg.aoiInterpDelayMs, 200);
            s.DelayMin = Ms(cfg.aoiInterpDelayMinMs, 150);
            s.DelayMax = Ms(cfg.aoiInterpDelayMaxMs, 250);
            s.CoastMaxSec = Ms(cfg.aoiInterpCoastMaxMs, 300);
            s.Alpha = cfg.aoiInterpAlpha > 0.01f ? cfg.aoiInterpAlpha : 0.25f;
            s.KJitter = cfg.aoiInterpKJitter > 0.01f ? cfg.aoiInterpKJitter : 1.5f;
            s.SlewUp = Ms(cfg.aoiInterpSlewUpMs, 40);
            s.SlewDown = Ms(cfg.aoiInterpSlewDownMs, 20);
            s.FarGapSec = Ms(cfg.aoiInterpFarGapMs, 400);
            s.BufferSize = cfg.aoiInterpBufferSize > 2 ? cfg.aoiInterpBufferSize : RemotePoseBuffer.Depth;
            s.ReenterReuseSec = Ms(cfg.aoiInterpReenterReuseMs, 100);
            s.DebugMax = cfg.aoiInterpDebugMax > 0 ? cfg.aoiInterpDebugMax : 8;
            s.MoveSpeed = GameMesh.Map.MoveStepLimit.ServerMetersPerSec;
            if (s.DelayMin > s.DelayMax)
                s.DelayMin = s.DelayMax;
            if (s.DelayDefault < s.DelayMin)
                s.DelayDefault = s.DelayMin;
            if (s.DelayDefault > s.DelayMax)
                s.DelayDefault = s.DelayMax;
            return s;
        }

        static float Ms(int ms, int fallback)
        {
            if (ms <= 0)
                ms = fallback;
            return ms / 1000f;
        }
    }

    /// <summary>
    /// Per-entity render delay. Scheme A: clamp to 150–250ms. Far entities use
    /// the same cap; coasting covers 600ms–1s MOVE gaps.
    /// </summary>
    public sealed class AoiInterpDelay
    {
        public float Delay = 0.2f;
        public float EwmaGap;
        public float EwmaAbsDev;
        float _lastArrive;
        bool _hasArrive;

        public bool HasArrive => _hasArrive;

        public void Reset(float defaultDelay)
        {
            Delay = defaultDelay > 0.01f ? defaultDelay : 0.2f;
            EwmaGap = 0f;
            EwmaAbsDev = 0f;
            _lastArrive = 0f;
            _hasArrive = false;
        }

        public void OnArrived(float now, AoiInterpSettings s)
        {
            if (!_hasArrive)
            {
                _hasArrive = true;
                _lastArrive = now;
                Delay = s.DelayDefault;
                return;
            }

            var gap = now - _lastArrive;
            if (gap < 0.05f)
                gap = 0.05f;
            if (gap > 2f)
                gap = 2f;
            var a = s.Alpha > 0.01f ? s.Alpha : 0.25f;
            if (EwmaGap <= 0.001f)
                EwmaGap = gap;
            else
                EwmaGap = (1f - a) * EwmaGap + a * gap;
            var dev = gap - EwmaGap;
            if (dev < 0f)
                dev = -dev;
            EwmaAbsDev = (1f - a) * EwmaAbsDev + a * dev;
            // Scheme A: hold_ratio is 0 for near and far. Delay stays in 150–250ms.
            var k = s.KJitter > 0.01f ? s.KJitter : 1.5f;
            var raw = k * EwmaAbsDev;
            var min = s.DelayMin > 0.01f ? s.DelayMin : 0.15f;
            var max = s.DelayMax > min ? s.DelayMax : 0.25f;
            var target = raw;
            if (target < min)
                target = min;
            if (target > max)
                target = max;
            var delta = target - Delay;
            var up = s.SlewUp > 0f ? s.SlewUp : 0.04f;
            var down = s.SlewDown > 0f ? s.SlewDown : 0.02f;
            if (delta > 0f)
                Delay += delta < up ? delta : up;
            else
                Delay += delta > -down ? delta : -down;
            _lastArrive = now;
        }

        public void CatchUpIfBehind(float tipTime, float renderTime, AoiInterpSettings s)
        {
            var max = s.DelayMax > 0.01f ? s.DelayMax : 0.25f;
            if (tipTime - renderTime <= max + 0.15f)
                return;
            var down = s.SlewDown > 0f ? s.SlewDown : 0.02f;
            var min = s.DelayMin > 0.01f ? s.DelayMin : 0.15f;
            Delay -= down;
            if (Delay < min)
                Delay = min;
        }
    }
}
