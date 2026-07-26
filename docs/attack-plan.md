# How to render a second 3D view — the engine's own recipe

> **END TO END — 2026-07-25 21:48.** A second 3D scene view, rendered from mod
> code, from an orbiting camera, displayed on an in-world LCD panel.
>
> ```
> Camera pass view source: ORBIT — target "LCD Panel [RTC]"
> Feed: HDR->LDR blit via CopyJob.
> Handover: OffscreenTargetManager=ok RequestRender=ok
> === HANDOVER: copying camera frame into the panel target from the UI stage. ===
> === HANDOVER SURVIVED 30 copies — the feed is on the panel. ===
> ```
>
> The last missing piece was `OffscreenTargetManager.RequestRender(GeneratedResourceHandle)`.
> `OffscreenUIRenderer.DoWork` only calls `DrawOne` for targets in
> `_pendingRenderList`, and nothing was queueing the panel's target — so the
> handover hook was right from the start, it simply never fired.

### What the pictures show

The first frame was planets and starfield on black, which looked like "distant
environment only, no local geometry". That reading was **wrong** — the camera was
simply pointing at empty space. As it orbited round to face the station, the panel
showed full grid geometry: block textures, yellow striping, individual coloured
blocks.

`IndirectEnvironmentPassJob` renders the complete scene. No G-buffer work is
needed; the pipeline above is sufficient.

### Remaining issue: exposure, not geometry

Large areas blow out to pure white (voxel terrain especially) while shadowed
interiors look correct. The scene renders **HDR** (`R11G11B10_Float`, linear,
unbounded) and `CopyJob` converts *format* but not *range* — everything above 1.0
clamps.

### Frame rate is capped by shared-context contention

The feed rate is limited by the culling context, not by GPU cost:

| Interval | Result |
|---|---|
| 500 ms (2 Hz) | 337+ copies, stable |
| ~80 ms (12 fps) | **7 copies, then CTD** |

The crash rate scales with the feed rate, which is contention rather than a
deterministic fault. The cause is the shortcut taken to get the first image:
the camera pass borrows the engine's **`EnvProbeCulling[0]`** as scratch, and the
engine uses that same context ~7 times a frame for its own probe faces. At 2 Hz
collisions are rare; at 12 fps we overwrite its culling data mid-use.

**A dedicated `CullingContext` (and `ClusteringContext`) is therefore the
prerequisite for any meaningful frame rate.** It is no longer a tidy-up item.
`ClusteringContext(AllocationGroup)` is easy; `CullingContext` needs three
`SharedReadableBuffer<T>` instances — the awkward part noted in the step-1
results. Cloning the buffers from an existing context is likely the shortest path.

Two throttles also have to move together, or the rate does not change at all:
`CameraRender`'s pass interval and `FeedHandover.RequestPanelRender`'s limiter.
Both now read `FeedConfig.IntervalMs` from `output/feed-config.txt`, which is
re-read live.

### Handing a texture between command lists (two bugs, one symptom)

The camera pass writes the LDR texture in **its** command list; the handover reads
it from the **UI stage's** list, later in the same frame. Two things were wrong:

1. **Resource state.** A copy source must be in `COPY_SOURCE`; ours was left in
   render-target state. `CopyCommandList.ExplicitStateTransition(autoState, state,
   discard)` is public and fixes it — but it takes an **`AutoResourceState`**, which
   hangs off a *view* (`ITexture2DView.AutoResourceState`), never off the texture.
2. **Synchronisation.** Even in the right state, the GPU may not have finished the
   write when the copy executes. Fixed by **double-buffering**: two session-owned
   textures, and the handover always receives the one written on the *previous*
   pass. At 2 Hz a one-frame delay is invisible.

Before: died on copy #1. After: 35+ copies, `transitionState = 1`.

### Watchdogs must not live inside the thing they watch

The panel's render target is evicted when the player walks out of range. The
eviction check was in `CapturePanelRenderTarget`, driven by the LCD tick hook —
**and that hook stops firing at exactly the same moment**. So the eviction was never
noticed and a freed target kept being used.

The check now lives in the camera pass (~430×/second, independent of player
position). Two follow-on bugs came with it, both worth remembering:

- **A resolve-order deadlock.** `IsPanelTargetAlive` returned false when the
  `OffscreenTargetManager` was null, but the manager was only resolved inside
  `RequestPanelRender`, which the alive check gates. Null → "not alive" → paused →
  never resolves. The feed never started at all. Resolve shared dependencies
  independently of the code paths that consume them.
- **No recovery after eviction.** A panel only borrows a render target when it has
  content to paint. Nothing marked it dirty after an eviction, so it never
  re-acquired one. Repaints are now driven *only while the target is missing*.

### The panel re-creates its render target (found by bisect)

**The LCD system swaps the panel's `OffscreenRenderTarget` for a new one mid-session.**
Observed live:

```
22:11:11  watching for offscreen target 2305843013508682735
22:15:05  watching for offscreen target 2305843013508683264
```

Anything cached from that target goes stale at that instant: the resolved
component, its resolution and format, and any texture sized to match. Copying a
stale-sized source into the fresh destination is a size mismatch, and D3D12
answers with device removal.

This presented as *"stable for minutes, then a crash"*, which is what made it so
misleading — three separate plausible fixes each appeared to work because the test
runs were shorter than the churn interval. **Anything derived from the panel's
render target must be re-resolved when its id changes.**

#### Bisect method (use this, not hypotheses)

Arming each stage separately is what actually found it:

| Stage | Markers | Result |
|---|---|---|
| 1 | `camera-armed` | stable 90 s, and 17 min earlier |
| 2 | `+ feed-copy-armed` | stable 90 s |
| 3 | `+ handover-armed` | survives minutes, then dies |

One trap: `_disarmed` latched at load from a stale `handover-live.marker`, so
stage 3 silently tested nothing and reported a false pass. Crash markers are now
cleared live when the file is deleted, so the latch cannot fake a result.

### The pooled-texture race (cost two crashes)

The camera pass originally borrowed a pooled LDR texture per cycle, parked it for
the handover, and returned the *previous* one. The handover reads that texture from
the **UI stage**, asynchronously — so the camera pass could return it to the pool
while the copy was still reading. Classic use-after-free, and it presents as a
random CTD: it survived 30 copies once and died after ~7 s the next time.

Fix: **one long-lived texture, borrowed once and never returned.** The camera pass
converts into it, the handover reads from it, and no lifetime coordination is
needed. Anything shared between the render pass and the UI stage must outlive both.

Note this was initially misattributed to `PostProcess.Normalize` because that
change happened to precede the first crash. Reverting it did not help — the second
crash had `postProcess: null`.

**`CopyJob.PostProcess.Normalize` is separately NOT the fix — it also crashes.** `CopyJob.DoWork`
takes `Nullable<CopyJob.PostProcess>` whose only member is `Normalize`, which looks
exactly like the answer. Passing it killed the game immediately; it evidently needs
resources (a histogram or normalisation buffer) that this call site does not set
up. `postProcess` must stay `null`. Verified by reverting: the same build with
`null` is stable.

The principled fix is the engine's own exposure chain. `EyeAdaptationJob` maintains
`_autoExposures` (a `RenderTargetTexture[]`) and is what the main view uses to
decide brightness — reading that and applying it during the blit would match the
main view's look rather than guessing at a curve. Treat it as a real piece of work
with a dry run first, not a one-line experiment.

## The working pipeline

1. **Hook** `SceneDrawSystem.ExecuteEnvironmentProbeUpdate` (postfix) — a live
   `DirectCommandList`, after shadows, ~430×/second so throttling is free.
2. **Camera** — `RenderViewSlim` built from the panel's world transform, orbiting
   at 100 m. Projection and far plane copied from the live main view so the
   engine's own conventions carry over.
3. **Render** — `DoCullingFirstPass` → `ClusteringJob.DoWork` →
   `IndirectEnvironmentPassJob.DoWork` into a pooled HDR target. Format is
   **not** negotiable: the PSOs are compiled for `R11G11B10_Float`.
4. **Convert** — `CopyJob.DoWork` blits HDR → the panel's `R8G8B8A8_UNorm_SRgb`.
   `CopyResource` cannot convert formats.
5. **Park** the converted frame; ownership of the pool borrow passes to the handover.
6. **Queue** — `OffscreenTargetManager.RequestRender(handle)` so `DoWork` will
   service the panel's target this frame.
7. **Hand over** — postfix on `UIStage.OffscreenUIRenderer.DrawOne`, filtered to the
   panel's handle, `CopyResource` the parked frame in. This is the only moment the
   target is writable.

The environment probe path is not just an existence proof, it is a **working
template**, and reading it overturned the assumption this project started with.

The critical finding: `SceneDrawSystem.ExecuteEnvironmentProbeUpdate` renders
scene geometry from an arbitrary viewpoint into a non-screen target **without
touching a single global**. It does not write `SettingsManager._renderView`. It
does not swap `CoreSystems.ScreenBuffers`. Everything that defines the view is
built locally and passed down as parameters.

That kills the "save and restore ~50 globals" problem outright. There is nothing
to save, because the engine's own second-view path never mutates the first view's
state.

## What the probe pass actually does

Reconstructed from IL (`docs/rtt-recon.md` §24). Signature is roughly
`ExecuteEnvironmentProbeUpdate(DirectCommandList cl, in Request request)`.

```
// 1. Output views — the probe's own cube faces, not the screen
closeRTV = request.Render.OutputCloseTexture.GetRenderTargetFace(faceIndex)
farRTV   = request.Render.OutputFarTexture .GetRenderTargetFace(faceIndex)
cl.ClearRenderTargetView(farRTV, null)

// 2. The camera, as a value — NOT a global write
var tracked = new TrackedCameraSettings {
    Camera = (CameraSettings)request.Render.View,   // RenderViewSlim -> CameraSettings
    Screen = new ScreenSettings { Resolution = closeRTV.Resolution },
};
using var cameraCB = CoreSystems.BindableBuffers
    .CreateTransientConstantBuffer("cameraSettingsBuffer", ref tracked);

// 3. Its own depth, borrowed from the pool
var depth = CoreSystems.BindableTexturePool.BorrowResizableDepthStencilTexture(
    "EnvProbeDepthTexture", DepthStencilFormat.HighQuality,
    request.Render.Resolution, null, 128);

// 4. Shared geometry buffers, borrowed and returned inside the pass
CoreSystems.DrawContexts.MainOutputGeometryBuffers.Borrow();
cl.ClearDepthStencilView(depth.Resource.DepthStencilReadWrite, 3);

// 5. Cull -> cluster -> draw, all against dedicated per-face contexts
_indirectCullingJob.DoCullingFirstPass(cl, ref view,
    CoreSystems.Settings.LOD.EnvironmentProbe,
    DrawContexts.EnvProbeCulling[faceIndex],
    DrawContexts.MainOutputGeometryBuffers, null, null, ...);

_clusterJob.DoWork(cl,
    DrawContexts.EnvProbeCulling[faceIndex].EntityProxies,
    DrawContexts.MainOutputGeometryBuffers,
    DrawContexts.EnvProbeClustering,
    ref request.Render.Resolution, request.Render.FarPlane);

_indirectEnvironmentPass.DoWork(cl,              // <-- the scene draw
    DrawContexts.MainOutputGeometryBuffers, cameraCB, ref view,
    DrawContexts.EnvProbeCulling[faceIndex].FirstPass,
    DrawContexts.EnvProbeClustering,
    DrawContexts.DirectionalLightShadowResources,
    closeRTV, depth.Resource.DepthStencilReadWrite, 1);

CoreSystems.DrawContexts.MainOutputGeometryBuffers.Return();

// 6. Skybox / planet, then give the depth texture back
_indirectPlanetEnvironmentJob.DoWork(cl, cameraCB, closeRTV, farRTV,
    depth.Resource.DepthTexture, ref view);
CoreSystems.BindableTexturePool.Return(depth);
```

Every ingredient is either a parameter, a pooled borrow, or a dedicated context.

## What this means for a camera feed

A camera feed is the same shape with three substitutions:

| Probe uses | Camera feed uses |
|---|---|
| cube face RTV | a 2D render target view |
| `RenderViewSlim` for a cube face | `RenderViewSlim` built from the camera block's transform |
| `EnvProbeCulling[face]` / `EnvProbeClustering` | its own `CullingContext` / `ClusteringContext` |

Everything else — the camera constant buffer, the borrowed depth, the geometry
buffer borrow/return, the three jobs — carries over unchanged.

### The pieces and where they come from

**Jobs.** `_indirectCullingJob`, `_clusterJob`, `_indirectEnvironmentPass`,
`_indirectPlanetEnvironmentJob` are private fields on `SceneDrawSystem`.
Reachable by reflection off the live instance — no need to construct our own.

**Contexts.** `DrawContextManager.CreateInitialContexts` allocates them all.
Two options: patch it to allocate one extra culling + clustering context, or
piggyback on `EnvProbeCulling[]`. Probes run on a slow state machine
(`EnvironmentProbeManager` has `MAX_STATE_COUNT` and blend weights), so faces are
idle most frames — but borrowing one is a correctness risk. Allocating our own is
cleaner.

**Output target.** `BindableTexturePoolManager.BorrowRWRenderTargetTexture` — the
same call `UIStage.OffscreenUIRenderer.DrawOne` uses. So the pooling machinery for
exactly this already exists and is exercised every frame.

**Where to call it.** Needs a live `DirectCommandList` inside the render frame.
A Harmony postfix on `ExecuteEnvironmentProbeUpdate` gets one in hand, already at
the right point in the frame — after shadows are ready, before post-processing.

### Getting the result onto the panel

`OffscreenUIRenderer.DrawOne` already shows the last hop:

```
BindableTexturePoolManager.BorrowRWRenderTargetTexture(...)
... draw ...
CopyCommandList.CopyResource(offscreenRenderTargetComponent.Texture, borrowed)
BindableTexturePoolManager.Return(borrowed)
```

So: render the camera view into a borrowed RW target, then `CopyResource` it into
an `OffscreenRenderTargetComponent`'s texture — the same kind of offscreen target
the LCD panel system already binds to a panel material. The panel needs no
special handling; it displays an offscreen target either way.

The blit spike in `src/RttProbe.Logic` covers the alternative last hop (drawing an
arbitrary texture through the panel's 2D batch). The `CopyResource` route is
likely better, because it hands the LCD material the texture it already expects.

## Step 1 results — confirmed in a live game (2026-07-25)

Everything the plan depends on is reachable. Raw dump in
`output/scene-draw-recon.txt`.

**All four jobs live** on the `SceneDrawSystem` instance: `_indirectCullingJob`
(`CullingJob`), `_clusterJob` (`ClusteringJob`), `_indirectEnvironmentPass`
(`IndirectEnvironmentPassJob`), `_indirectPlanetEnvironmentJob`.

**All statics live**: `DrawContexts`, `BindableTexturePool`, `BindableBuffers`,
`Settings`, `ScreenBuffers`. Live contexts: `EnvProbeCulling` **[length 6]**,
`EnvProbeClustering`, `MainViewCulling`, `MainViewClustering`,
`MainOutputGeometryBuffers`, `DirectionalLightShadowResources`.

**Cadence** — measured over 50 s at ~58 fps: the probe pass fires **~430×/second,
roughly 7.4 times per frame**; `DrawUnlit` once per frame. The hook point is
plentiful, so a camera feed can throttle itself to any rate it likes.

### Exact signatures

```csharp
CullingJob.DoCullingFirstPass(
    ComputeCommandList commandList, in RenderViewSlim renderView,
    PassLODSettings lodSettings, CullingContext cullingContext,
    OutputGeometryBufferContext geometryBuffersContext,
    VisibilityListBufferContext visibilityListBufferContext,
    OcclusionContext occlusionContext, in Matrix? posViewToNegViewProj,
    RenderViewSlim? baseRenderView, int rootEntityId,
    CharacterCullingBehavior characterCullingBehavior, int cascadeIndex)

ClusteringJob.DoWork(
    ComputeCommandList commandList, EntityProxyContext entityProxyContext,
    OutputGeometryBufferContext outputGeometryBuffers,
    ClusteringContext clustersContext, in Vector2I resolution, float farPlane)

IndirectEnvironmentPassJob.DoWork(
    DirectCommandList commandList, OutputGeometryBufferContext outputGeometryBuffers,
    TransientConstantBuffer cameraSettingsBuffer, in RenderViewSlim view,
    GeometryContext result, ClusteringContext clusteredEntities,
    DirectionalLightShadowResources shadowResources,
    IRenderTargetView rt, IDepthStencilView depthStencil, bool clearRenderTarget)

BindableTexturePoolManager.BorrowRWRenderTargetTexture(
    string debugName, Format resourceFormat, Format srvFormat,
    Vector2I resolution, int mipMaps, Color? clearColor, int lifetime)

BindableBufferManager.CreateTransientConstantBuffer<TData>(string debugName, in TData data)
```

### The camera is far simpler than expected

`RenderViewSlim` has **four fields**, and the engine supplies the whole conversion
chain to what the GPU wants:

```csharp
struct RenderViewSlim { MatrixD InvViewD; MatrixD ViewD; Matrix Projection; float CullingFarPlane; }

// RenderViewSlim -> CameraSettings -> TrackedCameraSettings, both engine-provided
CameraSettings        <- op_Implicit(in RenderViewSlim)
TrackedCameraSettings <- op_Explicit(in CameraSettings)
```

So describing our camera is: build a view matrix, its inverse, a projection
matrix, and a far plane. Standard camera maths — no GPU-layout struct to
reverse-engineer.

### The one awkward piece

`CullingContext`'s constructor wants three `SharedReadableBuffer<T>` instances:

```csharp
CullingContext(string debugName, ?  parentStatKey,
    SharedReadableBuffer<T> geometryContextCountersBuffer,
    SharedReadableBuffer<T> entityProxyContextCountersBuffer,
    SharedReadableBuffer<T> statsBuffer, bool isForMeshEffects)
```

Constructing those from outside is the only genuinely fiddly part.
`ClusteringContext(AllocationGroup)` is easy by comparison.

**Cheaper route for the first proof:** reuse `EnvProbeCulling[faceIndex]` from
inside the postfix for the face the engine has *just finished with*. Culling
context contents are per-pass scratch, overwritten on each use, so borrowing one
immediately after its pass completes avoids the construction problem entirely. If
that assumption is wrong the symptom is corrupted reflections, not a crash —
cheap to test and cheap to back out of. Build a dedicated context later, once the
approach is proven.

## Step 3 results — the second render runs (2026-07-25)

```
[18:46:34.637] Camera pass ARMED
[18:46:34.670] === CAMERA PASS SUBMITTED ===
[18:46:44.202] === CAMERA PASS SURVIVED 20 submissions. The second scene render works. ===
```

Game stayed up, no errors, `camera-live.marker` absent — every pass exited through
its `finally` and returned all pooled resources. Frame rate ~54 fps against ~57
before arming, while rendering an extra 256×256 scene pass twice a second.

**What this proves.** The engine's scene passes can be driven a second time from
mod code, with a mod-supplied view, into a mod-owned render target, on the render
thread, without destabilising the renderer. That was the entire open question of
this project, and the answer is yes.

**What it does not yet prove.** Nothing has been *displayed*. The target is
borrowed, drawn into, and returned within the same call, so the image has never
been looked at. The passes execute and are stable; whether they produce a correct
picture is the next thing to establish — it could be rendering black.

Confirmed working configuration:

| Piece | Value |
|---|---|
| hook | postfix on `SceneDrawSystem.ExecuteEnvironmentProbeUpdate` |
| view | the engine's current main view, via `SettingsManager.RenderView` |
| colour target | `BorrowRWRenderTargetTexture`, `R11G11B10_Float`, 256×256 |
| depth | `BorrowResizableDepthStencilTexture`, `DepthStencilFormat.HighQuality` |
| camera | `RenderViewSlim` → `CameraSettings` → `TrackedCameraSettings` → transient CB |
| culling | `EnvProbeCulling[0]`, reused post-pass — no visible ill effects so far |
| rate | 500 ms throttle |

### Two implementation notes worth keeping

`ResizableRWRenderTargetTexture` implements `IRenderTargetView` directly. The
depth texture exposes **both** `DepthStencilReadWrite` and `DepthStencilReadOnly`;
the pass needs the read-write one, and picking by "first assignable member" is a
silent-failure trap.

`DepthStencilFormat` is a struct with static presets, not an enum —
`DepthPyramidHighQuality`, `ShadowDepthHighQuality`, `ShadowMaskDepthQuality`,
`LowQuality`, `HighQuality`.

## Step 4 results — display: the last link is the blocker

Everything up to the panel works. Confirmed live:

```
[RTC] panel located: "LCD Panel [RTC]" at 250571.1,8855.0,-12737.9
Camera pass view source: ORBIT — target "LCD Panel [RTC]"
Feed: HDR->LDR blit via CopyJob
=== FEED COPIED into the LCD offscreen target. ===
=== CAMERA PASS SURVIVED 20 submissions. ===
repaint: surface collection = LcdPanelSurfaceContext[] _surfaces
repaint: rebuilding 1 surface context(s) per tick.
```

**`IDrawBatch.DrawImage` does not accept a render-target-backed handle.** This is
the "Route A gate" Grid Schematics left open, and the answer is no. The moment the
`[RTC]` tag was added to the panel text — which is what makes `DrawFeed` run — the
game died instantly. `UISystemComponent.GetTexture` asserts `IsGuid()`, and an
`OffscreenRenderTarget.TextureHandle` is a *generated* handle backed by a
`RenderId`, not a guid. The assert fires inside the render thread's command replay
where nothing can catch it.

So the offscreen target is displayable by the LCD **material** path but not through
the **2D draw batch** path.

### Two other constraints found the hard way

**Render format is not negotiable.** The scene pass's PSOs are compiled for
`R11G11B10_Float`. Binding any other format — e.g. matching the copy destination's
`R8G8B8A8_UNorm_SRgb` — is a pipeline-state mismatch and D3D12 answers with device
removal. Convert with `CopyJob` afterwards instead; `CopyResource` cannot convert.

**`SetSurfaceContent` / `SetSurfaceText` are replicated sync properties.** Writing
them from the render thread throws *"Sync property was disabled by
ISignalTableBuilder and must not be set"*. Grid Schematics succeeds only because it
calls from a simulation-side tick. A sim-side tick is therefore needed for any
panel reconfiguration.

### Attempt 2: write into the panel's own render target — also fatal

The panel owns an `OffscreenRenderTarget` it displays every frame, so writing the
camera frame into *that* avoids `DrawImage`, material rebinding and forced repaints
entirely. It got further than anything else:

```
panel RT captured: OffscreenRenderTarget Id=2305843013508683640 valid=True
component key=generated:2305843013508683640 ... <-- MATCH
Feed: HDR->LDR blit via CopyJob.
=== FEED COPIED into the LCD offscreen target. ===
```

Then the game died about five seconds later, after roughly ten copies.

**Why: resource state.** Every registered offscreen texture is an `ROTexture` — a
read-only view. `CopyResource` into one *is* legal, and `UIStage.OffscreenUIRenderer
.DrawOne` does exactly that every frame. The difference is *when*: `DrawOne` runs in
the UI stage with the target transitioned to a copy destination. We run inside the
environment-probe pass, where the panel's texture is bound as a shader resource for
its material. Copying into a resource in the wrong state is a D3D12 fault, and the
answer is device removal.

Note the matching detail, which cost a round: `_registeredTextures` is keyed by
`GeneratedResourceHandle` (prints as `generated:<id>`) while the target carries a
bare `RenderId`. They never compare equal — match on the id text.

### Attempt 3: hand over inside `OffscreenUIRenderer.DrawOne` — wrong hook

The idea was sound: `DrawOne` copies into offscreen targets itself, so its postfix
is by construction a moment when the resource is writable. It was patched
successfully and its arguments captured:

```
Patched OffscreenUIRenderer.DrawOne(DirectCommandList commandList,
                                    UISystemComponent uiSystem,
                                    OffscreenRenderTargetComponent target)
```

But it never fires for an LCD panel:

| Target | Id |
|---|---|
| served by `DrawOne` | `...683636` |
| the `[RTC]` panel's own | `...585559` |

**`OffscreenUIRenderer` serves the game's UI offscreen targets, not LCD panels.**
Panels render through `LcdContentRendererSessionComponent.Render`. Disproven with
no crash — the arming discipline held.

### Current on-screen state

With `[RTC]` in the panel's text the panel is **black**, which is informative: it
is in custom content mode, it has a render target, and it is displaying it. It is
black only because our panel hook now deliberately draws nothing (the fatal
`DrawImage` was removed). The display path works; it has no content.

### Where to go next: material binding

Every copy route is blocked by resource state, and the draw route by handle type.
The remaining approach avoids both by never copying into the panel's target at
all — instead point the panel's material at **our** target:

- Writing into our *own* offscreen target from the probe pass is **proven safe** —
  it ran for minutes without incident. Only writing into the *panel's* target
  faults, because that one is bound for sampling.
- `LcdPanelSurfaceContext.SetNewScreenMaterialHandle(renderer, materialDefinition,
  aspectRatio, orientation, colorMetalOverride)` builds the panel's material from
  `ctx.RenderTarget.TextureHandle` — so if `ctx.RenderTarget` is ours, the panel
  samples our texture.
- `TransitionToCustomRender` unconditionally borrows a fresh target and overwrites
  `ctx.RenderTarget`, so simply assigning the field is not enough; the material
  handle has to be re-created afterwards, which needs a `PBRMaterialDefinition`.
  Sourcing that is the open question — start by dumping what
  `TransitionToCustomRender` passes.

This is the last untried route, and unlike the others it has no known blocker —
only unknowns.

### Route B: readback to a file-backed texture (fully specified, unimplemented)

`DrawImage` rejects generated handles but accepts **file-backed guid** handles —
Grid Schematics depends on that and works. So the frame can leave the GPU, land on
disk, and come back as an ordinary texture. Slow, but every step is already proven
in this codebase.

Confirmed callback signature:

```csharp
RenderOutputManager.OnScreenshotToMemoryTaken
    : Action<OffscreenRenderTarget, Vector2I, int, Memory<byte>>   // target, resolution, stride, pixels
```

The chain:

1. Render the camera into **our own** offscreen target — proven safe from the
   probe pass; only the *panel's* target faults.
2. `ourRt.TakeScreenshotToMemory(waitUntilFullyLoaded: false)`.
3. Subscribe to `OnScreenshotToMemoryTaken`; filter to our target; take the bytes.
4. Write a PNG (`GridProbe.Logic/PngWriter.cs` already does this, and GS2's notes
   warn that a malformed file throws inside the render thread's replay — validate
   before registering).
5. `ResourceHandle.GetOrRegister(fileHandle)` for a guid-backed handle.
6. `UISystem.PreloadTexture(handle)` every frame — the streamer evicts by distance
   and an evicted texture silently draws nothing.
7. `batch.DrawImage(handle, ...)` in the panel postfix — the path GS2 uses daily.

Expect a few frames per second at 256×256: GPU readback plus PNG encode per frame.
Good enough to prove the feed end to end, and it produces a known-good reference
image that makes debugging route A far easier.

**Recommended order: B to get a picture, then A for frame rate.**

#### Route B progress (implemented, one step short)

`src/RttProbe.Logic/FrameReadback.cs` + `PngWriter.cs`. Working:

```
Readback: locating RenderOutputManager -> RenderEngineComponent.Instance.RenderOutputManager
Readback: subscribed to OnScreenshotToMemoryTaken (Action`4).
Readback: first TakeScreenshotToMemory request issued (1 params).
Readback: request timed out after 2s (attempt 1) — no callback delivered.
```

So the manager is found, the subscription binds, and the request is issued — but
**no callback ever arrives**. Two things worth checking next:

1. **Wrong manager instance.** `RenderOutputManager` is not a singleton;
   `RenderEngineComponent.Instance` and `Render12EngineComponent` each expose one,
   and `Render12EngineComponent/MainThread` holds one as a public field. We may be
   subscribed to a different instance from the one that raises the event. Enumerate
   all of them and subscribe to every candidate.
2. **The request may not reach the queue.** `OffscreenTargetManager` keeps
   `_immediatelyScreenshotsToMemory` and `_fullyLoadedScreenshotsToMemory` sets and
   drains them via `TryDequeueWork`, which consults `LoadingMonitor.LoadingCount`.
   We pass `waitUntilFullyLoaded: false`. Inspect those sets at runtime to confirm
   our target is actually enqueued.

#### Route B is a dead end: the readback drain is not wired

```
OffscreenUIRenderer.DoWork  ->  TryDequeueNextRenderRequest    (has a caller)
OffscreenTargetManager.TryDequeueWork                          (NO callers anywhere)
```

`TryDequeueWork` drains `_immediatelyScreenshotsToMemory` / `_fullyLoadedScreenshotsToMemory`,
and **nothing in the shipped assemblies calls it**. Requests enqueue correctly and
are never serviced, so `OnScreenshotToMemoryTaken` never fires. Confirmed from both
the contracts struct and the Render12 component directly, with the subscription
verified live.

Do not spend more time here unless a patch wires that drain. The subscription and
enqueue code in `FrameReadback.cs` is correct and worth keeping for that day.

**One live thread remains from this finding:** `OffscreenUIRenderer.DoWork` *does*
drain `TryDequeueNextRenderRequest` and then calls `DrawOne` for each dequeued
target. If our offscreen target can be pushed into that render-request queue, then
`DrawOne` fires for it, and `FeedHandover` (already written) copies our frame in at
exactly the moment the resource is writable. Finding what populates that queue is
the cheapest remaining lead.

Two implementation notes already paid for:

- The event is `Action<OffscreenRenderTarget, Vector2I, int, Memory<byte>>` and the
  handler must match **exactly** — `object` parameters fail to bind, because
  delegate creation will not box value types.
- Any "awaiting" flag needs a timeout. Clearing it only in the callback means one
  undelivered request silently stops the feed forever.

### Tooling note

`RttPlugin` truncates `rtt.log` on construction, so relaunching after a crash
destroys the evidence. Append with a session banner instead.

## Suggested order of attack

1. **Confirm the reflection surface.** ✅ *Built — `src/RttProbe.Logic/SceneDrawRecon.cs`.*
   Harmony postfixes on `ExecuteEnvironmentProbeUpdate` and `DrawUnlit` capture the
   live `SceneDrawSystem`; the first call dumps the job fields, their exact
   signatures, the `CoreSystems` statics, the live draw contexts and the camera
   struct layouts to `output/scene-draw-recon.txt`, plus pass cadence to the log.
   Read-only — no drawing, no allocation, no state mutation. See the README to run it.
2. **Allocate a context pair and a render target.** Patch
   `CreateInitialContexts` to add one `CullingContext` + `ClusteringContext`, and
   borrow an RW target. Still no drawing — just prove allocation works and
   nothing destabilises.
3. **One static view, low res, low rate.** Postfix `ExecuteEnvironmentProbeUpdate`,
   run the cull → cluster → draw sequence with a hardcoded `RenderViewSlim` at
   256×256, once every ten frames, into the borrowed target. `CopyResource` it to
   an offscreen target and put that on a panel. **This is the make-or-break step.**
4. **Only then** make the view follow a camera block, and tune resolution and
   refresh rate.

Steps 1 and 2 cannot crash the renderer. Step 3 can, and should be developed with
the arming-marker discipline already used by the blit spike — the failure lands on
the render thread, after the patch returns.

## Honest risk assessment

What has genuinely improved: the global save/restore problem is gone, the
temporal-state problem is gone with it (the probe pass doesn't participate in
FSR3 or SSR history), and the exact call sequence is known rather than guessed.

What remains real:

- **`MainOutputGeometryBuffers` is shared**, borrowed and returned within a pass.
  A second consumer in the same frame must respect that discipline exactly.
- **GPU cost** is a real extra geometry pass. Mitigated by low resolution and a
  low refresh rate — a camera feed does not need 60 fps or 1080p.
- **`IndirectEnvironmentPassJob` is tuned for probes**, not for a viewing camera.
  Expect the image to differ in shading from the main view; matching it exactly
  may mean a different pass job, which is more work.
- **Render-thread fragility and patch fragility** are unchanged. This is internal
  API on the render thread; mistakes are hard crashes, and any SE2 update can
  move these members.

None of that is a blocker. The verdict this project opened with — that a true
second 3D view is impossible — was wrong, and the reason it was wrong is that
the engine already does it six times a frame for environment probes.
