#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY="${UNITY_PATH:?set UNITY_PATH}"
mkdir -p "$ROOT/Logs" "$ROOT/TestResults"
XML="$ROOT/TestResults/playmode.xml"
"$UNITY" -batchmode -nographics -projectPath "$ROOT" -runTests -testPlatform PlayMode -testFilter GameMesh \
  -testResults "$XML" -logFile "$ROOT/Logs/playmode.log"
code=$?
if [[ ! -f "$XML" ]]; then
  echo "PlayMode NOT RUN or missing results XML: $XML"
  exit 1
fi
if grep -q 'result="Failed"' "$XML"; then
  echo "PlayMode has failed tests"
  exit 1
fi
echo "PlayMode exit=$code"
exit "$code"
