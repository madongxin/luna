using UnityEngine;

namespace GameMesh.Aoi
{
    /// <summary>World-space polyline of a remote pose buffer. Off unless requested.</summary>
    public static class AoiInterpDebugDraw
    {
        public struct Point
        {
            public float X, Y, Z;
        }

        static int _frame = -1;
        static int _drawn;

        public static bool TryReserve(int frame, int max)
        {
            if (max <= 0)
                max = 8;
            if (frame != _frame)
            {
                _frame = frame;
                _drawn = 0;
            }

            if (_drawn >= max)
                return false;
            _drawn++;
            return true;
        }

        public static void Draw(Point[] path, int count, Vector3 render, Vector3 tip,
            bool coasting, Color pathColor)
        {
            if (path == null || count <= 0)
                return;
            for (var i = 1; i < count; i++)
            {
                var a = new Vector3(path[i - 1].X, path[i - 1].Y + 0.2f, path[i - 1].Z);
                var b = new Vector3(path[i].X, path[i].Y + 0.2f, path[i].Z);
                Debug.DrawLine(a, b, pathColor, 0f, false);
            }

            var renderLift = render + Vector3.up * 0.35f;
            var tipLift = tip + Vector3.up * 0.35f;
            Debug.DrawLine(renderLift, renderLift + Vector3.up * 0.6f, Color.yellow, 0f, false);
            Debug.DrawLine(tipLift, tipLift + Vector3.up * 0.4f, Color.cyan, 0f, false);
            if (coasting)
                Debug.DrawLine(tipLift, renderLift, Color.magenta, 0f, false);
        }
    }
}
