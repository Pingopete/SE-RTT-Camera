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
| 0 (every frame) — RETRACTED, frozen panel | 72 | 72 | 13.6 | 16.9 | 19.1 | 0 |
| **0 (every frame) — verified LIVE panel** | **66** | **66** | **15.2** | **18.0** | **20.4-25.5** | **0** |

CORRECTION: the 72 fps row was measured with a FROZEN PANEL and must not be quoted.
The gate was cycled to restore a confirmed-live feed and the result held, at 66 fps
with ourDraw (CPU submit) at 2.1ms. Use the live row. Verify the panel is live before
trusting any number — the frozen reading looked like the better result.

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

## 4. Fidelity restoration — four layers, easiest first (agreed 2026-07-29)

User-agreed goals. Work them in this order; each is independently shippable and
independently revertible. Ordering is by implementation difficulty, NOT by value —
the most valuable one (probes) is deliberately last because it needs the most groundwork.

Every one of these changes WHICH ENGINE CODE RUNS, so every one is subject to Rule 14 and
Rule 17: pause the feed first, even the config-only ones.

### 4.1 Atmosphere LUTs — config-only (skip id 24)

**Change:** remove `24` from `wholeSceneSkipStages`.
**Adds:** the feed computes its own atmosphere scattering tables — sky colour, haze, the
glow at a planet's limb — instead of inheriting whatever the player's frame last computed.
**Cost:** small, a few compute dispatches on small LUTs.
**Expected visual gain right now: near zero**, because the orbit camera sits ~100 m from the
player, so the player's tables are already essentially correct for it. The gain is real only
once cameras are FAR from the player — different altitude, different planet, opposite side of
a world — which is the actual end goal.
**Risk:** `AtmosphereLUTJob` writes the SHARED per-planet `AtmosphereLUTTables` on
`CommonResourcesManager`, from our camera. That is why it was skipped. It ran without any
observed problem before being skipped, and it was skipped preventively during the ghost hunt
— which then turned out to be `FinalLDRTexture`, not this.
**Test:** enable, then watch the PLAYER'S sky and planet limbs, not the feed. A wrong shared
LUT shows up in the world, not in the camera.
**Why first:** one number in a config file, a known-clean prior run, and an unambiguous
failure signature.

### 4.2 Anti-aliasing — FXAA for the feed

**Why it matters:** the feed currently gets **no AA at all**, which at 512x512 is the single
largest source of visible aliasing. `UpscaleTargetFSR` early-outs (our target resolution
equals our ScreenBuffers resolution, and skip id 20 forces `IsFSREnabledAndAllowed` false),
and `AAMode` is left at the player's FSR, so `ApplyNonFSRUpscalingAndAA` self-gates to
nothing as well.
**Change:** scope `DRSSettings.AAMode = 1` (FXAA) for the duration of our render. FXAA is
spatial-only and needs no motion vectors, which is the right AA for a camera whose temporal
history is weak.
**Cost:** one fullscreen pass at 512x512. Sub-millisecond; will not show up in PERF.

**Verified offline already (`tools/EngineQuery`):**

- `FXAAJob` is constructed once in `SceneDrawSystem`'s ctor and its only initialisation is
  that ctor's `InitializeAsync` (PSO compile). **It does NOT depend on
  `UpsamplingJob.PrepareResources`** — so skipping stage 19 cannot starve it. This was the
  main safety worry and it is answered.
- The known crash mechanism behind `AAMode` — `PrepareResources` allocating one branch while
  disposing the other — **cannot fire**, because stage 19 already skips `PrepareResources`
  inside our render. This is what makes the retry informed rather than blind.

**Watch item:** `ApplyNonFSRUpscalingAndAA` borrows a `RenderTargetTexture` at
**`SwapChain.Resolution`** — the player's 4K, not ours. It is a pool borrow (the designed,
cheap path, not the dangerous Dispose+Create shape), but a 4K intermediate inside our render
is exactly the family that produced the phantom bleed. Log its live resolution before
trusting it, per Rule 20.
**History to respect:** three CTDs sit behind this knob. Pause protocol, one change, verify.

#### ATTEMPT 1 FAILED — CTD number FOUR behind this knob (2026-07-29)

`wholeSceneAAMode = 1` crashed the game ~2 s after the first feed render. Config reverted to
`-1`. **Do not retry as-is.**

DRED breadcrumb, and it is unambiguous:

```
EventStack:    [Upsampling, Post Passes, ForwardAndPostPasses]
CompletedOps:  ... Beginevent (Upsampling), Resourcebarrier, Resourcebarrier]
OutstandingOps:[Dispatch, ... Endevent (Upsampling), Beginevent (FXAA), ...]
PageFaultVA: 0x0   ExistingAllocations: 0   RecentFreedAllocations: 0
```

It died on the first `Dispatch` inside **Upsampling** — before FXAA was ever reached. Zero
existing AND zero freed allocations with `PageFaultVA 0x0` is a **NULL BIND**: something was
bound that had never been allocated.

**The mechanism, now complete.** `ApplyNonFSRUpscalingAndAA` calls `UpsamplingJob.DoWork`
FIRST, and only afterwards checks `AAMode == 1` to run FXAA. `DoWork` selects its branch from
`AAMode`:

- `AAMode == FSR` (the player's value, i.e. today) -> dispatches **FSR3**, whose resources
  the player's frame allocated in `PrepareResources`. Safe, which is why the current build
  works.
- `AAMode == FXAA or None` (what we scoped) -> dispatches **Bilinear**, whose resources
  **nobody ever allocated** — because we skip `PrepareResources` (stage 19) and the player's
  frame *disposed* bilinear when it prepared FSR. Null bind. Device removed.

**This is the vice the stage-19 comment already described, reached from the other side.**
Skipping `PrepareResources` is unsafe if we change `AAMode`; un-skipping it is unsafe because
it would dispose the player's FSR3 and re-prepare bilinear at our resolution. Both doors are
locked, and `AAMode` is the handle on both.

**Second finding, and it matters more broadly:** the Upsampling block did NOT gate itself out
for our render, though I asserted it would. Its gate is a resolution comparison OR'd with
`IsFSREnabledAndAllowed` — and **skip id 20, which forces that flag false, is part of what
opens the bilinear door.** The two existing workarounds (19 and 20) are only mutually safe
while `AAMode` stays at the player's value. Worth re-examining independently of AA: our
`finalLDR.Resolution` and our `ScreenBuffers.PreUpscaleResolution` apparently differ, which
is probably the same `MaxResolution == 3840` residue the phantom-bleed fix left behind, and
the `!!! FinalLDR resolution moved` tripwire that fires on every gate cycle.

**My analysis error, recorded so it is not repeated.** I verified that `FXAAJob` does not
depend on `UpsamplingJob.PrepareResources` — true, and irrelevant. I never checked the call
that runs *before* FXAA in the same method, even though `UpsamplingJob.DoWork` was sitting in
the callee list I had already printed. **Reading a callee list is not the same as reading it
in order.** The crash was fully predictable from evidence already on screen.

**Attempt 2, the clean route (unbuilt):** add a skip stage for `UpsamplingJob.DoWork` so the
upsampling block never executes during our render, leaving FXAA to run after it. Check first
whether `DoWork` has out-params or writes a target its callers consume — if it does, this is
the stage-4 problem and needs an override rather than a skip. Do that check offline, on a
closed game, before touching the config again.

### 4.3 Lens flares — needs plumbing (skip id 21)

**Adds:** sun and light glare in the feed. Worth having for a camera that can point near a
star.
**Cost:** near zero — a small additive pass.
**Blocker, and it is not performance:** our own `FlaresContext` is **empty**. Every light
registers its flare through the global `CoreSystems.DrawContexts.LensFlares`
(`PointLightEntityComponent.Init` / `SetParameters` / `OnRemovedFromScene`, the spot and
particle equivalents, `SceneManager.UpdateFlareDefinitions`), and the global is the engine's
whenever a light is actually created. So simply unskipping the pass renders nothing.
Meanwhile running the pass against the SHARED context is worse than nothing:
`RenderFlares` calls `ProcessFinishedFrame` and `PrepareReadback`, which advance the flare
OCCLUSION readback across frames — doing that twice per frame against one context corrupts
the player's flare occlusion. That combination is the confirmed cause of the old "planet's
atmosphere appears, completely unattached to the planet" bug.
**Two possible approaches, both unbuilt:**
1. Mirror flare definitions from the engine's context into ours each frame, keep our own
   readback state. Read-only against the engine's data.
2. Own the registration path (patch the three registration sites to write both contexts).
   More invasive; more likely to leak.
**Do 4.1 and 4.2 first** — this one needs a design pass, not just a config edit.

### 4.4 Environment probes — hardest, and the most important long-term (skip id 2)

**Why the user wants it, and the reasoning is right:** this is a REMOTE camera. The feed
currently samples the player's probe atlas, which is centred on the PLAYER — so its
reflections and ambient bounce are correct for where the player is standing, not where the
camera is. At 100 m that is subtle. At real remote-camera distances it will be visibly
wrong, and it will get worse the further the camera goes. This is a correctness problem
disguised as a fidelity problem.
**Cost: the highest on the list by a wide margin.** A probe render is six cube faces of
scene geometry — which is precisely why the engine amortises it round-robin across frames
rather than doing it in one go. Own probes could cost a multiple of our current render.
**Blockers:**
- `RenderEnvironmentProbe` iterates `DrawContexts.EnvProbesToUpdate`, and OUR
  `DrawContextManager`'s copy is never populated: it is filled in
  `DrawContextManager.OnBeginDraw`, which runs in `DrawInternal` on the ENGINE'S context,
  before `Draw`. So unskipping stage 2 today iterates an empty buffer and does nothing
  useful, while still running `EnvironmentProbeExposureJob` against the shared `CloseIBL`.
- Filling our own queue means driving `EnvironmentProbeManager.PrepareProbes()` — a method
  on a GLOBAL manager, which is Rule 8 territory, and which also stores `_lastSettings`,
  `_forceReprocess` and `_state` and can `DisposeTextures` + `RecreateProbes`.
- **Records contradict each other and must be reconciled first.** The stage-27 dossier in
  `RttPlugin.cs` calls `PrepareProbes` a CONFIRMED device removal at
  `wholeSceneIntervalMs=33`, with a full DRED breadcrumb. It was LATER proven that stage 27
  never executes at all (`OnBeginDraw` is not reached from `Draw`). Both cannot be true.
  Resolve that before designing anything here.
- Amortisation is mandatory, not optional: at minimum one face per feed render, and the
  round-robin must be fitted to the orbit camera.
**Also note:** Rule 19 — creating our own probe resources trips `_forceReprocess` and a mass
reprocess, and rendering into that batch is a device removal. The 30-frame settle window
exists because of exactly this.

## Hypothesis 2026-07-29: WHY the GPU starvation is gone

Status: **hypothesis, not proven.** Two candidate causes are confounded (see the caveat
at the end). Written down because the starvation was the most serious bug this project
had — Task Manager GPU dropping to 65-70% with the feed on, and the feed bleeding fps the
longer a session ran — and after ~15 minutes on the every-frame build it is not
reproducible. Identifying the mechanism matters more than the fix, because multiple feeds
will re-run whatever the mechanism was.

### The observation that constrains everything

**CPU submit fell 13-15 ms to 2.1 ms while the amount of rendering work stayed
identical.** Same stages, same resolution, same scene, same skip list. The only variable
was cadence.

So those missing 11-13 ms were never draw-call recording. They were WAITING. And a CPU
thread that is waiting is not feeding the GPU — which is precisely what a GPU sitting at
70% looks like from outside.

This also settles the older measurement that nobody could explain: `gpuWork` was FLAT at
~19.3 ms while frame time climbed. The growth was pure GPU **idle**. Bubbles, not
workload. (It also retracts the "our true GPU work is only ~3 ms" figure — that measured
a cold render, and the ~30 ms it appeared to cost was mostly the wait, not the work.)

### The proposed mechanism: resource-churn stalls

A throttled render must re-acquire, on every wake, everything that was returned to a pool
or evicted during the gap — pooled textures, descriptor state, resource-state transitions.
**Freeing and reallocating GPU memory makes the driver wait for outstanding work to drain.
That is a fence wait, and a fence wait is a bubble by definition.**

`CloudJob` was the extreme, measured case: dispose + recreate a multi-hundred-MB resource
20x/sec, visible as the +/-151 MB VRAM oscillation in every PERF line. Stage 26 removed
that one specifically. But the general shape was never addressed, only that instance of it.

Render every frame and none of it happens. Resources stay borrowed, resident and hot;
there is nothing to reacquire and nothing to wait on.

### Why this explains what every previous model failed to explain

The drift tracked **scene load, not session time.** As VRAM filled, the probability that a
returned resource had been evicted rather than kept went up, so the re-acquire cost grew.
That accounts for all three of the failures:

- **Time-decay model failed** — the user was stationary and dead flat for 12 minutes; the
  step coincided with movement, i.e. with new scene content streaming in.
- **A full teardown of everything we own did NOT reset it, but a game restart DID** — the
  aged state was allocator/residency state, which our teardown does not touch.
- **The player's own frames were never affected in the early phase** — they were not the
  ones doing the reacquiring.

### The confound, stated plainly

In-game graphics settings were lowered at roughly the same time as `intervalMs` went to 0,
**Texture Quality to Low especially**, which independently cuts VRAM pressure and therefore
eviction probability. The available data cannot split the two. The earliest drift work
(memory Rule 10) had already found VRAM residency thrashing at +/-442 MB evict/reload per
5 s presenting exactly as a rendering bug, so this is a live second candidate, not a
formality.

### The experiment that splits it

Raise Texture Quality back up; keep `wholeSceneIntervalMs = 0`; hold player position; run
long enough for scene load to build.

- Drift stays away -> the rate limit was the whole story.
- Drift returns -> VRAM pressure is a real second factor, and it must be designed for
  before multiple feeds run, because N feeds multiply resident footprint.

Grade the TAIL (p95, >50 ms) and total fps, not p50 — p50 hid this the first time.
