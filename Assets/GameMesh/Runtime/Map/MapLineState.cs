using System.Collections.Generic;
using GameMesh.Protocol;

namespace GameMesh.Map
{
    public sealed class MapLineState
    {
        public const uint DefaultSplitAt = 100;

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

        public uint EffectiveSplitAt => SplitAt(SoftCap);

        public static uint SplitAt(uint softCap)
        {
            return softCap != 0 ? softCap : DefaultSplitAt;
        }

        public uint ResolveConcreteLine(uint requested)
        {
            if (requested != 0)
            {
                var existing = FindLine(requested);
                if (existing != null)
                {
                    var cap = SplitAt(existing.SoftCap != 0 ? existing.SoftCap : SoftCap);
                    if (existing.Occupancy < cap)
                        return requested;
                }
            }

            var room = PickLineWithRoom();
            if (room != 0)
                return room;
            return 0;
        }

        public uint PickLoadTestLine(uint preferred, int index)
        {
            var chosen = FindLine(preferred);
            if (chosen == null)
            {
                for (var i = 0; i < Lines.Count; i++)
                {
                    var line = Lines[i];
                    if (line == null || line.LineNo == 0)
                        continue;
                    if (line.Occupancy < SplitAt(line.SoftCap != 0 ? line.SoftCap : SoftCap))
                    {
                        chosen = line;
                        break;
                    }
                }
            }

            if (chosen == null)
                return 0;

            var cap = SplitAt(chosen.SoftCap != 0 ? chosen.SoftCap : SoftCap);
            var occ = chosen.Occupancy;
            if (LineNo == chosen.LineNo && Occupancy > occ)
                occ = Occupancy;
            var slots = (int)cap;
            if (LineNo == chosen.LineNo && slots > 1)
                slots -= 1;
            if (occ >= cap || index >= slots)
            {
                for (var i = 0; i < Lines.Count; i++)
                {
                    var line = Lines[i];
                    if (line == null || line.LineNo == 0 || line.LineNo == chosen.LineNo)
                        continue;
                    if (line.Occupancy < SplitAt(line.SoftCap != 0 ? line.SoftCap : SoftCap))
                        return line.LineNo;
                }

                return 0;
            }

            return chosen.LineNo;
        }

        public uint PickLineWithRoom()
        {
            uint best = 0;
            var bestRoom = 0;
            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                if (line == null || line.LineNo == 0)
                    continue;
                var cap = (int)SplitAt(line.SoftCap != 0 ? line.SoftCap : SoftCap);
                var room = cap - (int)line.Occupancy;
                if (room <= bestRoom)
                    continue;
                bestRoom = room;
                best = line.LineNo;
            }

            return best;
        }

        public bool HasLine(uint lineNo)
        {
            return FindLine(lineNo) != null;
        }

        MapLineInfo FindLine(uint lineNo)
        {
            if (lineNo == 0)
                return null;
            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                if (line != null && line.LineNo == lineNo)
                    return line;
            }

            return null;
        }

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

        public void ApplyLines(IEnumerable<MapLineInfo> lines, bool replace = true)
        {
            if (lines == null)
                return;
            var incoming = new List<MapLineInfo>();
            foreach (var line in lines)
            {
                if (line != null && line.LineNo != 0)
                    incoming.Add(line);
            }

            if (incoming.Count == 0)
                return;
            if (replace)
            {
                Lines.Clear();
                Lines.AddRange(incoming);
            }
            else
            {
                for (var i = 0; i < incoming.Count; i++)
                    UpsertLine(incoming[i]);
            }

            Lines.Sort((a, b) => a.LineNo.CompareTo(b.LineNo));
        }

        void UpsertLine(MapLineInfo incoming)
        {
            for (var i = 0; i < Lines.Count; i++)
            {
                if (Lines[i] != null && Lines[i].LineNo == incoming.LineNo)
                {
                    Lines[i] = incoming;
                    return;
                }
            }

            Lines.Add(incoming);
        }

        public void ApplyPresence(string kind, uint lineNo, uint occupancy, uint softCap, uint hardCap)
        {
            if (!string.IsNullOrEmpty(kind))
                Kind = kind;
            LineNo = lineNo;
            Occupancy = occupancy;
            SoftCap = softCap;
            HardCap = hardCap;
        }

        public void ApplyEnter(EnterMapRsp enter)
        {
            if (enter == null)
                return;
            ApplyPresence(enter.Kind, enter.LineNo, enter.Occupancy, enter.SoftCap, enter.HardCap);
            if (!string.IsNullOrEmpty(enter.QueueToken))
                QueueToken = enter.QueueToken;
            QueuePosition = enter.QueuePosition;
            ApplyLines(enter.Lines, false);
        }

        public void ApplyQuery(QueryMapLinesRsp query)
        {
            if (query == null)
                return;
            if (!string.IsNullOrEmpty(query.Kind))
                Kind = query.Kind;
            ApplyLines(query.Lines, true);
            RefreshCurrentFromList();
        }

        public void ApplySwitch(SwitchLineRsp switched, uint requestedLineNo = 0)
        {
            if (switched == null)
                return;
            if (!string.IsNullOrEmpty(switched.Kind))
                Kind = switched.Kind;
            if (switched.LineNo != 0)
                LineNo = switched.LineNo;
            else if (requestedLineNo != 0)
                LineNo = requestedLineNo;
            if (switched.Occupancy != 0)
                Occupancy = switched.Occupancy;
            if (switched.SoftCap != 0)
                SoftCap = switched.SoftCap;
            if (switched.HardCap != 0)
                HardCap = switched.HardCap;
            ApplyLines(switched.Lines, false);
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

        public void BindPresence(ulong mapInstanceId)
        {
            if (mapInstanceId != 0)
            {
                for (var i = 0; i < Lines.Count; i++)
                {
                    var line = Lines[i];
                    if (line == null || line.MapInstanceId == 0 || line.MapInstanceId != mapInstanceId)
                        continue;
                    LineNo = line.LineNo;
                    Occupancy = line.Occupancy;
                    SoftCap = line.SoftCap;
                    HardCap = line.HardCap;
                    return;
                }
            }

            if (LineNo != 0)
            {
                RefreshCurrentFromList();
                return;
            }

            if (mapInstanceId != 0 && Lines.Count == 1 && Lines[0] != null && Lines[0].LineNo != 0)
            {
                LineNo = Lines[0].LineNo;
                Occupancy = Lines[0].Occupancy;
                SoftCap = Lines[0].SoftCap;
                HardCap = Lines[0].HardCap;
            }
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
