#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CLIENT="${1:-$ROOT/Builds/GameMeshClient/GameMeshClient.x86_64}"
HOST="${GAMEMESH_HOST:-127.0.0.1}"
PORT="${GAMEMESH_PORT:-8081}"
TIMEOUT="${TIMEOUT_SEC:-90}"

if [[ -z "${GAMEMESH_E2E_GATEWAY:-}" ]]; then
  echo "Real dual-client E2E NOT RUN. Set GAMEMESH_E2E_GATEWAY=1 and provide a live Gateway."
  exit 2
fi
if [[ ! -x "$CLIENT" && ! -f "$CLIENT" ]]; then
  echo "Real dual-client E2E NOT RUN. missing $CLIENT"
  exit 2
fi

STAMP="$(date +%Y%m%d-%H%M%S)"
WORK="$ROOT/Logs/e2e-$STAMP"
COORD="$WORK/coord"
A_DIR="$WORK/a"
B_DIR="$WORK/b"
mkdir -p "$COORD" "$A_DIR/data" "$B_DIR/data"
cleanup() {
  if [[ -n "${A_PID:-}" ]] && kill -0 "$A_PID" 2>/dev/null; then kill "$A_PID" || true; fi
  if [[ -n "${B_PID:-}" ]] && kill -0 "$B_PID" 2>/dev/null; then kill "$B_PID" || true; fi
}
trap cleanup EXIT

start_client() {
  local role="$1" device="$2" name="$3" data="$4" result="$5"
  "$CLIENT" \
    -gamemeshHost "$HOST" -gamemeshPort "$PORT" \
    -gamemeshDevice "$device" -gamemeshName "$name" \
    -gamemeshPassword "e2e-local" -gamemeshAutoScenario two-client \
    -gamemeshRole "$role" -gamemeshCoordDir "$COORD" -gamemeshResultDir "$result" \
    -dataPath "$data" &
  echo $!
}

A_PID="$(start_client a "e2e-a-$STAMP" Alice "$A_DIR/data" "$A_DIR")"
B_PID="$(start_client b "e2e-b-$STAMP" Bob "$B_DIR/data" "$B_DIR")"

for ((i=0; i<TIMEOUT; i++)); do
  if ! kill -0 "$A_PID" 2>/dev/null && ! kill -0 "$B_PID" 2>/dev/null; then
    break
  fi
  sleep 1
done

if [[ ! -f "$A_DIR/result.json" || ! -f "$B_DIR/result.json" ]]; then
  echo "missing result.json"
  exit 1
fi
python3 - <<PY
import json, pathlib, sys
a = json.loads(pathlib.Path("$A_DIR/result.json").read_text(encoding="utf-8"))
b = json.loads(pathlib.Path("$B_DIR/result.json").read_text(encoding="utf-8"))
if a.get("result") != "PASS" or b.get("result") != "PASS":
    raise SystemExit(f"client result not PASS A={a.get('result')} B={b.get('result')}")
if not a.get("map_instance_id") or a.get("map_instance_id") != b.get("map_instance_id"):
    raise SystemExit(f"map_instance mismatch {a.get('map_instance_id')} {b.get('map_instance_id')}")
ae = pathlib.Path("$A_DIR/events.jsonl").read_text(encoding="utf-8")
be = pathlib.Path("$B_DIR/events.jsonl").read_text(encoding="utf-8")
if "aoi_peer_seen" not in ae or "aoi_peer_seen" not in be:
    raise SystemExit("AOI peer assertion failed")
if "mail_sent" not in ae or "mail_received" not in be:
    raise SystemExit("mail assertion failed")
print("E2E PASS work=$WORK")
PY
