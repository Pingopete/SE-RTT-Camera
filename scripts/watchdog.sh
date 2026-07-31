#!/usr/bin/env bash
# Continuous game/feed watchdog. Appends one line every INTERVAL seconds to
# output/watch.log, so the state of the game BETWEEN chat turns is a readable record
# rather than a reconstruction. Detects: game exit, crash in the newest session log,
# gate transitions, second-render stalls, and new ERROR lines.
#
# Run in the background: bash scripts/watchdog.sh &
# Self-terminates after MAX_ITER intervals so it never outlives a debugging session.

set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG="$ROOT/output/rtt.log"
OUT="$ROOT/output/watch.log"
GAMELOGS="/c/Users/Pete/AppData/Roaming/SpaceEngineers2/Temp/Logs"
INTERVAL=20
MAX_ITER=720          # 4 hours

lastSR=""
lastCrashLog=""

echo "=== watchdog started $(date '+%H:%M:%S') pid=$$ interval=${INTERVAL}s ===" >> "$OUT"

for ((i = 0; i < MAX_ITER; i++)); do
  TS=$(date '+%H:%M:%S')
  LINE="$TS"

  if tasklist //FI "IMAGENAME eq SpaceEngineers2.exe" 2>/dev/null | grep -q SpaceEngineers2; then
    LINE="$LINE game=UP"
  else
    LINE="$LINE game=DOWN"
  fi

  # Newest session log with a fatal exception — report each crashed log once.
  CRASH=$(ls -t "$GAMELOGS"/SpaceEngineers2_*[0-9].log 2>/dev/null | grep -vE "Render12|Stats|Mission" | head -1)
  if [ -n "${CRASH:-}" ] && [ "$CRASH" != "$lastCrashLog" ] && grep -q "A fatal exception" "$CRASH" 2>/dev/null; then
    lastCrashLog="$CRASH"
    WHERE=$(grep -m1 -A2 "Exception occurred" "$CRASH" | tail -1 | sed 's/^ *//' | cut -c1-100)
    LINE="$LINE CRASH=$(basename "$CRASH") at=${WHERE}"
  fi

  # FOCUS. The user reports (2026-07-30) that alt-tabbing to the second screen drops the
  # game's frame rate hard — a background frame cap, not a bug. Every PERF window taken
  # while the game is backgrounded is therefore contaminated, and several of tonight's
  # "sustained stutter" alerts were exactly that: the user was typing in chat. Stamping
  # focus on every line makes those windows discountable at a glance instead of
  # diagnosable-looking. (The engine does not log focus changes; asking Windows directly
  # is the only way.)
  FOCUS=$(powershell -NoProfile -ExecutionPolicy Bypass -File "$ROOT/scripts/focus-probe.ps1" 2>/dev/null | tr -d '\r' | tail -1)
  case "$FOCUS" in
    SpaceEngineers2) LINE="$LINE focus=GAME" ;;
    "")              LINE="$LINE focus=?" ;;
    *)               LINE="$LINE focus=AWAY($FOCUS)" ;;
  esac

  if [ -f "$LOG" ]; then
    SR=$(grep -a "secondRenders" "$LOG" | tail -1 | grep -oE "secondRenders=[0-9]+" || true)
    GATE=$(grep -a "FEED GATE\|FEED PAUSED\|FEED UNPAUSED" "$LOG" | tail -1 | grep -oE "ACTIVE \(cycle [0-9]+\)|DORMANT|PAUSED|UNPAUSED" || true)
    ERR=$(grep -a "ERROR" "$LOG" | tail -1 | cut -c2-13 || true)
    LINE="$LINE gate=${GATE:-?} ${SR:-secondRenders=?}"
    [ "$SR" = "$lastSR" ] && [ -n "$SR" ] && LINE="$LINE (STALLED)"
    lastSR="$SR"
    [ -n "$ERR" ] && LINE="$LINE lastErr@$ERR"
  else
    LINE="$LINE log=missing"
  fi

  echo "$LINE" >> "$OUT"
  sleep "$INTERVAL"
done
echo "=== watchdog ended $(date '+%H:%M:%S') ===" >> "$OUT"
