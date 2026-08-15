#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
UNITY="${UNITY_PATH:?set UNITY_PATH}"
mkdir -p "$ROOT/Logs" "$ROOT/Builds/GameMeshClient"
"$UNITY" -batchmode -nographics -projectPath "$ROOT" -logFile "$ROOT/Logs/build_integration_client.log" -quit \
  -executeMethod GameMesh.Editor.IntegrationBuild.BuildWindows
