#!/usr/bin/env bash
# B1 — PER-LAYER COST TABLE. What does each render layer cost, in VRAM and in frame time?
#
# WHY THIS AND NOT "VRAM per preset". The presets do not exist yet, and they cannot be
# designed without this: a preset is a CHOICE of which layers to drop, and choosing well
# requires knowing what each one costs. D3 already established that RESOLUTION is nearly
# free in VRAM terms (4x the pixels, no measurable change), which means the footprint lives
# in the resolution-independent layers — exactly the ones measured here.
#
# It also unblocks the warmth-at-higher-N experiment: fitting 3-4 feeds needs a cheap
# config, and this says which knobs actually buy headroom.
#
# METHOD. Same as D3: nvidia-smi dormant-vs-active delta, because the mod's own PERF line
# stops when the gate is dormant. Medianed over several samples against streaming drift.
# Frame-time cost comes from the PERF line while active.
#
# DELIBERATELY NOT MEASURED HERE:
#   wholeSceneOwnProbes     — the manager leaks RTV descriptors once per hot reload (see the
#                             CTD note in feed-config.txt). Enabling it for a measurement is
#                             safe for ONE session but the knob is disarmed for a reason;
#                             measure it deliberately, not in a sweep.
#   wholeSceneOwnDrawContexts — turning it OFF makes our cull write the engine's visibility
#                             lists, which is the confirmed player-view corruption (flickering
#                             ship lights) the whole design exists to avoid. Likely the single
#                             largest allocation, so it IS worth measuring — under supervision,
#                             not unattended in a loop.

set -u
OUT="D:/Projects/Space Engineers Stuff/RTT Camera/output"
LOG="$OUT/rtt.log"
MARKER="$OUT/feed-paused.marker"
CFG="$OUT/feed-config.txt"
RESULTS="$OUT/layer-costs.txt"

vram() { nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>/dev/null | head -1; }
sample() {
  local n=$1 vals=()
  for _ in $(seq 1 "$n"); do vals+=("$(vram)"); sleep 1; done
  printf '%s\n' "${vals[@]}" | sort -n | awk '{a[NR]=$1} END{print a[int((NR+1)/2)]}'
}
wait_for() {
  local pat="$1" secs="$2" i
  for i in $(seq 1 $((secs * 2))); do
    tail -c 40000 "$LOG" | grep -aq "$pat" && return 0
    sleep 0.5
  done
  return 1
}
setkv() { sed -i "s/^$1 *=.*/$1 = $2/" "$CFG"; }

# Frame-time + submit from the PERF windows recorded since a mark.
perf_since() {
  tail -n +"$1" "$LOG" | grep -a "PERF .* fps over" | tail -3 | awk '
    match($0, /PERF ([0-9.]+) fps/, m)                            { fps += m[1] }
    match($0, /ours n=[0-9]+ mean=([0-9.]+)/, m)                  { om += m[1] }
    match($0, /ourDraw\(cpu submit\) n=[0-9]+ mean=([0-9.]+)/, m) { dm += m[1] }
    { n++ }
    END { if (n==0) print "0 0 0"; else printf "%.1f %.1f %.1f\n", fps/n, om/n, dm/n }'
}

echo "B1 per-layer cost table — $(date '+%Y-%m-%d %H:%M:%S')" | tee "$RESULTS"
echo "GPU: $(nvidia-smi --query-gpu=name,memory.total --format=csv,noheader)" | tee -a "$RESULTS"
echo "Baseline config: 1024x1024, ownShadows=1 (2x512), ownFlares=1, ownProbes=0, feedCount=1" | tee -a "$RESULTS"
echo "" | tee -a "$RESULTS"
printf '%-26s %9s %8s %8s %9s\n' "config" "feed_MiB" "fps" "ours_ms" "submit_ms" | tee -a "$RESULTS"

run_case() {  # run_case <label>
  touch "$MARKER"
  wait_for "FEED PAUSED by marker" 20 || true
  sleep 4
  local dormant; dormant=$(sample 5)

  rm -f "$MARKER"
  wait_for "FEED UNPAUSED" 20 || true
  sleep 12
  local mark; mark=$(wc -l < "$LOG")
  local active; active=$(sample 5)
  sleep 8
  local p; p=$(perf_since "$mark")

  printf '%-26s %9s %8s %8s %9s\n' "$1" "$((active - dormant))" \
     "$(echo "$p" | cut -d' ' -f1)" "$(echo "$p" | cut -d' ' -f2)" "$(echo "$p" | cut -d' ' -f3)" \
     | tee -a "$RESULTS"
}

# --- baseline ----------------------------------------------------------------
setkv wholeSceneOwnShadows 1; setkv wholeSceneOwnFlares 1; setkv wholeSceneCascadeResolution 512
run_case "baseline (all on)"

# --- one layer off at a time -------------------------------------------------
setkv wholeSceneOwnShadows 0
run_case "ownShadows=0"
setkv wholeSceneOwnShadows 1

setkv wholeSceneOwnFlares 0
run_case "ownFlares=0"
setkv wholeSceneOwnFlares 1

setkv wholeSceneCascadeResolution 256
run_case "cascades 2x256"
setkv wholeSceneCascadeResolution 512

# --- the cheapest safe combination -------------------------------------------
setkv wholeSceneOwnShadows 0; setkv wholeSceneOwnFlares 0
run_case "shadows+flares off"

# --- restore -----------------------------------------------------------------
setkv wholeSceneOwnShadows 1; setkv wholeSceneOwnFlares 1; setkv wholeSceneCascadeResolution 512
rm -f "$MARKER"
echo "" | tee -a "$RESULTS"
echo "restored baseline config" | tee -a "$RESULTS"
