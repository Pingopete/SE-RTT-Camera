#!/usr/bin/env bash
# Each feed's OWN frame rate, read off the newest PERF line.
#
#   scripts/feed-rate.sh
#
# The mod samples this itself now (FeedInstance.SampleFps) and prints it on the PERF line as
# "feed fps 0:26.6  1:25.9", so this script just reads it. A feed that is not rendering shows
# why instead of a number: off (no panel ticking), dis (feedsDisabled), set (settling after a
# rebuild), ERR (its route faulted).
#
# THE VERSION THIS REPLACES SAMPLED THE LOG, AND IT LIED. It diffed the per-feed
# "Whole-scene hook: ... secondRenders=N" line, which rides a PROCESS-GLOBAL 5 s timer — so
# only whichever feed holds the render slot when that timer expires prints at all, and the
# other feed's last-known count can be many seconds stale. On 2026-08-01 that produced a
# 52.0 vs 6.5 split out of a run whose real split was 26.6 vs 25.9, and cost an hour chasing
# a rotation bug that did not exist. Any instrument that samples a log line has to know how
# often that line is written.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG="$ROOT/output/rtt.log"

# Only the recent tail: rtt.log carries no dates and spans every session ever run.
LINE=$(tail -c 2000000 "$LOG" | grep -a "feed fps" | tail -1)

if [ -z "$LINE" ]; then
  echo "no 'feed fps' on any recent PERF line — the mod may be paused, dormant, or running a"
  echo "build from before this field existed."
  exit 1
fi

echo "${LINE%%]*}]"                                   # the timestamp, so staleness is visible
echo "  ${LINE##*feed fps }"
echo
echo "  engine: $(sed -nE 's/.*PERF ([0-9.]+) fps.*/\1/p' <<<"$LINE") fps"
