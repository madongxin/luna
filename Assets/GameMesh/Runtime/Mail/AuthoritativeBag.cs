using System.Collections.Generic;
using GameMesh.Protocol;

namespace GameMesh.Mail
{
    /// <summary>
    /// Replaces the visible bag from <see cref="FullStateSnapshotRsp"/>.
    /// Counts are never added on top of a previous snapshot.
    /// </summary>
    public sealed class AuthoritativeBag
    {
        public ulong PlayerId { get; private set; }
        public ulong AssetVersion { get; private set; }
        public bool HasSnapshot { get; private set; }
        public readonly Dictionary<uint, uint> Counts = new Dictionary<uint, uint>();

        public void Clear()
        {
            PlayerId = 0;
            AssetVersion = 0;
            HasSnapshot = false;
            Counts.Clear();
        }

        public bool TryReplace(FullStateSnapshotRsp snap, ulong expectedPlayerId, out bool advanced)
        {
            advanced = false;
            if (snap == null || !snap.Ok)
                return false;
            if (expectedPlayerId != 0 && snap.PlayerId != 0 && snap.PlayerId != expectedPlayerId)
                return false;
            if (snap.ItemIds.Count != snap.ItemCounts.Count)
                return false;

            var previous = AssetVersion;
            var had = HasSnapshot;
            Counts.Clear();
            for (var i = 0; i < snap.ItemIds.Count; i++)
                Counts[snap.ItemIds[i]] = snap.ItemCounts[i];
            AssetVersion = snap.AssetVersion;
            PlayerId = snap.PlayerId != 0 ? snap.PlayerId : expectedPlayerId;
            HasSnapshot = true;
            advanced = had && snap.AssetVersion > previous;
            return true;
        }
    }
}
