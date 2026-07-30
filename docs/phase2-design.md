# Phase 2 design notes — goals 9 and 8 groundwork

Investigated 2026-07-29 evening, offline (EngineQuery + our own source), game untouched.

## Goal 9: on-screen perf UI — FEASIBLE, cheap, path proven

**The primitive exists and we already drive its machinery.**
`IDrawBatch.DrawString(Font, Vector2, ColorSRGB, string, scale, ...)` is on the same
interface the 2D test pattern uses today (`BlitProbe.Fill` -> `DrawFill`, `DrawFeed` ->
`DrawImage`), and `PersistentDrawBatch.DrawString` confirms our batch class has it. The
test pattern already proves, in production: creating a persistent batch, recording draws
against a panel target, submitting, and retiring the previous batch. Text is one more call
on a proven path — no glyph rendering to build, no GPU resources created (Rule 11 intact).

**Fonts are obtainable, three routes, best first:**
1. The panel's own configured font: `LcdPanelSurfaceState.Font` is a
   `ResourceHandle<FontAsset>`, and `FontManager.GetFontHandle(handle)` returns the
   engine `Font`. We already hold the surface context in the panel hook.
2. The content renderer we already postfix (`LcdContentRendererSessionComponent`) resolves
   fonts itself — its `TextRun.Font` fields are resolved `Font` objects; a cached one can
   be read by reflection.
3. `VRage.Client.UI.StatRenderer._font` — the engine's own stat overlay font.

Which one yields a non-null Font at runtime is a ONE-SHOT SURVEY LOG, not more IL reading
(Rule 22). The implementation logs what it found and fails soft to "no stats UI".

**Placement decision: a SECOND tagged panel (e.g. `[RTS]`), not the feed panel.**
The feed panel has exactly one writer by hard-won design — the handover copy — and the
test-pattern machinery documents the mutual-exclusion pain ("the test pattern and the
camera copy are mutually exclusive writers"). Drawing stats into the feed target would
re-open that. A dedicated stats panel reuses the tag-scan discovery that exists, gets its
own persistent batch (the test-pattern path verbatim), and updates at whatever cadence we
re-record it (2 Hz is plenty for numbers).

**Content, v1:** the PERF line's essentials — fps, ours p50/p95, CPU submit, VRAM delta,
feed resolution, active features (probes/flares glyphs). All already computed by Perf.cs;
this is presentation only.

**Implementation steps (one evening, logic-only):**
1. Tag scan for `[RTS]` alongside `[RTC]` in the panel discovery.
2. Font resolve with the three-route survey + one-shot log.
3. Persistent batch per stats panel: clear fill + ~6 DrawString lines, re-recorded on a
   timer (default 500 ms), previous batch retired exactly as the test pattern does.
4. Config: `statsPanel = 1`, `statsPanelMs = 500`. Not in the rebuild signature.

**Later option, deliberately deferred:** player-HUD overlay text via the GPS-marker idiom
(`ImmediateDrawBatch.DrawString` — `GPSMarkerHelpers` shows the pattern). More useful for
"stats while looking anywhere", but it draws on the PLAYER'S screen, which is new
territory; the stats panel gets the same information with zero new risk.

## Goal 8 groundwork: rebuild-signature knob classification (first pass)

The gap goal 8 names: most knobs sit in `WholeSceneSignature()`, so ANY change is a gate
cycle + 30-frame settle. For preset switching and rapid A/B, knobs whose consumers read
per render can leave the signature. First-pass classification FROM CODE READING — each
"(a)" claim still needs one live A/B under the pause protocol before it moves (Rule 17:
evidence per knob, not optimism).

**Class (a) — consumer reads config per render; mechanically safe to move out:**

| knob | consumer | note |
|---|---|---|
| `wholeSceneIntervalMs` | TryRender's rate gate, read every frame | trivially (a) |
| `wholeSceneSkipStages` | `ShouldSkipStage`, read per render | mechanically (a); POLICY-gated — flipping which engine code runs is Rule 17 territory, so test each stage id once |
| `wholeSceneNoBloom` | `ScopeSharedState` per render | plain settings field, no PSO |
| `wholeSceneDisableEyeAdaptation` | same | same |
| `wholeSceneDisableProbeUpdates` | same | same (now coupled to OwnProbes in code) |
| `wholeSceneFlareIntensity` | same | ALREADY out of the signature — the working precedent |
| `wholeSceneExposure` | same | inert while skip 25 on |
| `dimDistance`, `emissivity`, orbit params | camera pass / panel tick | already live |

**Class (b) — scope/mirror machinery exists per render, but enabling needs state built at
DC-build time; small refactors would move them to (a):**

| knob | blocker |
|---|---|
| `wholeSceneOwnFlares` | `_engineFlares` is captured in BuildSecondDrawContexts; a live flip ON misses it. Lazy-capture on first mirror would fix. |
| `wholeSceneOwnProbes` | `InstallProbes` already resolves lazily per render — likely (a) as-is; verify one live flip both directions |
| `wholeSceneOwnShadows` | force-runs stage 3 per render, but the shadow-resources swap identity is chosen at DC build |

**Class (c) — genuinely rebuild-bound (buffer/object identity changes):**

`wholeSceneBuildBuffers`, `wholeSceneWidth/Height` (ScreenBuffers + FinalLDR + LDR ring
sizes — and the ring is allocated ONCE per gate cycle), `wholeSceneOwnDrawContexts`,
`wholeSceneToPanel` (binding identity), `wholeSceneLdrResize` (one-shot),
`wholeSceneCameraRebuild` mode changes.

**Class (danger) — mechanically (a) but flipping them rapidly is known-harmful regardless:**

`wholeSceneRtFlags` / `wholeSceneDisableRaytracing` mode 1 (scoping RTGISettings fields
rebuilds the job snapshot = async PSO compile — documented cause of the bright flashing),
`wholeSceneAAMode` (four CTDs; the 19/20/AAMode vice). These stay rebuild-gated as POLICY
even if the mechanism would allow live flips: the rebuild+settle is a safety brake, not an
inefficiency.

**Presets consequence:** low/med/high presets built ONLY from class (a) knobs can switch
live with zero interruption. If a preset needs a class (c) knob (resolution), the switch
costs one gate cycle — acceptable if presets differ mainly in layers, not resolution.

**Next concrete steps for goal 8:**
1. Move `wholeSceneIntervalMs` out of the signature (trivial, biggest QoL).
2. One-knob-at-a-time live-flip tests for the class (a) table, logged.
3. `wholeSceneOwnProbes` live-flip test both directions (suspected (a) already).
4. THEN define the three presets from `docs/feed-render-layers.md` + measured costs.

## Multi-feed display and scheduling (user design questions, 2026-07-29 late)

### One render, many panels: shared source + per-panel crop — ADOPTED

User proposal: default the scene render to a fixed square resolution and CROP per panel
(sides or verticals) to match each panel's aspect, so the same camera feeds any number of
panels without re-rendering. Verdict: right, and mostly already built.

- The CopyJob blit already takes a SOURCE RECTANGLE and scales to the destination — the
  1024->512 supersampling path is this exact call. Aspect cropping is the same call with a
  different rectangle. The per-panel work is: compute the centered sub-rect of the source
  matching the panel's aspect, blit into that panel's own ring/target (which is already
  sized from the panel).
- Cost structure (measured today): the render is CPU-SUBMIT-bound (~2.1ms); blits are
  unmeasurable. So one render -> N same-camera panels = 1x submit + N free blits.
  **The scheduler's unit is the UNIQUE CAMERA, not the panel.** Six monitors showing two
  cameras is two renders.
- Amendment to the proposal: default the shared source to **1024x1024, not 512** — pixels
  are free (proven at 4x) and cropping EATS resolution: a 2:1 panel cropped from a square
  source keeps half the vertical pixels. From 1024, wide-panel crops still land with ~2x
  supersampling; from 512 they arrive under-sampled.
- Semantics: cropping narrows each panel's effective FOV (sensor-crop behaviour). Natural
  for camera feeds; a letterbox option flag (full FOV, bars) can come later per feed.
- Composition detail: the per-panel mip-regeneration already keys off each panel target's
  own mip count, so mixed panel sizes compose with the mip fix as-is.

### Scheduling N unique feeds: round-robin REJECTED as default, kept as fallback

User proposal: cycle the frame allocation A -> B -> C -> A, one feed rendered per frame.

This is the classic scheduler, but it is precisely the shape today's headline measurement
killed: each feed rendering every Nth frame IS throttling, and throttling made a single
feed 6-7x more expensive per render (cold temporal history, denoiser/GI restart). We even
have a direct proxy measurement of the proposed cadence: intervalMs=33 at ~50fps was
"render every 2nd-3rd frame" — p50 29.5ms per render vs 15.2 warm, plus visible temporal
degradation. Round-robin would give each feed that cold cost AND 1/Nth the feed rate.

The pattern that measurably works is the one own-probes proved: **amortise WITHIN each
feed at full cadence, never ACROSS feeds by skipping frames.** Scheduler design:

1. Every active feed renders every frame — temporal state stays warm.
2. The budget trims LAYERS, not cadence: under pressure, feeds drop through the goal-8
   presets (class-(a) knobs switch live), amortise cascades/probe faces harder, pull far
   clip in.
3. Visibility dormancy for free: the gate's "absence of ticking IS the signal", and the
   LCD component only ticks panels it draws — so off-screen panels likely take their feeds
   dormant with zero scheduler code. NEEDS ONE TEST to confirm ticking really stops when
   a panel leaves the frustum.
4. Round-robin kept as the measured last resort if N warm feeds exceed budget even at the
   low preset. The burden of proof sits on throttling now.

Honest limit: at some N, N x 2.1ms of submit is real (3 feeds ~ 6ms). Whether
warm-at-low-preset beats cold-at-high-preset there is a MEASUREMENT, scheduled for when
two feeds exist.

### Budget hypotheses v2 (2026-07-29 late) — after the user's quality-first objection

User objection to "budget trims layers": quality-vs-framerate is a USER preference; some
owners rightfully want quality held. Accepted — and it exposed a framing error worth
keeping: **the scarce resource is render-thread SUBMIT time (passes x draws), not pixels.**
Quality/resolution knobs mostly spend GPU, which has headroom; submit is what limits feed
count (~2.1ms per warm feed per frame). Budget in submit-ms. The revised option stack:

- **A. Per-feed degradation POLICY.** The budget enforces only the TOTAL; each feed
  carries an owner-set policy for how it yields: quality | cadence | resolution | standby
  | never (= priority; others yield first). The system never chooses the axis.
- **B. Coarse-quantum rotation — the user's round-robin at the RIGHT timescale.** The
  cold-start tax is per WAKE: per-frame rotation pays it every render (pathological);
  multi-second slots pay ~0.2-1s of re-warm once, then render warm for the rest. 10s slots
  ~= 90% warm efficiency, each live feed at FULL quality and cadence during its slot; standby
  feeds hold their last frame. CCTV-multiplexer shape. Budget decides HOW MANY are live,
  never how good a live feed looks.
- **C. Perceptual budgeting.** Off-screen panels dormant (absence-of-ticking, test
  pending); small/distant panels absorb cadence/quality trims BECAUSE imperceptible there;
  the up-close watched panel gets full everything. Default yield order.
- **D. Staggered intra-feed amortisation at full cadence.** Base pass every frame per feed
  (warm history, current image); expensive sub-passes rotate offset across feeds (cascades
  even/odd, probe faces, RT diffuse/specular alternation). Cuts real submit without
  disabling anything; costs update latency only.
- **E. Per-feed submit diet, always on.** Far clip / LOD bias / pass masks for content a
  camera never needs. Raises the N where budgeting activates at all.

Stack: E always -> C default order -> A override -> B when yielding means fewer live
feeds -> D as the tier between full and standby. Gating unknown: the crossover where N
warm feeds beat K cold ones — measurable only once two feeds exist (the stats-panel
instancing work is the prerequisite).

### THE ADOPTED BUDGET MODEL: fixed total, render credits (user decision, 2026-07-29)

User clarification that supersedes the open-ended stack above: **the total cost of ALL
feeds combined is FIXED at one warm feed's cost at current settings, per player.** Adding
feeds divides the budget (2 feeds -> ~33 fps each, 3 -> ~22), ideally emerging naturally
rather than by rule. The single-feed budget is never exceeded. This is the right shape for
a shippable mod: worst-case frame cost is known at install time regardless of what players
build.

**Mechanism: the render credit.** Each engine frame carries one credit worth today's
budget (one whole-scene render, ~2.1ms submit). Active feeds claim it in rotation. Feed
fps = 66/N emergently; total load is invariant. Refinements:
- Meter MILLISECONDS, not render counts: a render that runs over (cold wake, heavy scene)
  delays the next credit until the average repays. Hard cap, not statistical.
- Dormant feeds return their share (visibility dormancy): budget flows to watched panels.
- Per-feed policy becomes share WEIGHTING inside the total (priority feed = 2 credits per
  rotation); quality-first owners choose fewer live feeds (CCTV slots) over degraded ones.
- Amortisation (D) and the submit diet (E) become fps MULTIPLIERS — cheaper renders mean
  each feed's slice buys more frames.
- Same-camera panels remain free (blits are off-budget).

**The load-bearing hypothesis, and the first two-feed experiment.** Interleaving looks
like the throttling we proved pathological, but the 6-7x cold-render figure was measured
with the pipeline going GLOBALLY cold between renders (pools churning, nothing rendering
our workload in the gaps). Under interleaving, some feed renders every frame: the global
pipeline stays hot and only each feed's OWN temporal history ages N frames — mild, since
per-feed PreviousCamera stamping keeps reprojection valid. Hypothesis: interleaved renders
cost near-warm. Experiment the moment two feeds exist: two interleaved feeds vs one feed
at every-2nd-frame — same per-feed cadence, different global heat. If the hypothesis
fails, ms-metering absorbs it (feed fps drops a little further); the cap holds either way.

**The time budget does not cap memory.** VRAM scales with INSTANTIATED feeds (ScreenBuffers,
cascades, eight probe cubes each; the 2048 run showed the wall). Second constant required:
max warm-feed count (resident resources); beyond it, extra feeds are fully torn down until
rotated in. The whole system is two constants — submit-ms per frame, feeds resident — both
fixed regardless of world content.

### Quality is GLOBAL across all feeds (user decision, 2026-07-29)

The feed render-quality setting applies to ALL feeds at once — it is deliberately the
user's VRAM/resource throttle, not a per-feed property. Consequences, mostly simplifying:

- **One knob drives both axes.** The quality preset scales per-feed VRAM (render
  resolution, cascade count/res, probes on/off, RT buffers) AND per-render submit cost —
  so lowering it both fits more resident feeds under the VRAM ceiling and makes each
  render credit buy more fps. Exactly the "throttle to taste" intent.
- **Per-feed policy shrinks to share WEIGHTING only** (priority = more credits). No
  per-feed quality axis exists; perceptual budgeting (C) modulates cadence/dormancy only.
  Simpler API, simpler UX: the camera-mod UI gets one global quality control.
- **Implementation is cheaper than per-feed quality:** FeedConfig is already global; the
  quality knobs simply STAY global-scoped when instancing happens, instead of migrating
  into per-feed state.
- **One design point to respect:** a preset change that touches class (c) knobs
  (resolution, cascade allocation) implies rebuilding EVERY feed. Stagger those rebuilds
  (one feed per settle window) rather than rebuilding N feeds in one frame, or a quality
  change becomes a hitch that scales with feed count.

### THE BUDGET LOCK: what is actually held constant, and how (user question, 2026-07-29)

"Lock 66 fps" names the wrong quantity. The feed renders once per engine frame, so feed
fps EQUALS engine fps — 66 was the engine rate on the reference build (about 50 now, with
everything enabled at higher world settings). Hard-locking an absolute renders/sec would
make the feed skip engine frames on faster rigs, reintroducing exactly the cadence gaps
proven pathological. **The constant is the PER-FRAME SLICE: one whole-scene render's cost
out of every engine frame.** Feed fps stays the readout, never the setting.

Three-level lock:

1. **The constant.** `rttBudgetMsPerFrame` = the MEASURED warm cost of one
   reference-quality render (~2.1-2.5 ms submit today), re-measured at each phase-exit
   reference build, stored PER GLOBAL QUALITY PRESET (the quality knob changes what a
   render costs; the budget stays honest about it). Capped at the reference value — users
   may lower it, never raise it. That is the "never exceed" rule in one line.

2. **The enforcement.** The credit scheduler meters ACTUAL cost, not assumptions: Perf.cs
   already measures every render's submit time; the scheduler keeps a rolling average per
   feed, grants renders each engine frame while that frame's budget lasts, and an overrun
   (cold wake, heavy scene) REPAYS by delaying the next credit until the average is back
   under. Hard cap in the long run, not statistical. With one feed this degenerates to
   exactly today's behaviour — render every frame — so v1 costs nothing.

3. **The regression guard.** A budget tripwire: persistent warning (log + stats-panel
   flag) whenever rolling p50 submit exceeds the constant by ~20% for a minute. Any future
   change that quietly makes renders more expensive surfaces as an on-screen budget
   violation, not a mystery sessions later.

Elegant consequence of defining the budget in MS rather than renders-per-frame: dropping
the global quality preset makes renders cheaper, so MORE feeds fit inside the same
per-frame envelope at full cadence each. The quality knob does not just throttle VRAM —
it directly buys feed smoothness under an identical total cost. Falls out for free.

### BUDGET LOCK v2: quality-coupled budget — savings flow to the GAME (user correction, 2026-07-30)

The v1 lock had an inversion the user caught before it was built. Defining the budget as a
FIXED ms envelope and letting cheaper renders fit more feed frames means the envelope is
always fully spent — so a user on a weaker PC who lowers feed quality to reclaim game
frame rate gains NOTHING: the RTT system keeps consuming the same slice, just on more feed
renders. A fixed penalty the quality knob cannot reduce. Wrong default.

**Corrected model:**

- `rttBudgetMsPerFrame` DEFAULTS to the measured warm cost of ONE render at the CURRENT
  global quality preset (per-preset constants come from the phase-B cost table). Lowering
  quality shrinks the envelope; the saved milliseconds return to the game.
- Feed cadence is unchanged by preset: one render per frame at every quality, so feed fps
  = engine fps / N regardless. And the cheaper render raises engine fps itself — lowering
  quality helps the game TWICE (smaller slice, faster frames) while feeds keep cadence.
- The absolute ceiling is unchanged: never above one feed's cost at reference (high)
  quality.
- The v1 behaviour — fixed envelope, savings reinvested as extra feed renders — survives
  ONLY as an explicit opt-in override: the budget may be pinned anywhere below the
  ceiling, including above the current preset's one-render cost, for users who want many
  cheap smooth feeds. The DEFAULT routes savings to the game.

Net: the global quality knob now serves all three stated intents at once — VRAM throttle,
uniform quality across feeds, and the whole-game frame-rate lever. The tripwire (A1)
compares rolling p50 submit against the CURRENT preset's constant, so preset changes move
the guard automatically.
