namespace GameMesh.LoadTest
{
    public static class MoveRecovery
    {
        public const int SnapshotAfterUnknown = 3;

        public static bool NeedsSnapshot(int unknownAfterWrite)
        {
            return unknownAfterWrite >= SnapshotAfterUnknown;
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
