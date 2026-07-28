#!/usr/bin/env bash
# One-screen health summary. Run at the start of a turn instead of guessing.
#
# Every wrong diagnosis in this project has come from acting without checking state
# first: a mod that was still running during an A/B, a VRAM leak read as a rendering
# bug, a config edit assumed live that never took. This answers all of those in one
# call, cheaply enough that there is no excuse to skip it.
#
#   scripts/status.sh          summary
#   scripts/status.sh -v       plus the last few PERF lines and skip list

set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG="$ROOT/output/rtt.log"
CFG="$ROOT/output/feed-config.txt"
GAMELOGS="/c/Users/Pete/AppData/Roaming/SpaceEngineers2/Temp/Logs"
VERBOSE="${1:-}"

echo "=== $(date '+%H:%M:%S') ==============================================="

# --- is the game even up? ---------------------------------------------------
if tasklist //FI "IMAGENAME eq SpaceEngineers2.exe" 2>/dev/null | grep -q SpaceEngineers2; then
  echo "GAME       running"
else
  echo "GAME       NOT RUNNING"
fi

# --- did it crash, and how recently? ----------------------------------------
CRASH=$(ls -t "$GAMELOGS"/SpaceEngineers2_*[0-9].log 2>/dev/null | grep -vE "Render12|Stats|Mission" | head -1)
if [ -n "${CRASH:-}" ] && grep -q "A fatal exception" "$CRASH" 2>/dev/null; then
  echo "CRASH      newest session log contains a fatal exception:"
  grep -m1 -A2 "Exception occurred" "$CRASH" | sed 's/^/           /' | head -3
fi

# --- markers that silently change behaviour ---------------------------------
for m in feed-paused handover-live camera-armed feed-copy-armed handover-armed bind-armed; do
  [ -f "$ROOT/output/$m.marker" ] && echo "MARKER     $m.marker PRESENT"
done

# --- our own state ----------------------------------------------------------
[ -f "$LOG" ] || { echo "LOG        missing"; exit 0; }
NOW=$(date +%s)
LOGAGE=$(( NOW - $(stat -c %Y "$LOG" 2>/dev/null || echo "$NOW") ))
echo "LOG        last written ${LOGAGE}s ago"

GATE=$(grep -a "FEED GATE\|FEED PAUSED\|FEED UNPAUSED" "$LOG" | tail -1)
[ -n "$GATE" ] && echo "GATE       ${GATE:0:150}"

SR=$(grep -a "secondRenders" "$LOG" | tail -1)
[ -n "$SR" ] && echo "RENDER     ${SR:0:120}"

PERF=$(grep -a "PERF [0-9]" "$LOG" | tail -1)
[ -n "$PERF" ] && echo "PERF       ${PERF:12:170}"

# Errors, but only ones from the CURRENT session — old ones are noise and have
# repeatedly been misread as live problems.
LASTINSTALL=$(grep -an "=== logic installed ===" "$LOG" | tail -1 | cut -d: -f1)
if [ -n "${LASTINSTALL:-}" ]; then
  ERRS=$(tail -n "+$LASTINSTALL" "$LOG" | grep -ac "ERROR" || true)
  echo "ERRORS     $ERRS since the last logic load"
  [ "${ERRS:-0}" -gt 0 ] && tail -n "+$LASTINSTALL" "$LOG" | grep -a "ERROR" | tail -3 | sed 's/^/           /'
fi

if [ "$VERBOSE" = "-v" ]; then
  echo "--- config ---"
  grep -aE "^(wholeSceneSkipStages|wholeSceneIntervalMs|wholeSceneRender|wholeSceneOwnShadows|wholeSceneAAMode|wholeSceneRtFlags|wholeSceneDisableRaytracing|emissivity|panelIdleMs) *=" "$CFG" 2>/dev/null | sed 's/^/           /'
  echo "--- recent PERF ---"
  grep -a "PERF [0-9]" "$LOG" | tail -3 | sed 's/^/           /'
  echo "--- stages actually skipping ---"
  grep -a "Whole-scene: skipping stage" "$LOG" | tail -14 | sed 's/.*skipping stage /           /' | cut -c1-60
fi
