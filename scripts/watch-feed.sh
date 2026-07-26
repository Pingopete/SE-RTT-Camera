#!/usr/bin/env bash
# Liveness by LOG FRESHNESS, not process presence.
#
# A crashed or hung SE2 leaves its process in the task list until force-closed, so
# "the process exists" measured nothing — it reported 33s of stability for a game
# that had died instantly. The log is appended by the render-thread hooks, so if
# rendering stops, the log stops. That is the real signal.
#
# usage: watch-feed.sh [seconds] [stale_threshold]
LOG="D:/Projects/Space Engineers Stuff/RTT Camera/output/rtt.log"
DUR=${1:-120}
STALE=${2:-5}

start=$(date +%s)
last_size=$(stat -c %s "$LOG" 2>/dev/null || echo 0)
last_change=$start

while :; do
  sleep 1
  now=$(date +%s)
  size=$(stat -c %s "$LOG" 2>/dev/null || echo 0)
  [ "$size" != "$last_size" ] && { last_size=$size; last_change=$now; }

  quiet=$(( now - last_change ))
  elapsed=$(( now - start ))

  if [ "$quiet" -ge "$STALE" ]; then
    echo "*** LOG WENT QUIET after ${elapsed}s (silent ${quiet}s) — render thread stopped ***"
    tail -6 "$LOG"
    exit 2
  fi
  [ "$elapsed" -ge "$DUR" ] && { echo "=== ALIVE ${elapsed}s (log still being written) ==="; grep -E "Rates/s:" "$LOG" | tail -3; exit 0; }
done
