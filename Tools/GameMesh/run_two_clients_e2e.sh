#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CLIENT="${1:-$ROOT/Builds/GameMeshClient/GameMeshClient.x86_64}"
if [[ ! -f "$CLIENT" && ! -x "$CLIENT" ]]; then
  FOUND="$(find "$ROOT/Builds" -type f \( -name 'GameMeshClient' -o -name 'GameMeshClient.x86_64' -o -name 'GameMeshClient.exe' \) 2>/dev/null | head -n 1 || true)"
  if [[ -n "${FOUND}" ]]; then
    CLIENT="$FOUND"
  fi
fi
HOST="${GAMEMESH_HOST:-127.0.0.1}"
PORT="${GAMEMESH_PORT:-8081}"
TIMEOUT="${TIMEOUT_SEC:-90}"
SCENARIO="${GAMEMESH_E2E_SCENARIO:-presence-move-logout}"

if [[ -z "${GAMEMESH_E2E_GATEWAY:-}" ]]; then
  echo "Real dual-client E2E NOT RUN. Set GAMEMESH_E2E_GATEWAY=1 and provide a live Gateway."
  exit 2
fi
if [[ ! -x "$CLIENT" && ! -f "$CLIENT" ]]; then
  echo "Real dual-client E2E NOT RUN. missing $CLIENT"
  exit 2
fi

HASH_FILE="$ROOT/maps/1001.grid.json.sha256"
if [[ ! -f "$HASH_FILE" ]]; then
  echo "Real dual-client E2E NOT RUN. missing $HASH_FILE"
  exit 2
fi
MAP_HASH="$(tr -d '\r\n' < "$HASH_FILE")"
COMMIT="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || true)"
export GAMEMESH_CLIENT_COMMIT="${GAMEMESH_CLIENT_COMMIT:-$COMMIT}"

STAMP="$(date +%Y%m%d-%H%M%S)"
WORK="$ROOT/Logs/e2e-$STAMP"
COORD="$WORK/coord"
A_DIR="$WORK/a"
B_DIR="$WORK/b"
mkdir -p "$COORD" "$A_DIR/data" "$B_DIR/data"
A_PID=""
B_PID=""
cleanup() {
  if [[ -n "${A_PID}" ]] && kill -0 "$A_PID" 2>/dev/null; then kill "$A_PID" || true; fi
  if [[ -n "${B_PID}" ]] && kill -0 "$B_PID" 2>/dev/null; then kill "$B_PID" || true; fi
}
trap cleanup EXIT

"$CLIENT" \
  -gamemeshHost "$HOST" -gamemeshPort "$PORT" \
  -gamemeshDevice "e2e-a-$STAMP" -gamemeshName Alice \
  -gamemeshPassword "e2e-local" \
  -gamemeshMapHash "$MAP_HASH" -gamemeshMapVersion 1 \
  -gamemeshAutoScenario "$SCENARIO" \
  -gamemeshRole a -gamemeshCoordDir "$COORD" -gamemeshResultDir "$A_DIR" \
  -dataPath "$A_DIR/data" -logFile "$A_DIR/player.log" >"$A_DIR/stdout.log" 2>&1 &
A_PID=$!
"$CLIENT" \
  -gamemeshHost "$HOST" -gamemeshPort "$PORT" \
  -gamemeshDevice "e2e-b-$STAMP" -gamemeshName Bob \
  -gamemeshPassword "e2e-local" \
  -gamemeshMapHash "$MAP_HASH" -gamemeshMapVersion 1 \
  -gamemeshAutoScenario "$SCENARIO" \
  -gamemeshRole b -gamemeshCoordDir "$COORD" -gamemeshResultDir "$B_DIR" \
  -dataPath "$B_DIR/data" -logFile "$B_DIR/player.log" >"$B_DIR/stdout.log" 2>&1 &
B_PID=$!

for ((i=0; i<TIMEOUT; i++)); do
  if ! kill -0 "$A_PID" 2>/dev/null && ! kill -0 "$B_PID" 2>/dev/null; then
    break
  fi
  sleep 1
done

if kill -0 "$A_PID" 2>/dev/null || kill -0 "$B_PID" 2>/dev/null; then
  echo "timeout; killing leftover clients"
  kill "$A_PID" "$B_PID" 2>/dev/null || true
  wait "$A_PID" 2>/dev/null || true
  wait "$B_PID" 2>/dev/null || true
  echo "clients did not exit by themselves"
  exit 1
fi

set +e
wait "$A_PID"
A_CODE=$?
wait "$B_PID"
B_CODE=$?
set -e
if [[ "$A_CODE" -ne 0 || "$B_CODE" -ne 0 ]]; then
  echo "nonzero client exit A=$A_CODE B=$B_CODE"
  exit 1
fi

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
if not a.get("hello_ok") or not b.get("hello_ok") or not a.get("login_ok") or not b.get("login_ok"):
    raise SystemExit("hello/login not ok")
if not a.get("player_id_before_logout") or not b.get("player_id_before_logout"):
    raise SystemExit("player_id_before_logout missing")
if not a.get("map_instance_id_before_logout") or a.get("map_instance_id_before_logout") != b.get("map_instance_id_before_logout"):
    raise SystemExit(f"map_instance mismatch {a.get('map_instance_id_before_logout')} {b.get('map_instance_id_before_logout')}")
if not a.get("peer_seen") or not b.get("peer_seen"):
    raise SystemExit("mutual visibility failed")
if not a.get("peer_move_seen") or not b.get("peer_move_seen"):
    raise SystemExit("bidirectional move failed")
if not a.get("logout_rsp_ok") or not b.get("logout_rsp_ok"):
    raise SystemExit("logout_rsp_ok missing")
if not b.get("peer_leave_seen"):
    raise SystemExit("B did not see AOI Leave after A logout")

def events(path):
    out = []
    for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return out

def find(evts, name):
    for e in evts:
        if e.get("event") == name:
            return e
    raise SystemExit(f"missing structured event {name}")

ae = events("$A_DIR/events.jsonl")
be = events("$B_DIR/events.jsonl")
a_seen = find(ae, "aoi_peer_seen")
b_seen = find(be, "aoi_peer_seen")
if not a_seen.get("peer_id") or not b_seen.get("peer_id"):
    raise SystemExit("aoi_peer_seen missing peer_id")
a_moved = find(ae, "aoi_peer_moved")
b_moved = find(be, "aoi_peer_moved")
if a_moved.get("old_state_seq") and a_moved.get("new_state_seq", 0) <= a_moved.get("old_state_seq", 0):
    raise SystemExit("A did not observe increasing state_seq")
if b_moved.get("old_state_seq") and b_moved.get("new_state_seq", 0) <= b_moved.get("old_state_seq", 0):
    raise SystemExit("B did not observe increasing state_seq")
find(be, "aoi_peer_left")
a_logout = find(ae, "logout")
b_logout = find(be, "logout")
if not a_logout.get("ok") or not b_logout.get("ok"):
    raise SystemExit("structured logout ok=false")
blob = pathlib.Path("$A_DIR/result.json").read_text(encoding="utf-8") + pathlib.Path("$B_DIR/result.json").read_text(encoding="utf-8")
blob += pathlib.Path("$A_DIR/events.jsonl").read_text(encoding="utf-8") + pathlib.Path("$B_DIR/events.jsonl").read_text(encoding="utf-8")
for secret in ("e2e-local", "password", "reconnect_ticket"):
    if secret in blob:
        raise SystemExit(f"secret leaked into result/events: {secret}")
print("E2E PASS work=$WORK")
PY
