#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ ! -d "$ROOT/Assets" ]]; then
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
SOURCE="${1:?usage: import_server_contract.sh <server-repo-or-export-dir> [server-commit]}"
SERVER_COMMIT="${2:-}"
SOURCE_REPO="${SOURCE_REPO:-https://github.com/madongxin/webserver}"
VERSIONS="$ROOT/Tools/GameMesh/versions.json"

find_proto() {
  local base="$1"
  for c in "$base/game.proto" "$base/proto/game.proto" "$base/Schema/game.proto" \
           "$base/Assets/GameMesh/Protocol/Schema/game.proto"; do
    if [[ -f "$c" ]]; then
      python3 -c "import os; print(os.path.realpath('$c'))"
      return 0
    fi
  done
  echo "game.proto not found under $base" >&2
  return 1
}

SRC_PROTO="$(find_proto "$SOURCE")"
DST_DIR="$ROOT/Assets/GameMesh/Protocol/Schema"
mkdir -p "$DST_DIR"
cp -f "$SRC_PROTO" "$DST_DIR/game.proto"
DST_PROTO="$DST_DIR/game.proto"
SHA="$(python3 -c "import hashlib,pathlib; print(hashlib.sha256(pathlib.Path(r'$DST_PROTO').read_bytes()).hexdigest())")"

PROTOC="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['protoc'])")"
GPB="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['google_protobuf'])")"
NS="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['csharp_namespace'])")"
FRAME="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['frame_format'])")"
MAXB="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['max_frame_bytes'])")"

if [[ -z "$SERVER_COMMIT" && -d "$SOURCE/.git" ]]; then
  SERVER_COMMIT="$(git -C "$SOURCE" rev-parse HEAD)"
fi

REQUIRED=(
  RegisterReq LoginReq LogoutReq ReconnectReq PushAckReq
  PlayerAttributes Vec3 EntitySnapshot
  EnterMapReq LeaveMapReq MoveReq AoiDelta
  PlayerMailSendReq MailboxSummaryReq MailListReq MailGetReq
  MailboxChangedNotify ServerPushEnvelope
)
PROTO_TEXT="$(cat "$DST_PROTO")"
PRESENT=()
MISSING=()
for t in "${REQUIRED[@]}"; do
  if grep -Eq "message[[:space:]]+$t\\b" "$DST_PROTO"; then
    PRESENT+=("$t")
  else
    MISSING+=("$t")
  fi
done
if [[ ${#MISSING[@]} -gt 0 ]]; then
  echo "Missing required types: ${MISSING[*]}" >&2
  exit 1
fi

"$ROOT/Tools/GameMesh/generate_csharp_proto.sh"
DESC="$ROOT/Assets/GameMesh/Protocol/game.desc"
DESC_SHA=""
if [[ -f "$DESC" ]]; then
  DESC_SHA="$(python3 -c "import hashlib,pathlib; print(hashlib.sha256(pathlib.Path(r'$DESC').read_bytes()).hexdigest())")"
fi

python3 - <<PY
import json
present = """${PRESENT[*]}""".split()
manifest = {
  "schema_file": "Schema/game.proto",
  "schema_sha256": "$SHA",
  "generated_csharp": "Generated/Game.cs",
  "descriptor": "game.desc",
  "descriptor_sha256": "$DESC_SHA",
  "frame_format": "$FRAME",
  "max_frame_bytes": int("$MAXB"),
  "csharp_namespace": "$NS",
  "protoc_version": "$PROTOC",
  "google_protobuf": "$GPB",
  "source_repo": "$SOURCE_REPO",
  "source_path": "proto/game.proto",
  "source_commit": "$SERVER_COMMIT",
  "source": r"$SRC_PROTO",
  "required_types_present": present,
  "required_types_missing": [],
}
path = r"$ROOT/Assets/GameMesh/Protocol/protocol_manifest.json"
with open(path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print("Imported $SRC_PROTO")
print("schema_sha256=$SHA")
print("source_commit=$SERVER_COMMIT")
PY
