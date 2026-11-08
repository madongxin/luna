using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GameMesh.Network;
using UnityEngine;

namespace GameMesh.Aoi
{
    /// <summary>
    /// One remote's motion state for a single rendered frame.
    /// </summary>
    public struct AoiStallSample
    {
        public ulong EntityId;
        public ulong PlayerId;
        public string Name;
        public float X, Y, Z;
        public float AuthorityX, AuthorityZ, AuthorityYaw;
        public float TargetX, TargetZ;
        public float Speed;
        public ulong StateSeq;
        public long ServerTimeMs;
        public float SampleAge;
        public float GapSec;
        public float LateSec;
        public float DelaySec;
        public int PoseCount;
        public int DropOlderSeq;
        public int DropServerTime;
        public int DropPose;
        public bool HasAuthority;
    }

    /// <summary>
    /// Classifies a remote that has not moved. Null means the rendered body is still moving.
    /// </summary>
    public static class AoiStallRules
    {
        public const float StillMeters = 0.05f;
        public const float ReportStillSec = 1f;
        // Far AOI may repeat a body position for about 3 seconds. Longer than that with no
        // new sample means the watcher is holding the last pose, not interpolating a path.
        public const float FarSampleSec = 3.5f;
        public const float RenderGapMeters = 1f;
        public const float RenderSpeed = 0.2f;

        public static string Reason(float stillSec, float sampleAgeSec, float transformToTarget,
            float speed, int poseCount)
        {
            if (stillSec < ReportStillSec)
                return null;
            if (poseCount <= 0)
                return "no_pose";
            if (sampleAgeSec < 0f)
                return "no_sample_time";
            if (transformToTarget > RenderGapMeters && speed < RenderSpeed)
                return "render_stall";
            if (sampleAgeSec >= FarSampleSec)
                return "no_aoi";
            return "held_at_sample";
        }
    }

    /// <summary>
    /// Writes one JSON line per frame for AOI remotes whose rendered body has stopped.
    /// Moving remotes are not recorded.
    /// </summary>
    public sealed class AoiStallTrace
    {
        sealed class Track
        {
            public float LastX;
            public float LastZ;
            public float LastMoveAt;
            public bool Seen;
        }

        struct Row
        {
            public AoiStallSample Sample;
            public float StillSec;
            public float Dist;
            public string Reason;
        }

        readonly Dictionary<ulong, Track> _tracks = new Dictionary<ulong, Track>();
        readonly List<ulong> _gone = new List<ulong>();
        readonly List<Row> _rows = new List<Row>(32);
        readonly StringBuilder _pending = new StringBuilder(4096);
        readonly StringBuilder _line = new StringBuilder(1024);
        StreamWriter _writer;
        string _path = "";
        float _frameNow;
        float _observerX;
        float _observerZ;
        float _frameDt;
        int _frame;
        float _lastFlushAt = -999f;
        bool _closed;
        bool _announced;

        public string Path => _path;

        public void Open(string path)
        {
            if (_closed || _writer != null)
                return;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _path = path;
            _writer = new StreamWriter(path, false, new UTF8Encoding(false));
            _writer.WriteLine(
                "{\"kind\":\"aoi_stall_trace\",\"still_m\":0.05,\"still_sec\":1,\"far_sample_sec\":3.5," +
                "\"reasons\":{\"no_pose\":\"没有坐标采样\",\"no_sample_time\":\"没有采样时间\"," +
                "\"render_stall\":\"最新坐标已离开模型但模型没跟上\",\"no_aoi\":\"超过3.5秒没有新的AOI坐标\"," +
                "\"held_at_sample\":\"停在最新AOI坐标，等待下一条\"}}");
        }

        public void BeginFrame(float now, int frame, float frameDt, float observerX, float observerZ)
        {
            if (_closed)
                return;
            _frameNow = now;
            _frame = frame;
            _frameDt = frameDt;
            _observerX = observerX;
            _observerZ = observerZ;
            _rows.Clear();
            foreach (var kv in _tracks)
                kv.Value.Seen = false;
        }

        public void Observe(in AoiStallSample sample)
        {
            if (_closed)
                return;
            Track track;
            if (!_tracks.TryGetValue(sample.EntityId, out track))
            {
                track = new Track
                {
                    LastX = sample.X,
                    LastZ = sample.Z,
                    LastMoveAt = _frameNow
                };
                _tracks[sample.EntityId] = track;
            }

            var dx = sample.X - track.LastX;
            var dz = sample.Z - track.LastZ;
            if (dx * dx + dz * dz > AoiStallRules.StillMeters * AoiStallRules.StillMeters)
            {
                track.LastX = sample.X;
                track.LastZ = sample.Z;
                track.LastMoveAt = _frameNow;
            }

            track.Seen = true;
            var still = _frameNow - track.LastMoveAt;
            var targetDx = sample.X - sample.TargetX;
            var targetDz = sample.Z - sample.TargetZ;
            var targetDist = Mathf.Sqrt(targetDx * targetDx + targetDz * targetDz);
            var reason = AoiStallRules.Reason(still, sample.SampleAge, targetDist, sample.Speed,
                sample.PoseCount);
            if (reason == null)
                return;
            var ox = sample.X - _observerX;
            var oz = sample.Z - _observerZ;
            _rows.Add(new Row
            {
                Sample = sample,
                StillSec = still,
                Dist = Mathf.Sqrt(ox * ox + oz * oz),
                Reason = reason
            });
        }

        public void EndFrame()
        {
            if (_closed)
                return;
            _gone.Clear();
            foreach (var kv in _tracks)
            {
                if (!kv.Value.Seen)
                    _gone.Add(kv.Key);
            }

            for (var i = 0; i < _gone.Count; i++)
                _tracks.Remove(_gone[i]);
            if (_rows.Count == 0)
                return;
            try
            {
                EnsureOpen();
                AppendFrame();
                if (_pending.Length >= 262144 || _frameNow - _lastFlushAt >= 0.5f)
                    Flush();
            }
            catch (Exception ex)
            {
                _closed = true;
                GameMeshLog.Warn("aoi stall file failed " + ex.Message);
            }
        }

        public void Flush()
        {
            if (_writer == null || _pending.Length == 0)
                return;
            _writer.Write(_pending.ToString());
            _writer.Flush();
            _pending.Length = 0;
            _lastFlushAt = _frameNow;
        }

        public void Close()
        {
            if (_closed && _writer == null)
                return;
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                GameMeshLog.Warn("aoi stall flush failed " + ex.Message);
            }

            try
            {
                if (_writer != null)
                    _writer.Dispose();
            }
            catch
            {
                /* play mode is ending */
            }

            _writer = null;
            _closed = true;
        }

        void EnsureOpen()
        {
            if (_writer != null || _closed)
                return;
            var dir = System.IO.Path.Combine(Application.dataPath, "..", "Logs");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Open(System.IO.Path.Combine(dir, "aoi-stall-" + stamp + ".jsonl"));
            if (!_announced && !string.IsNullOrEmpty(_path))
            {
                _announced = true;
                GameMeshLog.Info("aoi stall file " + _path);
            }
        }

        void AppendFrame()
        {
            _line.Length = 0;
            _line.Append("{\"t\":").Append(Num(_frameNow));
            _line.Append(",\"frame\":").Append(_frame);
            _line.Append(",\"frame_dt\":").Append(Num(_frameDt));
            _line.Append(",\"observer\":[").Append(Num(_observerX)).Append(',').Append(Num(_observerZ)).Append(']');
            _line.Append(",\"n\":").Append(_rows.Count);
            _line.Append(",\"stalled\":[");
            for (var i = 0; i < _rows.Count; i++)
            {
                if (i != 0)
                    _line.Append(',');
                AppendRow(_rows[i]);
            }

            _line.Append("]}");
            _line.Append('\n');
            _pending.Append(_line);
        }

        void AppendRow(Row row)
        {
            var s = row.Sample;
            _line.Append("{\"id\":").Append(s.EntityId);
            _line.Append(",\"player\":").Append(s.PlayerId);
            _line.Append(",\"name\":\"");
            AppendEscaped(_line, s.Name);
            _line.Append("\",\"reason\":\"").Append(row.Reason);
            _line.Append("\",\"still_sec\":").Append(Num(row.StillSec));
            _line.Append(",\"sample_age\":").Append(Num(s.SampleAge));
            _line.Append(",\"seq\":").Append(s.StateSeq);
            _line.Append(",\"server_ms\":").Append(s.ServerTimeMs);
            _line.Append(",\"gap\":").Append(Num(s.GapSec));
            _line.Append(",\"late\":").Append(Num(s.LateSec));
            _line.Append(",\"delay\":").Append(Num(s.DelaySec));
            _line.Append(",\"poses\":").Append(s.PoseCount);
            _line.Append(",\"dist\":").Append(Num(row.Dist));
            _line.Append(",\"speed\":").Append(Num(s.Speed));
            _line.Append(",\"has_authority\":").Append(s.HasAuthority ? "true" : "false");
            _line.Append(",\"transform\":[").Append(Num(s.X)).Append(',').Append(Num(s.Y)).Append(',')
                .Append(Num(s.Z)).Append(']');
            _line.Append(",\"authority\":[").Append(Num(s.AuthorityX)).Append(',').Append(Num(s.AuthorityZ))
                .Append(',').Append(Num(s.AuthorityYaw)).Append(']');
            _line.Append(",\"target\":[").Append(Num(s.TargetX)).Append(',').Append(Num(s.TargetZ)).Append(']');
            _line.Append(",\"drop_seq\":").Append(s.DropOlderSeq);
            _line.Append(",\"drop_time\":").Append(s.DropServerTime);
            _line.Append(",\"drop_pose\":").Append(s.DropPose);
            _line.Append('}');
        }

        static string Num(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return "null";
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        static void AppendEscaped(StringBuilder sb, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\' || c == '"')
                {
                    sb.Append('\\');
                    sb.Append(c);
                }
                else if (c < 32)
                    sb.Append(' ');
                else
                    sb.Append(c);
            }
        }
    }
}
