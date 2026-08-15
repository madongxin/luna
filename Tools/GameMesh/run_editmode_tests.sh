#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY="${UNITY_PATH:?set UNITY_PATH}"
mkdir -p "$ROOT/Logs" "$ROOT/TestResults"
XML="$ROOT/TestResults/editmode.xml"
"$UNITY" -batchmode -nographics -projectPath "$ROOT" -runTests -testPlatform EditMode -testFilter GameMesh \
  -testResults "$XML" -logFile "$ROOT/Logs/editmode.log"
code=$?
if [[ ! -f "$XML" ]]; then
  echo "EditMode NOT RUN or missing results XML: $XML"
  exit 1
fi
if grep -q 'result="Failed"' "$XML"; then
  echo "EditMode has failed tests"
  exit 1
fi
echo "EditMode exit=$code"
exit "$code"
