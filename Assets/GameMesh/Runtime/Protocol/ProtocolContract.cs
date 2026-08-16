using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameMesh.Protocol;

namespace GameMesh.Protocol
{
    public static class ProtocolCapabilities
    {
        public static readonly string[] RequiredTypes =
        {
            "RegisterReq", "LoginReq", "LogoutReq", "ReconnectReq", "PushAckReq",
            "PlayerAttributes", "Vec3", "EntitySnapshot",
            "EnterMapReq", "LeaveMapReq", "MoveReq", "AoiDelta",
            "PlayerMailSendReq", "MailboxSummaryReq", "MailListReq", "MailGetReq",
            "MailboxChangedNotify", "ServerPushEnvelope",
            "ClientHelloReq", "ServerHelloRsp", "HeartbeatReq", "HeartbeatRsp",
            "FullStateSnapshotRsp", "WorldSnapshotReq", "RespawnReq", "RespawnRsp"
        };

        public static bool HasType(string typeName)
        {
            return typeof(GameRequest).Assembly.GetType("GameMesh.Protocol." + typeName) != null;
        }

        public static bool HasMove => HasType("MoveReq");
        public static bool HasAoiDelta => HasType("AoiDelta");
        public static bool HasPlayerAttributes => HasType("PlayerAttributes");
        public static bool HasPlayerMailSend => HasType("PlayerMailSendReq");
        public static bool HasMailboxChangedNotify => HasType("MailboxChangedNotify");
        public static bool HasVec3 => HasType("Vec3");
        public static bool HasEntitySnapshot => HasType("EntitySnapshot");

        public static List<string> MissingRequiredTypes()
        {
            var missing = new List<string>();
            foreach (var name in RequiredTypes)
            {
                if (!HasType(name))
                    missing.Add(name);
            }

            return missing;
        }
    }

    [Serializable]
    public sealed class ProtocolManifest
    {
        public string schema_file;
        public string schema_sha256;
        public string generated_csharp;
        public string descriptor_sha256;
        public int protocol_version;
        public int min_supported_protocol_version;
        public string frame_format;
        public int max_frame_bytes;
        public string csharp_namespace;
    }

    public static class ProtocolContract
    {
        public const string ExpectedFrameFormat = "uint32_be_length_prefixed";
        public const int ExpectedMaxFrameBytes = 4 * 1024 * 1024;
        public const string ExpectedNamespace = "GameMesh.Protocol";
        public const int ExpectedProtocolVersion = 1;

        public static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string ComputeFileSha256(string path)
        {
            return ComputeSha256(File.ReadAllBytes(path));
        }

        public static void ValidateManifest(ProtocolManifest manifest, string schemaPath, string generatedPath)
        {
            if (manifest == null)
                throw new InvalidOperationException("protocol_manifest.json missing");
            if (!string.Equals(manifest.frame_format, ExpectedFrameFormat, StringComparison.Ordinal))
                throw new InvalidOperationException("frame_format drift: " + manifest.frame_format);
            if (manifest.max_frame_bytes != ExpectedMaxFrameBytes)
                throw new InvalidOperationException("max_frame_bytes drift: " + manifest.max_frame_bytes);
            if (!string.Equals(manifest.csharp_namespace, ExpectedNamespace, StringComparison.Ordinal))
                throw new InvalidOperationException("csharp_namespace drift");
            if (manifest.protocol_version != 0 && manifest.protocol_version != ExpectedProtocolVersion)
                throw new InvalidOperationException("protocol_version drift: " + manifest.protocol_version);
            if (!File.Exists(schemaPath))
                throw new InvalidOperationException("schema missing: " + schemaPath);
            var actual = ComputeFileSha256(schemaPath);
            if (!string.Equals(actual, manifest.schema_sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"schema hash drift manifest={manifest.schema_sha256} actual={actual}");
            if (!File.Exists(generatedPath))
                throw new InvalidOperationException("generated C# missing; re-run generate_csharp_proto");
            var generated = File.ReadAllText(generatedPath);
            if (!generated.Contains("namespace GameMesh.Protocol"))
                throw new InvalidOperationException("generated C# namespace drift");
            if (!generated.Contains("source: game.proto"))
                throw new InvalidOperationException("generated C# is not from game.proto");
        }
    }
}
