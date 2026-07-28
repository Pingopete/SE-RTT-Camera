# Whole-scene render — status and plan (2026-07-27)

The POC works: the panel shows the engine's full renderer from the orbit camera —
textures (incl. the probe-route-impossible asteroid triplanar), deferred lighting, sun
and flares, atmosphere both halves, correct 1:1 aspect, orbit-locked sky. Stable, with
the player's view intact. This file is the audit of what remains, agreed with the user.

## The feed today

| state | detail |
|---|---|
| ✓ in | MainView geometry + real triplanar terrain, deferred lighting on our own cluster grid, skybox/stars/planets/belts, sun + glare, atmosphere (in-scatter + extinction), volumetrics, transparency, bloom, tonemap, decals, **our own sun-shadow cascades fitted around the orbit camera** |
| ⚠ degraded | ambient + reflections = player-positioned probe atlas; exposure fixed (adaptation scoped off) but now tunable per-feed in EV; texture streaming driven by player position; no local-light shadows |
| ✗ out (fixable) | HBAO (stage 9 — CTD'd, see below), particles (stage 7 — user parked; needs sim-stepping investigation first) |
| ✗ out (by user decision) | raytraced GI/reflections — "not for now, that can be an extra later" |
| ✗ out (by scope) | probe updates, water surfels, HUD (deliberate) |

## User-observed glitches → diagnosis

| observation | diagnosis | status |
|---|---|---|
| blocks flash in brightness | decals missing | FIXED (stage 8 unskipped) |
| in-shadow areas flash bright↔dark | uncleared `DiffuseGIBuffer` read as recycled pool garbage | masked by the 10fps rate; see "latent" below |
| whole ship/objects go fully dark at orbit points | camera exits the player-centred cascade volume | FIXED (`wholeSceneOwnShadows`) |
| ghosting/smearing | FSR fed the player's previous-frame camera | FIXED (our own prev-orbit position) |
| squashed aspect, head-tracked sky | shaders read the camera CB, not the view | FIXED (`CameraCbSwap` around our Draw) |

## Own shadow cascades — how it works

`DrawContextManager.OnBeginDraw` is the engine's cascade setup, and it turns out to be
almost entirely **per-context**:

```
CascadeShadowsContext.FlushUpdates()
    reads    CoreSystems.Settings.RenderView          <- OURS while installed
    reads    Settings.Shadow.DirectionalLight, Settings.Light.Sun
    mutates  only its OWN _cascades / _cascadePriorities / _lastCameraPosition
    calls    Cascade.UpdateViewSetupFull(mainView, lightDir)   -> refits every frustum
DirectionalLightShadowResources.OnBeginDraw()
    reads    CoreSystems.DrawContexts.CascadeShadows / .CharacterShadows   <- OURS
    builds   the depth-map Texture2DTable + the setup constant buffer
```

So a second independent cascade set needed no new machinery — just those two calls made
while our view and our contexts are installed, plus stage 3 allowed to run.

**We do NOT call `DrawContextManager.OnBeginDraw()` itself**, even though it is the
engine's entry point, because it also runs `CoreSystems.LocalLights.FlushUpdates()` and
`EnvironmentProbeManager.PrepareProbes()` — global managers whose queues the player's
frame owns. Draining those twice a frame is the double-stepping bug class this project
has already paid for twice.

Leaving `LocalLightsToUpdate` / `ShadowMasksToUpdate` unset is safe: `Buffer<T>` is a
**struct** (`IntPtr _data, int _count, int _capacity`), so the unassigned field is a
zero-count buffer and `RenderLocalLightShadows` iterates it zero times. No local-light
shadows in the feed — a fidelity gap, not a fault.

Costs no extra VRAM: `CascadeShadowsContext`'s ctor calls `CheckShadowSettingChanged`,
which sees `_cascades.Length == 0 != CascadesCount` and allocates the full set. We had
been paying for those textures since the second manager was built, and rendering
nothing into them.

## Exposure — semantics, confirmed not guessed

`wholeSceneExposure` is an **EV bias in stops**, signed. From the shipped HLSL:

```
ConstantExposure.hlsl:   CalculateExposure(Post_.ConstantLuminance, Post_.LuminanceExposure)
Exposure.hlsli:          log2(keyValue(avgLum)/avgLum) + exposure
GetExposureLinear.hlsl:  exp2(that)
```

`ConstantLuminance` is 1 and `LuminanceKeyValueCendos(1)` is 1, so the first term is
exactly 0 — the field is a pure signed EV offset on unity. +1 doubles, −1 halves. It was
gated `> 0`, which made the knob brighten-only; a 512px feed pointed at a sunlit planet
needs to come *down* more often than up.

## Probe strip

The probe pipeline was the route; it is now scaffolding. It still runs a full cull,
GBuffer, deferred texturing, environment and lighting pass at 30 Hz plus an exposure and
tonemap chain, and `CopyToFeed` discards all of it in favour of the whole-scene image.

The only thing the whole-scene route takes from that pass is the **orbit transform**
(`OrbitViewSlim()` storing `_lastCamWorld` / `_lastViewD`), which is CPU work done before
any GPU work starts. `wholeSceneStripProbe` returns immediately after it and goes
straight to the ring/blit/handover.

Two parts, one already unconditional:

- **Tonemap skip (always on):** the probe tonemap chain ran and its output was
  overwritten by `blitSrc = wsSrv` on the very next line. Pure waste; now skipped
  whenever the whole-scene source exists. No visual change possible.
- **Full strip (`wholeSceneStripProbe`, default off):** skips the scene render too.
  Self-disabling — only applies while `PanelSource` is non-null, so the probe render
  comes straight back if the route errors or is switched off.

## What is still latent

**The uncleared `DiffuseGIBuffer`.** `ComputeGI` borrows it with no clear value, safe for
the engine because `RaytraceGIJob` overwrites every pixel. With raytracing fully off
(mode 1, which the user wants kept), `AmbientLightJob` reads a recycled uncleared pool
texture. The flashing *looks* fixed because at 10 fps we recycle the same pool texture
consistently — the garbage is stable rather than varying. It could resurface under
different GPU load.

Mode 2 (keep `Enabled`, clear only the world-space accumulators) was the principled fix,
but the user has ruled raytracing out for now. **The alternative that respects that
constraint is to clear the buffer ourselves** rather than turn RT back on. Not yet built.

## Plan

1. ~~unskip decals~~ done · ~~temporal hygiene~~ done · ~~feed exposure knob~~ done
2. ~~own shadow cascades~~ done — verify in game, then consider mode 2 (force all)
3. **probe strip** — flip `wholeSceneStripProbe`, expect zero visual change + GPU refund
4. **clear the GI buffer ourselves** — the RT-free fix for the latent ambient garbage
5. **own or double-buffer a probe set** — the last borrowed-lighting item (reflections
   and ambient still come from the player's position)
6. LATER — player-view flicker (parked by user); particles (parked by user); HBAO
7. LATER — raise the rate once the strip has paid for it

## Standing rules (hard-won, do not relearn)

- Own the contexts → RUN the prepare stages; producers (4, 6, 14, likely 15) cannot be
  skipped. Owning a resource means rendering into it — that is why `wholeSceneOwnShadows`
  force-runs stage 3.
- View feeds culling, camera CB feeds shaders — swap both.
- Never let the installed view's `_resolution` diverge (race → CTD); mode 2 handles it.
- Resizable engine textures go to the panel via the CopyJob blit, never the raw copy.
- CTD leaves `output/handover-live.marker` → delete or the feed is blank.
- Never call a global manager's `FlushUpdates`/`Prepare` a second time per frame. Reach
  into the per-context object instead.
- ONE change per test. Bundling has cost this project three CTDs it could not attribute.

## Bleed into the player's world — the current hunt

**Established by measurement, not argument.** The feed gate (see below) makes a
mod-free frame one panel toggle away. With the panel off the main world is *completely*
stable; with it on, jittering aliasing and phantom images of the feed return. It is us.

**The gate itself had to be fixed first.** It inferred "panel alive" from the LCD render
component ticking — but switching a panel off does not stop it ticking, it keeps ticking
to draw the powered-off screen. The gate never fired and an entire A/B was run against a
fully active mod. It now reads `LcdPanelSurfaceContext.CurrentMaterialState`
(`PowerOff=0` / `DefaultScreen=1` / `CustomRender=2`), which the engine states outright.

### Fixed

- **Jittering aliasing** — `ScenePreparation` runs inside our Draw and calls
  `UpsamplingJob.PrepareResources`, which calls
  `_fsr3_1.PrepareResources(maxRenderResolution, displayResolution)` with OUR 512×512.
  `TryCreateContext` recreates the FSR context when dimensions change, destroying its
  temporal history — ours at 512, the player's at 4K, ten times a second. Skip stage 19.
  Safe only because our final target and our `ScreenBuffers` are both 512×512, so
  `UpscaleTargetFSR` early-outs and `ApplyNonFSRUpscalingAndAA`'s
  `resolution != PreUpscaleResolution` gate is false. An earlier attempt page-faulted
  because `AAMode` was scoped to 0 then, sending us down the bilinear path whose
  resources the skip never allocated.

### Ruled out (do not re-check)

- **Environment probes / IBL.** `RenderEnvironmentProbe` (stage 2, skipped) is the ONLY
  caller of `ExecuteEnvironmentProbeUpdate` and `RenderPendingIBL`.
- **Pooled-texture aliasing.** `ResizableRWRenderTargetTextureKey` includes
  `MaxResolution` and excludes the debug name, so our 512 borrows and the player's 4K
  borrows land in different buckets and cannot alias.
- **Panel emissive light.** The bleed appears everywhere, with no relation to line of
  sight to the panel.

### Open

- **Phantom feed images on world surfaces.** Still unexplained. `CommonResourcesManager`
  owns the remaining shared surfaces — `CloudShadowmap`, `WeatherMapTables`,
  `AtmosphereLUTTables`, `SkyboxIBL`, `PlanetSpheres`, `PlanetEnvSetup*`. **But note the
  ordering caveat below before building anything on them.**
- **The feed's planet atmosphere is positioned by the PLAYER's aim.** `PlanetSpheres`
  and `PlanetEnvSetupFirst` are built from the player's camera in `CommonResources`'
  `OnBeginDraw`, before our Draw, and our nested render inherits them. Identical bug
  class to the camera constant buffer — that one was fixed, these were never touched.
  Fixing it is also a prerequisite for good RT ambient in the feed, since the GI solve
  reads the environment setup.

### THE ORDERING CAVEAT (check this before blaming any shared write)

Within one engine frame our commands are recorded AFTER the player's. So the player's
consumer reads the resource their own producer just wrote, and our later overwrite
cannot reach backwards into it. A shared write can only bleed if the resource is
**progressive, scrolled, or accumulated across frames**. This should be established for
a given resource before any work is done to stop writing it.

### Do not bisect by removing pipeline stages

Skipping stage 11 to see whether the bleed carried image data CTD'd immediately:
`RenderMainView` populates the GBuffer and every consumer of it still runs. Our render
is a *pipeline* — almost every stage produces something, so removing one to see what
changes will usually break a consumer rather than answer the question.

**Bisect by changing PARAMETERS.** `wholeSceneIntervalMs` removes nothing and makes the
bleed's *timing* observable, which no hypothesis had tested:

- phantom updates in discrete steps in lockstep with the feed → our **output** is being
  written where the player's frame samples it (a buffer we write and they read)
- phantom shimmers continuously between our renders → our **presence** perturbs
  something temporal that then evolves on its own (an accumulator we disturb)

Those need entirely different fixes, and every theory so far assumed the first.

### RT in the feed is a bounded change, not an open problem

`RTGIContext` holds `TemporalResources` (the ReSTIR reservoirs), `PreviousScreenDepth`,
`PreviousScreenNormals`, `DiffuseProbes`, `Specular`, `DiffuseDirect` — the entire RT
temporal state — and it lives on `DrawContextManager.RTGIContext`, **which we already
own**. `RaytraceGIJob.TryPrepareWork` does its `ResizeAndSwap` ping-ponging on whichever
context is passed in, and during our render that is ours. **The reservoirs were never
shared.**

The only shared RT state is `IRCacheResourcesManager`, which sits under `Core.Systems`
rather than `DrawContexts` — a global world-space irradiance cache. Scoping
`EnableIRCache` + `EnableIRCacheScrolling` covers it, and both were in the mode-2 set
already proven not to cause the bright flashing.

So: un-skip 17, scope those two flags, leave everything else alone.

### Stage 24 never fired

`AtmosphereLUTJob.DoWork` only runs for atmospheres flagged dirty (`GetAndClearDirty`),
which is rare. We were never writing those LUTs. The atmosphere-LUT bleed theory is dead
rather than untested.

### Ray tracing in the feed — what it took

**RTGIContext is ours already.** It holds `TemporalResources` (the ReSTIR reservoirs),
`PreviousScreenDepth`, `PreviousScreenNormals`, `DiffuseProbes`, `Specular`,
`DiffuseDirect` — the whole RT temporal state — and lives on
`DrawContextManager.RTGIContext`. `RaytraceGIJob.TryPrepareWork` does its
`ResizeAndSwap` on whichever context is passed in. The reservoirs were never shared.

**Never scope `RaytracingSettings` at all.** `EnableIRCache` is a field of `RTGISettings`
— the struct `RaytraceGIJob` keys its `LazyJobSnapshotHandler` on — and
`BuildTraceShaderDefines` turns it into the define `ENABLE_IRCACHE`. Flipping it per
render means `LazyJobSnapshotHandler.Update` → `CreateSnapshotAsync`, an **async PSO
compile**, twenty times a second. That is the bright-flashing mechanism *and* the
async-races-the-recorder shape that removed the device twice. Any temporal accumulator
running against a snapshot in perpetual rebuild cannot converge.

**And the guard was unnecessary.** `IRCacheTraceJob.DoWork` is the only caller of
`SwapDataBuffers`/`SwapIrradianceBuffers` (the shared world-space cache ping-pong), and
it is invoked from `SceneDrawSystem.RaytracingPrepare` inside
`ExecuteRaytracingPrepareAndSceneFinalize` — **stage 1, which we already skip**. Our
render never runs the IR cache. It samples the one the player's frame maintains, free
and consistent.

So: un-skip 17, leave `wholeSceneRtFlags` **empty**, keep stage 1 skipped. Done.

### The camera CB's previous-view matrix was ZERO

`TrackedCameraSettings.PreviousCamera` is written by exactly two methods:
`SettingsGroup.CreateCameraSettings` and one surfel job. Our CB is built through
`CameraSettings.op_Explicit`, which is neither — so `PreviousCamera_.ViewTransform` was
`default`, i.e. the zero matrix, on every render the feed has ever done.

Everything doing temporal reprojection reads it. `SkyboxMotionVectorsPixel.hlsl`:
`mul(positionWorld, (float3x3) MatrixFromWorldTransform(PreviousCamera_.ViewTransform))`.
The RT denoiser reprojects the same way. Through a zero matrix nothing lands where it
should, history never matches, and the accumulator discards it every frame — a
permanent-noise machine, not a converging one.

Fixed by holding the `PreviousCameraSettings` built from OUR view last render, stamping
it into this render's CB, then recording this render's for next time. The engine pairs
consecutive frames; ours pairs consecutive *second renders*, which is the right interval
for our own history. See `CameraRender.StampPreviousCamera`.

### The job-skip rule does not generalise

"Skip the JOB, not the STAGE" works for `RaytraceGIJob` (17): `ComputeGI` borrows the
buffer itself and `AmbientLightJob` reads it either way. It FAILED for the cloud path —
skipping `CloudShadowJob.DoWork` page-faulted in `CloudShading` at
`PageFaultVA 0x3A7206000`, the same site as skipping stage 14. That job is not merely a
writer of shared state, it is the producer of the per-frame resource its consumer reads.
Establish who produces what before skipping anything.

## Dead ends, recorded

- **HBAO (stage 9)** — unskipping removed the device within two seconds
  (2026-07-27 19:26:34, `DXGI_ERROR_DEVICE_REMOVED`, `PageFaultVA 0x0`, breadcrumb
  1174/1635 in "ScenePreparation + Render", stopping at the `ClearRenderTargetView`
  right after ToneMapping). Not diagnosed further — small fidelity win, wrong fight.
- **DRS / AA mode** — not safely switchable per-render. Three CTDs in
  `ForwardAndPostPasses`, the last with `wholeSceneAAMode` isolated. `UpscaleTargetFSR`
  produces temp buffers bloom/tonemap consume; switching branch leaves consumers reading
  unpopulated buffers.
- **`mainViewCulling` as a swap** — malformed by construction (disjoint pass groups).
- **Material-state shader swap** — needs a coherent whole-state change, and it is a
  game-install edit, not shippable.

## CloudJob: the resolution-keyed reallocation (2026-07-28, CONFIRMED)

The most consequential finding since the route started working, because it is at once a
crash cause, a performance cost, and a shared-state leak of exactly the shape the bleed
hunt was looking for.

`CloudJob.DoWork` calls `ValidateHalfResTemporalResource`, which decompiles to:

```csharp
var halfMax = CoreSystems.ScreenBuffers.MaxPreUpscaleResolution / 2;
if (resource.PeekNext().MaxResolution != halfMax) {
    resource.Dispose();                                   // free the player's history
    resource = new TemporalResource<ResizableRWRenderTargetTexture>(
        () => BindableTextures.CreateRWResizableRenderTargetTexture(name, format, halfMax),
        (cl, a) => a.Resize(cl, CoreSystems.ScreenBuffers.PreUpscaleResolution / 2));
}
```

It keys off `CoreSystems.ScreenBuffers` — the global our render swaps. Ours is 512x512 so
`halfMax` is 256x256; the player's 3840x2160 gives 1920x1080. Every one of our renders
therefore disposes the player's cloud accumulation buffer and rebuilds it at 256, and the
player's very next frame does it straight back.

Evidence, from DRED rather than inference:

- breadcrumb `[15] ForwardAndPostPasses: 20/255`
- `EventStack: [CloudShading, ForwardPasses, ForwardAndPostPasses]`
- `PageFaultVA: 0x1B54406000` — a **real address**, so a use-after-free. Every previous
  device removal on this project faulted at `0x0`, a null bind. Different family.
- 360 allocation nodes in the dump, **every one of them `CloudAccumulateLightAlpha`**
- the `+/-151MB` VRAM oscillation visible in every PERF line is this, not a leak

### Why owning DrawContextManager did not already cover it

Every other resolution-keyed context hangs off `DrawContextManager`, which we swap:

| context | owner | our render |
|---|---|---|
| `VolumeRenderingContext` | `DrawContextManager` | ours — safe |
| `RTGIContext` | `DrawContextManager` | ours — safe |
| `StochasticTransparencyContext` | `DrawContextManager` | ours — safe |
| `WaterContext` | `DrawContextManager` | ours — safe |
| **`CloudJob`** | **`SceneDrawSystem._cloudPass`** | **shared — thrashes** |

`SceneDrawSystem` is a singleton we do not swap, so its ~60 job fields are all shared with
the player. `CloudJob` is the only one of them that owns a resource keyed on
`MaxResolution`.

### The shape to grep for

Reading `MaxPreUpscaleResolution` is harmless. Thirty-eight methods do it. The dangerous
shape is **`Dispose()` + `Create*` keyed on `MaxResolution`**. Checked and cleared:
`HBAOJob.DoWork`, `HighlightJob.DoWork`, `TerrainBlendingJob.DoWork`,
`AtmosphereAdditiveJob.DoWork` — all only call `Resize(commandList, res)` on a borrowed
pool texture, which is the designed per-frame path. `CloudJob` is alone.

This also retro-explains the undiagnosed **stage-9 HBAO** device removal above: same
family, same global, and `HBAOJob` is likewise a `SceneDrawSystem` field.

### The fix and its cost

Skip id 26 (`PostProcessStage.CloudJob.DoWork`) inside our render only. Costs the feed its
own volumetric clouds; it uses whatever the player's frame last accumulated. User confirmed
this is free — planet atmospheres come from `AtmosphereAdditiveJob` / `AtmosphereMultiplyJob`
and the planet-env rebuild, none of which this touches.

Note this is a *different* type from stages 22/23, which were backed out:
`PostProcessStage.CloudJob` vs `LightingStage.CloudShadowJob` / `CloudWeatherMapJob`.

## Reading the frame-time numbers

The average is misleading and has been quoted misleadingly. Measured 2026-07-28 at
`wholeSceneIntervalMs = 100`:

```
ours n=49 mean=52.1 p50=54.7 p95=59.4 >50ms=40 | idle n=203 mean=12.3 p50=10.8 >50ms=0
```

Ten frames a second take ~55ms; the rest take ~11ms. "50 fps" is the arithmetic mean of a
bimodal distribution — the game is alternating ~90fps and ~18fps, and that is what reads as
choppy. `ourDraw(cpu submit)` accounts for ~21ms of the 55; the remainder is GPU.

So the levers are the *cost* of one render or the *rate* of them, not anything that would
move the average. Re-measure after stage 26, since the CloudJob thrash inflated both.

## Correction: stages 27/28 are inert (2026-07-28)

Recorded because the reasoning was plausible, took a game restart to disprove, and would
otherwise be re-derived from the same evidence.

At `wholeSceneIntervalMs = 33` the player's frame took a device removal in
`ExecuteEnvironmentProbeUpdate`: breadcrumb `ScenePreparation + Render` 1010/1474,
`EventStack: [EnvironmentProbes, ScenePreparation + Render]`, `PageFaultVA 0x0` with
`ExistingAllocations 0` and `RecentFreedAllocations 0` — a null bind, the opposite signature
to the CloudJob use-after-free, so a distinct bug.

`DrawContextManager.OnBeginDraw` reads two `CoreSystems` globals
(`LocalLights.FlushUpdates()`, `EnvironmentProbeManager.PrepareProbes()`), and
`PrepareProbes` stores `_lastSettings` / `_forceReprocess` / `_state` and can
`DisposeTextures()` + `RecreateProbes()`. That looked exactly like the CloudJob shape, so
skip ids 27 and 28 were built for them.

**They never fire.** `OnBeginDraw`'s only caller is
`Render12EngineComponent.<Draw>g__DrawInternal` — the engine's OUTER loop, once per frame,
BEFORE `SceneDrawSystem.Draw`. Our nested Draw starts below it and never reaches it. Proof
is cheap and conclusive: `ShouldSkipStage(27)` was invoked zero times across a whole session
with 27 in the live skip list, while 20/21/25/26 logged normally.

**Lesson: confirm a call path is inside our render before building a stage for it.** The
skip log answers that in one session, and `eq callers <method>` answers it offline in
seconds — `-- 1 callers` naming a type outside `SceneDrawSystem` is the tell.

### What the evidence actually points at

The crash came 2.1 s after the config change, with `secondRenders=1` — the FIRST second
render after `WholeSceneRender.Reset()` rebuilt our ScreenBuffers and DrawContextManager,
not after any accumulation. And the DRED `OutstandingOps` showed a LARGE queued batch of
`EnvProbe_Blending` passes, i.e. the probe system mid mass-reprocess. `_forceReprocess` has
exactly two writers: `PrepareProbes` and `OnResetContext`.

So the working hypothesis is a REBUILD TRANSIENT, not a rate problem: the rebuild trips a
context reset, the shared probe manager force-reprocesses every probe, and at a 33 ms
interval our second render lands inside that batch while the cube textures are being
recreated. At 100 ms there are ~3 engine frames of slack and we never land in the window.

Discriminating test, no code required: put `wholeSceneIntervalMs = 33` in the config BEFORE
launching, so it is the boot value and no mid-session `Reset()` ever happens. Stable means
the rate is fine and the fix is a settling delay after `Reset()` before the first second
render (the `StartupDelayMs` pattern). A crash means the rebuild hypothesis is dead too, and
the next step is instrumenting `EnvProbesToUpdate.Count` per frame rather than inferring
batch size from `OutstandingOps`.
