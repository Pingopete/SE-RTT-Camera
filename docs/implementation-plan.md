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
