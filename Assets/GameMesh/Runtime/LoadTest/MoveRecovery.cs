namespace GameMesh.LoadTest
{
    public static class MoveRecovery
    {
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
    }
}
