namespace GameMesh.LoadTest
{
    public static class MoveRecovery
    {
        public const float SamePointMeters = 0.5f;
        public static bool NeedsSnapshot(int unknownAfterWrite)
        {
            // A full snapshot is not a Move recovery path. Remote AOI gaps and Move timeouts
            // stay separate; the exact timed-out Move target is retried instead.
            return false;
        }

        public static bool TryAdoptLate(ulong haveStateSeq, ulong gotStateSeq, int haveGeneration, int gotGeneration,
            ulong haveMap, ulong gotMap)
        {
            if (haveGeneration != gotGeneration)
                return false;
            if (haveMap != 0 && gotMap != 0 && haveMap != gotMap)
                return false;
            if (gotStateSeq == 0 || gotStateSeq <= haveStateSeq)
                return false;
            return true;
        }

        /// <summary>
        /// A late MoveRsp OK still carries this bot's server pose even when state_seq did not
        /// advance (unchanged / duplicate_target). Adopting that pose unsticks sparse patrol;
        /// refusing it leaves _pos short of the waypoint and arms a 2s same-target resend loop.
        /// </summary>
        public static bool ShouldAdoptLatePose(bool ok, bool hasPosition)
        {
            return ok && hasPosition;
        }

        /// <summary>
        /// An OK for this request, or any newer one, ends the "resend that exact target" latch.
        /// uncertainRequestSeq 0 means nothing is pinned.
        /// </summary>
        public static bool ConfirmClearsResend(long confirmedRequestSeq, long uncertainRequestSeq)
        {
            return uncertainRequestSeq == 0 || confirmedRequestSeq >= uncertainRequestSeq;
        }

        /// <summary>
        /// Resend only while the confirmed pose is still short of the pinned target.
        /// Standing on that target must fall through to the next patrol segment.
        /// </summary>
        public static bool ShouldResendSamePoint(float confirmedX, float confirmedZ,
            float targetX, float targetZ, bool armed)
        {
            if (!armed)
                return false;
            var dx = confirmedX - targetX;
            var dz = confirmedZ - targetZ;
            return dx * dx + dz * dz > SamePointMeters * SamePointMeters;
        }
    }
}
