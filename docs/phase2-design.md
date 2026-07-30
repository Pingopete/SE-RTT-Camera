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
