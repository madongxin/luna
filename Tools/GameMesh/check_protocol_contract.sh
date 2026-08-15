#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ ! -d "$ROOT/Assets" ]]; then
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
SOURCE="${1:-}"
SCHEMA="$ROOT/Assets/GameMesh/Protocol/Schema/game.proto"
GENERATED="$ROOT/Assets/GameMesh/Protocol/Generated/Game.cs"
MANIFEST="$ROOT/Assets/GameMesh/Protocol/protocol_manifest.json"
VERSIONS="$ROOT/Tools/GameMesh/versions.json"

python3 - <<PY
import hashlib, json, pathlib, sys
root = pathlib.Path(r"$ROOT")
schema = pathlib.Path(r"$SCHEMA")
generated = pathlib.Path(r"$GENERATED")
manifest_path = pathlib.Path(r"$MANIFEST")
versions = json.loads(pathlib.Path(r"$VERSIONS").read_text(encoding="utf-8"))
for p in (schema, generated, manifest_path):
    if not p.exists():
        raise SystemExit(f"missing {p}")
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
client_sha = hashlib.sha256(schema.read_bytes()).hexdigest()
if manifest.get("schema_sha256", "").lower() != client_sha:
    raise SystemExit(f"client schema hash drift manifest={manifest.get('schema_sha256')} actual={client_sha}")
if manifest.get("frame_format") != versions["frame_format"]:
    raise SystemExit("frame_format drift")
if int(manifest.get("max_frame_bytes", 0)) != int(versions["max_frame_bytes"]):
    raise SystemExit("max_frame_bytes drift")
if manifest.get("csharp_namespace") != versions["csharp_namespace"]:
    raise SystemExit("csharp_namespace drift")
if manifest.get("protoc_version") != versions["protoc"]:
    raise SystemExit("protoc version drift")
if manifest.get("google_protobuf") != versions["google_protobuf"]:
    raise SystemExit("google_protobuf version drift")
cs = generated.read_text(encoding="utf-8")
proto = schema.read_text(encoding="utf-8")
if "namespace GameMesh.Protocol" not in cs:
    raise SystemExit("generated C# namespace drift")
if "source: game.proto" not in cs:
    raise SystemExit("generated C# is not from game.proto")
required = [
    "RegisterReq", "LoginReq", "LogoutReq", "ReconnectReq", "PushAckReq",
    "PlayerAttributes", "Vec3", "EntitySnapshot",
    "EnterMapReq", "LeaveMapReq", "MoveReq", "AoiDelta",
    "PlayerMailSendReq", "MailboxSummaryReq", "MailListReq", "MailGetReq",
    "MailboxChangedNotify", "ServerPushEnvelope",
]
missing = []
for t in required:
    if f"message {t}" not in proto and f"message {t} " not in proto:
        # tolerate newline after name
        import re
        if not re.search(rf"message\s+{t}\b", proto):
            missing.append(t)
    if f"class {t}" not in cs:
        missing.append(t + "(C#)")
for token in ("EnterMap", "GetSelfProfile", "PlayerMailSend", "MailboxChanged"):
    if token not in cs:
        missing.append("oneof " + token)
if missing:
    raise SystemExit("required types missing: " + ", ".join(missing))
if manifest.get("required_types_missing"):
    raise SystemExit("manifest required_types_missing not empty")
source = r"$SOURCE"
if source:
    candidates = [
        pathlib.Path(source) / "game.proto",
        pathlib.Path(source) / "proto" / "game.proto",
        pathlib.Path(source) / "Schema" / "game.proto",
    ]
    src = next((p for p in candidates if p.exists()), None)
    if src is None:
        raise SystemExit(f"server game.proto not found under {source}")
    server_sha = hashlib.sha256(src.read_bytes()).hexdigest()
    if server_sha != client_sha:
        raise SystemExit(f"server/client schema hash mismatch server={server_sha} client={client_sha}")
    print(f"server schema matches client: {server_sha}")
print(f"protocol contract OK schema_sha256={client_sha} commit={manifest.get('source_commit')}")
PY
