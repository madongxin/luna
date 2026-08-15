#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY="${UNITY_PATH:?set UNITY_PATH}"
mkdir -p "$ROOT/Logs" "$ROOT/TestResults"
"$UNITY" -batchmode -nographics -projectPath "$ROOT" -runTests -testPlatform EditMode -testFilter GameMesh \
  -testResults "$ROOT/TestResults/editmode.xml" -logFile "$ROOT/Logs/editmode.log"
echo "EditMode exit=$?"
