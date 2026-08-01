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
    # ONLY THE RECENT TAIL. rtt.log carries no dates and spans every session ever run
    # (79 MB at the time of writing), so a plain grep happily returns a line from three
    # days ago and reads as current — a trap that has fired repeatedly. The last couple of
    # megabytes is comfortably within the running session and is also far cheaper to scan
    # every 20 s than the whole file.
    RECENT=$(tail -c 2000000 "$LOG")

    SR=$(grep -a "secondRenders" <<<"$RECENT" | tail -1 | grep -oE "secondRenders=[0-9]+" || true)
    GATE=$(grep -a "FEED GATE\|FEED PAUSED\|FEED UNPAUSED" <<<"$RECENT" | tail -1 | grep -oE "ACTIVE \(cycle [0-9]+\)|DORMANT|PAUSED|UNPAUSED" || true)
    ERR=$(grep -a "ERROR" <<<"$RECENT" | tail -1 | cut -c2-13 || true)
    LINE="$LINE gate=${GATE:-?} ${SR:-secondRenders=?}"
    [ "$SR" = "$lastSR" ] && [ -n "$SR" ] && LINE="$LINE (STALLED)"
    lastSR="$SR"

    # EACH FEED'S OWN RATE (phase F1). `gate=` above is whichever feed transitioned last,
    # which with two feeds says nothing about the other one.
    #
    # Read from the PERF line, NOT from "FEED ROTATION:". The rotation line only fires when
    # the eligible SET changes, and settling does not change that set — so it went stale the
    # moment both feeds were up and reported "0=settling 1=settling" for minutes after they
    # were both rendering happily. A field that is only correct at the instant it is written
    # is worse than no field, because it looks live. The PERF line is written every 5 s and
    # carries the sampled per-feed rate.
    FEEDS=$(grep -a "feed fps" <<<"$RECENT" | tail -1 | sed -E 's/.*feed fps //' || true)
    [ -n "$FEEDS" ] && LINE="$LINE feeds=[$FEEDS]"

    # A rotation stall means one feed is eligible and not rendering while others wait — the
    # backstop fired, so the mod kept running, but something is wrong with that feed.
    # Anchored on the "!!!" prefix: a bare grep for STALL also matches "INSTALLED", which is
    # in the camera-CB line every rebuild writes. (It cost me a false alarm today.)
    STALL=$(grep -ac "!!! FEED ROTATION STALL" <<<"$RECENT" || true)
    [ "${STALL:-0}" != "0" ] && LINE="$LINE ROTATION-STALL x$STALL"

    [ -n "$ERR" ] && LINE="$LINE lastErr@$ERR"
  else
    LINE="$LINE log=missing"
  fi

  echo "$LINE" >> "$OUT"
  sleep "$INTERVAL"
done
echo "=== watchdog ended $(date '+%H:%M:%S') ===" >> "$OUT"
