#!/usr/bin/env bash
# Per-feed render rate, sampled from rtt.log over a window.
#
# The evidence for "the survivors absorb the departed feed's share of the frame cycle"
# (phase F1). Every other instrument in the mod is an aggregate — the PERF line, the stats
# panel, secondRenders in the watchdog — so with two feeds they read the same whether both
# are rendering or one has gone away. This is the per-feed split.
#
#   scripts/feed-rate.sh [seconds]     default 15
#
# Reads the "[feed N] Whole-scene hook: ... secondRenders=X" line each feed prints every 5 s,
# so the window wants to be comfortably longer than 5 s to catch at least one per feed.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG="$ROOT/output/rtt.log"
WINDOW="${1:-15}"

# Only the recent tail: rtt.log carries no dates and spans every session ever run, so a
# plain grep will happily return a line from days ago and read as current.
snap() {
  tail -c 4000000 "$LOG" | grep -a "Whole-scene hook:" | tail -40 |
    sed -nE 's/.*\[feed ([0-9]+)\].*secondRenders=([0-9]+).*/\1 \2/p' |
    awk '{ last[$1] = $2 } END { for (f in last) printf "%s %s\n", f, last[f] }' | sort
}

BEFORE=$(snap)
sleep "$WINDOW"
AFTER=$(snap)

echo "per-feed second renders over ${WINDOW}s   ($(date '+%H:%M:%S'))"
join <(echo "$BEFORE") <(echo "$AFTER") 2>/dev/null |
  awk -v w="$WINDOW" '{ d = $3 - $2; printf "  feed %s: %6d -> %6d   %+5d   %5.1f/s\n", $1, $2, $3, d, d / w }'

# A feed that printed nothing at all in the window is not slow, it is ABSENT — and that is
# a different finding from "0/s", so say which.
for f in 0 1 2 3; do
  echo "$AFTER" | grep -q "^$f " || {
    echo "$BEFORE" | grep -q "^$f " && echo "  feed $f: stopped logging during the window"
  }
done
