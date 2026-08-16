#!/usr/bin/env bash
set -euo pipefail
if [[ -z "${GAMEMESH_E2E_GATEWAY:-}" ]]; then
  echo "Session-replaced E2E NOT RUN. Set GAMEMESH_E2E_GATEWAY=1 and provide a live Gateway."
  exit 2
fi
echo "Session-replaced scenario requires a second process to Login the same player_id with kick_other_device=true."
exit 2
