#!/usr/bin/env bash
# THE FPS-CAP COST CURVE — what does one feed's render actually cost, and what does
# capping its rate buy back?
#
# WHY THIS MEASUREMENT IS BETTER THAN AN A/B. The PERF line already splits every engine
# frame into two buckets: `ours` (frames on which our second render ran) and `idle` (frames
# it did not). Same thread, same frame loop, same session, same second — so
#
#     ours.mean - idle.mean  =  our render's true frame-time cost
#
# with no session-age confound, no player-position confound, and no need to compare two
# builds. At wholeSceneIntervalMs = 0 the idle bucket is empty and the number is
# unmeasurable; ANY nonzero cap makes it fall out directly. That is the real reason to
# sweep rather than just cap and eyeball the fps.
#
# ALSO ANSWERS: does capping the rate reclaim any VRAM? Prediction is NO — the buffers are
# persistent and cost the same idle as busy — but that is reasoning from how they are
# allocated, not a measurement, so VRAM is recorded at every step to check it.
#
# wholeSceneIntervalMs is LIVE (freed from the rebuild signature in plan phase A2), so this
# whole sweep costs no gate cycles and no rebuilds.

set -u
OUT="D:/Projects/Space Engineers Stuff/RTT Camera/output"
LOG="$OUT/rtt.log"
CFG="$OUT/feed-config.txt"
RESULTS="$OUT/fps-cap-curve.txt"

echo "FPS-cap cost curve — $(date '+%Y-%m-%d %H:%M:%S')" | tee "$RESULTS"
echo "" | tee -a "$RESULTS"
printf '%-9s %7s %7s %8s %7s %8s %9s %8s\n' \
  "cap_ms" "fps" "ours_n" "ours_ms" "idle_n" "idle_ms" "cost_ms" "VRAM_GB" | tee -a "$RESULTS"

for CAP in 0 16 33 66 100; do
  sed -i "s/^wholeSceneIntervalMs .*/wholeSceneIntervalMs = $CAP/" "$CFG"

  # Mark the log AFTER the config poll picks it up (~2 s), then let it reach steady state.
  sleep 4
  MARK=$(wc -l < "$LOG")
  sleep 16                      # >= 3 PERF windows at 5 s each

  # Average the PERF windows recorded since MARK. Averaging rather than taking the last
  # one: a single window can land on a streaming hitch and misreport the whole row.
  tail -n +"$MARK" "$LOG" | grep -a "PERF .* fps over" | tail -3 | awk -v cap="$CAP" '
    match($0, /PERF ([0-9.]+) fps/, m)                              { fps += m[1] }
    match($0, /ours n=([0-9]+) mean=([0-9.]+)/, m)                  { on += m[1]; om += m[2] }
    match($0, /idle n=([0-9]+) mean=([0-9.]+)/, m)                  { in_ += m[1]; im += m[2] }
    match($0, /ourDraw\(cpu submit\) n=[0-9]+ mean=([0-9.]+)/, m)   { dm += m[1] }
    match($0, /VRAM=([0-9.]+)GB/, m)                                { vr += m[1] }
    { n++ }
    END {
      if (n == 0) { printf "%-9s %7s\n", cap, "NO DATA"; exit }
      ourm = om/n; idlem = im/n
      # cost is only meaningful once the idle bucket has samples in it
      cost = (in_ > 0) ? sprintf("%.1f", ourm - idlem) : "n/a"
      printf "%-9s %7.1f %7.0f %8.1f %7.0f %8.1f %9s %8.2f\n",
             cap, fps/n, on/n, ourm, in_/n, idlem, cost, vr/n
    }' | tee -a "$RESULTS"
done

sed -i "s/^wholeSceneIntervalMs .*/wholeSceneIntervalMs = 0/" "$CFG"
echo "" | tee -a "$RESULTS"
echo "restored wholeSceneIntervalMs = 0" | tee -a "$RESULTS"
