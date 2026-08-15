#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY="${UNITY_PATH:?set UNITY_PATH}"
mkdir -p "$ROOT/Logs" "$ROOT/TestResults"
"$UNITY" -batchmode -nographics -projectPath "$ROOT" -runTests -testPlatform PlayMode -testFilter GameMesh \
  -testResults "$ROOT/TestResults/playmode.xml" -logFile "$ROOT/Logs/playmode.log"
