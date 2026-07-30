#!/usr/bin/env bash
# D3 — measure ONE feed's resident VRAM footprint, per resolution.
#
# WHY THIS IS A SINGLE-FEED EXPERIMENT. The original plan put C3 (two feeds) before D3,
# reasoning that you need two feeds to measure what two feeds cost. That is exactly the
# assumption that took the device on 2026-07-30: two full-quality feeds do not merely run
# slowly, they exceed the card's VRAM budget. One feed's footprint plus arithmetic answers
# the same question without betting the GPU on it.
#
# METHOD. The mod's own PERF line stops when the gate is dormant, so it cannot report the
# baseline. nvidia-smi can, and is independent of the mod entirely. For each resolution:
# pause -> gate dormant -> teardown releases everything we own -> sample; then set the
# resolution, unpause -> rebuild -> settle -> sample. The DELTA is our feed.
#
# Sampled repeatedly and MEDIANED because the game streams voxels and textures continuously;
# a single reading brackets nothing.

set -u
OUT="D:/Projects/Space Engineers Stuff/RTT Camera/output"
LOG="$OUT/rtt.log"
MARKER="$OUT/feed-paused.marker"
CFG="$OUT/feed-config.txt"
RESULTS="$OUT/d3-vram.txt"

vram() { nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>/dev/null | head -1; }

# Median of N samples one second apart.
sample() {
  local n=$1 vals=()
  for _ in $(seq 1 "$n"); do vals+=("$(vram)"); sleep 1; done
  printf '%s\n' "${vals[@]}" | sort -n | awk '{a[NR]=$1} END{print a[int((NR+1)/2)]}'
}

wait_for() {  # wait_for <pattern> <seconds>
  local pat="$1" secs="$2" i
  for i in $(seq 1 $((secs * 2))); do
    tail -c 40000 "$LOG" | grep -aq "$pat" && return 0
    sleep 0.5
  done
  return 1
}

set_res() {
  sed -i "s/^wholeSceneWidth .*/wholeSceneWidth      = $1/" "$CFG"
  sed -i "s/^wholeSceneHeight .*/wholeSceneHeight     = $1/" "$CFG"
}

echo "D3 VRAM sweep — $(date '+%Y-%m-%d %H:%M:%S')" | tee "$RESULTS"
echo "GPU: $(nvidia-smi --query-gpu=name,memory.total --format=csv,noheader)" | tee -a "$RESULTS"
echo "" | tee -a "$RESULTS"
printf '%-8s %10s %10s %10s\n' "res" "dormant" "active" "feed_MiB" | tee -a "$RESULTS"

for R in 512 768 1024; do
  # --- dormant baseline -------------------------------------------------------
  touch "$MARKER"
  wait_for "FEED PAUSED by marker" 20 || echo "  (warn: pause not confirmed)"
  sleep 4                       # let the 30-frame teardown complete and settle
  DORMANT=$(sample 6)

  # --- active at this resolution ---------------------------------------------
  set_res "$R"
  rm -f "$MARKER"
  wait_for "FEED UNPAUSED" 20 || echo "  (warn: resume not confirmed)"
  sleep 14                      # rebuild + 30-frame settle + steady state
  ACTIVE=$(sample 6)

  printf '%-8s %10s %10s %10s\n' "${R}x${R}" "$DORMANT" "$ACTIVE" "$((ACTIVE - DORMANT))" | tee -a "$RESULTS"
done

# Leave the feed where we found it.
set_res 1024
rm -f "$MARKER"
echo "" | tee -a "$RESULTS"
echo "restored wholeSceneWidth/Height = 1024" | tee -a "$RESULTS"
