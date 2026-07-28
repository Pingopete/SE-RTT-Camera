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
