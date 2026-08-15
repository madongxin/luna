using UnityEngine;

namespace GameMesh.Auth
{
    public sealed class LocalIdentity
    {
        public string DeviceId = "";
        public ulong PlayerId;
        public string DisplayName = "";
        public string Host = "";
        public int Port;
    }

    public static class LocalIdentityStore
    {
        const string DeviceKey = "GameMesh.DeviceId";
        const string PlayerKey = "GameMesh.PlayerId";
        const string NameKey = "GameMesh.DisplayName";
        const string HostKey = "GameMesh.Host";
        const string PortKey = "GameMesh.Port";

        public static LocalIdentity Load()
        {
            return new LocalIdentity
            {
                DeviceId = PlayerPrefs.GetString(DeviceKey, ""),
                PlayerId = ParseUlong(PlayerPrefs.GetString(PlayerKey, "0")),
                DisplayName = PlayerPrefs.GetString(NameKey, ""),
                Host = PlayerPrefs.GetString(HostKey, ""),
                Port = PlayerPrefs.GetInt(PortKey, 0)
            };
        }

        public static void Save(string deviceId, ulong playerId, string displayName, string host, int port)
        {
            if (!string.IsNullOrEmpty(deviceId))
                PlayerPrefs.SetString(DeviceKey, deviceId);
            if (playerId != 0)
                PlayerPrefs.SetString(PlayerKey, playerId.ToString());
            if (!string.IsNullOrEmpty(displayName))
                PlayerPrefs.SetString(NameKey, displayName);
            if (!string.IsNullOrEmpty(host))
                PlayerPrefs.SetString(HostKey, host);
            if (port > 0)
                PlayerPrefs.SetInt(PortKey, port);
            PlayerPrefs.Save();
        }

        public static void Clear()
        {
            PlayerPrefs.DeleteKey(DeviceKey);
            PlayerPrefs.DeleteKey(PlayerKey);
            PlayerPrefs.DeleteKey(NameKey);
            PlayerPrefs.DeleteKey(HostKey);
            PlayerPrefs.DeleteKey(PortKey);
            PlayerPrefs.Save();
        }

        static ulong ParseUlong(string text)
        {
            return ulong.TryParse(text, out var v) ? v : 0UL;
        }
    }
}
