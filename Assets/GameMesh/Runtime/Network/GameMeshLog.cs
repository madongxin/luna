using UnityEngine;

namespace GameMesh.Network
{
    public static class GameMeshLog
    {
        public static void Info(string message)
        {
            Debug.Log("[GameMesh] " + Redact(message));
        }

        public static void Warn(string message)
        {
            Debug.LogWarning("[GameMesh] " + Redact(message));
        }

        public static void Error(string message)
        {
            Debug.LogError("[GameMesh] " + Redact(message));
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
