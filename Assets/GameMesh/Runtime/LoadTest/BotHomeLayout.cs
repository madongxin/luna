using System;
using System.IO;
using UnityEngine;

namespace GameMesh.LoadTest
{
    /// <summary>
    /// One home per bot, on a walkable cell within 20 cells of the player spawn.
    /// The server still drops everyone on that spawn. Each bot walks to its cell,
    /// then stays inside its own radius.
    /// </summary>
    public static class BotHomeLayout
    {
        public const int SpawnCellRadius = 20;
        public const float PlayerSpawnX = 179.3f;
        public const float PlayerSpawnZ = 324.2f;
        const string GridFile = "1002.grid.json";
        static readonly object Gate = new object();
        static bool[] _walkable;
        static bool[] _taken;
        static int[] _near;
        static int _width;
        static int _height;
        static int _anchorCol;
        static int _anchorRow;
        static float _originX;
        static float _originZ;
        static float _step = 8f;
        static float _minX;
        static float _maxX;
        static float _minZ;
        static float _maxZ;
        static bool _loaded;
        static int _walkableCount;

        public static void BeginWave()
        {
            lock (Gate)
            {
                if (_taken != null)
                    Array.Clear(_taken, 0, _taken.Length);
            }
        }

        public static void InstallForTests(float originX, float originZ, float step, int width, int height,
            bool[] walkable, float anchorX = float.NaN, float anchorZ = float.NaN)
        {
            lock (Gate)
            {
                Install(originX, originZ, step, width, height, walkable);
                if (float.IsNaN(anchorX) || float.IsNaN(anchorZ))
                {
                    anchorX = originX + width * 0.5f * _step;
                    anchorZ = originZ + height * 0.5f * _step;
                }

                SetAnchor(anchorX, anchorZ);
                _loaded = true;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _walkable = null;
                _taken = null;
                _near = null;
                _loaded = false;
                _walkableCount = 0;
            }
        }

        public static Vector3 Pick(int botIndex, int botCount, float y)
        {
            lock (Gate)
            {
                EnsureLoaded();
                if (_near == null || _near.Length == 0)
                    return CellAt(_anchorCol, _anchorRow, y);
                var start = StableSlot(botIndex, _near.Length);
                for (var n = 0; n < _near.Length; n++)
                {
                    var cell = _near[(start + n) % _near.Length];
                    var col = cell % _width;
                    var row = cell / _width;
                    if (_taken != null && cell < _taken.Length && _taken[cell])
                        continue;
                    if (_taken != null && cell < _taken.Length)
                        _taken[cell] = true;
                    return CellAt(col, row, y);
                }

                var reuse = _near[start];
                return CellAt(reuse % _width, reuse / _width, y);
            }
        }

        static void SetAnchor(float x, float z)
        {
            _anchorCol = CellIndex(x, _originX, _width);
            _anchorRow = CellIndex(z, _originZ, _height);
            var cells = new System.Collections.Generic.List<int>();
            if (_walkable != null)
            {
                for (var row = _anchorRow - SpawnCellRadius; row <= _anchorRow + SpawnCellRadius; row++)
                {
                    for (var col = _anchorCol - SpawnCellRadius; col <= _anchorCol + SpawnCellRadius; col++)
                    {
                        if (col < 0 || row < 0 || col >= _width || row >= _height)
                            continue;
                        var index = row * _width + col;
                        if (index < _walkable.Length && _walkable[index])
                            cells.Add(index);
                    }
                }
            }

            if (cells.Count == 0)
            {
                var index = _anchorRow * _width + _anchorCol;
                if (index >= 0)
                    cells.Add(index);
            }

            _near = cells.ToArray();
        }

        static int CellIndex(float world, float origin, int limit)
        {
            var cell = (int)Math.Floor((world - origin) / _step);
            if (cell < 0)
                return 0;
            if (limit > 0 && cell >= limit)
                return limit - 1;
            return cell;
        }

        static Vector3 CellAt(int col, int row, float y)
        {
            return new Vector3(
                _originX + (col + 0.5f) * _step,
                y,
                _originZ + (row + 0.5f) * _step);
        }

        static int StableSlot(int botIndex, int count)
        {
            unchecked
            {
                uint x = 20261021u * 2246822519u ^ (uint)botIndex * 3266489917u;
                x ^= x >> 16;
                x *= 2246822519u;
                x ^= x >> 13;
                return (int)(x % (uint)count);
            }
        }

        static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            var path = FindGrid();
            if (path == null)
            {
                Install(-1800f, -1800f, 200f, 19, 19, null);
                SetAnchor(-1800f + 9.5f * 200f, -1800f + 9.5f * 200f);
                return;
            }

            try
            {
                LoadFile(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[BotHomeLayout] grid load failed: " + ex.Message);
                Install(-1800f, -1800f, 200f, 19, 19, null);
                SetAnchor(-1800f + 9.5f * 200f, -1800f + 9.5f * 200f);
            }
        }

        static void LoadFile(string path)
        {
            var text = File.ReadAllText(path);
            var min = ReadVec3(text, "bounds_min");
            var step = ReadFloat(text, "nav_sample_step");
            var width = ReadInt(text, "grid_width");
            var height = ReadInt(text, "grid_height");
            if (step < 0.01f || width < 1 || height < 1)
                throw new InvalidDataException("grid header");
            var cells = new bool[width * height];
            DecodeRle(text, cells);
            Install(min.x, min.z, step, width, height, cells);
            var spawn = ReadSpawn(text);
            SetAnchor(spawn.x, spawn.z);
        }

        static void Install(float originX, float originZ, float step, int width, int height, bool[] walkable)
        {
            _originX = originX;
            _originZ = originZ;
            _step = step < 0.01f ? 8f : step;
            _width = width;
            _height = height;
            _walkable = walkable;
            _taken = walkable == null ? null : new bool[walkable.Length];
            _walkableCount = 0;
            _minX = originX;
            _maxX = originX + Math.Max(0, width - 1) * _step;
            _minZ = originZ;
            _maxZ = originZ + Math.Max(0, height - 1) * _step;
            if (walkable == null)
                return;
            var any = false;
            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var index = row * width + col;
                    if (index >= walkable.Length || !walkable[index])
                        continue;
                    var x = originX + (col + 0.5f) * _step;
                    var z = originZ + (row + 0.5f) * _step;
                    if (!any)
                    {
                        _minX = _maxX = x;
                        _minZ = _maxZ = z;
                        any = true;
                    }
                    else
                    {
                        if (x < _minX) _minX = x;
                        if (x > _maxX) _maxX = x;
                        if (z < _minZ) _minZ = z;
                        if (z > _maxZ) _maxZ = z;
                    }

                    _walkableCount++;
                }
            }
        }

        static void DecodeRle(string text, bool[] cells)
        {
            var key = text.IndexOf("\"walkable_rle\"", StringComparison.Ordinal);
            if (key < 0)
                throw new InvalidDataException("walkable_rle");
            var start = text.IndexOf('[', key);
            var end = text.IndexOf(']', start + 1);
            if (start < 0 || end < 0)
                throw new InvalidDataException("walkable_rle");
            var cursor = 0;
            var i = start + 1;
            while (i < end && cursor < cells.Length)
            {
                while (i < end && (text[i] < '0' || text[i] > '9') && text[i] != '-')
                    i++;
                if (i >= end)
                    break;
                var bit = ReadNumber(text, ref i) != 0;
                while (i < end && (text[i] < '0' || text[i] > '9') && text[i] != '-')
                    i++;
                if (i >= end)
                    break;
                var count = ReadNumber(text, ref i);
                for (var n = 0; n < count && cursor < cells.Length; n++)
                    cells[cursor++] = bit;
            }
        }

        static int ReadNumber(string text, ref int i)
        {
            var sign = 1;
            if (text[i] == '-')
            {
                sign = -1;
                i++;
            }

            var value = 0;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            {
                value = value * 10 + (text[i] - '0');
                i++;
            }

            return value * sign;
        }

        static float ReadFloat(string text, string key)
        {
            var token = "\"" + key + "\"";
            var at = text.IndexOf(token, StringComparison.Ordinal);
            if (at < 0)
                return 0f;
            at = text.IndexOf(':', at + token.Length);
            if (at < 0)
                return 0f;
            at++;
            while (at < text.Length && (text[at] == ' ' || text[at] == '\n' || text[at] == '\r'))
                at++;
            var end = at;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '-' || text[end] == '.'))
                end++;
            float.TryParse(text.Substring(at, end - at),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value);
            return value;
        }

        static int ReadInt(string text, string key)
        {
            return (int)Math.Round(ReadFloat(text, key));
        }

        static Vector3 ReadSpawn(string text)
        {
            var at = text.IndexOf("\"spawn_points\"", StringComparison.Ordinal);
            if (at < 0)
                return new Vector3(PlayerSpawnX, 0f, PlayerSpawnZ);
            var pos = text.IndexOf("\"position\"", at, StringComparison.Ordinal);
            if (pos < 0)
                return new Vector3(PlayerSpawnX, 0f, PlayerSpawnZ);
            var start = text.IndexOf('[', pos);
            var end = start < 0 ? -1 : text.IndexOf(']', start + 1);
            if (start < 0 || end < 0)
                return new Vector3(PlayerSpawnX, 0f, PlayerSpawnZ);
            var body = text.Substring(start + 1, end - start - 1).Split(',');
            float Part(int index)
            {
                if (index >= body.Length)
                    return 0f;
                float.TryParse(body[index].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value);
                return value;
            }

            var spawn = new Vector3(Part(0), Part(1), Part(2));
            if (Math.Abs(spawn.x) < 0.001f && Math.Abs(spawn.z) < 0.001f)
                return new Vector3(PlayerSpawnX, spawn.y, PlayerSpawnZ);
            return spawn;
        }

        static Vector3 ReadVec3(string text, string key)
        {
            var token = "\"" + key + "\"";
            var at = text.IndexOf(token, StringComparison.Ordinal);
            if (at < 0)
                return Vector3.zero;
            var start = text.IndexOf('[', at + token.Length);
            var end = start < 0 ? -1 : text.IndexOf(']', start + 1);
            if (start < 0 || end < 0)
                return Vector3.zero;
            var body = text.Substring(start + 1, end - start - 1).Split(',');
            float Part(int index)
            {
                if (index >= body.Length)
                    return 0f;
                float.TryParse(body[index].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value);
                return value;
            }

            return new Vector3(Part(0), Part(1), Part(2));
        }

        static string FindGrid()
        {
            try
            {
                var fromAssets = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "maps", GridFile));
                if (File.Exists(fromAssets))
                    return fromAssets;
            }
            catch
            {
                /* edit-mode tests always have Application; a bare runner does not */
            }

            var dir = Directory.GetCurrentDirectory();
            for (var hop = 0; hop < 6 && !string.IsNullOrEmpty(dir); hop++)
            {
                var candidate = Path.Combine(dir, "maps", GridFile);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }
    }
}
