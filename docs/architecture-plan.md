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

## Recommended order

Rate is solved; everything left is fidelity or hardening.

1. **Tonemapping** — cheap, self-disabling, biggest visual win per unit of risk.
   Already wired behind `tonemap-armed.marker` and still untested. Fixes the clamped
   highlights, which is now the most visible defect.
2. **Mips 1–9 of the destination** — the subresource copy writes mip 0 only, so the
   rest hold whatever `DrawOne`'s mip generation left. Check whether the panel looks
   wrong at distance before deciding this matters. `MipMapJobExtensions.DoWork(job,
   cl, RWRenderTargetTexture, mipsCount)` takes an RW texture, so it would have to run
   on our LDR source before the copy, with the source allocated with a full chain.
3. **Phase 3** — own `ScreenBuffers` for real deferred lighting. The fidelity project.
4. **Phase 1** — owned culling/clustering contexts. No longer needed for rate; revisit
   only if extra render layers reintroduce contention.

~~Phase 1 — the only thing standing between here and 15–30 fps.~~ Superseded: 29 fps
runs today while still sharing the probe context.

## Worth measuring before Phase 3

We have no timing data. Before committing to full lighting it is worth logging
per-pass GPU/CPU cost at 15 and 30 fps, so the budget is known rather than assumed —
Grid Schematics' `BandCost` telemetry is the model.
