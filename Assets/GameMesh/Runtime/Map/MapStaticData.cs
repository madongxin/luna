using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameMesh.Map
{
    public sealed class MapVec3
    {
        public float x, y, z;
        public MapVec3() { }
        public MapVec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public sealed class MapSpawnPoint
    {
        public string id = "default";
        public float x, y, z, yaw;
    }

    public sealed class MapStaticData
    {
        public int schema_version = 1;
        public ulong map_template_id = 1001;
        public string scene_name = "MainScene";
        public uint data_version = 1;
        public MapVec3 bounds_min = new MapVec3();
        public MapVec3 bounds_max = new MapVec3();
        public float aoi_cell_size = 12f;
        public float nav_sample_step = 1f;
        public int grid_width;
        public int grid_height;
        public List<int> walkable_rle = new List<int>();
        public List<MapSpawnPoint> spawn_points = new List<MapSpawnPoint>();

        public static List<int> EncodeRle(bool[] cells)
        {
            var rle = new List<int>();
            if (cells == null || cells.Length == 0)
                return rle;
            var cur = cells[0] ? 1 : 0;
            var n = 1;
            for (var i = 1; i < cells.Length; i++)
            {
                var bit = cells[i] ? 1 : 0;
                if (bit == cur)
                {
                    n++;
                    continue;
                }

                rle.Add(cur);
                rle.Add(n);
                cur = bit;
                n = 1;
            }

            rle.Add(cur);
            rle.Add(n);
            return rle;
        }

        public static bool[] DecodeRle(IList<int> rle, int expected)
        {
            var list = new List<bool>(expected);
            if (rle == null)
                return list.ToArray();
            for (var i = 0; i + 1 < rle.Count; i += 2)
            {
                var bit = rle[i] != 0;
                var count = rle[i + 1];
                for (var n = 0; n < count; n++)
                    list.Add(bit);
            }

            return list.ToArray();
        }

        public static int Col(float x, float minX, float step)
        {
            return (int)Math.Floor((x - minX) / step);
        }

        public static int Row(float z, float minZ, float step)
        {
            return (int)Math.Floor((z - minZ) / step);
        }

        public static int CellIndex(int col, int row, int width)
        {
            return row * width + col;
        }

        public bool TryGetWalkable(float x, float z, bool[] cells, out int col, out int row)
        {
            col = Col(x, bounds_min.x, nav_sample_step);
            row = Row(z, bounds_min.z, nav_sample_step);
            if (col < 0 || row < 0 || col >= grid_width || row >= grid_height)
                return false;
            var index = CellIndex(col, row, grid_width);
            return cells != null && index < cells.Length && cells[index];
        }

        public string ToDeterministicJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema_version\": ").Append(schema_version).Append(",\n");
            sb.Append("  \"map_template_id\": ").Append(map_template_id).Append(",\n");
            sb.Append("  \"scene_name\": ").Append(Quote(scene_name)).Append(",\n");
            sb.Append("  \"data_version\": ").Append(data_version).Append(",\n");
            sb.Append("  \"bounds_min\": ").Append(Arr(bounds_min)).Append(",\n");
            sb.Append("  \"bounds_max\": ").Append(Arr(bounds_max)).Append(",\n");
            sb.Append("  \"aoi_cell_size\": ").Append(Dec(aoi_cell_size)).Append(",\n");
            sb.Append("  \"nav_sample_step\": ").Append(Dec(nav_sample_step)).Append(",\n");
            sb.Append("  \"grid_width\": ").Append(grid_width).Append(",\n");
            sb.Append("  \"grid_height\": ").Append(grid_height).Append(",\n");
            sb.Append("  \"walkable_rle\": [");
            for (var i = 0; i < walkable_rle.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(walkable_rle[i].ToString(CultureInfo.InvariantCulture));
            }

            sb.Append("],\n");
            sb.Append("  \"spawn_points\": [");
            for (var i = 0; i < spawn_points.Count; i++)
            {
                var p = spawn_points[i];
                if (i > 0)
                    sb.Append(", ");
                sb.Append("{\"id\":").Append(Quote(string.IsNullOrEmpty(p.id) ? "default" : p.id))
                    .Append(",\"position\":[").Append(Num(p.x)).Append(", ").Append(Num(p.y)).Append(", ").Append(Num(p.z))
                    .Append("],\"yaw\":").Append(Num(p.yaw)).Append('}');
            }

            sb.Append("]\n}\n");
            return sb.ToString();
        }

        public string Sha256()
        {
            var bytes = new UTF8Encoding(false).GetBytes(ToDeterministicJson());
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        static string Arr(MapVec3 v) =>
            "[" + Num(v.x) + ", " + Num(v.y) + ", " + Num(v.z) + "]";

        static string Num(float v)
        {
            if (Math.Abs(v - (float)Math.Round(v)) < 0.0005f)
                return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.000", CultureInfo.InvariantCulture);
        }

        static string Dec(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        static string Quote(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
