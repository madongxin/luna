#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCHEMA="$ROOT/Assets/GameMesh/Protocol/Schema/game.proto"
OUT="$ROOT/Assets/GameMesh/Protocol/Generated"
DESC="$ROOT/Assets/GameMesh/Protocol/game.desc"
CACHE="$ROOT/Tools/GameMesh/cache"
VERSION="${PROTOC_VERSION:-$(python3 -c "import json; print(json.load(open('$ROOT/Tools/GameMesh/versions.json'))['protoc'])")}"
PROTOC="${PROTOC_PATH:-}"

if [[ -z "$PROTOC" ]]; then
  if command -v protoc >/dev/null 2>&1; then
    PROTOC="$(command -v protoc)"
  else
    mkdir -p "$CACHE"
    if [[ "$(uname -s)" == "Darwin" ]]; then
      ZIP="protoc-${VERSION}-osx-x86_64.zip"
    else
      ZIP="protoc-${VERSION}-linux-x86_64.zip"
    fi
    if [[ ! -x "$CACHE/protoc-$VERSION/bin/protoc" ]]; then
      curl -L "https://github.com/protocolbuffers/protobuf/releases/download/v${VERSION}/${ZIP}" -o "$CACHE/$ZIP"
      python3 - <<PY
import hashlib, json, pathlib, sys
versions = json.loads(pathlib.Path("$ROOT/Tools/GameMesh/versions.json").read_text(encoding="utf-8"))
key = "osx_x86_64" if "$ZIP".find("osx") >= 0 else "linux_x86_64"
expected = (versions.get("protoc_sha256") or {}).get(key) or ""
path = pathlib.Path("$CACHE/$ZIP")
actual = hashlib.sha256(path.read_bytes()).hexdigest()
if expected and actual != expected.lower():
    raise SystemExit(f"protoc zip SHA-256 mismatch expected={expected} actual={actual}")
print(f"protoc zip sha256={actual}")
PY
      mkdir -p "$CACHE/protoc-$VERSION"
      unzip -o "$CACHE/$ZIP" -d "$CACHE/protoc-$VERSION"
    fi
    PROTOC="$CACHE/protoc-$VERSION/bin/protoc"
  fi
fi

mkdir -p "$CACHE/gen" "$OUT"
python3 - <<PY
from pathlib import Path
text = Path("$SCHEMA").read_text(encoding="utf-8")
if "option csharp_namespace" not in text:
    text = text.replace("package game;", "package game;\\noption csharp_namespace = \\"GameMesh.Protocol\\";")
Path("$CACHE/gen/game.proto").write_text(text, encoding="utf-8")
PY
"$PROTOC" -I "$CACHE/gen" --csharp_out="$OUT" --descriptor_set_out="$DESC" "$CACHE/gen/game.proto"
echo "Generated C# with $($PROTOC --version)"
