# Hunting for a second-camera system in the engine

Written after a systematic sweep, prompted by a fair challenge: this project has more
than once been told "there is no other way", only for a stray type name to open a door.
So this is the sweep done properly, with the negatives recorded as carefully as the
positives.

## What does NOT exist

Searched across all 67 shipped assemblies. These came back empty or were false leads:

| Looked for | Result |
|---|---|
| Portal / planar reflection / mirror | **Nothing.** `MirrorData` is a font-rendering struct (Slug text). |
| Minimap, cinematic, split-screen, viewport | **No types at all.** |
| `Imposter` | Only `PointLightImposterCustomData` / `SpotLightImposterCustomData` — light billboards, not render-to-texture. |
| `PreviewHostComponent` | The **projector/hologram** system. Spawns preview *grids* as entities. No camera, no render target. |
| `Is3DMapEnabled` | A global **mode flag** on the one main render, read by nine passes. Not a second view. |
| `ScreenshotToolComponent` | Captures the existing frame. Player viewpoint only. |
| `ScreenBuffers.CreateWithResolution` | **FALSE POSITIVE** — belongs to `WaterContext.IScaledScreenBuffers<T>`, not to `ScreenBuffers`. A grep that matched two types. |

So: there is no dedicated "render the world from another camera" subsystem to borrow.
That part of the earlier assessment holds.

## What DOES exist, and is better founded than previously stated

The whole-scene render route was tried early in the project, failed with two exceptions,
and was set aside. The sweep says it deserved more than that.

### 1. `Draw` is a real, parameterised entry point

```
pub  Void  SceneDrawSystem.Draw(ResizableRWRenderTargetTexture finalLDRBuffer)
int  Void  ExecuteScenePreparationAndRender(Vector2I finalResolution)
```

`Draw` is **public**, takes its destination buffer as a parameter, and derives the render
resolution from it:

```
IL_0043  finalLDRBuffer.get_Resolution()
IL_0048  ExecuteScenePreparationAndRender(Vector2I)
```

The output and the size are already parameterised. The camera is the global.

### 2. It has ZERO managed callers

`Draw` is invoked from engine glue outside the managed assemblies. That makes it a clean
Harmony prefix site: run our second render *before* the engine's own, with a static
reentrancy guard so our nested call passes straight through to the original. No
re-entering a frame from inside itself — which is what made the probe hook unusable for
this.

### 3. ScreenBuffers can be owned outright

```
pub  Void  .ctor()                                            <- public, parameterless
int  Void  InitializeBuffers(in Vector2I maxResolution)
pub  Void  Update(CopyCommandList, maxResolution, preUpscaleResolution)
pub  prop  Vector2I PreUpscaleResolution { get; set; }
pub  prop  ResizableRWRenderTargetTexture[] GBuffer { get; set; }
pub  prop  ResizableRWRenderTargetTexture FinalLDRTexture { get; set; }
```

We have been swapping individual textures inside the engine's single `ScreenBuffers`.
We can instead construct a **complete second set at 512x512** and swap the whole object.
That separates far more per-view state in one move — and much of what makes a second
render hard (TAA history, motion vectors, depth, GBuffer) lives in exactly this object.

### 4. Every engine global is a public static FIELD

`CoreSystems` exposes ~70 `pub sta field` members — `ScreenBuffers`, `DrawContexts`,
`Settings`, `CommonResources`, `SwapChain`, and the rest. Fields, not readonly
properties. All assignable by reflection for the duration of a call. The engine is not
defended against this; it simply assumes one view per frame.

### 5. The failures we hit were specific, not structural

From the project log:

```
ERROR whole-scene render: KeyNotFoundException: The given key 'R11G11B10_Float'
                         was not present in the dictionary.
ERROR whole-scene render: InvalidOperationException: Nullable object must have a value.
```

`R11G11B10_Float` is `HDR_FORMAT = 26`. `Draw` borrows its LBuffer as:

```
BindableTexturePool.Borrow("LBuffer", 26, ScreenBuffers.MaxPreUpscaleResolution, ...)
```

— format and resolution taken from the **global**, against our own smaller target. Both
exceptions are consistent with that mismatch, and both are addressed by owning
`ScreenBuffers` (item 3) rather than patching around it. Neither is evidence that the
route cannot work.

## Honest assessment

This is not a hidden second-camera system. It is the *main renderer, driven a second
time*, which is what the deferred route has been reaching for all along — but attacked
at the top of the pipeline instead of the middle.

**Why it is attractive**

- It is the real renderer: full shaders, deferred lighting, atmosphere, AO, textured
  terrain. No fidelity ceiling to argue about.
- Distance-independent, unlike anything derived from the player's frame.
- No frame interlacing, no moving the player camera.
- One well-defined hook site rather than a growing collection of mid-pipeline patches.

**Why it is hard**

- Temporal state that is not in `ScreenBuffers` — exposure history, LOD transitions (the
  flicker cause found tonight), occlusion pyramids — still needs handling.
- Cost is a second full render. At 512x512 that is far cheaper than at 4K, but it is not
  the probe path's cost.
- The globals get discovered by breaking things, exactly like tonight. The difference is
  the ceiling: tonight's grind was to make a low-fidelity path slightly better, this one
  ends at parity with the main view.

**What is already built that carries over**

Private culling contexts, correct `RangeStats` sizing, the LOD-transition fix, the
camera CB swap, the GBuffer swap, and the panel handover are all reusable. Little of
tonight is wasted if this becomes the route.
