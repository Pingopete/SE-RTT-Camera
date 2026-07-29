# The feed's render layers — reference snapshot

**What this is.** A complete inventory of which engine render stages the RTT feed runs,
which it skips, and what each skip costs, taken at the best-performing build this project
has produced. Written to be the baseline that later fidelity levels (low / medium / high)
are defined against.

**Build identity** — see `docs/reference-build.md` for the pinned commit, tag and binary
hashes. Config: `docs/feed-config.known-good.txt` (byte-identical to the live
`output/feed-config.txt` at the time of writing).

**Measured, with a verified-live panel:**

```
PERF 65.8-66.0 fps | ours n=329-331 mean=15.1-15.3 p50=15.1-15.2 p95=17.9-18.5
                   | max=20.4-25.5 >50ms=0 | idle n=0
                   | ourDraw (CPU submit) mean=2.1 ms
copies=22.0  park#2757  secondRenders=8338
```

Caveat carried from the session: this was measured with **reduced in-game graphics
settings**, and the feed was not at full fidelity. 66 fps is not the number to expect at
max settings — it is the number for this configuration.

### ⚠ THE MOST IMPORTANT THING ABOUT THIS BUILD

**No GPU starvation.** Roughly **fifteen minutes** of continuous play on this build showed
**no sign of the progressive GPU-starvation / frame-time drift** that dominated the whole of
the earlier session — the symptom where the feed and the world both bled fps the longer the
session ran, and where moving around went from smooth to hitchy.

That drift is the single problem this project spent the most time on and repeatedly failed to
explain. Three models for it were proposed and all three were retracted: time-decay
(disproved — the user was stationary and flat for 12 minutes), the LDR resize (reproduced at
equal session age), and start-of-frame submission (null result, 41.6 fps both arms).

The thing that actually changed is `wholeSceneIntervalMs = 0`. The working explanation is
that **the rate limit was the drift**: every throttled wake paid a cold-start tax — stale
temporal history, evicted caches, GI and denoiser restarting — and that tax grew with scene
load. Rendering every frame keeps all of it warm. CPU submit dropped from 13-15 ms to
**2.1 ms**, which is a 6-7× reduction from *removing* a throttle.

**This has not been proven, and 15 minutes is not a long soak.** It is the best evidence
available and the reason this build is pinned. It also means the planned multi-feed frame
budget — "rate-limit each feed to share a total cost" — is **designed around the wrong
model** and needs rethinking before it is built: throttling is what made a single feed
expensive.

---

## 1. How the feed is produced

`SceneDrawSystem.Draw(finalLDRBuffer)` is patched at both ends. The **postfix** runs a
second, nested `Draw` with our own `ScreenBuffers` (512×512) and our own
`DrawContextManager` swapped into `CoreSystems`, from an orbit camera, into our own
`FinalLDRTexture`; the result is blitted to the LCD panel material.

A `[ThreadStatic]` guard (`_inOurRender`) makes the nested call re-entrancy-safe, and every
stage skip below is a Harmony prefix that only returns `false` while that guard is set — so
**nothing here changes the player's frame.**

`wholeSceneIntervalMs = 0`: the feed renders **every engine frame**. This is not a
throttle that was left off — throttling was measured as actively pathological (stale
temporal history, evicted caches, GI/denoiser cold-start every wake), costing 13-15 ms of
CPU submit versus 2.1 ms when running every frame.

---

## 2. Layers ACTIVE in the feed

| Stage | Engine entry point | Notes |
|---|---|---|
| 3 | `RenderShadows` | **Own** sun cascades — 2 × 512, fitted to the orbit camera, not the player |
| 4 | `ComputeExposure` | Runs, but exposure is read-only (see id 25) — no adaptation drift |
| 6 | `PrepareClusters` | Light cluster grid |
| 8 | `RenderDecals` | Decal atlas |
| 10 | `ExecuteLighting` | The whole lighting stage |
| 11 | `RenderMainView` | Depth pre-pass → GBuffer → grass → deferred texturing → terrain blending |
| 12 | `ComputeDirectionalLighting` | Sun + shadow mask |
| 13 | `ComputeLocalLights` | Clustered point / spot / capsule / area lights |
| 14 | `ComputeCloudShadows` | ⚠ see §5 — still writes a **shared** world-space resource |
| 15 | `UpdateAtmosphere` | Stage runs; the LUT write itself is skipped (id 24) |
| 17 | `RaytraceGIJob.DoWork` | **Full raytraced GI trace, in the feed** |
| 18 | `ComputeGI` | RT GI + `AmbientLightJob` (the feed's ambient term) |
| 22 | `CloudShadowJob.DoWork` | ⚠ see §5 |
| 23 | `CloudWeatherMapJob.DoWork` | ⚠ see §5 |
| — | Forward passes | Unlit, water pre-pass, volumetrics, transparent, water, stochastic transparency, highlights, holograms, OIT resolve |
| — | Post | Bloom **ON**, tone mapping, hole patching |

Also active and worth naming: **planet environment rebuild** (`wholeScenePlanetEnv = 1`)
and a **per-view far-clip override of 2500 m** applied to our camera only —
`VeryFarClipping` is deliberately untouched so planets and atmospheres survive.

---

## 3. Layers SKIPPED, and what each costs

Skip list: `0,1,2,5,7,9,16,19,20,21,24,25,26`

| id | Target | Cost to the feed | Why |
|---|---|---|---|
| 0 | `ExecuteAccelerationStructuresBuilding` | none observed | TLAS/BLAS already built by the player's frame this same frame |
| 1 | `ExecuteRaytracingPrepareAndSceneFinalize` | none observed | as above |
| 2 | `RenderEnvironmentProbe` | **no environment-probe or IBL rendering of its own** | avoids double-advancing the shared probe queue. **Leading suspect for the missing skybox — see §4** |
| 5 | `UpdateSurfels` | no water GI surfels | shared water surfel state |
| 7 | `ProcessParticles` | particles not re-simulated | correct: prevents the feed double-stepping particle simulation |
| 9 | `ExecuteHBAO` | **no ambient occlusion in the feed** | `HBAOJob` keys off the shared `ScreenBuffers`; previously an undiagnosed device removal |
| 16 | `DrawUI` | no HUD baked into the feed | required |
| 19 | `UpsamplingJob.PrepareResources` | — | ⚠ **see §5 — the source marks this DO NOT USE, yet it is enabled** |
| 20 | `IsFSREnabledAndAllowed` → `false` | **no FSR, and in practice no AA at all** — see §4 | an override, not a skip; takes the feed off the shared FSR3 upscaler without touching `AAMode` |
| 21 | `RenderFlares` | **no lens flares in the feed** | paired with sharing the engine's `FlaresContext`: we read flare definitions, never advance the shared occlusion readback |
| 24 | `AtmosphereLUTJob.DoWork` | no atmosphere LUT refresh of its own; uses the player's | shared, world-space, per-planet |
| 25 | exposure override | feed follows the player's exposure rather than computing its own | read-only override, not a skip |
| 26 | `CloudJob.DoWork` | **no volumetric clouds** | confirmed device removal otherwise: `ValidateHalfResTemporalResource` disposed and rebuilt a multi-hundred-MB resource 20×/sec at our half-resolution. User-approved as free |

**Settings scoped off for the duration of our render** (restored immediately after):

- **Eye adaptation** — off. Temporal auto-exposure history is shared; running it twice per
  frame at two different average luminances made the player's lighting flicker at our
  render cadence. The feed therefore has a fixed exposure, which for a camera is arguably
  correct.
- **Environment probe updates** (`ProbeSettings.Enable`) — off. Second suspect for §4.
- **Raytracing** — *nothing scoped.* `wholeSceneRtFlags` empty, `wholeSceneDisableRaytracing = 0`.
  The feed gets the full RT path.
- **AA mode** — *not scoped* (`-1`). Deliberate: scoping `AAMode` made `UpsamplingJob`
  dispose the shared FSR3 resources, which is what stage 20 exists to avoid.
- **Bloom** — on (`wholeSceneNoBloom = 0`).

**Patched but proven INERT** (left in place with comments, so ids don't shift):

- 27 `EnvironmentProbeManager.PrepareProbes`, 28 `LocalLightsManager.FlushUpdates` — both
  live in `DrawContextManager.OnBeginDraw`, which is called from `DrawInternal`, **not**
  from `Draw`. `ShouldSkipStage` is never invoked for either. They cost and do nothing.
- 29 `ScreenSpaceReflections.DoWork` — gated by `SettingsManager.SSSR` in
  `ExecuteForwardPasses`, which is off in the current in-game settings.

---

## 4. Known missing layers

### The skybox — NOT a feed gap. RESOLVED.

The feed had no skybox because **the player's whole render had no skybox**: a consequence
of the reduced in-game graphics settings used for this test. Nothing about the RTT route was
responsible, and no change is needed here.

This is worth keeping rather than deleting, because it is a clean confirmation of the
architecture:

- The sky *cubemap* (`CommonResources.VariousGroup._skyboxIBL`, surfaced as
  `EnvironmentProbeManager.CloseIBL` / `FarIBL`) is a **shared** engine resource that we
  deliberately do **not** swap. Whatever the player's frame puts there is what the feed
  reads. Sky off in the world ⇒ sky off in the feed, necessarily.
- `SceneDrawSystem.DrawSkybox`, called from `ExecuteLighting`, runs **only**
  `SkyboxMotionVectorsJob` — motion vectors for reprojection, **not** the sky's colour. It
  runs in the feed either way.
- `AmbientLightJob.DoWork` reads `CloseIBL`/`FarIBL` with **no gate on
  `ProbeSettings.Enable`**, so the feed's sky-derived ambient term survives our probe-update
  scoping. That is why ambient looked right while the sky was absent.

The relevant engine fields, if the setting ever needs to be found again:
`EnvironmentSettings.HideSkybox`, `SkyboxBrightness`, `Skybox` (a resource handle),
`IBLResolution`, `SkipIBLevels`.

**Method note.** I had this one lined up as a two-candidate call-graph hypothesis (unskip
stage 2, or stop scoping `ProbeSettings.Enable`) and was one step from proposing a config
test on the feed. Both candidates were wrong, because the premise — that the feed was missing
something the world had — was never checked. **Confirm the player's render has a layer before
investigating why the feed lacks it.** Cheaper than any amount of IL reading.

### Anti-aliasing — probably none at all

`ExecutePostPasses` calls `UpscaleTargetFSR` and `ApplyNonFSRUpscalingAndAA`
unconditionally; each self-gates. Stage 20 forces `IsFSREnabledAndAllowed` false and our
target resolution equals our `ScreenBuffers` resolution, so `UpscaleTargetFSR` takes its
early-out. `AAMode` is left at the player's value (FSR), so `ApplyNonFSRUpscalingAndAA`
very likely also self-gates to nothing. Net effect: **the 512×512 feed is rendered with no
AA**, which is the most likely source of visible aliasing. FXAA is spatial-only and needs
no motion vectors, making it the natural AA for this feed — but reaching it means setting
`AAMode`, and that field is exactly the one with three CTDs behind it.

### Other absences, all deliberate

No HBAO, no lens flares, no volumetric clouds, no own atmosphere LUTs, no own screen-space
reflections, no bloom-free reference (bloom is on), no HUD.

---

## 5. Open risks in this configuration

**Stage 19 is enabled but the source says DO NOT USE.** `wholeSceneSkipStages` contains
`19`, while the table comment in `RttPlugin.cs` marks it *"DO NOT USE. Kept so the ids
below do not shift"* and records a device removal from skipping it. The comment's stated
mechanism assumed `AAMode` was scoped to Bilinear; `AAMode` is no longer scoped at all
(`-1`), so skipping it now plausibly *prevents* our render resizing the player's FSR
resources — which may be part of why this build is fast. **Either way the comment and the
config disagree, and that must be reconciled rather than left to be rediscovered.**

**Stages 22 and 23 are still running.** `CloudShadowJob` and `CloudWeatherMapJob` write
`CommonResources.CloudShadowmap` and the weather map tables — shared, world-space, written
from whatever camera is rendering. The table comment predicts exactly the ghosting symptom
that was chased for a whole session. The phantom bleed turned out to be the 3840×2160
`FinalLDRTexture` instead, so these were never the cause — but they are still a live shared
write from the orbit camera, and a candidate for both a correctness and a perf win.

**Panel-freeze bug, undiagnosed.** After a signature config change the panel can show a
stale frame while every counter stays healthy. Workaround: toggle the panel off and on. One
attempted fix (forcing a rebind from `WholeSceneRender.Reset`) **crashed the game** —
`Reset` runs on the render thread and the rebind destroys and rebuilds a runtime material
mid-frame. Suspects still to test: whether `BlitProbe.FeedTarget` actually changes across a
`Reset`, and whether `CameraRender`'s cached `_feedTexture` / `_resolvedPanelId` go stale
independently.

---

## 6. Cheap fidelity wins to try next

Ordered by expected value against risk, all reversible:

1. **Skybox** — the two candidates in §4. Highest visual value, config-only.
2. **FXAA** — biggest apparent-quality gain per GPU cost at 512×512, but sits behind the
   `AAMode` knob with three CTDs behind it. Needs the paused-feed protocol.
3. **HBAO (stage 9)** — cheap and visually significant. Blocked on the same shared
   `ScreenBuffers` pattern as CloudJob; would need the CloudJob treatment rather than a
   plain unskip.
4. **Stages 22/23** — skip them: removes a shared world-space write *and* saves feed GPU
   time. Cost is the feed using the player's cloud shadows, which is already the accepted
   trade for stage 24.
5. **Far clip** — currently 2500 m with no observed visual loss. Worth an A/B lower, and
   worth confirming it is not itself clipping distant sky content.
6. **Cascade amortisation** — update one of the two cascades per frame instead of both.
