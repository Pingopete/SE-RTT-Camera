# Implementation plan — phase 2 route

Written 2026-07-29. This is THE ordered route; design rationale lives in
`docs/phase2-design.md`, goals in `docs/roadmap.md`, layer facts in
`docs/feed-render-layers.md`. Each phase has an exit gate; nothing advances past a gate
that is not green. Standing discipline throughout: pause protocol for anything that
changes which engine code runs, one variable at a time, grade the tail not the mean,
watchdog running between work blocks, and pin a reference build at every phase exit.

**The settled design this route builds toward (decided 2026-07-29):**

- Fixed total budget: ALL feeds combined never exceed one warm feed's cost at current
  settings. Render credits, ms-metered; feed fps divides by N emergently.
- Quality is GLOBAL across feeds — it is the user's VRAM/resource throttle. Per-feed
  properties are priority weighting and visibility only.
- One render per unique CAMERA; panels are free (shared 1024x1024 source, per-panel
  aspect CROP in the existing blit).
- Two constants close the system: submit-ms per frame, max resident feeds (VRAM cap).
- End state: an RTT Feed API other mods drive (CreateFeed/DestroyFeed, caller-driven
  camera, liveness contract, graceful cut ALWAYS), consumed by a separate camera-block
  mod.

---

## Phase A — instruments and quick wins (current build; 1-2 sessions)

The measuring tools everything later depends on, plus experiments that cost seconds.

| # | item | test / exit evidence |
|---|---|---|
| A1 | **Stats panel v1** — `[RTS]` tag scan, font survey (3 routes, one-shot log), persistent batch with ~6 DrawString lines re-recorded every 500ms. Include the **budget tripwire** from day one: a line showing rolling p50 submit vs `rttBudgetMsPerFrame` (the measured reference constant), flagged when >20% over for a minute | stats visible in game; feed panel untouched; 3 teardown cycles clean. THIS IS THE INSTANCING PATHFINDER — it forces target/binding/batch out of single-panel statics on a surface where failure = broken numbers, not broken feed |
| A2 | **`intervalMs` out of the rebuild signature** (trivially class (a)) | live change 0->33->0 with NO gate cycle in the log; rate follows within one poll |
| A3 | **Ten-second experiments**: (i) FSR smear discriminator — player AA off FSR, look at distant panel; (ii) visibility dormancy — walk the panel out of view, watch for DORMANT; (iii) `ownProbes` live flip both directions | each answered with one log line + user observation; findings recorded |
| A4 | ~~**Class (a) knob A/B sweep**~~ — **CANCELLED 2026-07-30**, see below | — |

Exit gate: perf numbers readable in game. **MET** (A1, A2 done; A3 partially, A4 cancelled).

**A4 CANCELLED, and the reasoning is worth keeping.** A3(iii) — the `ownProbes` live flip — cost
THREE device removals in a row. A4 was the same activity generalised to every knob, so its
expected cost was "several more crashes" and its entire payoff was that a preset change could be
instant rather than costing a ~2 s gate cycle. That is a UX detail bought with device removals on
the user's machine, and one of the earlier CTDs in this family did damage outside the game.

The trade is simply bad, so preset changes are now ALLOWED to cost a gate cycle — which the phase
E3 design already tolerates (staggered rebuilds, one feed per settle window). Any knob is
"live-switchable" only if it is already out of `WholeSceneSignature()`; nothing new gets promoted
by experiment. `wholeSceneOwnProbes` is classified **restart/gate-cycle**, permanently.

**A3 outcomes:**

- (iii) `ownProbes` live flip — **answered, expensively.** Not live-flippable. Three crashes, one
  real bug found and fixed along the way (Reset() running INSIDE our render, nulling the statics
  every `finally` block needs — latent for as long as `Poll()` has been called from there, and it
  would have bitten every restore path eventually, not just probes). Manager is now KEPT; knob is
  out of the rebuild signature.
- (i) FSR smear discriminator — **overtaken by events. NOT RUN, and does not need to be.** The
  user reports the star smear is GONE. Three candidate fixes landed between the last sighting and
  the report — the panel mip-chain fix, the FSR reactive mask, and own probes — and no discriminator
  was run between them, so **the cause is unattributed and this is recorded as an observation, not
  a diagnosis.** It cannot be re-run cheaply now that the symptom is absent. Phase F6 ("act on the
  A3(i) finding") is therefore reduced to: watch for recurrence, and if it returns, bisect those
  three. Claiming a specific fix for it would be exactly the Rule-26 mistake.
- (ii) visibility dormancy — not run, still cheap, folded into phase F1 where it belongs.

## Phase B — presets and cheap wins (1 session)

| # | item | test / exit evidence |
|---|---|---|
| B1 | **Per-layer cost table** — flip one layer at a time, read the delta off the stats panel | table in docs: layer -> submit/GPU/VRAM cost |
| B2 | **Define low/med/high presets** from B1 + `feed-render-layers.md`; global `feedQuality` knob selects; class (c) members documented as "costs a gate cycle" | preset switch live for class (a) members; visual + cost verified per preset |
| B3 | Cheap fidelity/correctness items as time allows: far-clip floor sweep below 2500; **stages 22/23 skip test** (removes the last shared world-space writes AND saves submit); reconcile the stage-19 comment/config contradiction in docs | each: one change, one soak, one verdict |

Exit gate: three presets switch live; their costs are measured numbers.

## Phase C — per-feed instancing (the big refactor; 2-3 sessions)

The enabling work for everything after it. A1 has already rehearsed the shape.

| # | item | test / exit evidence |
|---|---|---|
| C1a | **Inventory the statics** (ScreenBuffers, DrawContextManager, cascade set, probe manager, flares mirror + originals, LDR ring, orbit/camera state, panel binding, gate state) -> `FeedInstance`. **DONE 2026-07-30** | compiles; instance count = 1 |
| C2 | **Single-instance parity** — one FeedInstance must equal the current build | PERF within noise of the **pre-C1a build at the SAME config** (see below); same visuals; 3 teardown cycles; 15-min soak. Pin as reference |
| C1b | **The render-thread pump** — `Feeds.Cur` becomes a per-thread ambient the pump sets around each feed's work; teardown per instance under Rule-25 (dispose only what the instance allocated) | pump selects instance 0 explicitly; parity holds a second time |
| C3 | **Second unique feed** — second tagged panel, second camera (offset orbit), simple alternator (A on even frames, B on odd) as the placeholder scheduler | both feeds live and correct; destroy/power-off panel A -> feed B unaffected; teardown matrix per feed; 15-min two-feed soak |

Exit gate: two independent feeds, independently killable, no cross-contamination.

### C3 postmortem 2026-07-30: BOTH two-feed failures were bugs, not limits

The first two-feed run produced a black second panel and, half an hour later, a device
removal. Both were read at the time as "two feeds at 1024 do not fit in VRAM". **Neither
was.** Two feeds cost **+580 MB** and ran at 12.78 GB against a 13.61 GB budget — roughly
850 MB of headroom, stable, 45-47 fps, `>50ms=0` for two minutes.

**Bug 1 — the black panel: a transient failure latched permanently.**
`CameraRender.CopyToFeed` set `_feedState = -1` on the FIRST view-lookup failure, with no
retry. A feed's camera pass legitimately runs before its own whole-scene render has produced
a frame, so `WholeSceneRender.PanelSource` is null for the first second of that feed's life.
Feed 1 failed that test once at 19:21:00.565, its first render landed at 19:21:01.639, and it
then rendered 291 more frames into a panel that never received one — `park#0 copies=0` for
its entire life while feed 0 sat at `park#290`.

`ResolveFeedTexture` already carried a comment describing this exact bug (a startup-ordering
race turned into a permanent disable by a retry gate that only fires at 0, never at -1). The
same mistake sat forty lines away in the same file and was not caught, because **at one feed
the failure window is usually shorter than the arm delay** — the bug needed a second feed to
become observable. Fixed with a per-feed consecutive-failure streak (~20 s of budget) that a
single good pass clears.

**Bug 2 — the device removal: a gate cycle orphaned a whole feed.**
`FeedGate.PumpRenderThread` was called from inside `using (Feeds.Enter(Feeds.NextForRender()))`,
so only the feed holding the render slot counted down to teardown. `AdvanceSlot()` runs only
after a render COMPLETES, and a dormant feed never completes one — so the moment both feeds
went dormant the slot froze, one feed released, and the other's countdown stayed pinned at 30
forever:

```
[19:20:51.364] [feed 0] === FEED GATE: DORMANT. ... releasing resources in 30 frames. ===
[19:20:51.364] [feed 1] === FEED GATE: DORMANT. ... releasing resources in 30 frames. ===
[19:20:52.357] [feed 1] Feed gate: releasing resources now.
[19:20:52.364] [feed 1] Whole-scene Reset: VRAM 12847 MB -> 12720 MB (-127 MB)
               ...and nothing whatsoever from feed 0.
```

The next cycle rebuilt BOTH feeds, allocating a fresh ScreenBuffers and DrawContextManager
for feed 0 on top of the set never freed. VRAM went **12.78 -> 13.70 GB in one gate cycle and
stayed flat there** — retention, not churn — against a 13.61 GB budget. Device removed 40 s
later. Fixed by pumping every slot's countdown once per frame, outside the render scope.

**The principle:** a teardown countdown is per-feed BOOKKEEPING, not per-feed RENDERING.
Scheduling it on the render slot tied the freeing of resources to the activity that had just
stopped. Generalised into `Feeds.ForEachSlot`: **sweeping to RUN covers the ACTIVE feeds;
sweeping to RELEASE covers ALL slots** — a slot retired by a `feedCount` change or by the
VRAM cap is invisible to every Count-bounded sweep from the instant it stops being active,
which is exactly when its resources became garbage.

**Methodological note.** The "two feeds do not fit" conclusion came from comparing VRAM
readings without scoping them to a session. `rtt.log` carries times but no dates, so a naive
grep for high-VRAM lines returned rows from a previous day — the same trap recorded earlier
in this project, fallen into twice. Scope every log window from the last
`=== RttProbe bootstrap` line before reading a number off it.

### C3 RESULT 2026-07-30 20:35-20:41: functional gate MET; VRAM gate needs a re-run (see correction at the end)

**What is proven.** Two feeds rendered and delivered simultaneously for the first time:

```
[feed 0] park#617 copies=504      [feed 1] park#306 copies=156
[feed 0] HANDOVER SURVIVED 30 copies — the feed is on the panel.
[feed 1] HANDOVER SURVIVED 30 copies — the feed is on the panel.
```

Steady state was excellent and better than expected: **45.7 fps vs 48.2 single-feed, p50 21.9
vs 20.6, p95 24.5 vs 22.8, `>50ms = 0`, and CPU submit UNCHANGED at 2.1 ms.** Rule 9 holds at
two feeds — the second render costs almost nothing in submit, which is the route's bottleneck.

The teardown fix was observed firing, three separate times, both feeds each time:

```
[feed 0] Whole-scene Reset: VRAM 12457 MB -> 12331 MB (-126 MB)
[feed 1] Whole-scene Reset: VRAM 12331 MB -> 12205 MB (-126 MB)
```

Against the broken build, where feed 0 released nothing at all.

**What is NOT proven, and why the gate is not met.** VRAM RATCHETED across gate cycles and
never came back:

| time | state | VRAM | avail |
|---|---|---|---|
| 20:29 | 1 feed | 12.05 GB | 13.61 |
| 20:36 | 2 feeds | 12.23 GB | 13.62 |
| 20:39 | 2 feeds, after a rebuild | 12.79 GB | 13.59 |
| 20:40:59 | 2 feeds, after another rebuild | **13.58 GB (+1225 MB in one step)** | 13.52 |
| 20:42 | back to 1 feed | 13.45 GB | 13.98 |

Each teardown returns a consistent **126 MB per feed** while each rebuild consumes several
hundred. The run was stopped by hand at 13.58 GB against a 13.52 GB budget — the same
condition that preceded the 19:21 device removal, reached this time without the orphaned-feed
bug, so **there is a second and independent VRAM problem still open.**

Two candidate explanations, not yet distinguished, and the difference matters:

1. **Allocator pooling.** `UsedVRAM` counts blocks the D3D allocator holds, not blocks in
   use. Dispose returns memory to the pool without lowering the counter, and the next
   rebuild reuses it. Supported by: single-feed VRAM was dead flat across many gate cycles
   all evening. If this is it, the ratchet is cosmetic and the real ceiling is higher.
2. **A genuine per-rebuild leak that only shows at N>1.** Supported by: the -126 MB figure is
   suspiciously constant and far below the ~380 MiB the resource walk attributes to a feed,
   so `DrawContextManager.Dispose` may not be releasing what the walk says it owns.

**Next test, and it is cheap:** cycle the gate ~5 times at ONE feed on a fresh boot, watching
VRAM. Flat = pooling (candidate 1), and two feeds are viable. Climbing = a real leak
(candidate 2) present all along and merely invisible at one feed. Do it on a fresh session:
tonight's readings all sit on top of an already-elevated engine footprint.

**Do not run two feeds at 1024 unattended until this is settled.** The E1 cap is a real
backstop — it clamped itself to 1 as soon as headroom tightened, exactly as designed — but a
cap calibrated on a per-feed constant cannot protect against a per-REBUILD cost it does not
model.

**THE C2 BASELINE IS NOT `reference/every-frame-baseline`.** That pin is 512x512 with no flares,
no own probes and no atmosphere LUTs — a materially cheaper build. Grading today's 1024 SSAA
full-fidelity build against its 66 fps / 2.1 ms would show a large "regression" that is entirely
the configuration and nothing to do with instancing. Comparing across configs is how a refactor
gets blamed for a feature's cost.

The correct baseline is the **pre-C1a build at TODAY's config**, whose warm figure was
`ourDraw ~2.2 ms` with `>50ms = 0`.

Measured on the C1a build, 2026-07-30 18:25, steady state after ~25 s warm-up, one live panel:

```
PERF 49.1-49.4 fps | ours n=246-247 mean=20.3-20.4 p50=20.3 p95=22.0-22.5 max=24.9-33.4
                   | >50ms=0 | idle n=0 (every engine frame, intervalMs=0)
                   | ourDraw (CPU submit) mean=2.4 p95=2.7-2.8 max=3.2-3.7
                   | VRAM=12.23 GB, flat across four consecutive windows
```

Submit is within noise of the 2.2 ms baseline, the smoothness invariant holds (`>50ms = 0`, and
the p95-p50 gap is ~2 ms), and VRAM is flat — so the instance allocation leaks nothing. **This is
a strong signal, NOT a pass.** The gate also requires three teardown cycles, a 15-minute soak and
a visual check, and none of those are done from 30 seconds of steady state.

**C1 was split into C1a/C1b deliberately, with C2 BETWEEN them.** The seam (state moves onto an
instance) and the selection (a pump chooses which instance) are independent changes with
independent failure modes, and only the second one can alter threading behaviour. Proving parity
after C1a means `Feeds.Cur` is still a constant, both threads still see the same object, and a
parity failure can only be the field mapping. Doing both at once would have left a numbers
regression ambiguous between "wrong field moved" and "wrong feed selected" — on a build whose
whole value is that it currently works.

**The C1a technique, recorded because it is reusable.** Per-feed state was ~55 fields across seven
files read from several hundred call sites. Rather than thread an instance parameter through all
of them, each per-feed FIELD became a same-named static PROPERTY over a `FeedInstance` field:

```csharp
private static object _ourScreenBuffers;                              // before
private static object _ourScreenBuffers                               // after
{ get => Feeds.Cur.OurScreenBuffers; set => Feeds.Cur.OurScreenBuffers = value; }
```

Every call site compiles and behaves identically, untouched; the diff is "delete a field, add a
property" per item, reviewable field by field. The compiler then verifies completeness from both
directions: a dropped static errors at its use sites, and an orphaned instance field warns CS0649
— so **a clean build with zero warnings is real evidence that the mapping is total**, not just
that it typechecks. What it cannot check, and what was checked by hand: crossed get/set pairs, and
mutating calls or member writes on struct-typed properties, which would silently land on a
temporary copy.

## Phase D — the decisive experiments (0.5 session; needs C)

The measurements the budget model is built on. Cheap once two feeds exist.

| # | experiment | decides |
|---|---|---|
| D1 | **Interleaved 2 feeds vs 1 feed at every-2nd-frame** — same per-feed cadence, different global heat | the load-bearing hypothesis: interleaved ~= warm. If it fails, ms-metering absorbs it — but the constants change |
| D2 | **N-warm vs K-cold crossover** at 2-3 feeds | whether CCTV slots ever beat interleaving inside the budget |
| D3 | **VRAM per instantiated feed** (measured, per quality preset) | the max-resident-feeds constant, per preset |

Exit gate: the two budget constants are numbers with measurements behind them.

### D3 result 2026-07-30: the per-feed footprint, and why the analytic walk is not the input

Two independent measurements of the same quantity, and they disagree by ~1.5x:

| method | figure at 1024x1024 | status |
|---|---|---|
| analytic resource walk (`FeedResourceReport`) | 384.7 MiB | **lower bound, self-declared** — it prints an UNSIZED list of types it cannot measure |
| measured marginal cost of `feedCount` 1 -> 2 | **~580 MB** | what actually happened |

The admission cap uses the **measured** figure. Using the analytic one would let it admit
feeds ~1.5x smaller than they really are, which is precisely the failure it exists to
prevent. The walk keeps its value for finding WHAT to cut — it is how character shadows were
found — but "does another one fit" is answered by the number that was watched happening.

Split for the cap: ~92 MB scales with our pixel count (ScreenBuffers 60, RTGI histories 32),
~488 MB is structural. **The structural part is the floor under `maxResidentFeeds`** and no
quality preset can remove it, because owning a second culling context means owning
scene-sized entity-instance and light-clustering buffers.

## Phase E — the credit scheduler (goal 3 realized; 1-2 sessions)

| # | item | test / exit evidence |
|---|---|---|
| E1 | **The render SLOT** (see the smoothness constraint in phase2-design.md): at most one render per engine frame, strict cyclic rotation among ACTIVE feeds; the ms constant is calibration + tripwire, NOT a per-frame gate; overruns NEVER cause skips — sustained drift is absorbed by variance-free cost levers (amortisation, far clip), surfaced on the tripwire; dormant feeds return their share; priority = extra slots on a strictly PERIODIC schedule | total submit flat as N goes 1->2->3 AND the p95-p50 gap + >50ms count hold the single-feed baseline (smoothness IS the invariant); feed fps ~engine/N; kill a feed -> others speed up within a second |
| E2 | **Shared source + per-panel aspect crop** — same camera onto 2+ panels of different aspects via per-panel source rects | crops centred and correct; N same-camera panels cost no extra credits (blits off-budget, verified on stats panel) |
| E3 | Global quality preset changes under N feeds: staggered rebuilds, one feed per settle window | quality switch with 3 feeds live -> no >50ms frame |

Exit gate: the fixed-budget invariant demonstrated on the stats panel while feeds are
added and removed.

## Phase F — reliability hardening (goal 7; 1-2 sessions)

The graceful-cut contract, exercised deliberately, path by path.

| # | path | required behaviour |
|---|---|---|
| F1 | panel destroyed / powered off / ground down mid-feed | that feed dormant, others untouched |
| F2 | world exit + save/load with N feeds live | clean shutdown, clean re-arm on load |
| F3 | alt-tab, resolution change, device events | survive or degrade to dormant; NEVER device-remove |
| F4 | hot reload with N feeds | Rule-25 clean teardown xN, re-arm |
| F5 | **panel-freeze bug** — root-cause with instancing insight (suspects: BlitProbe.FeedTarget stability across Reset, CameraRender's cached `_feedTexture`/`_resolvedPanelId`) | diagnosed, fixed from the panel tick (NOT a render-thread rebind — that CTD'd), or explicitly waived with the gate-cycle workaround documented |
| F6 | **FSR smear** — act on the A3(i) finding | fixed, or root-caused with a documented trade |

Exit gate: the matrix is green; "a cut feed never device-removes" has evidence per row.

## Phase G — the RTT Feed API surface (goal 5; 1-2 sessions)

| # | item | test / exit evidence |
|---|---|---|
| G1 | Public handles: `CreateFeed(cameraTransformProvider, targetSurfaceId, options)` / `DestroyFeed(handle)` / `SetPriority`; liveness registration (entity id + damage threshold -> self-close) | a toy consumer (script/second plugin) creates and destroys feeds without touching internals |
| G2 | The orbit camera becomes a demo CLIENT of the API | orbit runs through CreateFeed like any consumer |
| G3 | Versioning/compat shim so consumers survive the eventual move to Keen-shipped APIs | documented contract; version handshake |

Exit gate: feeds fully driveable from outside the plugin.

## Phase H — the camera-block mod (goal 6; separate add-on, after G)

Block part + terminal UI (global quality selector, per-camera priority, target LCD
picker); consumes the API via handles; liveness contract live (damage -> feed closes
itself). Exit: place a camera block, pick an LCD, get a feed; grind the block down, feed
closes gracefully.

## Phase I — in-game control panel (goal 1; decide after H)

The LCD config menu for the CURRENT build may be superseded by the camera-mod UI. Decide
once H exists rather than building both. The config file stays the persistence layer
either way.

---

## Standing items, all phases

- Watchdog between work blocks; status.sh before acting; A/B at equal session age;
  CONTROL PLAYER POSITION; verify the panel is live before trusting numbers.
- Pin a reference build (branch + tag + config snapshot + binary hashes) at every phase
  exit, same as `reference/every-frame-baseline`.
- Any new mirror/share of engine state answers the Rule-25 question FIRST: can our
  teardown reach what we borrowed?
- Known-parked: AAMode/FXAA (4 CTDs — superseded by supersampling anyway), stage 9 HBAO
  (needs the CloudJob treatment), 2048+ resolution (VRAM wall), start-of-frame submit
  (null result, kept as a hook).

### CORRECTION to the C3 VRAM finding, same evening

The "second independent VRAM problem" above is **withdrawn.** The ratchet was the deploy
path, not the feeds.

`FeedGate.Reset()` set `_paused = false`, so after every hot reload the gate woke believing
it was unpaused, saw tagged panels ticking, and built a complete feed BEFORE re-reading the
marker — then went dormant, freed nothing (the teardown ran against half-built state and
reported `0 MB`), and rebuilt on resume. **~570 MB orphaned per deploy, by the very protocol
that exists to make deploys safe.** Four deploys, four steps: 12.05 -> 12.23 -> 12.79 -> 13.58.

Every steady-state window in between was **dead flat** — 12.79 GB for fifteen consecutive
samples at two feeds. There is no per-rebuild leak and no evidence that two feeds approach
the ceiling.

Fixed by reading the marker inside `Reset()`, and failing CLOSED if it cannot be read.
Verified on the next deploy: the gate stayed silent through the install and produced exactly
one build on resume instead of two.

**The lesson is about instrumentation, not about feeds.** Every VRAM number taken across a
deploy this session was inflated by my own tooling, and the "+580 MB marginal cost of a
second feed" was one deploy's orphan attributed to feed 1. A measurement harness that
perturbs the quantity it measures will produce a coherent, plausible, and entirely wrong
model — this one survived two rounds of analysis before the log ordering gave it away.

**Re-run the C3 VRAM gate on a fresh session with NO mid-run deploys**: launch, set
`feedCount = 2`, and leave it alone for fifteen minutes.

### CTD 2026-07-30 20:54: flipping feedCount LIVE is unsafe. NOT VRAM.

The C3 VRAM re-run was started on a clean session — 52.4 fps, p50 19.1, `>50ms=0`, VRAM dead
flat at 12.24 GB with **1.5 GB of headroom**, the best reference this project has had.
`feedCount` was flipped 1 -> 2 with feed 0 live. The game died 2.5 seconds later.

```
DXGI_ERROR_DEVICE_REMOVED
EventStack:    [CullingProxies, MainViewCulling[FirstPass], ScenePreparation + Render]
PageFaultVA: 0x0   ExistingAllocations: 0   RecentFreedAllocations: 0
VideoMemoryInfo:  Budget 14.93 GB   CurrentUsage 13.33 GB     <- 1.6 GB FREE
```

**This is not a memory problem.** Zero existing and zero freed allocations with
`PageFaultVA 0x0` is a NULL BIND — something bound that was never allocated — and it happened
inside CULLING, which is exactly what the DrawContextManager owns.

The mod's own log, scoped to that session:

```
20:53:58.908  [feed 0] FEED VRAM CAP: max resident feeds = 2 (headroom 1118 MB)
20:53:58.915  [feed 0] Whole-scene Reset: VRAM 12511 -> 12385 MB (-126 MB)   <- nulls the DC
20:53:58.917  routing: [RTC] -> feed 0, [RTC2] -> feed 1
20:54:00.425  [feed 0] SECOND ScreenBuffers built                            <- 1.5 s later
              <<< DEVICE_REMOVED at 20:54:01.425 >>>
```

Feed 0's ScreenBuffers came back but **there is no `SECOND DrawContextManager built` line and
no `settled after the rebuild` line.** The rebuild was still in progress, and it was unusually
slow — 1.5 s between the Reset and the ScreenBuffers, against ~30 ms in a normal gate cycle,
because the feedCount change re-routes panels and rebuilds both feeds at once.

**It did NOT reproduce.** The next launch came up with `feedCount = 2` already set, built both
feeds from a dormant gate, and both delivered (`HANDOVER SURVIVED` on each). So the hazard is
not "two feeds" — it is **rebuilding under a live render**, which is the same hazard class
already recorded for `wholeSceneAAMode` (4 CTDs) and `wholeSceneOwnProbes` (3 CTDs).

**One concrete defect found while reading, worth fixing on its own merits.**
`EnsureDrawContexts` sets its latch BEFORE the build:

```csharp
if (_dcBuilt || _ourDrawContexts != null) return;
_dcBuilt = true;                                  // latched before anything is built
try { _ourDrawContexts = Activator.CreateInstance(t); ... }
```

Every early return and every exception inside that `try` leaves `_dcBuilt = true` with
`_ourDrawContexts = null`, permanently — the latch conflates ATTEMPTED with SUCCEEDED, so the
build is never retried. That is survivable by design (a null context falls back to the
engine's, "degraded not broken"), which is why it has never been noticed, but it means a
transient build failure silently downgrades the feed for the rest of the session with no log
line saying so.

**The fix for the CTD itself, unbuilt:** a `feedCount` change must not rebuild in place. Take
the gate DORMANT, let the full teardown complete, and rebuild only from the quiesced state —
i.e. make the pause path automatic for this knob rather than relying on the operator. Until
that exists, **`feedCount` is classified restart/pause-protocol, like `ownProbes`.**

Do this with fresh crash patience, not at the end of a session. Single feed at 1024 remains
rock solid: 52.6 fps, p50 19.1, p95 21.1, `>50ms=0`, VRAM flat, zero errors.

### C3 EXIT GATE MET 2026-07-30 21:26-21:35 — and two feeds are essentially FREE

With the quiesced-rebuild fix in, `feedCount` was flipped 1 -> 2 **live**, on the same clean
session, and the feed came back in half a second. Measured side by side, same session, same
position, no deploys inside either window:

| | single feed | TWO feeds |
|---|---|---|
| fps | 52.4 - 53.3 | **53.2 - 53.9** |
| ours p50 | 19.1 | **18.5 - 19.2** |
| ours p95 | 20.8 - 21.2 | **20.3 - 21.3** |
| `>50ms` | 0 | **0** (one 62.9 ms frame in eleven windows) |
| CPU submit | 2.0 - 2.2 ms | **2.0 - 2.2 ms** |
| VRAM | 12.24 - 12.35 GB | **12.32 - 12.38 GB** |

**The second feed costs nothing measurable.** Frame time is identical — if anything marginally
better — and the VRAM delta is inside the ±100 MB noise floor. Both feeds deliver at the same
rate (~27 copies/s each, `skip(noFrame)=0.0`), 0 errors, 0 unscoped accesses, VRAM flat across
a five-minute soak.

**This is the phase-E budget model's core hypothesis, demonstrated.** The render slot gives at
most one feed a render per engine frame, so TOTAL per-frame work is constant and each feed's
own rate divides by N — 53 fps engine, ~27 renders/s per feed. "Fixed total cost, fps divides
by N" is no longer a design intention; it is what the instrument reads.

**Every earlier per-feed VRAM figure is retracted.** The "+580 MB marginal cost" came from
windows that spanned deploys, and each deploy was orphaning ~570 MB via the `FeedGate._paused`
bug. With that fixed and measured cleanly, the marginal cost is under 100 MB.

Consequence for E1: **the cap's 580 MB constant is now far too conservative** — it would refuse
a third feed that would fit easily. That is the safe direction to be wrong in, so it is being
left alone tonight rather than loosened on a single N=2 measurement. Re-derive it from a 3- and
4-feed sweep before changing it, and note the structural floor argument no longer has evidence
behind it either: if feed 2 costs <100 MB, the "scene-sized buffers per feed" reasoning from
the resource walk is not what dominates.

**Remaining C3 nit, not a blocker:** on one rebuild feed 0's handover started ~80 s after feed
1's (`copies=225` against `park#2323`) before catching up to an identical rate. Belongs with
the F5 panel-freeze family rather than with instancing.

### E2 second half, RECONNOITRED 2026-07-30: panel fan-out is a binding-list change

The aspect-crop half of E2 is LANDED (090916a). The "N same-camera panels" half was
reconnoitred offline and the answer is favourable — recorded here so the implementation
session starts from evidence, not memory.

**The design: panels are free because binding is per-panel and the target is per-feed.**
A feed delivers into ONE OffscreenRenderTarget WE create ("RttProbe1"); a panel shows it
because `PanelBinding.TryBind` hands that target's TextureHandle to
`ctx.SetNewScreenMaterialHandle(renderer, baseMaterial, aspect, orientation, handle)`.
Nothing in that call is exclusive: two panels' surface contexts can each be handed the SAME
handle. No second render, no second copy, no second anything on the render thread.

**The engine's material lifecycle is safe for it.** The "Can't remove material" assert that
fires on gate cycles was read in IL tonight: `MaterialsManager.ChangeMaterial` is
remove-then-add BY GUID, and the assert is the remove half running for a guid that is
already gone (a queued RenderCommandBuffer replay landing after our unbind). The add half
then proceeds. Benign by construction, per-material-instance, no cross-panel shared state —
6 occurrences tonight, zero consequences. Fan-out doubles this noise and nothing else.

**What actually blocks it is OUR single-panel state,** all self-inflicted:

1. `FeedInstance.Bound` / `BoundRenderer` / `BoundCtx` — one latch, one weak pair. Becomes a
   small list of (renderer, ctx) weak pairs; `TryBind` appends per claiming panel; `Unbind`
   and `RestoreEngineState` sweep the list.
2. `CameraFeed.Target` — the tick side publishes whichever claiming panel ticked LAST, so two
   panels on DIFFERENT grids would thrash the orbit target. Semantic decision: the feed's
   camera follows its FIRST claimant (the primary panel); later claimants are display-only.
3. The router already does the right thing: a panel asking for a feed beyond `feedCount`
   SHARES feed 0 and logs it. That means the activation test needs no new tag scheme —
   `feedCount = 1` with both existing panels tagged puts [RTC2] on feed 0's picture iff the
   binding fan-out works.

Estimated ~100 lines across FeedInstance/PanelBinding/CameraFeed. It touches the
double-release path (one forced-rebind CTD in its history), so it opens a session rather
than ending one — with the ChangeMaterial groundwork above already done.

### E2 COMPLETE 2026-07-30 22:28 — both halves, verified live

**Aspect crop** (090916a) and **panel fan-out** (this) are in. E2's exit gate is met.

The fan-out activation, `feedCount = 1` with both tagged panels present:

```
[RTC] panel located: "LCD Panel [RTC]" (1 surfaces registered)
Feed 0: panel "LCD Panel [RTC2]" MIRRORS this feed — it shows "LCD Panel [RTC]"'s
        camera. Display only; the orbit target is unchanged.
Phase 2: panel material rebound to our own render target.      <- bind 1
Phase 2: panel material rebound to our own render target.      <- bind 2
```

**Two panels, two material binds, ONE render.** Measured against the single-panel
single-feed reference from the same session: 53.3 fps vs 52.4-53.3, p50 18.8 vs 19.1, p95
20.4 vs 20.8-21.2, `>50ms = 0`, VRAM 12.32 GB — indistinguishable. Extra panels really are
free, because a panel is a material binding and not a render. That is the last input the
phase-E budget model needed: **credits are per unique CAMERA, and panels do not consume
them.**

Design decisions worth keeping:

- **First claimant elects the primary.** The feed's identity — orbit target, captured panel
  RT, LastRenderComponent — follows the panel that claimed it FIRST. Letting every tick
  publish made the camera thrash between claimants (last-wins, twice a frame), which on two
  different grids is a camera oscillating between two ships. Mirrors register their surfaces
  (that is what routes their bind) and nothing else. Re-elected on every gate cycle, so a
  destroyed primary hands over within one.
- **Bind list, not bind latch.** Registered at ATTEMPT so a failed bind is not retried every
  content pass, and `Unbind` sweeps every entry independently — one destroyed panel must not
  strand the others on our runtime material.
- **`WantsRepaint` counts live binds against claims**, so a panel joining an already-bound
  feed re-arms the repaint drive until its own bind lands. Without that a mirror never enters
  the content-render hook and never binds.

### The quiesce needed a v2, and its own tripwire found it

The first DOWNWARD count change (2 -> 1) exposed what going up never could: shrinking
`Feeds.Count` retires a slot IMMEDIATELY, so its panel stops routing to it on the very poll
that requested the quiesce — and **a feed nobody polls can never see itself go dormant.**
`_active` stayed true, `AllQuiesced` never returned true, and after 10 s the timeout escape
hatch released the hold — doing exactly its job, on its first ever firing — leaving the
retired feed's ScreenBuffers and DrawContextManager stranded resident.

`RequestQuiescedRebuild` now forces every active slot dormant itself rather than waiting to
be polled. **The 10 s fail-open bound is why this was a diagnosable log line instead of a
mod that silently never came back**, which is the strongest argument yet for writing the
escape hatch at the same time as the mechanism.

**Protocol lesson (CTD #6):** recovering the stranded feed by flipping the count back UP
worked, but a count change REBUILDS before it releases, and it was issued into an allocator
already residency-thrashing at 18 fps with 216 MB of headroom. Device removed ~5 s later in
the GPU profiler's readback at Present. Under VRAM pressure: **pause FIRST** — release with
re-arm blocked — **then** change the count.

### C3 exit gate: VISUALLY confirmed 2026-07-30 22:46

The user asked whether the two feeds were genuinely distinct, having seen both panels showing
the same angle. They were right to ask, and the answer had two halves:

1. **At `feedCount = 1` both panels are the SAME feed by design** — RTC2 is an E2 mirror.
   Identical pictures are the feature working, not a fault.
2. **At `feedCount = 2` the per-feed orbit phase offset already existed** and had never been
   looked at: `t += Feeds.Cur.Id * (OrbitPeriod / Feeds.Count)`, so two feeds sit half an
   orbit apart.

Capture at `feedCount = 2` (`output/frames/twofeeds-224611-227.png`):

- **feed 0** — the ship side-on, silhouetted against the Milky Way, sun off-frame.
- **feed 1** — the opposite side of the orbit: sun blazing in frame, looking along the hull
  with thrusters lit and structure filling the corner.

Nothing alike, which is the whole point of the offset. From that comment, written before the
second feed existed: *"two feeds on panels of the SAME grid would otherwise sit at the same
orbit angle and produce pixel-identical pictures, which is the one arrangement that makes a
multi-feed bug invisible — cross-contamination between feeds looks exactly like correct
output when both feeds show the same thing."*

`47.3 fps, p50 21.2, p95 22.7, >50ms=0, submit 2.4 ms, VRAM 12.10 GB` with two independent
whole-scene renders. **C3's "both feeds live and correct" is now met by inspection, not just
by counters.**

### Instrument note: the in-game FPS overlay lies during a capture

The first remoteness capture showed `FPS 17 | GPU 99%` next to our own `54.3 fps`, and was
briefly written up as a regression. It was the 4K window grab stalling the game for the
instant the overlay sampled. The engine's own STATISTICS row read `Frame=18.5 ms` (54 fps)
at the same moment, and a later capture that did not stall read `FPS 120`.

**Never grade performance from a screenshot's overlay.** Use the engine's STATISTICS row or
our PERF line, both of which sample continuously rather than at the instant of the stall the
measurement itself caused.
