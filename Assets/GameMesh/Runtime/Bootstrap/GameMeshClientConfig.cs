using UnityEngine;

namespace GameMesh.Bootstrap
{
    [CreateAssetMenu(menuName = "GameMesh/Client Config", fileName = "GameMeshClientConfig")]
    public sealed class GameMeshClientConfig : ScriptableObject
    {
        public string host = "127.0.0.1";
        public int port = 8081;
        public int connectTimeoutMs = 5000;
        public int requestTimeoutMs = 8000;
        public int reconnectMaxAttempts = 6;
        public int reconnectMaxTotalMs = 30000;
        public float moveSendHz = 10f;
        public int interpolationDelayMs = 100;
        public ulong mapTemplateId = 1001;
        public uint realmId = 1;
        public uint dataVersion = 1;
        public string mapDataHash = "";
        public string mainSceneName = "MainScene";
        public bool disableSprint = true;
        public float snapError = 2.5f;
        public float smoothError = 0.35f;

        public static GameMeshClientConfig LoadOrCreate()
        {
            var loaded = Resources.Load<GameMeshClientConfig>("GameMeshClientConfig");
            return loaded != null ? loaded : CreateInstance<GameMeshClientConfig>();
        }

        public void ApplyCommandLine(string[] args)
        {
            if (args == null)
                return;
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                var value = i + 1 < args.Length ? args[i + 1] : "";
                switch (key)
                {
                    case "-gamemeshHost":
                        host = value;
                        break;
                    case "-gamemeshPort":
                        int.TryParse(value, out port);
                        break;
                    case "-gamemeshMapTemplate":
                        ulong.TryParse(value, out mapTemplateId);
                        break;
                }
            }
        }
    }

    public sealed class GameMeshLaunchArgs
    {
        public string DeviceId = "unity-dev";
        public string Password = "";
        public string DisplayName = "Luna";
        public string AutoScenario = "";
        public ulong PeerPlayerId;

        public static GameMeshLaunchArgs Parse(string[] args)
        {
            var parsed = new GameMeshLaunchArgs();
            if (args == null)
                return parsed;
            for (var i = 0; i < args.Length; i++)
            {
                var value = i + 1 < args.Length ? args[i + 1] : "";
                switch (args[i])
                {
                    case "-gamemeshDevice":
                        parsed.DeviceId = value;
                        break;
                    case "-gamemeshPassword":
                        parsed.Password = value;
                        break;
                    case "-gamemeshAutoScenario":
                        parsed.AutoScenario = value;
                        break;
                    case "-gamemeshPeerPlayerId":
                        ulong.TryParse(value, out parsed.PeerPlayerId);
                        break;
                    case "-gamemeshName":
                        parsed.DisplayName = value;
                        break;
                }
            }

            return parsed;
        }

        public void ClearPassword() => Password = "";
    }
}
