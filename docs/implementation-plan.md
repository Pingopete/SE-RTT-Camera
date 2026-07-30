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
| A1 | **Stats panel v1** — `[RTS]` tag scan, font survey (3 routes, one-shot log), persistent batch with ~6 DrawString lines re-recorded every 500ms | stats visible in game; feed panel untouched; 3 teardown cycles clean. THIS IS THE INSTANCING PATHFINDER — it forces target/binding/batch out of single-panel statics on a surface where failure = broken numbers, not broken feed |
| A2 | **`intervalMs` out of the rebuild signature** (trivially class (a)) | live change 0->33->0 with NO gate cycle in the log; rate follows within one poll |
| A3 | **Ten-second experiments**: (i) FSR smear discriminator — player AA off FSR, look at distant panel; (ii) visibility dormancy — walk the panel out of view, watch for DORMANT; (iii) `ownProbes` live flip both directions | each answered with one log line + user observation; findings recorded |
| A4 | **Class (a) knob A/B sweep** — one knob at a time under pause protocol, per the classification table | verified live-switch list; any knob that misbehaves demoted with evidence |

Exit gate: perf numbers readable in game; knob classes verified by test, not reading.

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
| C1 | **Inventory the statics** (ScreenBuffers, DrawContextManager, cascade set, probe manager, flares mirror + originals, LDR ring, orbit/camera state, panel binding, gate state) -> `FeedInstance`; one global render-thread pump; teardown per instance uses the Rule-25 discipline (dispose only what the instance allocated) | compiles; instance count = 1 |
| C2 | **Single-instance parity** — one FeedInstance must equal the current build | PERF within noise of the reference build; same visuals; 3 teardown cycles; 15-min soak. Pin as reference |
| C3 | **Second unique feed** — second tagged panel, second camera (offset orbit), simple alternator (A on even frames, B on odd) as the placeholder scheduler | both feeds live and correct; destroy/power-off panel A -> feed B unaffected; teardown matrix per feed; 15-min two-feed soak |

Exit gate: two independent feeds, independently killable, no cross-contamination.

## Phase D — the decisive experiments (0.5 session; needs C)

The measurements the budget model is built on. Cheap once two feeds exist.

| # | experiment | decides |
|---|---|---|
| D1 | **Interleaved 2 feeds vs 1 feed at every-2nd-frame** — same per-feed cadence, different global heat | the load-bearing hypothesis: interleaved ~= warm. If it fails, ms-metering absorbs it — but the constants change |
| D2 | **N-warm vs K-cold crossover** at 2-3 feeds | whether CCTV slots ever beat interleaving inside the budget |
| D3 | **VRAM per instantiated feed** (measured, per quality preset) | the max-resident-feeds constant, per preset |

Exit gate: the two budget constants are numbers with measurements behind them.

## Phase E — the credit scheduler (goal 3 realized; 1-2 sessions)

| # | item | test / exit evidence |
|---|---|---|
| E1 | **ms-metered render credit**: one credit per engine frame worth the budget; rotation among ACTIVE feeds; overrun repays by delaying the next credit; dormant feeds return their share; priority = extra credits | total submit flat as N goes 1->2->3 (THE invariant); feed fps ~66/N; kill a feed -> others speed up within a second |
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
