# Routes: what to try next, ranked

Product of a static deep dive over the shipped assemblies (2026-07-26): eight parallel
lines of enquiry, each load-bearing claim adversarially re-checked against the IL, plus
a completeness critic. 41 agents, ~1300 tool calls. Claims marked **[verified here]**
were re-checked by hand afterwards because they are load-bearing and cheap to confirm.

Two discoveries reframe the project before any route is discussed.

## Discovery 1: the shipped HLSL is on disk

```
D:\SteamLibrary\steamapps\common\SpaceEngineers2\VRage\GameData\Engine\Shaders
```

660 files, ~99% of the shader tree (3 of 248 registered shaders are DXIL-only). Every
question of the form "what does this pass actually compute" is now *readable* rather
than inferable. The assemblies remain the only source for what a pass is **bound to** or
**gated on** — which is what decides reusability — so the two have to be read together.

Settings are on disk too, at
`GameData\Engine\Assets\Core\DefaultFrameSettingsConfiguration.def`.

## Discovery 2: RETRACTED — there is no material-state ceiling

**This section originally claimed the probe path could not draw 59% of the world's
opaque materials, that voxel terrain was among them, and that frame interleaving was the
only way to reach it. All three were wrong.** Recorded rather than deleted because the
way it was wrong is worth not repeating.

The count filtered on `PassGBuffer` and *then* asked which of those also had
`PassIndirect`. That excluded exactly the states the engine authors **for** the indirect
path — which declare `PassIndirect` and no `PassGBuffer` at all:

```
TriplanarGIGlobal.def
  "Flags": "StateBackfaceCulling, StateZWriteEnabled, PassIndirect, RayTracing, MarkVoxelStencil"
```

`TriplanarGIGlobal` and `SphericalTriplanarGIGlobal` are the voxel-terrain indirect
variants; `PBRBlendedFlora` and `PBRBlendedFloraInstanced` are the flora ones. The engine
maintains simplified indirect variants on purpose — environment probes need terrain in
them too. The count also read only the root directory, 98 of 153 files.

Correct numbers, over all 153 shipped material states: **39 declare `PassIndirect`,
71 declare `PassGBuffer`.** Asteroids, planet surfaces and voxel terrain draw in the feed
today, which is the observation that exposed the error.

What *is* genuinely absent from the indirect path — `PassGBuffer` with no indirect
sibling — is a much narrower list: plain skinned characters (armor-skinned has
`PassIndirect`, so ship and player armor is fine), alpha-blended glass, non-cutout
parallax, tessellated terrain detail, water particles. **All of those carry
`PassGBuffer`, so they are reachable through the deferred path — not only through frame
interleaving.**

**The lesson:** a set-difference over a filtered subset answers a different question from
the one asked, and it answers it confidently. The claim survived an adversarial verify
pass and two hand-checks because every individual number in it was correct. What was
wrong was the population.

---

## Tier 0 — free, one-line, in code we already own

All four are changes to arguments we already pass or fields we already own. No global
mutation, no new pass, no engine state touched. Do them one at a time.

### 0.1 `Screen.Resolution` is the player's, so everything is zoomed 7.5×

The largest single defect found, and it is one field.

`CameraSettings::op_Explicit(in CameraSettings) → TrackedCameraSettings` stamps
`CoreSystems.ScreenBuffers.PreUpscaleResolution` — the player's 3840×2160 — into
`Screen.Resolution`. We call it at [CameraRender.cs:484](../src/RttProbe.Logic/CameraRender.cs#L484)
to build our camera CB, *before* the resolution swap. So every shader that reconstructs
a view ray does:

```hlsl
ScreenToUV() = rcp(Screen_.Resolution)      // 1/3840, while rasterising 512
```

3840/512 = **7.5**, which is a quantitative match for the reported "sky too zoomed,
rotating much too fast". But it is not only the sky: `Pass_Pixel_Indirect.hlsli` uses the
same `ScreenToUV`, so **view vectors, specular response, depth-based ambient fade and the
`DimDistance` term in the geometry pass are all mis-scaled too**.

The engine's own probe path avoids this by hand-building the struct with
`Screen.Resolution = renderTargetFace.Resolution`.

**Fix:** after building `tracked`, overwrite `Screen.Resolution` with our render
resolution before creating the CB.
**Signal:** the sky's angular rate matches the orbit 1:1.

### 0.2 We render every mesh at the coarsest LOD

**[verified here]** `DefaultFrameSettingsConfiguration.def`:

```json
"MainView":         { "LODShift": 0, "MinLOD": 0, "FloraMinLOD": 0 },
"EnvironmentProbe": { "LODShift": 0, "MinLOD": 8, "FloraMinLOD": 8 },
```

We pass `Settings.LOD.EnvironmentProbe` to our own `CullingJob.DoCullingFirstPass` call,
because we copied the probe recipe. `MinLOD: 8` clamps **everything** to LOD 8 — a
coarse-to-full mesh swap, not a subtle tweak.

**Fix:** pass `Settings.LOD.MainView` instead. One argument, in a call we already make.
**Signal:** block and hull detail appears; silhouettes stop being faceted.

This is the best fidelity-per-risk change on the entire list.

### 0.3 The camera CB tells shaders the camera is at the world origin

We build the CB via `RenderViewSlim → CameraSettings` (`op_Implicit`). That conversion
writes 7 of `CameraSettings`' 14 fields and leaves these at **zero**:

```
ViewTransform, InvViewTransform    <- the double-precision camera world transform
TanFOV, FOVScaleFactor             <- LOD/mip selection, specular and AO scaling
PositionDelta, CameraSpeed, CameraFlags
```

Worse, it is not self-contained: it sets
`MainViewCameraPos = Transform(CoreSystems.Settings.RenderView.CameraPosition, ourViewD)`
— **the player's camera position**, in our view space. That field is read by
`SpherizationCommon.hlsli` (planet horizon curvature) and `TriplanarSingleVertex.hlsl` /
`TriplanarMultiVertex.hlsl` (voxel surface texturing), i.e. exactly the planet-look
features being complained about.

**Fix:** build a real `RenderView` and use the public static
`CameraSettings.CreateNonjitteredCameraSettings(in RenderView)`, which fills all 14 and
sets `MainViewCameraPos = Vector4.Zero`.
**Signal:** log `TanFOV` and `FOVScaleFactor` — currently 0, should be ≈`tan(0.61)/tan(fov/2)`.
No render change needed to prove it.

Two traps, both confirmed in IL:
- `RenderView` is a struct whose one reference field is `Queue<double> _cameraSpeedBuffer`,
  **shared by every copy**. `SetCameraParameters(…, smooth: false, …)` reaches
  `ResetContext()` → `.Clear()` and wipes the *player's* speed history through our copy.
  Pass `smooth: true`, or null the queue on our copy first.
- Never touch `LastUpdateWasSmooth` on the global view:
  `ScreenBuffers.GetCurrentFrameRenderTarget()` reads it and, when false, diverts the
  **player's whole frame** to `FinalLDRPlaceholder` for `CameraJumpWaitFrames` frames.

### 0.4 Two shipped config values crush the image

**[verified here]** `DefaultFrameSettingsConfiguration.def` → `ProbeSettings`:

```json
"DimDistance": 5,
"EnableRecursiveReflections": false,
"LocalLightAmbientScale": 0.1,
"LocalLightAmbientMaxClamp": 0.3,
```

**[verified here]** `Pass_Pixel_Indirect.hlsli`:

```hlsl
ShadeForward(surface, input.PositionScreenSpace, LocalLightAmbient, diffuse, specular);
float dimFactor = clamp(surface.ZDepth / Environment_.DimDistance, 0, 1);
dimFactor *= dimFactor;
float3 shadedColor = (diffuse + specular) * dimFactor;
```

Everything within 5 m of the camera is multiplied by `(z/5)²` — at 1 m, ×0.04. This
exists so a probe does not contaminate itself with the hull it sits inside.

**Caveat the dive overstated:** our test camera orbits ~100 m out, so `dimFactor` is
already 1.0 for the target ship and this is *not* what is crushing the current image. It
becomes the dominant defect the moment the camera is mounted near structure, which is
the actual use case for a security camera. Fix it, but do not expect the orbit test to
change.

`EnableRecursiveReflections: false` is more immediately relevant — it makes
`IndirectEnvironmentPassJob` bind a **flat default cubemap** instead of the real
`CloseIBL`/`FarIBL`, so our ambient term is a constant. Both are plain public settable
fields; set-and-restore around our pass, because they are global and also affect the
player's own probes.

Note this also corrects a belief in [architecture-plan.md](architecture-plan.md):
`ShadeForward` **does** call `AmbientLight()`. The probe path is not ambient-free by
construction; its ambient is real but bound to a flat placeholder.

---

## Tier 1 — the master lever

### 1.1 Swap the global camera constant buffer around our pass block

`CommonResources.SettingsGroup._jitteredCameraSettings` /
`._nonjitteredCameraSettings` are private `Nullable<TransientConstantBuffer>` fields
written by exactly one method (`SettingsGroup.OnBeginDraw`) and read by **~92 passes** —
including most of what this project has been calling the "unsafe family":
`AmbientLightJob`, `DirectionalLightJob`, `AtmosphereMultiplyJob`, `ToneMappingJob`,
`LocalFogJob`, `ScreenSpaceReflections`, `GBufferPassJob`, `ClusteringJob`,
`LocalLightsJob`.

This corrects the project's own rule. "Reads the global camera CB" is **not** what makes
a pass unusable — `ClusteringJob` and `GBufferPassJob` do it and are already proven safe
here. What actually correlates is `ldsfld CoreSystems.ScreenBuffers` plus binding
`GBuffer[0..4]` and the global depth — which the existing GBuffer/depth swap already
addresses.

So the combination *(GBuffer swap + depth swap + camera-CB swap)* is the real unlock, and
two thirds of it are already proven at 29 fps.

Access is two non-public hops: `CoreSystems.CommonResources` →
`CommonResourcesManager._settingsGroup` → the two fields. Name trap: the property on the
group is `NonjitteredJitteredCameraSettings` (Keen's typo), not `NonjitteredCameraSettings`.

**Restore in a `finally`.** `OnEndDraw` disposes whatever is in the field; leaving ours
there disposes ours and leaks the engine's, and writing null makes the getter throw.

**First test:** swap only `_jitteredCameraSettings` around the *existing*
`IndirectPlanetEnvironmentJob` call, which takes its CB as an explicit parameter — so the
output must be **identical**. That proves the swap/restore mechanism is inert before
anything depends on it.

### 1.2 Then: atmosphere and ambient occlusion become reachable

With 1.1 in place, in order of value:

- **`AtmosphereMultiplyJob.DoWork(cl, rtView)`** — the *missing companion pass*.
  `IndirectPlanetEnvironmentJob` (which we already call) is `BlendState.Additive` and
  supplies in-scatter only; geometry never gets aerial-perspective extinction and there
  is no atmospheric sun disc. Multiply is the other half.
- **`HBAOJob.DoWork(cl, in HBAOSettings, rtView, depthTexture, normalTexture)`** — depth
  and normals arrive as explicit parameters, and we already produce a 512×512 GBuffer.
  Caveat: AO reaches pixels only through **deferred** lighting —
  `Lighting/ConvertToSurface.hlsli:38`, the line that would feed screen-space AO into the
  forward path, **is commented out in the shipped shader**. On the current forward feed,
  HBAO output would be computed and ignored.
- **`AmbientLightJob`** — the real IBL ambient. Still blocked by an unexplained
  `InvalidCastException (ResizableRWBuffer → IConstantBufferView)` that the dive could not
  localise; the camera CB is not the cause.

### 1.3 Corrections to closed doors

- ~~**`DrawSkybox` will never produce sky.**~~ **WRONG — retracted.** The claim was made
  from the job's name and its profiler label. The shipped pixel shader has **two**
  outputs:

  ```hlsl
  // PostProcess/SkyboxMotionVectorsPixel.hlsl
  struct PS_OUTPUT {
      float3 SkyLight      : SV_Target0;   // skybox stars AND the sun disc
      float4 MotionVectors : SV_Target1;
  };
  ```

  `GetSkyLight` computes `SkyboxColor(...) * SkyboxBrightness`, then composites the sun:
  `GetSunAlpha` / `GetSunColor` with `Environment_.SunDisc{Color,Intensity,InnerDot,OuterDot}`.
  It is the **only** pass in the shipped tree that draws the sun disc in space —
  `grep -rl "GetSunColor\|GetSunAlpha"` returns just `SkyboxSampling.hlsli` (the
  definitions) and this file (the only caller).

  It was inert when we drove it because it binds `CommonResources.JitteredCameraSettings`
  — the **player's** camera — plus `ScreenBuffers.GBuffer[Motion]` and
  `DepthStencilReadOnly`. Those are exactly what `swapCameraCb` and the GBuffer/depth
  swaps now provide, so `skyMode = 2` is worth retrying rather than abandoned.

  **Why the feed has no sun:** our sky comes from `IndirectPlanetEnvironmentJob`
  (`Lighting/IndirectAtmosphere.hlsl`), which includes `SkyboxSampling.hlsli` and draws
  `SkyboxColor` — the stars — but never calls `GetSunColor`. That is deliberate: an
  environment probe is the *input* to ambient lighting, and baking a blazing sun disc into
  the ambient cube would double-count a sun that is already applied as a directional
  light. The probe path is not missing the sun by accident.

  Separately, the **glare** around the sun is not the disc: that is `RenderFlares` (a
  distinct pass reading `MainOutputGeometryBuffers`) plus real bloom, and the feed
  currently runs `cheapBloom`, a flat 64×64 stand-in that cannot bloom anything.
- **`AtmosphereAdditiveJob` is not atmosphere.** It is gated on `EnableGodRays` — it is
  the god-ray/light-shaft term. With god rays off it is a guaranteed no-op regardless of
  arguments. "Invokes cleanly, adds nothing" is fully explained.
- **The near/far sky-target theory was wrong.** `IndirectAtmosphere.hlsl` ends
  `closeProbe = result; farProbe = result;` — byte-identical. But keep two distinct
  targets anyway: the PSO is additive with `IndependentBlendEnable = false`, so binding one
  texture to both RTV slots is a simultaneous-write hazard.

---

## Tier 2 — the display path (independent axis)

**[verified from shipped HLSL]** `LCDPixel.hlsl`:

```hlsl
float metalness = cm.Values.w;                                  // feed alpha IS metalness
output.Emissivity = saturate(ext.Values.y - 1/255.) * materialInstance.EmissivityMultiplier;
```

This settles the long-running alpha question: **both claims were true of different
textures.** Our feed's alpha (in `ColorMetalTexture`) is metalness. The *GBuffer0* alpha
is exponentially-packed emissivity. Same channel index, different texture — hence
`GBufferIndex` reporting `BaseColor=0, Emissivity=0`. Writing high alpha does not make
the feed emit; it makes the panel a mirror. (Already handled — `blitAlpha=0`.)

The panel **is** emissive: `EmissivityMultiplier` is 10 on `LCDScreen_On`, and emissivity
is added to the HDR light buffer (`specular += basecolor * Emissivity * Post_.BloomEmissiveness`)
before bloom (threshold 5) and Hable tonemapping. So the display has real headroom and
already feeds the engine's own bloom.

But `SetNewScreenMaterialHandle` overrides **only** `ColorMetalTexture`. Emissivity comes
from the base material's `ExtensionsTexture` green channel, which our feed cannot write —
so what we get is a **uniform ×10 gain on an image already clamped to 1.0 by GBuffer0.rgb**,
not per-pixel HDR.

- **2.1 (one line):** `ctx.ScreenMaterial.EmissivityMultiplier = 200f` after the bind.
  Public setter, calls `MaterialChanged()`. Either the panel blooms dramatically —
  confirming the path is live with headroom — or nothing happens, which means the stock
  extensions green is ~0 and binding our own is mandatory. Decisive either way.
- **2.2:** `PBRMaterialDefinition.ExtensionsTexture` is also a public setter. Binding our
  own extensions plane gives genuine **per-pixel** gain — a two-plane (chroma + exponent)
  HDR encode into a path that already reaches the main HDR buffer. This is the real fix
  for "HDR look", and it is independent of every render-side route.

---

## Tier 3 — completeness at zero marginal cost

**Ruled out by the user, and Discovery 2's retraction removes most of its motivation.**
Kept for the mechanism, which is sound and well-evidenced.

What this buys is no longer "the only route to a complete world" — it is completeness at
zero marginal GPU cost. Every specific feature it would deliver is reachable another way:
the material states it would unlock all carry `PassGBuffer`, so the deferred path reaches
them, and the temporal effects (TAA/FSR, SSR, adaptive exposure) would need per-view
history built either way.

### 3.1 Frame interleaving through the game-thread camera

Stop rendering a *second* view. Alternate which camera the engine's **one existing,
unmodified, full-fidelity `Draw`** uses.

```
prefix  CameraComponent.UpdateRenderSettingsInternal(in WorldTransform wt)
          -> substitute the RTT camera's transform (+ _customFov)
        ScreenBuffers._cameraJumpWaitFrameId = FrameSpan.FrameId + 1
          -> GetCurrentFrameRenderTarget() returns FinalLDRPlaceholder
postfix SceneDrawSystem.Draw
          -> copy the placeholder into our 512x512 offscreen target
next frame: pass the real camera through untouched; player's frame presents normally
```

Why it works: `DrawInternal` hands `GetCurrentFrameRenderTarget()` to `Draw` (IL_0328)
but copies the backbuffer from `FinalLDRTexture` (IL_0535) — so an armed camera-jump wait
renders a frame **the player never sees**. That is engine-designed behaviour for camera
cuts, not a hack. And `CameraComponent.UpdateRenderSettingsInternal` is the *sole*
game-side caller of the engine's camera setter, marshalling through `RenderCommandBuffer`
exactly like the real camera — so `_renderView`, both camera CBs, planet environment,
cascade fitting, voxel clipmap LOD **and texture-streaming residency** all follow, with no
reflection into private render-thread state.

- **Fidelity:** total, by construction. All 49 GBuffer material variants, deferred
  texturing, cascades fitted to our frustum, both atmosphere halves, HBAO, real exposure
  and tonemapping, SSR, water, particles, transparents.
- **Perf:** net **zero** extra GPU work — the RTT frame *replaces* a player frame. This
  is the only route that plausibly holds 30 fps at full fidelity.
- **Cost:** the player's framerate halves. At 60 fps that is 30/30 — which meets the spec,
  but it is a real trade the user has to accept.
- **Secondary cost:** temporal history alternates between two cameras. Mitigate by
  disabling FSR (`DRS.AAMode`) and eye adaptation (`PostProcess.EyeAdaptation = false`,
  use `ConstantExposure`); accept SSR and cloud ghosting.
- **No re-entrancy, no assert, no queue-state hazard, no mid-frame global swap, no nested
  `Draw`** — there is still exactly one `Draw` per frame. That is what makes it safer than
  everything in tier 1 despite being more ambitious.

**First test:** prefix only, one frame on a keypress, substitute the transform, nothing
else. The player's screen should show the RTT view for exactly one frame and recover. If
it does not move, `CameraComponent` is not the only writer in practice and the route dies
in one test.

### 3.2 Rejected: add `PassIndirect` to the 29 material states

`MaterialStateDefinition` exposes settable `Flags`. But shader variants are compiled from
declared passes, so an undeclared pass was never compiled — the shipped cache is 8682
content-addressed blobs with no name index, and a flag flip would be inert or throw
unless runtime shader compilation is both reachable and permitted. Not worth the attempt
before 3.1.

---

## Closed by this dive

- **Nested `SceneDrawSystem.Draw` from our current hooks.** Both `ExecuteEnvironmentProbeUpdate`
  and `DrawUnlit` run *inside* `SceneDrawSystem.Draw`, which has already opened an
  async-compute scope (`FrameDispatcher._isComputeQueueBranched`) and an
  `OnBeginDraw`/`OnEndDraw` scope. A nested `StartComputeQueue` overwrites
  `_directToComputeBoundary`, dropping the outer boundary's pending transitions; the paired
  `OnEndDraw` frees the player's live per-frame CBs and LUT tables while the outer command
  lists still reference them. Structural corruption, not a soft assert.
- **Calling `Render12EngineComponent.Draw` twice.** Double `Scene.Tick()`, double
  `ReplayBatches()`, re-consumed upload queue, unbalanced `_isInsideDraw`.
  `DisposeInternal`'s call is no precedent — it passes `draw: false`.
- **Owning a second `ScreenBuffers` as a route to fidelity.** Constructible, and the static
  is writable — but `Update` and `GetCurrentFrameRenderTarget` re-read the *singleton*
  mid-body regardless of `this`, `InitializeBuffers` is one-shot, and nothing caches the
  instance anyway, so the existing field swap already gets ~all of the benefit.
- **Impostor baking, holograms, the video player, editor/ContentBuilder/AutoTests previews,
  planar reflections, mirrors, portals, reflection probes.** None is a second render.
  `eq strings "SecondaryView|PictureInPicture|SecondaryCamera|ViewportIndex" -a all`
  returns zero literals across all 68 assemblies. The engine's only second views are
  environment probes, shadow cascades and water-surfel voxelization.
- **`ScreenshotsManager` / `MainRenderTarget.TakeScreenshotAsync`.** A readback of the
  frame that already exists. No camera parameter, no second instance.
- **`LocalViewToMainViewClip` being wrong for an off-axis camera.** Real, but the field is
  referenced by **zero** shaders in the 660-file tree. Do not spend a build on it.

## The flicker: one coupling, two symptoms (2026-07-26)

Objects popping in and out at certain angles was introduced by the fix for the
single-frame flash to the player's position. Both are the same coupling.

`DrawContextManager.BorrowShadowCulling(int rootEntityId)` is a plain LIFO free-list —
`TryPop`, else construct, then `context.RootEntityId = rootEntityId`. **The id is not a
pool key**, so the earlier "borrow with a distinct id" fix was a no-op for contention,
which is why the flicker survived it. `RootEntityId` *is* a real culling parameter with
one consumer, `GeometryContext.UpdateRanges`, so a bogus entity id was itself a suspect
for geometry going missing. Back to `-1`, what the engine's own paths pass.

The real cause is the other half of the pair. `Borrow()` on an
`OutputGeometryBufferContext` is an `_isBorrowed` mutex flag, not an allocation — the
same physical draw-command buffers come back every time — and we write our draw commands
into `MainOutputGeometryBuffers`, which **eighteen** engine methods read and rewrite
across the frame (`MainViewCulling`, `ExecuteForwardPasses`, `DrawUnlit`,
`RenderTransparent`, `RenderHolograms`, `RenderFlares`, `DrawWater`, `SceneFinalize`,
`DrawUI`, …).

Sharing *both* the culling context and the geometry buffer was self-consistent: the pass
drew the engine's probe-view data, which is the flash. Taking a private culling context
fixed the viewpoint but **split the pair** — culling results in our context, draw commands
in a buffer the engine rewrites around us.

**`MainOutputEffectGeometryBuffers` is not a usable spare.** It is a real second instance
read by one pass, so it looked like a free vehicle for the fix. Instant CTD: the log ends
18 ms after the switch, one pass, no managed exception. Its buffers are evidently ranged
for the handful of highlight / transparent-unlit draws it normally carries, and a full
scene cull overruns them — a GPU-side overrun is device removal with nothing to catch.

So the fix is a privately **constructed** `OutputGeometryBufferContext` with
`EnsureRanges(in RangeStats, in ComputeCommandList)` called for our own workload;
`SceneDrawSystem.EnsureRangesOutputGeometryBuffers` is the engine's wrapper to copy.
Note the precedent that a hand-built `CullingContext` constructed fine and then died
within seconds — replicate the engine's `EnsureRanges` call, not just the constructor.

## Asserts are deferred-fatal, which changes how to read a failed experiment

`DiagnosticHandlerBase..ctor` sets `AutoIgnoreMessages = true`, so a tripped `Assert.True`
mid-frame **logs once and returns** — no dialog, no throw, execution continues past a
violated invariant. But `BuildConfiguration.Type == Release` activates `DiagnosticReporter`,
and on shutdown `VRageCore.Dispose → ReportIfNeeded()` throws `FirstAssertionException`
if any assert fired all session.

Two consequences worth internalising:

1. **Past experiments may have silently violated invariants with no visible symptom.** The
   log file, not the crash, is the diagnostic.
2. **Crash-on-exit is the tell-tale of a tripped assert.** Log
   `ReportAssertionSummary`'s "triggered N time(s)" lines to find what was hit.

## Recommended order

1. **0.2** LOD (`MainView` instead of `EnvironmentProbe`) — biggest win, one argument.
2. **0.1** `Screen.Resolution` — fixes the sky and the geometry pass's view vectors.
3. **2.1** `EmissivityMultiplier = 200` — one line, decides the whole display axis.
4. **0.3** real `RenderView` camera CB — unblocks planet/triplanar world-space effects.
5. **0.4** `EnableRecursiveReflections` (+ `DimDistance` for the mounted-camera case).
6. **1.1** camera-CB swap, proven inert first against `IndirectPlanetEnvironmentJob`.
7. **1.2** `AtmosphereMultiplyJob`, then HBAO.
8. **2.2** own `ExtensionsTexture` for per-pixel HDR.
9. ~~**3.1** frame interleaving~~ — ruled out by the user, and Discovery 2's retraction
   removed the argument for it. Everything it would have unlocked is reachable through
   the deferred path or by building per-view history.

Steps 1–5 are all inside code we already own and none mutates engine state beyond a
set-and-restore of a settings field.
