namespace GameMesh.Network
{
    public static class GameMeshLog
    {
        public static void Info(string message)
        {
            UnityEngine.Debug.Log("[GameMesh] " + Redact(message));
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Debug(string message)
        {
            UnityEngine.Debug.Log("[GameMesh] " + Redact(message));
        }

        public static void Warn(string message)
        {
            UnityEngine.Debug.LogWarning("[GameMesh] " + Redact(message));
        }

        public static void Error(string message)
        {
            UnityEngine.Debug.LogError("[GameMesh] " + Redact(message));
        }

        public static string Redact(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;
            return message
                .Replace("password=", "password=***")
                .Replace("credential=", "credential=***")
                .Replace("token=", "token=***")
                .Replace("reconnect_ticket=", "reconnect_ticket=***");
        }
    }
}
