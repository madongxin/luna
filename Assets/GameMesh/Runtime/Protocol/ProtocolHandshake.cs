using System;
using System.Collections.Generic;

namespace GameMesh.Protocol
{
    public static class ProtocolHandshake
    {
        public const uint ProtocolVersion = 1;
        public const uint MinSupportedProtocolVersion = 1;
        public static readonly string[] ClientCapabilities =
        {
            "move", "aoi", "mail", "snapshot", "respawn"
        };

        public static bool TryValidate(ServerHelloRsp hello, string expectedSchemaSha256,
            uint clientProtocolVersion, out string errorCode, out string message)
        {
            errorCode = "";
            message = "";
            if (hello == null)
            {
                errorCode = "ERR_PROTOCOL_VERSION";
                message = "missing ServerHelloRsp";
                return false;
            }

            if (!hello.Ok)
            {
                errorCode = string.IsNullOrEmpty(hello.ErrorCode) ? "ERR_PROTOCOL_VERSION" : hello.ErrorCode;
                message = hello.Message ?? "";
                return false;
            }

            if (hello.MinSupportedProtocolVersion != 0 &&
                clientProtocolVersion < hello.MinSupportedProtocolVersion)
            {
                errorCode = "ERR_CLIENT_UPGRADE_REQUIRED";
                message = "client protocol below server minimum";
                return false;
            }

            if (hello.ProtocolVersion != 0 && hello.ProtocolVersion != clientProtocolVersion &&
                clientProtocolVersion < hello.ProtocolVersion &&
                (hello.MinSupportedProtocolVersion == 0 ||
                 clientProtocolVersion < hello.MinSupportedProtocolVersion))
            {
                errorCode = "ERR_PROTOCOL_VERSION";
                message = "protocol generation mismatch";
                return false;
            }

            var serverHash = (hello.SchemaSha256 ?? "").Trim();
            var localHash = (expectedSchemaSha256 ?? "").Trim();
            if (!string.IsNullOrEmpty(serverHash) && !string.IsNullOrEmpty(localHash) &&
                !string.Equals(serverHash, localHash, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "ERR_SCHEMA_MISMATCH";
                message = "schema_sha256 mismatch local=" + localHash + " server=" + serverHash;
                return false;
            }

            return true;
        }

        public static bool TryMatchMap(IEnumerable<MapManifestEntry> maps, ulong templateId,
            string localHash, uint localVersion, out MapManifestEntry entry, out string errorCode)
        {
            entry = null;
            errorCode = "";
            if (maps == null)
            {
                errorCode = "ERR_MAP_DATA_MISMATCH";
                return false;
            }

            foreach (var item in maps)
            {
                if (item != null && item.MapTemplateId == templateId)
                {
                    entry = item;
                    break;
                }
            }

            if (entry == null)
            {
                errorCode = "ERR_MAP_DATA_MISMATCH";
                return false;
            }

            if (!string.IsNullOrEmpty(localHash) && !string.IsNullOrEmpty(entry.Sha256) &&
                !string.Equals(localHash.Trim(), entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "ERR_MAP_DATA_MISMATCH";
                return false;
            }

            if (localVersion != 0 && entry.DataVersion != 0 && localVersion != entry.DataVersion)
            {
                errorCode = "ERR_MAP_DATA_MISMATCH";
                return false;
            }

            return true;
        }
    }
}
