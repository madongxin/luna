namespace GameMesh.Aoi
{
    /// <summary>Observer-side interpolation counters. Off unless Enabled.</summary>
    public static class AoiInterpMetrics
    {
        public static bool Enabled;

        public static int Underrun;
        public static int Catchup;
        public static int Teleport;
        public static int LeaveDespawn;
        public static int ReconcileDespawn;
        public static int DropOldSeq;
        public static float CoastMs;
        public static float LastDelayMs;

        public static void Reset()
        {
            Underrun = 0;
            Catchup = 0;
            Teleport = 0;
            LeaveDespawn = 0;
            ReconcileDespawn = 0;
            DropOldSeq = 0;
            CoastMs = 0f;
            LastDelayMs = 0f;
        }

        public static void NoteUnderrun()
        {
            if (Enabled)
                Underrun++;
        }

        public static void NoteCatchup()
        {
            if (Enabled)
                Catchup++;
        }

        public static void NoteTeleport()
        {
            if (Enabled)
                Teleport++;
        }

        public static void NoteLeave()
        {
            if (Enabled)
                LeaveDespawn++;
        }

        public static void NoteDropOldSeq()
        {
            if (Enabled)
                DropOldSeq++;
        }

        public static void AddCoast(float sec)
        {
            if (Enabled && sec > 0f)
                CoastMs += sec * 1000f;
        }

        public static void NoteDelay(float delaySec)
        {
            if (Enabled)
                LastDelayMs = delaySec * 1000f;
        }
    }
}
