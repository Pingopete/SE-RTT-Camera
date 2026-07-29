# Roadmap

Agreed goals beyond the POC, recorded 2026-07-28. The POC itself is achieved: a stable
30fps whole-scene render on an `[RTC]` panel with RT ambient, own shadow cascades,
planet atmospheres, and no main-world bleed.

## 1. In-game control panel (LCD UI menu)

Many of the render-layer options and the frame-rate cap should be controllable from an
on-LCD UI menu in game, not from feed-config.txt. Candidate controls, all already
live-tunable through the config layer they would sit on top of:

- feed frame rate (`wholeSceneIntervalMs` — hard floor removed, 0 = every frame)
- render layers: shadows (own/shared/off, cascade count + resolution), RT GI, bloom,
  volumetrics, decals, atmosphere, far clip
- orbit parameters (radius, period, height)
- resolution

The config file remains the persistence layer; the UI writes it. The 2s poll and the
rebuild-signature machinery already make every change safe to apply live.

## 2. Multiple camera feeds

Make the mod stable and modular enough to support several simultaneous feeds. Known
work this implies:

- Per-feed instances of what is currently static: ScreenBuffers, DrawContextManager,
  cascade set, LDR ring, panel binding, orbit state. The gate/teardown machinery
  (FeedGate) becomes per-feed with a shared render-thread pump.
- One `SceneDrawSystem.Draw` postfix dispatching N feeds round-robin — NOT N hooks.
- Panel discovery generalised from "first [RTC] panel" to a registry.

## 3. Automatic feed frame-rate management (the frame budget)

The core idea, verbatim intent: an adjustable target frame cost for RTT rendering as a
whole, defaulting to the cost of one 30fps feed. Each additional ACTIVE, ON-SCREEN feed
takes a fraction of that budget rather than adding on top, so the main world's frame
rate stays stable no matter how many feeds run.

Sketch on top of the existing gate:

- `rttBudgetMs` (config + UI): total ours-frame milliseconds allowed per second,
  default ≈ one feed at 30fps × ~28ms ≈ 840ms/s.
- Perf.cs already measures per-render cost; divide budget by measured cost to get total
  renders/sec, split across feeds (equal shares first; priority weights later).
- Off-screen / occluded / panel-off feeds cost zero (the gate already proves dormancy
  works); their share returns to the pool.
- The per-feed interval becomes derived state, not a user knob, once the budget exists.

## Perf backlog (minimal visual cost, in order)

1. Cascade resolution 1024 -> 512 for the feed (config-only; at a 512 output the shadow
   texel density is still ~1:1 — near-imperceptible).
2. Far-clip tuning below 4000 m (user-verified lossless at 4000; find the floor).
3. Amortised cascades: render the feed's shadow cascades every Nth feed frame (own
   resources persist between renders; cost ~66ms shadow latency in the feed).
4. Cascade count 3 -> 2 (mild far-shadow softening in the feed only).
5. THE BIG ONE, parked as risky: move our render AFTER present, so our GPU work
   overlaps the next frame's CPU record instead of delaying the swap. Recovers most of
   the ours-frame cost from the main world. Bootstrap change; frame-span lifetime at
   present is the hazard; needs its own careful session.

## Addendum 2026-07-28: the session drift, characterised

Controlled experiment (90s sampling of the engine's live Stats log + a panel-off
dormant phase) on a ~50-minute session:

| phase | gpuFrame | gpuWork | verdict |
|---|---|---|---|
| feed on, aged | 29-30.6ms | ~19.3ms flat | ~10ms GPU idle per frame |
| feed OFF | 16.6ms | 16.5ms | bubbles vanish entirely |
| feed back on | p50 lands at 48 | — | teardown does NOT reset the aging |

Conclusions: our render's true GPU work is ~3ms; its apparent cost is dominated by
induced pipeline gaps; the gaps grow with ENGINE session age and only a game restart
resets them; GC stalls accrue equally while dormant (exonerated as driver).

Consequence: backlog item 5 (submit the feed render at start-of-frame, outside the
mid-frame serial position) is promoted from optimisation to THE fix for the drift.
It hides the induced gaps under the next frame's ~15ms CPU record window. Risk
unchanged (frame-span lifetime across the present boundary); deserves a fresh
session with crash patience.

## Design note for start-of-frame-submit (parked ready, 2026-07-28)

The refined position: a PREFIX on SceneDrawSystem.Draw — NOT post-present.

    postfix (today):  [prep][player record][OUR record][present copy]
                       GPU: player -> OURS -> present   (present waits for us)
    prefix (the fix): [prep][OUR record][player record][present copy]
                       GPU: OURS runs while CPU records the player
                       (present waits for the player only; our gaps hide
                        under the player's ~15ms record window)

Why prefix beats post-present: at the prefix moment the engine has finished ALL
frame prep (transient CBs, descriptor tiles, the OnBeginDraw chain) — everything
our nested Draw consumes is valid, same frame, same span. Post-present crosses
the frame-end boundary where spans close and transients recycle — the
device-removal family. Same overlap benefit, a fraction of the risk.

Implementation sketch (bootstrap + logic, restart to adopt):
- RttBridge.WholeSceneEarlyHook; bootstrap adds a prefix to the SAME Draw patch
  site firing it (re-entrancy guarded — our nested Draw triggers it too).
- Logic: config flag wholeSceneSubmitEarly routes WHICH hook runs
  RunSecondRender; the postfix keeps gate/Poll/Perf bookkeeping either way.
  The flag is outside the rebuild signature -> flipping it is a live A/B.

## Addendum 2026-07-29: start-of-frame submission LIVE — first evidence

Adopted on a fresh boot (one unrelated world-load crash in the game's replication
layer first — KeyNotFoundException on EntityObjectBuilder, no render involvement).
Flipped live under the pause protocol. Immediate result, first sample:

    end-of-frame (last night, feed on):  gpuFrame ~29-30ms, gpuWork ~19.3ms  -> ~10ms bubbles
    START-of-frame (now, feed on):       gpuFrame 20.40ms,  gpuWork 20.20ms -> 0.2ms

The frame-vs-work gap with the feed RUNNING now matches what previously required
the feed to be OFF. p50 unchanged vs baseline on a fresh session (~27-29ms), as
predicted — the verdict metric is the SLOPE over session age (last night:
+0.7ms/min to p50 48 at ~50min). Slope watch armed.

## Addendum 2026-07-29b: start-of-frame submission is a NULL RESULT

Paired A/B at CONSTANT session age (~17 min), which is the only valid comparison:

| position | fps | ours p50 | ours p95 |
|---|---|---|---|
| START-of-frame | 41.6 | 29.6 | 49.3 |
| END-of-frame   | 41.6 | 30.7-31.1 | 49.4-51.1 |

Identical. The submission position does not affect the drift.

Why the theory was wrong: the GPU is ONE serial queue, so moving our submission
within a frame cannot take it off that frame's critical path to present. The
overlap only ever hides work while it fits under the CPU record window, and it
does not address the growth at all.

Methodology lesson: the first "success" reading compared the new position at
minute 13 against the old at minute 2 — confounded by the very drift being
measured. And p50 was picked as the verdict metric in advance while the user's
felt experience tracked p95 (34.5 -> 49.3) and total fps (53 -> 42). Choppiness
lives in the TAIL. Grade tails, and A/B at equal session age.

Kept anyway: the prefix hook, the shared TryRender() and the live flag are a
permanent capability (a zero-cost A/B switch, and the submission-scheduling point
multi-feed budgeting needs). Default is 0 = end-of-frame, the proven position.

## The drift, localised (2026-07-29)

| | fresh (min 2) | aged (min 19) |
|---|---|---|
| our CPU submit | 15.6ms | 13.1ms (flat/down) |
| idle frames | 11.2ms | 8.2ms (FASTER) |
| ours frames | 26.6ms | 35.8ms (+9ms) |

So it is NOT CPU submit, and NOT the player's own work — which improves. It is a
GPU-side wait attached specifically to OUR frames.

LEADING HYPOTHESIS (one sample, unproven): texture residency. VRAM at 89.1% with
"Missing: 1.14Gi" in RenderSceneStats. The orbit camera's working set differs from
the player's first-person view, so as the session streams the world in, OUR set is
what gets evicted, and each of our renders stalls re-fetching over PCIe. Fits every
observation including why the player's frames get faster (their set stays cached)
and why only a process restart resets it.

Next tests: (a) does "Missing" grow with session age — sample it over an hour;
(b) does shrinking our working set help — a texture MIP/LOD bias for our render
only (note SamplerManager.GetSamplerLODBias in DrawInternal, and the deleted
lodMainView knob's history) would demand smaller mips and overlap better with what
is already resident. (b) is the candidate real fix if (a) confirms.

## Addendum 2026-07-29c: the LDR resize is NOT the drift, and the knee is reproducible

The ghost fix was suspected because a >50ms step coincided with its deploy. Tested
by A/B at constant session age (~22 min):

| resize | fps | p50 | p95 | >50ms |
|---|---|---|---|---|
| ON (ghost fixed) | 38.8-39.4 | 33.7-34.5 | 53-54 | 28-30 |
| OFF (ghost back) | 38.5-38.8 | 33.6-34.1 | 54-55 | 35-36 |

Identical, marginally worse without it. Exonerated; kept on.

The coincidence explained: the >50ms count has a KNEE at ~16-20 minutes of session
age, and it reproduces across two different builds on two different boots:

    2026-07-28 boot 22:11 -> 16min: >50ms 5-9    22min: 28-34
    2026-07-29 boot 17:44 -> 13min: >50ms 2-4    16min: 28-30   22min: 35-36

Same age, same knee, different code. That is strong evidence the drift is
ENVIRONMENTAL (VRAM/streaming state) rather than anything we deploy — and it is
why deploy-time correlations have been misleading all along. Always A/B at equal
session age.

Three suspects now eliminated by controlled test: submission position, the LDR
resize, and (last night) our own resource teardown. The residency hypothesis
stands untested.

## Addendum 2026-07-29d: the "session drift" is LOAD-dependent, not time-dependent

Controlled run: lower texture settings (VRAM 52% instead of 89%) AND the player
holding a fixed position looking at the test panel.

    18:17 -> 18:27  stationary   65 fps  p50 24-26  >50ms 0  gap 1-2ms
    18:29           moved        45 fps  p50 27.7   >50ms 2

TWELVE MINUTES FLAT — no degradation whatsoever, the best sustained window ever
measured on this route. The step down coincides exactly with the player moving for
the first time.

This retracts the time-decay characterisation in addendum 2026-07-28. Every earlier
measurement was taken while playing/moving (I asked for "play normally"), so what
read as session aging was most likely SCENE LOAD — what is in view from the
player's position and the orbit camera's. It appeared monotonic and irreversible
because exploration moves you into denser surroundings and you stay there; a
process restart "fixed" it because it returns you to the spawn point.

Also settled: "Missing" in RenderSceneStats is a CONSTANT (~1.13Gi at both 52% and
89% VRAM) — it is unfetchable/absent assets, not evictions. Useless as a residency
gauge; do not use it as one.

Caveat on attribution: two variables changed at once (texture settings AND player
movement), so the improvement cannot yet be split between them. The flat 12 minutes
is solid; the cause is not.

Next test, to separate them: stationary with the ORIGINAL texture settings. If it
is also flat, texture settings were incidental and position is everything. If it
drifts, VRAM headroom matters after all.

Methodology rules earned the hard way, now three deep:
- A/B only at equal session age.
- Grade the TAIL (p95, >50ms), not p50 — the user feels the tail.
- CONTROL PLAYER POSITION. An uncontrolled camera makes scene load masquerade as
  time-based decay, which cost a whole night's theorising and one built-and-
  disproved architecture change.

## THE BREAKTHROUGH 2026-07-29: the RATE LIMIT was the bug

Setting wholeSceneIntervalMs = 0 (render the feed EVERY engine frame) three-and-a-half
times the feed rate AND raises world fps at the same time, with the stutter gone:

| interval | world fps | feed fps | frame p50 | p95 | max | >50ms |
|---|---|---|---|---|---|---|
| 33ms (30fps target) | 43 | 24 | 29.5 | 47.9 | 51.5 | 1-2 |
| **0 (every frame)** | **72** | **72** | **13.6** | **16.9** | **19.1** | **0** |

Reproduced across three consecutive clean 5s windows, while the player MOVED.
IDLE n=0 — there is no longer any such thing as a cheap frame, because every frame
does the same work, so there is nothing left to alternate between.

### Why throttling was expensive

Every render at a 33ms gap paid a COLD-START tax: temporal history stale (GI
reprojection, ReSTIR reservoirs, denoiser accumulation all rebuilding from nothing),
caches and resources evicted between renders. Rendering every frame keeps all of it
warm and each render costs 13.6ms instead of 29.5ms. The route was never slow — the
rate gate was forcing it down its own worst-case path, and the harder we throttled
the more we paid per render.

### What this retracts

- "Our true GPU work is ~3ms of a ~30ms ours-frame" — that measured a COLD render.
- The "session drift" — largely the cold-start tax growing as the scene got heavier,
  which is why it tracked scene load and never touched the player's own frames.
- The entire start-of-frame-submission investigation was built on the cold-render
  cost model. (Null result anyway; kept as a live A/B switch and the scheduling hook.)
- The bimodal "choppiness" framing from the very start of this work: the fix was
  never to make the expensive frames cheaper, it was to stop having two kinds of frame.

### Consequence for the roadmap

Multi-feed budgeting (goal 3) needs rethinking from the ground up. The assumption was
that each feed costs a fixed slice, so N feeds must share a budget by throttling. But
throttling is what makes a render expensive — so the naive budget would make every
feed pay the cold-start tax. Any scheduler must keep each feed's temporal state warm,
or amortise differently (e.g. round-robin at full rate rather than rate-limiting all
feeds). Measure before designing.
