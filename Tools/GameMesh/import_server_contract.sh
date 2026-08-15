#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ ! -d "$ROOT/Assets" ]]; then
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
SOURCE="${1:?usage: import_server_contract.sh <server-repo-or-export-dir>}"
powershell.exe -NoProfile -File "$ROOT/Tools/GameMesh/import_server_contract.ps1" -Source "$SOURCE"
