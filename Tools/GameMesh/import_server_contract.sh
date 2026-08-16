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
python3 - <<PY
from pathlib import Path
src = Path(r"$SRC_PROTO").read_bytes()
text = src.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
Path(r"$DST_DIR/game.proto").write_bytes(text)
PY
DST_PROTO="$DST_DIR/game.proto"
SHA="$(python3 -c "import hashlib,pathlib; print(hashlib.sha256(pathlib.Path(r'$DST_PROTO').read_bytes()).hexdigest())")"

PROTOC="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['protoc'])")"
GPB="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['google_protobuf'])")"
NS="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['csharp_namespace'])")"
FRAME="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['frame_format'])")"
MAXB="$(python3 -c "import json; print(json.load(open(r'$VERSIONS'))['max_frame_bytes'])")"
PVER="$(python3 -c "import json; print(int(json.load(open(r'$VERSIONS'))['protocol_version']))")"
MINVER="$(python3 -c "import json; print(int(json.load(open(r'$VERSIONS')).get('min_supported_protocol_version', 1)))")"

if [[ -z "$SERVER_COMMIT" && -d "$SOURCE/.git" ]]; then
  SERVER_COMMIT="$(git -C "$SOURCE" rev-parse HEAD)"
fi

python3 - <<PY
import json, pathlib, re, sys
versions = json.loads(pathlib.Path(r"$VERSIONS").read_text(encoding="utf-8"))
proto = pathlib.Path(r"$DST_PROTO").read_text(encoding="utf-8")
required = list(versions.get("required_types") or [])
present, missing = [], []
for t in required:
    if re.search(rf"message\s+{t}\b", proto):
        present.append(t)
    else:
        missing.append(t)
if missing:
    print("Missing required types: " + ", ".join(missing), file=sys.stderr)
    sys.exit(1)
pathlib.Path(r"$ROOT/Tools/GameMesh/cache/_present_types.txt").parent.mkdir(parents=True, exist_ok=True)
pathlib.Path(r"$ROOT/Tools/GameMesh/cache/_present_types.txt").write_text(" ".join(present), encoding="utf-8")
print("required types ok")
PY
PRESENT_STR="$(cat "$ROOT/Tools/GameMesh/cache/_present_types.txt")"

"$ROOT/Tools/GameMesh/generate_csharp_proto.sh"
DESC="$ROOT/Assets/GameMesh/Protocol/game.desc"
DESC_SHA=""
if [[ -f "$DESC" ]]; then
  DESC_SHA="$(python3 -c "import hashlib,pathlib; print(hashlib.sha256(pathlib.Path(r'$DESC').read_bytes()).hexdigest())")"
fi

python3 - <<PY
import json
present = """$PRESENT_STR""".split()
manifest = {
  "schema_file": "Schema/game.proto",
  "schema_sha256": "$SHA",
  "generated_csharp": "Generated/Game.cs",
  "descriptor": "game.desc",
  "descriptor_sha256": "$DESC_SHA",
  "frame_format": "$FRAME",
  "max_frame_bytes": int("$MAXB"),
  "protocol_version": int("$PVER"),
  "min_supported_protocol_version": int("$MINVER"),
  "csharp_namespace": "$NS",
  "protoc_version": "$PROTOC",
  "google_protobuf": "$GPB",
  "source_repo": "$SOURCE_REPO",
  "source_path": "proto/game.proto",
  "source_commit": "$SERVER_COMMIT",
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
