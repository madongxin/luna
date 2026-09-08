using System.Collections.Generic;
using GameMesh.Protocol;

namespace GameMesh.Map
{
    public sealed class MapLineState
    {
        public string Kind { get; private set; } = "";
        public uint LineNo { get; private set; }
        public uint Occupancy { get; private set; }
        public uint SoftCap { get; private set; }
        public uint HardCap { get; private set; }
        public string QueueToken { get; private set; } = "";
        public uint QueuePosition { get; private set; }
        public bool QueueReady { get; private set; }
        public readonly List<MapLineInfo> Lines = new List<MapLineInfo>();

        public bool IsLineMap =>
            string.Equals(Kind, "LINE", System.StringComparison.OrdinalIgnoreCase);

        public void Clear()
        {
            Kind = "";
            LineNo = 0;
            Occupancy = 0;
            SoftCap = 0;
            HardCap = 0;
            QueueToken = "";
            QueuePosition = 0;
            QueueReady = false;
            Lines.Clear();
        }

        public void ApplyLines(IEnumerable<MapLineInfo> lines)
        {
            Lines.Clear();
            if (lines == null)
                return;
            foreach (var line in lines)
            {
                if (line != null)
                    Lines.Add(line);
            }
        }

        public void ApplyEnter(EnterMapRsp enter)
        {
            if (enter == null)
                return;
            Kind = enter.Kind ?? "";
            LineNo = enter.LineNo;
            Occupancy = enter.Occupancy;
            SoftCap = enter.SoftCap;
            HardCap = enter.HardCap;
            if (!string.IsNullOrEmpty(enter.QueueToken))
                QueueToken = enter.QueueToken;
            QueuePosition = enter.QueuePosition;
            ApplyLines(enter.Lines);
        }

        public void ApplyQuery(QueryMapLinesRsp query)
        {
            if (query == null)
                return;
            if (!string.IsNullOrEmpty(query.Kind))
                Kind = query.Kind;
            ApplyLines(query.Lines);
            RefreshCurrentFromList();
        }

        public void ApplySwitch(SwitchLineRsp switched)
        {
            if (switched == null)
                return;
            Kind = switched.Kind ?? Kind;
            LineNo = switched.LineNo;
            Occupancy = switched.Occupancy;
            SoftCap = switched.SoftCap;
            HardCap = switched.HardCap;
            ApplyLines(switched.Lines);
        }

        public void ApplyEnqueue(EnqueueMapRsp queued)
        {
            if (queued == null)
                return;
            if (!string.IsNullOrEmpty(queued.QueueToken))
                QueueToken = queued.QueueToken;
            QueuePosition = queued.QueuePosition;
            QueueReady = queued.Ready;
            if (queued.LineNo != 0)
                LineNo = queued.LineNo;
        }

        void RefreshCurrentFromList()
        {
            if (LineNo == 0)
                return;
            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                if (line != null && line.LineNo == LineNo)
                {
                    Occupancy = line.Occupancy;
                    SoftCap = line.SoftCap;
                    HardCap = line.HardCap;
                    return;
                }
            }
        }
    }
}
