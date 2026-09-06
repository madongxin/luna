using UnityEngine;

namespace Unity.FPS.Game
{
    public static class CursorCapture
    {
        public static bool UiOwnsCursor;

        public static bool BlocksGameInput => UiOwnsCursor;
    }
}
