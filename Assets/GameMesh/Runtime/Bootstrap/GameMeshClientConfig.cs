using System.IO;
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
        public int helloTimeoutMs = 5000;
        public int heartbeatTimeoutMs = 4000;
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
            var cfg = loaded != null ? loaded : CreateInstance<GameMeshClientConfig>();
            cfg.ResolveMapContract();
            return cfg;
        }

        public void ResolveMapContract()
        {
            if (string.IsNullOrEmpty(mapDataHash))
            {
                var hashPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "maps",
                    mapTemplateId + ".grid.json.sha256"));
                if (File.Exists(hashPath))
                    mapDataHash = File.ReadAllText(hashPath).Trim().ToLowerInvariant();
            }

            if (dataVersion == 0)
                dataVersion = 1;
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
                    case "-gamemeshMapHash":
                        mapDataHash = value ?? "";
                        break;
                    case "-gamemeshMapVersion":
                        uint.TryParse(value, out dataVersion);
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
        public string ResultDir = "";
        public string CoordDir = "";
        public string Role = "a";
        public float MoveX = -26f;
        public float MoveY = -0.2f;
        public float MoveZ = -5f;

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
                    case "-gamemeshResultDir":
                        parsed.ResultDir = value;
                        break;
                    case "-gamemeshCoordDir":
                        parsed.CoordDir = value;
                        break;
                    case "-gamemeshRole":
                        parsed.Role = value;
                        break;
                    case "-gamemeshMoveX":
                        float.TryParse(value, out parsed.MoveX);
                        break;
                    case "-gamemeshMoveY":
                        float.TryParse(value, out parsed.MoveY);
                        break;
                    case "-gamemeshMoveZ":
                        float.TryParse(value, out parsed.MoveZ);
                        break;
                }
            }

            return parsed;
        }

        public void ClearPassword() => Password = "";
    }
}
