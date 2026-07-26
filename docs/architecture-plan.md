# Where the feed goes next

Written after getting a stable 2 Hz feed and discovering that frame rate is capped
by contention, not by cost. Targets: **15 fps minimum, 30 fps ideal**, with room to
add render layers afterwards.

## What we run today, per feed frame

```
probe-pass postfix (render thread)
  borrow HDR target, borrow depth, transient camera CB
  MainOutputGeometryBuffers.Borrow()          <- shared with the engine
  DoCullingFirstPass  -> EnvProbeCulling[0]   <- shared with the engine
  ClusteringJob       -> EnvProbeClustering   <- shared with the engine
  IndirectEnvironmentPassJob -> HDR target
  return everything
  CopyJob: HDR -> our LDR texture             <- copy 1
  RequestRender(panel target)

UI stage, DrawOne postfix
  ExplicitStateTransition(source -> COPY_SOURCE)
  CopyResource: our LDR -> panel's target     <- copy 2
```

### What limits it

1. **Shared contexts.** Measured: 337+ copies stable at 500 ms, 7 copies then CTD at
   ~80 ms. The engine drives `EnvProbeCulling[*]` ~7×/frame for its own probe faces;
   at speed we corrupt each other. **This is the frame-rate ceiling** — not GPU cost.
   512×512 is 0.26 MP, trivially cheap next to the main view.
2. **Two full-texture copies**, one of which needs a cross-command-list state
   transition and a double-buffer to be safe.
3. **We borrow the panel's target**, so LCD distance-eviction can pull it out from
   under us. Every crash after the first hour traced back to this.
4. **`IndirectEnvironmentPassJob` alone** — geometry and sky, no deferred lighting or
   post. This is the fidelity ceiling.

## The shape to aim for

Own the three things a second view actually needs, instead of borrowing them.

### Phase 1 — own culling/clustering contexts  *(unblocks frame rate)*

```csharp
ClusteringContext(AllocationGroup allocationGroup)                    // easy
CullingContext(string debugName, ? parentStatKey,
    SharedReadableBuffer<T> geometryContextCountersBuffer,
    SharedReadableBuffer<T> entityProxyContextCountersBuffer,
    SharedReadableBuffer<T> statsBuffer, bool isForMeshEffects)       // needs 3 buffers
```

Shortest path: read the three buffers off an existing `EnvProbeCulling[n]` by
reflection and pass the same instances — they are plausibly shared infrastructure
rather than per-context state. If not, `DrawContextManager.CreateInitialContexts`
constructs them and can be copied.

Also needs its own `OutputGeometryBufferContext`; `MainOutputEffectGeometryBuffers`
already exists as a second instance and may be borrowable, which would avoid
constructing one.

**Expected result: 15–30 fps.** Everything else is already rate-independent.

### Phase 2 — own the render target  *(removes the crash surface and a copy)*

The panel's material samples a texture handed to it as `colorMetalOverride`:

```csharp
LcdPanelSurfaceContext.SetNewScreenMaterialHandle(
    LcdContentRendererSessionComponent renderer,   // we hold this from the Render hook
    PBRMaterialDefinition baseMaterial,            // source from the surface definition
    float aspectRatio,
    LcdScreenOrientation orientation,
    ResourceHandle<TextureAsset>? colorMetalOverride)   // <- the render target's TextureHandle
```

`CreateRuntimeLcdMaterial` calls `PBRMaterialDefinition.set_ColorMetalTexture` with
it. So pointing that at **our own** `OffscreenRenderTarget` makes the panel sample
our texture directly, and deletes all of this:

- the `CopyResource` into the panel (copy 2)
- the `DrawOne` postfix and `FeedHandover` entirely
- `RequestRender`, and the cross-command-list state transition
- the double-buffer and its one-frame delay
- **the entire eviction problem** — we own the target, so nothing evicts it

That is a large net simplification *and* faster. The open question is sourcing the
base `PBRMaterialDefinition`; `TransitionToCustomRender` obtains one and can be read.

### Phase 3 — own `ScreenBuffers`  *(unblocks fidelity)*

`ExecuteLighting(lBuffer)` reads `CoreSystems.ScreenBuffers.GBuffer` from the
**global**, so real deferred lighting needs a second `ScreenBuffers` swapped around
our pass. It is constructible (public ctor + `InitializeBuffers`) and the static is
a single public field, so this is mechanical rather than novel — but it resizes to
the main viewport, so expect to render at main resolution and downscale.

Cheaper fidelity available *without* Phase 3, already wired behind
`tonemap-armed.marker` and untested:

```csharp
ComputeExposure(cl, hdr, out exposure, out _)          // drives EyeAdaptationJob
ApplyToneMapping(cl, hdr, ldr, exposure, bloom: null)  // neither reads ScreenBuffers
```

That alone should fix the clamped highlights, which is the most visible defect.

## Phase 1 result, and why Phase 2 is now urgent

**Phase 1 worked.** `DrawContextManager.BorrowShadowCulling(rootEntityId)` /
`ReturnShadowCulling(ctx)` is a public pool of properly-initialised culling
contexts. Borrowing one per pass instead of sharing `EnvProbeCulling[0]` took the
camera render from *"7 copies then dead at 12 fps"* to **90 seconds stable at
15 fps**.

Note what did **not** work, since it looks obvious and costs a session: building a
`CullingContext` by hand. The constructor's three `SharedReadableBuffer<T>`
arguments live two levels down (`FirstPass._countersToken.SharedBuffer`), and even
once supplied correctly the context constructs fine and then crashes within
seconds — a bare context lacks whatever setup the engine does around the ones it
owns. Use the pool.

### The handover is structurally unstable, not rate-limited

Capping to 2 fps to separate "is the structure wrong" from "is it too fast"
answered it — the structure is wrong:

| Configuration | Result |
|---|---|
| camera pass alone @ 2 fps | 17 min stable |
| camera pass alone @ 15 fps | 90 s stable |
| camera + handover @ 2 fps (early build) | 337 copies |
| camera + handover @ 2 fps, pooled culling | **6 s** |
| camera + handover @ 2 fps, no pooled culling | **33 s** |

The camera render is solid at every rate tested. The handover dies within tens of
seconds at **2 fps**, in every configuration, with erratic lifetimes — the
signature of a race, not of a bug awaiting one more fix. Eight targeted fixes each
addressed a genuine defect and none made it durable.

Telemetry also showed `drawOne(ours)` firing ~5×/s while we requested 2.5×/s, so
the panel's target is being serviced by the LCD system on its own schedule as well
as ours. **We do not control when that resource is read, written, recycled or
evicted** — which is the root cause in one sentence, and no amount of guarding on
our side changes it.

`BorrowShadowCulling` is also implicated as a secondary regression (6 s vs 33 s):
it is the pool the engine uses for shadow cascades, so borrowing from it for an
environment pass trades one collision for another. Phase 1 needs a context that is
genuinely ours, not one borrowed from another subsystem.

### Superseded: the handover does not scale

| Configuration | Result |
|---|---|
| camera pass @ 15 fps, handover **off** | 90 s stable |
| camera pass @ 15 fps, handover **on** | dead in ~1 s |
| same handover @ 2 Hz | 337+ copies stable |

Eight fixes went into that path — pool race, wrong-target sizing, format mismatch,
resource-state transition, GPU sync, eviction, a resolve-order deadlock, and
producer/consumer tracking. Every one was a real bug; each time another appeared
behind it. The pattern is consistent: **it is fragile because it writes into a
resource owned, recycled and scheduled by someone else.**

No further patching. Phase 2 deletes the whole path rather than repairing it, and
should be done before any fidelity work lands on top of it.

## Phase 2 result: the binding works, the copy site does not

**The material rebind is proven.** A panel can be pointed at a render target we own:

```
Phase 2: binding — material=ok aspect=1 orientation=Deg0
                   handle=ResourceHandle`1 -> ResourceHandle`1
=== PHASE 2: panel material rebound to our own render target. ===
Phase 2: UpdateMaterialReplacements applied.
```

Sourced from `ctx.Definition.DefaultScreenMaterial` (the `LCDScreen_On` PBR
material), `ctx.Definition.AspectRatio`, `ctx.State.Orientation`, and the target's
`TextureHandle` converted via `ResourceHandle<T>` → `ResourceHandle` →
`ResourceHandle<TextureAsset>`.

**What does not work is writing that target from the camera pass.** Copying into an
`OffscreenRenderTargetComponent`'s `ROTexture` from the probe pass is fatal — tested
both without state transitions and *with* explicit `COPY_SOURCE`/`COPY_DEST` on both
ends (`Feed: state transitions available`, no `AutoResourceState` warning, then
death on the first copy). The destination is UI-stage-managed and must be written
there, irrespective of resource state.

### The synthesis this points to

The two halves fit together, and neither alone is enough:

1. **Bind the panel's material to OUR OffscreenRenderTarget** (Phase 2, proven).
2. **Write that target from the UI stage** via `RequestRender(ourHandle)` +
   the `DrawOne` postfix — the handover mechanism, but aimed at *our* target
   instead of the panel's.

That keeps the only legal write site while removing every ownership problem the
original handover had: our target is never evicted by distance, never rebuilt when
the panel churns, never has its id change underneath us, and no other writer
touches it. The panel needs no forced repaints and no `RequestRender` of its own —
it simply samples our texture.

An `OffscreenRenderTarget` is the only resource that offers both a `ResourceHandle`
(needed for material binding) and a writable texture, so it has to be the target;
the constraint is only *where* it may be written.

## Phase 2 delivered an image (2026-07-25 23:50)

Camera frames reached the panel — several frames of a passing comet — through the
full Phase 2 path: scene render → HDR → LDR → parked → `DrawOne` copy into **our**
offscreen target → panel material samples it.

Two faults were visible in that same run, and the symptom named each one:

**"Back to the test pattern"** — two writers to one target. The persistent UI batch
and the camera copy both write during the same `DrawOne` servicing, so whichever
ran last won and the panel alternated. They are mutually exclusive by design; the
test pattern is now suppressed while the handover is armed.

**"Ground to a halt, then crashed"** — progressive slowdown is a *leak*, not a race.
Prime suspect is the persistent batch: `CreatePersistentBatchFor(rt, 0, previous,
deletePrevious: true)` is called every 500 ms, and if the previous batch is not
actually retired they accumulate. Verify before assuming — a `Rates/s` line plus a
batch counter would settle it.

### What the log actually showed, and the three faults behind it

Re-reading the run that produced those frames turned the two symptoms into three
concrete defects, all of them in the handover rather than the render.

**1. The double buffer raced with itself.** The producer/consumer handshake cleared
`_inFlight` on the *first* copy, but `_pendingResource` was never cleared, so the UI
stage kept copying the same texture on every subsequent servicing. Meanwhile the
camera pass saw "consumed" and took that buffer as its next write target:

```
pass N    writes A, parks A
pass N+1  writes B, parks B      <- DrawOne is still copying A
pass N+2  writes A               <- while DrawOne may still be copying A
```

A GPU read/write hazard on the same resource, which is a device removal, which
presents exactly as *ground to a halt, then crashed*. Fixed with a **three-slot
ring** and no handshake at all: write slot *n*, hand over slot *n-1*. The slot being
handed over was written a full pass ago (so the GPU has finished it) and the slot
being written has not been the parked one for a full pass (so nothing is reading
it). Two buffers cannot give both properties; three can, for 1 MB.

**2. The barrier was a red herring, twice over.** `TransitionForCopy` moved the
*source* into `COPY_SOURCE` and never touched the destination, so adding the matching
`COPY_DEST` looked obviously right. It died on the first copy. Turning it back off
died on the first copy too, in exactly the same place — so the barrier was never the
variable. Both ends are now switchable from `feed-config.txt` (`srcTransition` /
`destTransition`) rather than argued about.

### The variable that actually correlates

Lining the sessions up by *which target was being written* separates them cleanly:

| Destination | Result |
|---|---|
| the **LCD system's** offscreen target | 337 copies, sustained |
| a target **we created** via `CreateOffscreenTarget` | dead on copy #1, three launches running |

Every recent crash is copy #1 into our own target; every sustained run wrote the
engine's. That is a property of the two *resources*, not of the barriers, the ring,
or the rate — and `last-copy.txt` never showed it, because its fixed field list
printed `dest: ROTexture, Resolution 512,512` and stopped. `CopyResource` demands
identical resource descriptions — format, dimensions, mip count, array size, sample
count — and none of the ones that could differ were being logged.

So the next run copied nothing. `copyEnabled = 0` made the handover dump both ends of
the copy, plus the first few engine-owned targets as a control group, into
`copy-diag.txt`, and return. The game survived, and the diff answered it in one
number.

### The answer: a mip-count mismatch

```
destination — our offscreen target, and every engine one   MipLevels = 10
source      — our LDR ring texture                         MipLevels =  1
```

`CopyResource` copies a **whole resource** and requires identical descriptions.
A 10-mip destination and a 1-mip source is invalid. Not "wrong pixels" — undefined
behaviour, which in D3D12 without the debug layer means it can limp along for
hundreds of copies and then remove the device.

That is the true shape of nearly every result in this document. *"337 copies stable
at 2 Hz, 7 copies then dead at 12 fps"* is not a contention curve; it is UB with a
rate-dependent fuse. The eight handover fixes that each addressed a genuine defect
and none of which made it durable were all downstream of an invalid copy that no
amount of correct surrounding code could rescue. `RenderContracts.CreateOffscreenTarget(name, resolution)`
has no mip parameter — every offscreen target is created with a full chain — so the
mismatch was there from the first copy ever issued.

The fix is the call, not the resources:

```csharp
CopyTextureSubresource(dst, dstSubresource: 0, src, srcSubresource: 0)
```

One subresource, so mip counts need not match. Mip 0 is what the panel samples at
the range this is tested from; mips 1..9 keep whatever `DrawOne`'s own mip generation
left there, which is worth revisiting if the panel looks wrong at distance.

**The lesson worth keeping:** an intermittent, rate-dependent GPU death is a
*validity* bug until proven otherwise, not a race. Three of us-hours went into
barriers, buffer rotations and consumption handshakes around a copy that was never
legal. The diagnostic that solved it — dump both ends, copy nothing, survive, read
the file live — should have been the first move after the second crash, not the
fifth.

**3. The telemetry hid the one number that mattered.** `drawOne(ours)` read `0.0` for
a whole session — not because the engine was not servicing our target, but because
the "no frame parked" early-out ran *before* the classification. Classification now
happens first, and the rate line carries `heap=`, `pending=` (the manager's pending
render list) and `park#`, so a leak is visible as a climbing number rather than
inferred from the game slowing down.

Two smaller leaks found by reading rather than by measuring: `_targetSurfaces` was
an unbounded `HashSet` pinning every `LcdPanelSurfaceContext` ever rebuilt (each of
which holds a runtime LCD material), now bounded; and the persistent test-pattern
batch kept being drawn on every servicing even after painting stopped, so it is now
explicitly retired with an empty batch when the feed takes over.

### Rules established for writing an offscreen target

1. A target is only drawn when `RequestRender(handle)` queues it and `DrawOne`
   services it. An unqueued target stays black however much is submitted to it.
2. `CreateImmediateBatchFor` is one-shot — recorded, drawn once, discarded. Content
   that must persist across servicing needs `CreatePersistentBatchFor`.
3. The **only** legal write site is `DrawOne`. Copying from the camera pass is fatal
   even with explicit `COPY_SOURCE`/`COPY_DEST` transitions on both ends.
4. One writer per target per servicing.
5. `DrawOne` clears the target before drawing, so the copy must happen on **every**
   servicing, not once per camera frame — otherwise the panel blanks between frames.
   Which means the parked texture must stay readable for a full camera period, which
   is what forces three buffers rather than two.
6. Transition the copy **source** and nothing else. The engine's `AutoResourceState`
   tracker owns the destination; a `COPY_DEST` barrier on top of it is fatal on the
   first copy. Barriers are for resources the tracker cannot see — i.e. ones written
   in another command list.

Rules 1 and 2 are why the panel was black; rules 4 and 5 are why it alternated;
rules 3 and 6 are what the crashes were made of.

## 30 fps reached (2026-07-26 00:19)

With the copy legal, the rate ladder went up without a single fix in between:

| Requested | Delivered | Result |
|---|---|---|
| 2 fps (500 ms) | 2.0 | first `HANDOVER SURVIVED 30 copies` ever |
| 15 fps (66 ms) | 10–12 | 810 copies, no errors |
| 30 fps (33 ms) | 20 | clean, but capped |
| 30 fps, fine clock | **26–29** | 580 copies, `skip=0`, `pending=0` |

The shortfall at every step was **`Environment.TickCount64`**, which quantises to the
system timer interval (~15.6 ms on Windows). A 66 ms gate rounds to ~78 ms → 12.8 fps;
a 33 ms gate rounds to ~47 ms → 21 fps. Both predicted the measurement to within
noise before the fix, which is what made it worth trusting. The frame gates and the
orbit clock now use `Stopwatch` (`Clock.Ms`); the 2 s arm and config polls still use
the coarse one, because they do not care.

Everything the contention analysis earlier in this document predicted would need
Phase 1 — owned culling and clustering contexts — turned out not to be the ceiling at
all. `usePooledCulling = 0` throughout: the feed is still sharing `EnvProbeCulling[0]`
with the engine at 29 fps with no ill effect. **The "frame-rate ceiling from shared
contexts" was the invalid copy's rate-dependent fuse all along.** Phase 1 is not
needed for rate; it may still be worth doing before extra render layers land.

Telemetry across the whole climb: `copies` tracked `drawOne(ours)` 1:1, `skip=0`,
`pending=0` (no queue growth), heap sawtoothing 1.2–1.7 GB with the GC keeping up.
Hot-reload while bound also worked cleanly — Phase 2 re-binds to the new target on
reload, leaking one 512×512 offscreen target per reload, which is fine for dev.

## Fidelity: the cheap tier is reachable (2026-07-26 00:45)

Tonemapping, bloom and sky all run on our own targets. The blocker was a type, and
it was one type for the entire tier:

```
ResizableRWRenderTargetTexture  <- what every post pass is declared against
RWRenderTargetTexture           <- what we were borrowing
Resizable.IsAssignableFrom(plain) = False
```

They are **siblings**, not related by inheritance — same interfaces
(`ITexture2DView`, `IRenderTargetView`, `IRWTexture2DView`, `ICopySourceView`, …),
unrelated classes. So one choice of borrow gates `ComputeExposure`,
`ApplyToneMapping`, `ApplyBloom`, `DrawSkybox`, `ApplyAtmosphere`, `ComputeSSR`,
`ExecuteVolumetricPasses`, `RenderTransparent` and `ExecuteLighting` simultaneously.
`BindableTexturePoolManager.BorrowResizableRWRenderTargetTexture` is the answer, and
the colour target now uses it whenever a fidelity layer is on.

Two things that cost a cycle each and are worth remembering:

**`ApplyToneMapping`'s `bloom` parameter is not optional.** It is a plain reference
type, so null looks legal, and it throws `NullReferenceException` from inside the
engine method. Bloom is therefore computed as *part of* tonemapping rather than as an
independent layer.

**`ApplyBloom`'s out parameter is a `Borrowed<T>` — a pool loan, not a gift.**
Dropping it leaks one texture per frame, thirty a second, which is exactly the
"grinds to a halt" shape already chased once in this document. It is returned after
the tonemap consumes it.

Also: the LDR ring must stay plain `RWRenderTargetTexture` at exactly 512×512,
because it is the source for `CopyTextureSubresource` and subresource dimensions must
match. Tonemapping therefore writes a *resizable* scratch target and the existing
`CopyJob` blit lands it in the ring, rescaling if needed.

### The cost ladder, measured

| Layers | fps |
|---|---|
| geometry + clustered lights + shadows (as before) | **28** |
| + exposure, bloom, tone response | **14** |
| + sky (`IndirectPlanetEnvironmentJob`) | **9.5** |

Every layer is a live `feed-config.txt` switch (`tonemap`, `bloom`, `sky`) and each
self-disables on error without taking the feed with it, so this table can be
re-measured in one session.

### Two open defects in what landed

**Sky spins rapidly** — switched off. `IndirectPlanetEnvironmentJob` is the *probe*
pipeline's sky pass, and probes render six cube faces; it takes a `closeTarget` and a
`farTarget` and we hand it the same render target for both. Its orientation almost
certainly comes from probe-face state rather than from the `view` we pass. Needs
either the correct per-face setup or `DrawSkybox(cl, lBuffer)` instead, which is now
reachable since the colour target is resizable.

**Still overexposed, despite tonemapping running.** The tonemap is genuinely
applied — the log confirms it and bloom is visibly correct — but the *exposure* is
wrong, and the recon says why:

```
SceneDrawSystem._eyeAdaptationJob : EyeAdaptationJob
EyeAdaptationJob.get_Exposure
EyeAdaptationJob.DynamicExposure / ConstantExposure
```

`ComputeExposure` drives and reads the engine's **shared, temporal** eye-adaptation
state — which is adapting to the *player's* view, not ours. So we apply the player's
exposure to our camera's image. Two consequences, one of them worse than the visible
one: our feed is exposed for the wrong scene, and we may be perturbing the player's
own adaptation by feeding our HDR buffer into it every frame.

**Attempt 1 at fixing it, and what it found.** `SceneDrawSystem` carries a
`_environmentProbeExposureJob` — the engine's own exposure for probe-style offscreen
renders, which is exactly what we are. Its `Exposure` property turns out to be a
**`Single`**, a scalar, where `ApplyToneMapping` wants an `ITexture2DView`. Right
number, wrong shape.

Passing it threw an `ArgumentException` that disabled tonemapping *and* bloom for the
session, so the value is now type-checked and falls back to `ComputeExposure`
automatically. The remaining work is to get that scalar into a 1×1 texture — or to
use `EyeAdaptationJob.ConstantExposure`, which the recon shows as a call site
alongside `DynamicExposure` and is backed by a `ScreenQuadJob _constantExposureJob`,
i.e. something that *produces* an exposure texture rather than returning a float.
That is the next thing to try.

### The post passes are not ours to call — main-view corruption, then CTD

Roughly two seconds after `TONEMAP`/`BLOOM` started applying, the game died with
`0xc0000005` (access violation). The screenshot from just before it is the evidence
that matters: **the main view was corrupted** — ship interior stitched into skybox,
the panel's content bleeding into the player's render. Not a bad image on the panel;
our pass clobbering the engine's own render state.

That reframes the whole cheap tier. `ComputeExposure`, `ApplyBloom` and
`ApplyToneMapping` are the *main view's* post-processing passes. Their signatures
take a texture, which made them look self-contained, but they own internal scratch
state, histograms and eye-adaptation buffers that belong to the frame the engine is
already rendering. Driving them a second time, mid-frame, from inside the probe-pass
hook corrupts that state.

This is the same class of problem as `ExecuteLighting`'s global `ScreenBuffers`, and
it was hiding behind a type error: the earlier `ArgumentException` on the exposure
scalar was *protecting* us by disabling tonemapping before it could do damage. It ran
properly for the first time in the session that crashed.

Two suspects within it, in order:

1. **`ComputeExposure` drives shared, temporal eye adaptation.** Already flagged
   above as a correctness bug — the crash suggests it is a stability bug too. The
   feed's HDR buffer is being folded into the adaptation state the main view depends
   on, every frame, from the wrong point in the frame.
2. **`_ldrResizable` is a resizable pooled texture held indefinitely.** Resizable
   textures resize with the viewport; holding one across frames while the engine may
   reclaim or resize it is exactly the kind of aliasing that produces a stitched
   frame. The per-pass HDR borrow is returned correctly; this one is not.

`tonemap` and `bloom` are switched off in `feed-config.txt`. The geometry feed is
unaffected and still runs at ~28 fps.

**What this means for the roadmap.** Fidelity probably cannot be added by borrowing
the main view's post passes at all — which makes Phase 3 (an owned `ScreenBuffers`,
and by extension owned post state) less of an optional escalation and more of the
actual price of admission.

Before committing to that, run the experiment: `passOnFrameHook = 1` moves the camera
pass from the probe hook to the per-frame hook (`DrawUnlit`, id 1), a point where the
engine's own post work for the frame is finished. Same insight that fixed the
base-view snapshot. Not the one-line move it first looked like — the tonemap lives
inside `CopyToFeed`, operating on an HDR target borrowed and returned within the pass,
so moving *only* the post chain would mean keeping that target alive across two hooks.
Moving the whole pass avoids that entirely, and the pass is self-contained (it borrows
every resource it uses), so it is a dispatch change rather than a restructure.

Test in two steps, because they answer different questions:

1. `passOnFrameHook=1`, `tonemap=0` — does the plain feed still work from the
   per-frame hook, and at what frame rate? The probe hook fires ~7×/frame and the
   per-frame hook once, so the achievable ceiling may differ.
2. then `tonemap=1` — and watch the **main view**, not the panel. That is how this
   failure announced itself: the panel looked fine.

If step 2 is clean, the cheap tier is genuinely available and Phase 3 stays optional.
If it corrupts again, the post passes are not borrowable at any point in the frame and
Phase 3 is the only route.

### Recovering frame rate

**Ruled out: render resolution.** The obvious suspicion about
`BorrowResizableRWRenderTargetTexture` was that "resizable" means it sizes to the
main viewport, which would have the whole post chain running at screen resolution.
Logged on every change rather than once (the one-shot version reported `256x256`
from the first pass, before the panel's resolution had resolved, and answered
nothing):

```
Fidelity: colour target RESIZABLE — asked 512, 512, got 512, 512.
```

It honours the request. The post chain runs at 512×512 and the cost is real work,
not an accidental resolution.

Still worth trying, in order:

1. **Constant exposure** — removes the per-frame histogram/adaptation pass entirely,
   and is the correctness fix as well. Two wins from one change, which is why it
   leads.
2. **A pre-made bloom texture** instead of running `ApplyBloom` every frame. The
   tonemap needs a non-null bloom; there is no evidence it needs a freshly computed
   one. Bloom is a multi-pass downsample/upsample chain and accounts for the first
   halving. Note the user likes how bloom looks, so this trades fidelity — measure
   before adopting.
3. **Shorten the culling far plane.** `ClusteringJob.DoWork` is passed `5000f`
   today. A camera orbiting 100 m from a ship does not need 5 km of clusters, and
   this costs nothing to try.
4. **Drop the intermediate blit.** With tonemapping on the chain is
   geometry → HDR → tonemapped LDR → CopyJob → ring slot → panel. The CopyJob step
   exists only because the ring must stay exact-sized for `CopyTextureSubresource`. The tonemap needs a non-null bloom, but there is
no evidence yet that it needs a freshly *computed* one — and bloom is a multi-pass
downsample/upsample chain, so it is the obvious suspect for the first halving.

## GBuffer and deferred lighting: steps 1–3 work, step 4 is blocked (2026-07-26)

Done carefully, one step per build, each independently switchable.

| Step | Result |
|---|---|
| 1. survey, read-only | `ScreenBuffers.GBuffer` is a public settable `ResizableRWRenderTargetTexture[5]` |
| 2. swap array in/out | **works** — ~630 swaps, 29 fps, player's view clean |
| 3. `GBufferPassJob` | **works** — real surface data in our own GBuffer, 27 fps, view clean |
| 4. lighting jobs | **1 of 3.** `LocalLightsJob` runs; directional and ambient are blocked |

Two things step 2/3 needed that were not obvious:

**The depth buffer must be swapped too.** `GBufferPassJob` takes no depth parameter,
so it writes through `ScreenBuffers.DepthStencilBuffer` — the player's 4K one. That
property is `CanWrite=False`, and the backing field is `_depthStencilBuffer`,
**lowercase after the underscore**, so a case-sensitive `Contains("DepthStencilBuffer")`
reported "no backing field" while the storage sat in plain sight. The pass is gated on
owning the depth buffer and correctly refused to run until it did.

**Our GBuffer is 512×512 where the engine's is 3840×2160** — 56× cheaper, which is
why the added pass costs almost nothing.

### Why step 4 stops, and it is the same wall as the post passes

`LocalLightsJob(cl, rtView, rtViewDiffuseOnly, clusteringResult, outputGeometryBuffers)`
works because every input is a parameter — it even takes *our* clustering context.
(Note: `rtViewDiffuseOnly` may not be null; the job throws. Sharing our target works.)

The other two reach past their parameters:

```
DirectionalLightJob(cl, shadowRtView, shadowResources, rtView)   -> NullReferenceException
    DirectionalLightShadowResources exposes DepthMaps (Texture2DTable) and
    SetupConstantBuffer — no ITexture2DView anywhere. shadowRtView is the
    SCREEN-SPACE shadow mask (SHADOW_PASS_FORMAT = R16_UNorm) produced by the
    shadow pass we do not run.

AmbientLightJob(cl, rtView, giBufferDiffuse, giBufferSpecular)
    -> null GI buffers: NullReferenceException
    -> real empty GI buffers: InvalidCastException,
       ResizableRWBuffer -> IConstantBufferView, thrown INSIDE the job
```

That cast failure is decisive: ambient reads a constant buffer from state the main
view's frame sets up, and no argument we can pass changes it. **Same wall as
`ComputeExposure`** — not types, not resolution, not hooks, but per-frame singleton
state that only exists for the frame the engine is actually rendering.

**Symptom worth recording:** with only `LocalLightsJob` running, geometry rendered as
**black silhouettes against a visible sky**. That is the correct output of a partial
deferred chain — local lights composite nothing in open space, and the sky pass runs
afterward filling wherever depth is empty. Not a bug; a complete deferred chain minus
its two main light sources.

### What this means

Deferred lighting through the engine's own jobs is not reachable from a second view
without replicating the frame setup those jobs assume. The forward
`IndirectEnvironmentPassJob` — sun, clustered lights, shadows, no ambient term —
stands as the fidelity ceiling for now, and the harshness is the missing ambient.

Reachable work that does not fight this:

1. **Our own shadow cascades / shadow mask.** Would fix the forward pass's shadows
   (currently the player's cascades, fitted to their frustum) *and* is the missing
   `shadowRtView` for `DirectionalLightJob`. One piece of work unblocking two things —
   the best remaining lead.
2. **Render resolution and AA.** Our target is 512×512 and the panel is 512×512; the
   pixelation in the feed is resolution, not lighting.
3. **`AtmosphereAdditiveJob(cl, rtView)`** — 2 arguments, no GBuffer. Untried.

## Branch `alternate-rendering`: there is a whole-view render entry point

The feed has used `IndirectEnvironmentPassJob` since the first spike — the
**environment-probe** shading path. That was the right choice for proving the plumbing
and it is the reason the lighting is primitive: probes are the *input* to the engine's
ambient lighting, so the pass deliberately renders without ambient, pre-exposed and
range-compressed for cube-map storage. Every fidelity attempt so far has been trying to
bolt main-view quality onto a pass designed to be the cheapest in the engine.

There is a better door, and it was hiding in plain sight:

```
pub Void Draw(ResizableRWRenderTargetTexture finalLDRBuffer)                    <- everything
int Void ScenePreparationAndRender(DirectCommandList commandList, Vector2I finalResolution)
int Void ExecuteScenePreparationAndRender(Vector2I finalResolution)
int Void ExecuteForwardAndPostProcess(lBuffer, out screenshotTexture, finalLDRBuffer)
```

`SceneDrawSystem.Draw` is the **complete main-view pipeline** — preparation, shadow
cascades, GBuffer, full deferred lighting, forward passes, volumetrics, post, exposure,
bloom, tonemap, AA — into one target supplied by the caller. It is public, and its lone
parameter is the type our resizable LDR target already is.

### Why this changes the camera problem too

Swapping `SettingsManager._renderView` did nothing when the target was
`GBufferPassJob`, because by our postfix the camera constant buffer had already been
written for the frame — `_renderView` is the CPU-side *source* of that conversion, not
the binding. `Draw` re-runs the preparation stage, which reads `RenderView` fresh. So
the swap we already built becomes the mechanism rather than a dead end:

```
swap _renderView -> our orbit camera
Draw(our LDR target)
restore _renderView
```

### The risks, stated before trying

1. **Re-entrancy.** Our hook fires from inside the frame that `Draw` itself drives.
   Calling `Draw` from within `ExecuteEnvironmentProbeUpdate` risks recursion or
   half-finished state. It needs a hard re-entrancy guard, and it belongs on the
   per-frame hook rather than the probe hook.
2. **It writes everything.** `Draw` owns the GBuffer, ScreenBuffers, shadow atlases and
   post chain. Running it twice per frame means the player's frame is clobbered unless
   every global it touches is saved and restored — far more than the five we currently
   swap. The GBuffer/depth/resolution/camera swaps already built are the start of that
   list, not the whole of it.
3. **Temporal state, again.** GI, eye adaptation, motion vectors and TAA all accumulate.
   `Draw` advances all of them. This is the same wall that stopped `ExecuteLighting`,
   and `Draw` hits it harder because it includes more temporal passes.
4. **Cost.** It is the full pipeline at whatever resolution it is told. Even at 512x512
   this is a second complete frame render, not a cheap probe pass.

Risk 3 is the one that decides it. If `Draw` cannot be made to leave the player's
temporal state alone, the honest outcome is the same as `ExecuteLighting`: it works,
looks correct, and degrades the main view. `ScenePreparationAndRender(cl, resolution)`
is the more surgical option — it takes an explicit command list and stops before the
final post chain, so it may avoid the worst of the temporal passes while still giving
real deferred lighting.

**Recommended order on this branch:** try `ScenePreparationAndRender(cl, res)` first
(explicit command list, less post, fewer temporal passes), and keep `Draw` as the
fallback if the more surgical call turns out to need state we cannot supply.

## The clamped lighting range: the panel is ALBEDO, not a display

Chasing exposure was the wrong end of the pipe. Sweeping `exposureValue` from 0.25 to
**0.02 — a 12× change — produced no visible difference at all**, and neither did adding
an explicit `ClearRenderTargetView` to guarantee the value reached the texture. A lever
that does nothing across 12× is not a lever that needs tuning.

The reason is what the feed is bound *as*:

```
SetNewScreenMaterialHandle(renderer, PBRMaterialDefinition, aspect, orientation,
                           colorMetalOverride)   ->  PBRMaterialDefinition.ColorMetalTexture
```

`ColorMetalTexture` is the PBR **base colour / albedo** input. Albedo is a
*reflectance*: physically it lives in [0,1], and the surface is then **lit by the
scene** like any other block face. So what reaches the eye is

```
displayed = our_pixel  x  light_falling_on_the_LCD
```

Two consequences, and together they are the whole complaint:

* **Nothing can exceed white.** There is no headroom, because albedo has none. Bright
  parts of the feed cannot bloom or read as light sources.
* **The feed is at the mercy of the room.** The panel sits inside a dim ship interior,
  so everything is multiplied down and compressed into a narrow band.

No exposure value, tonemap curve or render format on our side can beat this, which is
exactly why the sweep was inert. `_hdrFormat` is `R11G11B10_Float` — a genuine HDR
float target — so the range exists right up until the material stage flattens it.

### The route to an actual HDR look: make the panel EMIT

A real LCD in this game glows in the dark, so the emissive path exists. The survey
points at where:

```
ctx.ScreenMaterial          LCDMaterialDefinition
ctx.ScreenMaterial.DefaultState    MaterialStateDefinition   <- inspect this
ctx.Definition.DefaultScreenMaterial   ...\LCDScreen_On.def
```

We have only ever touched `DefaultScreenMaterial` (a raw `PBRMaterialDefinition`,
whose only exposed setter is `set_ColorMetalTexture`). `LCDMaterialDefinition` with its
`MaterialStateDefinition` is the richer object and is where an emissivity multiplier
or emissive texture slot would live. Drive that from our feed and bright pixels become
*emitted light*: they exceed 1.0, survive into the main view's HDR buffer, and get
picked up by the engine's own bloom and tonemapping. That is the HDR look, and it comes
from the main view's post chain — the one place we are allowed to produce HDR values.

A second, cheaper lead supports it. `GBufferIndex` reports **`BaseColor=0,
Emissivity=0` — the same slot**, so emissivity is almost certainly packed into
GBuffer0's **alpha**. Our feed is `R8G8B8A8_UNorm_SRgb` and we blit it with
`_channelAll`, so there is an alpha channel we have never thought about. If the LCD
shader derives emissive strength from it, writing a high alpha may light the feed up
with no material work at all — worth testing before the deeper route.

### Ranked solutions

1. **Emissive material binding.** Dump `MaterialStateDefinition` and look for an
   emissivity multiplier / emissive texture. The real fix.
2. **Alpha as emissivity.** Cheap to test given `BaseColor` and `Emissivity` share
   GBuffer slot 0. Try before (1).
3. **The inert exposure is a separate, real bug.** Even as albedo, 12× should have been
   visible, so the value is not reaching `ApplyToneMapping`'s shader. Likely the two
   channels of that `R32G32_Float` exposure texture are not both "exposure" — clearing
   both to the same number may be meaningless. Needs the channel semantics, read from
   what the engine's own exposure texture actually contains.
4. **Probe pre-exposure at the source.** `EnvironmentProbeExposureJob.Exposure` is a
   scalar in the probe pipeline; if `IndirectEnvironmentPassJob` pre-exposes for
   cube-map storage, range is being compressed before we ever see it. Log its value.

Note also `DisplayHDRIntensity.DoWork(cl, destination, source, viewport)` — four
explicit parameters, safe family, a purpose-built HDR-magnitude visualiser. Blitting
that to the panel instead of the normal copy would show directly whether the HDR buffer
has range, settling (4) without guesswork. No `SceneDrawSystem` field holds it, so it
would need constructing.

## Atmosphere: invokes cleanly, produces nothing (2026-07-26)

`AtmosphereAdditiveJob.DoWork(cl, rtView)` — two arguments, no GBuffer — runs without
error at 27 fps and shows **no scattering on planet atmospheres** in the feed.

So the parameter test predicts *safety*, not *usefulness*. The job is additive over a
target we own and evidently reads its atmosphere setup — LUTs, planet parameters,
camera-relative scattering — from state prepared earlier in the frame by
`UpdateAtmosphere` / `AtmosphereLUTJob` for the **main view**. Nothing throws because
nothing is missing from its arguments; it simply has nothing to add from our
viewpoint.

Left enabled: it costs nothing measurable and is harmless. **Re-test close to a planet
with a real atmosphere before concluding** — the current test position is deep space
looking at a bare moon, which is not a fair trial. If it stays blank there too, it
belongs with `ComputeExposure` and `AmbientLightJob` on the wrong side of the wall.

### Supersampling: attempted twice, reverted. It needs a design, not a parameter.

`renderScale` scales the scene targets and leaves the LDR ring at the panel's exact
size. Two attempts, both wrong, both reverted to `renderScale = 1`:

1. **Scale the render, leave the blit alone** → the feed showed the **top-left
   quadrant**. `CopyJob`'s `cropRect` (a `Nullable<System.Drawing.Rectangle>`) is the
   SOURCE region, and with it null the job reads a rect the size of the DESTINATION.
   1024 source into a 512 destination therefore copies the top-left 512×512 one-to-one.
2. **Pass the full source rect** → top-left quadrant *plus* heavy streaking, i.e.
   sampling past the end of valid data.

The second failure names the real problem. With tonemapping on, the chain is four
stages and they do not all agree on size:

```
HDR (scaled, 1024)  ->  ApplyToneMapping  ->  _ldrResizable (panel res, 512)
                    ->  CopyJob  ->  LDR ring (512)  ->  CopyTextureSubresource  ->  panel (512)
```

The crop rect was computed from the **HDR** target (1024) while the blit's actual
source is `_ldrResizable` (512) — so it read a 1024 rect out of a 512 texture. And
`ApplyToneMapping` was already crossing 1024→512 with no stated intent.

So supersampling is not a one-line change. Every stage needs an explicit, consistent
resolution, and the decision to make is **where the downsample happens**:

* downsample first (HDR 1024 → HDR 512 via `CopyJob` with a correct crop rect), then
  tonemap at 512 — one extra blit, all later stages unchanged; or
* keep everything at the scaled resolution including `_ldrResizable`, and downsample
  only in the final `CopyJob` into the ring — fewer passes, but `ApplyToneMapping` then
  runs at 4× the pixels.

The first is the safer build. Either way the fix is to make each stage's resolution
explicit rather than inferring it, which is what both attempts got wrong.

### Note on perceived fidelity

The visible blockiness and smeared starfield in the feed is **512×512 render
resolution**, not lighting. The panel is 512×512 and the camera target matches it.
Raising render resolution and downsampling is likely to buy more apparent quality than
any remaining lighting pass, and it is a one-line change to `RenderW`/`RenderH` with a
known cost curve.

## Shadows: the route in, and the shortcut

```
DepthPassJob _shadowsDepthPass
  DoWork(cl, TrackedCameraSettings& view, GeometryContext, OutputGeometryBufferContext,
         IDepthStencilView depthStencil, clear, DepthJobType, allowTessellation, isFarCascade)
```

Every input is a parameter — **including the view and the depth target** — so a shadow
map can be rendered from the sun's viewpoint into a depth texture we own. Safe family.

`DirectionalLightShadowResources` has a **public parameterless constructor** and only
three fields:

```
Texture2DTable   _depthMaps
Int32            _depthTableLength
Nullable`1       _setupConstantBuffer     <- the cascade view-projection matrices
```

**The shortcut:** `IndirectEnvironmentPassJob` — the forward pass already producing
every image — takes `DirectionalLightShadowResources` **as a parameter**. So the
shadow mask and `DirectionalLightJob` are not needed at all. Construct our own
resources with cascades fitted to *our* frustum, hand them to the pass we already
run, and the feed's shadows become correct. Same core work, and the payoff lands in
the path that works rather than the one that is blocked.

Next concrete step: survey `_setupConstantBuffer`'s layout (it is a
`Nullable<TransientConstantBuffer>`, so the struct behind it needs dumping) and the
`Texture2DTable` construction. Then: build per-cascade light views, cull each with
`CullingJob.DoCullingFirstPass(..., cascadeIndex)`, render each with `DepthPassJob`,
assemble the table, and pass the bundle to the forward pass.

## HDR to the panel is not available

`RenderContracts.CreateOffscreenTarget(String name, Vector2I resolution)` is the only
overload — **no format parameter** — and every offscreen texture registered at runtime
is `R8G8B8A8_UNorm_SRgb`. The panel path is LDR by construction. An
`OffscreenRenderTarget` is also the only resource offering both a `ResourceHandle`
(needed to bind the panel material) and a writable texture, so there is no alternative
resource to point the material at.

The scene *is* rendered HDR (`R11G11B10_Float`) and tonemapped down, so the dynamic
range is being used — it is only the final handoff that is 8-bit sRGB. What HDR would
buy is highlight headroom for the main view's bloom to pick up, and that would need
the panel material treated as emissive as well as an HDR source.

## Correction: the culprit was `ComputeExposure` alone (2026-07-26)

The "unsafe family" rule below was drawn from a test that never separated
`ComputeExposure` from `ApplyToneMapping` — they ran together, the main view
corrupted, and the conclusion was written about the pair. Bisected properly by
reusing an exposure texture the engine had already computed (a passive read) instead
of calling `ComputeExposure` (which writes the histogram, `_autoExposures`, and the
temporal adaptation the main view depends on):

**`ApplyToneMapping` is safe.** Running for an extended session with the player's
view confirmed clean. Tonemapping is available today, without Phase 3.

So the rule needs sharpening. "Is it a job class?" was the wrong test —
`AmbientLightJob` is a job class whose `DoWork(cl, rtView)` overload clearly reads
normals it was never handed. The real test is narrower:

> **Does the pass read anything it was not passed as a parameter?** If yes, it needs
> that state to be ours before it can run. If it only writes what it is given, it is
> safe.

`ComputeExposure` fails because it *writes* shared temporal state. `AmbientLightJob`
fails because it *reads* a shared GBuffer. `ApplyToneMapping` passes because input,
output, exposure and bloom are all arguments.

### Exposure is now owned outright

`EnvironmentProbeExposureJob.Exposure` is a scalar `Single`, so it cannot drive
`ApplyToneMapping` directly — but the texture pool clears a borrowed target to a
colour we supply, which means **a 1×1 target cleared to `x` IS a constant-exposure
texture**. Owned, no shared state, format matched to the engine's own
(`R32G32_Float`), re-borrowed only when the value changes, and live-tunable via
`exposureValue`. No `ComputeExposure`, no `EyeAdaptationJob.ConstantExposure` needed.

## Why the feed looks harsh: there is no GBuffer

Tonemapping and exposure turned out not to be the problem. The feed renders via
`IndirectEnvironmentPassJob`, which writes **colour and depth only** — no normals, no
roughness, no motion vectors. And essentially every pass that would soften the
lighting needs exactly those:

| Want | Pass | Needs |
|---|---|---|
| ambient / indirect fill | `AmbientLightJob` | GBuffer normals |
| ambient occlusion | `HBAOJob(cl, settings, rtView, depth, **normal**)` | GBuffer normals |
| local lights, properly | `LocalLightsJob(cl, rtView, rtViewDiffuseOnly, clusters, buffers)` | GBuffer |
| reflections | `ScreenSpaceReflections(cl, cb, dest, depth, **roughness**, **normal**, motion)` | GBuffer |
| GI | `RaytraceGIJob`, `SurfelGenerationJob` | GBuffer |

**And this is by design, not a defect.** Environment probes are the *input* to the
engine's ambient lighting, so the probe path deliberately renders without indirect
light to avoid feeding itself. We adopted the cheapest, flattest shading path in the
engine — which was the right call for proving the plumbing, and is now the ceiling.

Direct sun plus clustered lights with no ambient term is precisely the "harsh" look:
lit faces blow out, unlit faces go flat. No exposure value fixes that, because the
dynamic range in the image is genuinely wrong, not merely mis-mapped.

### Phase 3 is smaller than previously assumed

The earlier note said a whole second `ScreenBuffers` instance was needed. It is not:

```
ScreenBuffers.GBuffer          prop ResizableRWRenderTargetTexture[]   // settable
ScreenBuffers.set_GBuffer(ResizableRWRenderTargetTexture[] value)
ScreenBuffers.GetGBuffer(GBufferIndex)   GBufferFormats
```

The array is a **public settable property**, so the shape is a swap around our pass:

1. Allocate our own array matching `GBufferFormats` at our render resolution.
2. Save `ScreenBuffers.GBuffer`, set ours.
3. `GBufferPassJob.DoWork(cl, geometryContext, outputGeometryBuffers, clear, fsrMasks)`
   — no render-target parameters, so it writes whatever the global points at, which
   is now ours.
4. Lighting: `DirectionalLightJob`, `LocalLightsJob`, `AmbientLightJob` — several take
   explicit render targets and our own clustering context.
5. Restore the saved array.

Two things make this tractable that were not known before: `ScreenSpaceReflections`
and `HBAOJob` take every GBuffer component as an explicit parameter, so those need no
swapping at all once a GBuffer exists; and `LocalLightsJob` already accepts our
`ClusteringContext` and `OutputGeometryBufferContext`.

The risk is unchanged in kind: swapping a global mid-frame while the engine's own
work may be in flight. The probe hook is inside probe work rather than main-view
GBuffer work, which is the reason to expect it to survive — and the reason to verify
it with the player's view rather than the panel.

## The rule that decides what is addable: two families of pass

The per-frame-hook experiment settled this, and it reorganises the whole roadmap.
What matters is not *when* in the frame a pass runs — it is **whether the pass takes
its state as parameters or owns it as a singleton**.

**Safe family — reusable job objects, all state explicit.** These live on their own
job classes and receive every context they touch as an argument. The probe pipeline
itself calls them repeatedly with different contexts, so a second caller is expected
by design:

```
CullingJob.DoCullingFirstPass(cl, view, lodSettings, cullingContext, buffers, …, cascadeIndex)
ClusteringJob.DoWork(cl, entityProxies, buffers, clustersContext, resolution, farPlane)
IndirectEnvironmentPassJob.DoWork(cl, buffers, cameraCb, view, …, rt, depthStencil, clear)
IndirectPlanetEnvironmentJob.DoWork(cl, cameraCb, closeTarget, farTarget, depthTex, view)
MipMapJobExtensions.DoWork(job, cl, target, mipsCount)
```

Three of these already run in the feed, for hours, at 29 fps.

**Unsafe family — `SceneDrawSystem` methods shaped `(commandList, lBuffer)`.** They
*look* self-contained because a texture is the only obvious input, but the texture is
the only thing they take as a parameter; everything else — histograms, eye-adaptation
buffers, internal scratch, `ScreenBuffers` — is per-frame singleton state belonging to
the frame the engine is already rendering:

```
ComputeExposure(cl, lBuffer, out exposure, out histogram)
ApplyBloom(cl, input, exposure, out bloom)
ApplyToneMapping(cl, input, output, exposure, bloom)
DrawSkybox(cl, lBuffer)      ApplyAtmosphere(cl, lBuffer)     ComputeSSR(cl, lBuffer)
ExecuteVolumetricPasses(cl, lBuffer, oit, fsr)                ExecuteLighting(lBuffer)
```

Driving the first three corrupted the player's render from **both** hooks — a hard
stitch plus `0xc0000005` from the probe hook, flickering lights and black lines from
the per-frame hook. Less acute, same defect. There is no safe moment, only a less
frequent one. `ExecuteLighting` takes no command list at all, which is the same
signal in its purest form.

**The distinguishing test, for anything not yet tried:** does the method belong to a
job class and receive its contexts as arguments? Then it is a candidate. Is it a
`SceneDrawSystem` method whose only parameter is a buffer? Then it needs Phase 3
first, and trying it costs a launch and possibly the player's frame.

## Recommended order

Rate is solved. Everything left is fidelity, and it splits cleanly along the rule
above.

1. **Sky — fix `IndirectPlanetEnvironmentJob`.** Safe family, fully explicit
   parameters, already resolved and invoked. It spun rapidly, which is a *calling*
   bug and not corruption: both `closeTarget` and `farTarget` were handed the same
   render target, so the near and far sky layers overwrite each other with different
   projections. The largest visible gap in the feed — there is no sky at all today.
2. **Own shadow cascades.** Today `IndirectEnvironmentPassJob` gets the engine's
   `DirectionalLightShadowResources` — cascades fitted to the *player's* frustum,
   which is why shadows in the feed are partial and unreliable. `CullingJob
   .DoCullingFirstPass` takes a `cascadeIndex`, so culling per cascade is available in
   the safe family. Real lighting fidelity without Phase 3.
3. **Mips 1–9.** The subresource copy writes mip 0 only; the rest hold whatever
   `DrawOne`'s own mip generation left. `MipMapJobExtensions.DoWork` is safe family.
   Check whether the panel actually looks wrong at distance before spending on it.
4. **Phase 3 — own `ScreenBuffers` and owned post state.** The only route to
   tonemapping, SSR, atmosphere, volumetrics and transparency. No longer an optional
   escalation; it is the price of admission for the unsafe family, and that is now a
   measured result rather than a prediction.

Preserved from the abandoned cheap-tier attempt, all still valid:
`BorrowResizableRWRenderTargetTexture` is the type key to every post pass;
`ApplyToneMapping`'s `bloom` argument must be non-null but need not be computed
(cheap stand-in bought 14 → 24 fps); `EnvironmentProbeExposureJob.Exposure` is a
scalar `Single`, so `EyeAdaptationJob.ConstantExposure` is the exposure route.

~~Phase 1 — the only thing standing between here and 15–30 fps.~~ Superseded: 29 fps
runs today while still sharing the probe context.

## Worth measuring before Phase 3

We have no timing data. Before committing to full lighting it is worth logging
per-pass GPU/CPU cost at 15 and 30 fps, so the budget is known rather than assumed —
Grid Schematics' `BandCost` telemetry is the model.
