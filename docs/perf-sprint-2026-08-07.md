# Perf sprint — 2026-08-07 (autonomous session)

Test bed: the user's valley save. Feed 0, manual camera FROZEN at 34953,-38102,-207519
(verified static across samples), looking across a valley of voxel mountains, grassy
meadows, trees, bushes and LOD foliage. Player seated in cockpit facing the panel,
game at 4K, RTX 5080. All soaks 90 s, game focused (the unfocused game throttles to
~14 fps and grades nothing — first thing verified).

Protocol: one knob at a time, restore to baseline between families, PERF lines
aggregated over exactly the soak window (`output/perf-sprint-results.csv`). Baseline
drift between windows is real (residency settling, heap): ±1-2 fps. Trust big deltas,
re-run anything inside the noise.

## The frame economics (measured first, before touching anything)

| condition | main fps | frame ms | notes |
|---|---|---|---|
| feed ON, baseline (1024x1024 SSAA, interval 0) | 41.2-43.6 | 23.0-24.2 | renders 43/s, delivered to panel ~21/s |
| feed OFF (feedsDisabled=1) | 53.9 | 18.5 | GPU 99% — the player's own 4K frame is the floor |

- **The feed costs ~5 ms/frame ≈ 12 fps** at baseline settings in this scene.
- The ceiling with a per-frame feed is **~54 fps**; 60 is above this scene's no-feed
  ceiling at 4K, so "as close to 60 as possible" = drive the feed's cost toward zero
  and optionally trim global costs.
- Delivery to the panel runs at ~21/s, NOT the 30/s the 33 ms panel gate implies: the
  gate is sampled on ~23 ms frame ticks, so it beats down to every-2nd-frame. The
  panel's real refresh is 21 Hz today. (Credit-based gate fix designed — see #25.)

## Finding 1: cutting render rate alone buys NOTHING (the O(gap) stall)

interval50 (renders 43/s -> 17/s, a 2.6x cut): main fps UNCHANGED (42.6 vs 43.6
fresh baseline, inside noise). Feed fps fell to 17 as expected.

Why: per-render CPU submit inflated **2.5 ms -> 17.8 ms** (7x) and render-frame wall
stretched 23.8 -> 37 ms. The nested Draw flushes GPU queues and joins the present
queue internally; at every-frame cadence that flush meets a drained queue (steady
pipelining), but a sparse render lands mid-player-frame against a FULL queue and the
render thread sits inside our Draw waiting for the drain. Aggregate submit cost
TRIPLED (108 -> 303 ms/s).

Implication chain:
- Render-on-demand (#25) pays only if the flush stall is fixed first.
- The existing `wholeSceneSubmitEarly=1` knob (record ours in Draw's PREFIX, GPU
  overlaps the player's CPU recording) is the designed counter — testing next.
- Transient-CB reclaim exonerated (bounded list swap, not O(gap)).

## Ladder results

| rung | main fps | feed fps | submit ms | verdict |
|---|---|---|---|---|
| baseline 1024² int0 (first) | 41.2 | 41.3 | 2.94 | reference; drifted to ~43.6 after the feed-off rebuild |
| feed OFF | 53.9 | — | — | the ceiling |
| interval50 (renders 17/s) | 42.6 | 17 | 17.74 (!) | ZERO gain — the O(gap) submit inflation eats it all |
| farclip 2500→1200 | 44.1 | 44.2 | 2.97 | zero cost — planet bodies are VeryFarClipping-exempt, so this scene has nothing in the cut band. Mountains still render (screenshot-verified) |
| submitEarly=1, int0 | 44.0 | 43.6 | 3.64 | neutral at every-frame cadence |
| submitEarly=1 + interval50 | **38.3** | 18.1 | 13.32 | WORSE — the early position aggravates sparse renders; O(gap) inflation is position-independent |
| grass 1000→300 (feed only) | 44.1 | 44.4 | 2.98 | zero cost — grass is GPU-light at 1024² |

**Reading so far**: every content-distance knob is ~free in this scene, and cadence
cuts are self-defeating until the O(gap) inflation is understood. The feed's ~5 ms is
FIXED pipeline overhead per render — the stage table (built this session, deploys at
the next restart) will name the stage. The one untested big lever is resolution
(pixels ÷4 at 512²) — gate-cycling to it now. Flora/viewer rungs skipped by
inference from the grass/farclip nulls (same family; viewer radius is a VRAM/quality
knob more than an fps knob).

New suspect for the O(gap) inflation, to check against the stage table: stage 2's
probe manager re-rendering ACCUMULATED dirty probe faces per sparse pass (a probe face
is a mini scene render — exactly submit-shaped cost that scales with elapsed frames).

## THE HEADLINE NULL: resolution

| rung | main fps | ours ms | verdict |
|---|---|---|---|
| res 1024→512 (gate cycle, ¼ the pixels) | 43.6-44.1 | 22.7-23.9 | **IDENTICAL to 1024** |

The feed's cost is RESOLUTION-INDEPENDENT. With every content knob also null, the
~5 ms/frame is fixed per-render pipeline overhead — the nested Draw's queue
flush/present-join plus per-stage fixed costs, × 43 renders/s. The GPU pixel work of
a 1024² scene was never the bill. Restored to 1024 (512 pays quality for nothing:
visible alpha-test speckle on foliage, screenshot in the session scratchpad).

Observed during the cycles: the [RTS] stats panel lost its content after repeated
quiesced rebuilds (feed panel unaffected) — the known re-bind fragility family (#26/#31
neighborhood), restart restores it. Also: EVERY config-file save appears to trigger a
quiesced rebuild ("slot being retired by a count change" at 17:52:50 and 17:57:51,
matching doc-comment-only edits) — harmless but worth knowing when reading logs: config
edits are not free of side effects even when no live knob changed. Worth a look in
FeedConfig's signature hash someday.

## Where this leaves the 60 fps goal

frame = player's 18.5 ms (GPU-bound at 4K, not ours) + feed fixed overhead ~5 ms.
The path: (1) name the fixed overhead by stage (instrumented build, next), (2) kill or
amortize the top stages, (3) fix the O(gap) inflation so render-on-demand pays, then
renders track the panel's 30 Hz and the amortized cost roughly halves. Speculative
budget if both land: ~48-50 fps main with the feed visually unchanged; past that means
trimming the player's own frame or accepting sub-30 Hz feed refresh presets.
