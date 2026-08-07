# Localising raytraced ambient and reflections to the feed camera

**Reported 2026-08-04, from the camera inside and near the Red Ship in space:** the feed's
raytraced ambient and reflections are visibly feeding off the MAIN world's full-screen
render, not the environment the feed camera is actually in.

That observation is almost certainly right, and the reason it is worth writing down before
building anything is that **there are at least four independent mechanisms that could each
produce it**, they need different fixes, and three of them are cheap to test while the
fourth is a large piece of work. Building the large one first — which is the tempting move —
risks spending a day on a component that was not the dominant vector.

---

## What is already known, with evidence

**1. The acceleration structure is built around the PLAYER, and there is only one.**
Stage 0 `ExecuteAccelerationStructuresBuilding` is SKIPPED for our pass (it is in
`wholeSceneSkipStages`) because `RayTracingSceneManager.CreateTLAS` is camera-dependent AND
world-space shared — building it from our camera corrupted the player's. Stage 17
`RaytraceGIJob.DoWork` is NOT skipped and `wholeSceneDisableRaytracing = 0`, so **we trace
OUR camera's rays against a structure built for the PLAYER'S position.**

**2. The TLAS is populated, but we cannot yet say WITH WHAT.** Live RT probe:

    HasScene=True RTInstanceCount=3065 instances=3065 geometries=5020
    roots=1112 instancedModels=2112 flora=233 pointLights=112
    1112 root(s), NONE positionable — reader blind, draw no conclusion

3065 instances is not an empty structure. Whether any of them are near the feed camera is
**unmeasured** — the `Buffer<T>` fields are native (`IntPtr _data + _count`), so counts read
but elements do not. `_rootEntityToIndex` is a managed Dictionary, so its KEYS are the one
available route to "is any RT root entity actually near our camera". Until that is read, "the
TLAS is the problem" is a hypothesis, not a finding.

**3. The radiance accumulators are SHARED and TEMPORAL.** ReSTIR reservoirs, the IR cache and
its scrolling are global (`EnableTemporalReSTIR`, `EnableSpatialReSTIR`, `EnableTemporalFilter`,
`EnableIRCache`, `EnableIRCacheScrolling`). Two cameras writing one accumulator is
cross-contamination by construction, and it is temporal, so the player's radiance persists in
our frames and vice versa.

**4. Screen-space reflections are literally the player's screen.** `ScreenSpaceReflections.DoWork`
is skippable as stage 29 — described in our own stage table as "THE PHANTOM BLEED... the
SHARED SSR temporal history (AverageRadianceHistory / VarianceHistory / SampleCountHistory)
live on `SceneDrawSystem._screenSpaceReflectionsJob`, which we do not swap" — and **stage 29
is NOT in the skip list.** SSR is screen-space: it reflects what is on screen. For our pass
that history is the player's. This is the single most direct explanation of "reflections are
feeding off the main world render", and it is currently unsuppressed.

**5. Entity-level RT inclusion is ALREADY partly localised.** `viewerDistance` overrides
`RenderUtilities.CalculateDistanceToCamera`, which the log line itself describes as "the one
input to StreamingTag, the impostor swap, shadow tracking and the raytracing near/far tags".
So which entities are TAGGED for raytracing does already see our camera, within
`viewerDistanceRadius` (1000 m). That is a partial localisation nobody should re-do.

---

## 2026-08-05 UPDATE — H1 IS SETTLED AND FIXED; H2 WAS PARTLY WRONG

Two new inputs changed this materially.

**The user confirmed the SSR half observationally:** "screen spaced reflections are also
clearly taking from the main world render and not the feed's perspective." Combined with the
IL below, H1 stopped being a hypothesis.

**And the fidelity budget was set:** "I'd be happy with a comparatively low resolution and
noise RT based ambient lighting and reflection solution to save on cost." That is what makes
the SSR fix a single flag instead of a second history to own.

### The asymmetry that decides everything here

The budget helps enormously on reflections and barely at all on ambient, and the reason is
that the two settings structs are CONSUMED DIFFERENTLY:

| | how DoWork reads it | safe to scope per-pass? |
|---|---|---|
| `RaytracingSettings` | `RaytraceGIJob` keys a `LazyJobSnapshotHandler` off it; `BuildTraceShaderDefines` turns fields into **shader defines** | **NO** — async PSO compile at our cadence. The bright flashing; took the device twice |
| `SSSRSettings` | `ScreenSpaceReflections.DoWork` reads `CoreSystems.Settings.SSSR` **directly**, fields consumed inline. PSOs built once in `InitializeAsync` | **YES** |

So `RaytracingResolutionScaleFactor`, the five `Quality` enums and `LODOffset` are all real
dials — and all unusable per-pass, because they are `RaytracingSettings` fields. The "just
turn the quality down for the feed" move works for SSR and not for GI.

### H1 — CONFIRMED, and it was contaminating BOTH views

`EnableTemporalAccumulation` gates the **entire** denoiser block in `DoWork` (branch at
`IL_047a` jumps all of it). Inside that block, and only inside it:

1. reads the shared `RadianceHistory` / `VarianceHistory` / `SampleCountHistory` /
   `AverageRadianceHistory` — the player's accumulated radiance, which is exactly "reflections
   feeding off the main world render";
2. writes our radiance back into them — the original phantom bleed, seen from the other side;
3. `IL_0558 stfld _previousViewProjection` — **our camera's view-projection stamped onto the
   job, so the PLAYER'S next frame reprojects its reflection history through OUR camera.**
   That third one is a defect in the player's view that nobody had attributed.

Clearing it for our pass alone removes all three. The feed KEEPS reflections: the false path
falls through to `_applyReflectionsJob` on the raw intersection result, so they become noisy
rather than absent. **This is the rare fix that is cheaper than the status quo** — the
denoiser dispatches go with it.

Shipped as `wholeSceneSsrLocal`. Not a rebuild-signature knob, so it toggles live: flip 0/1
in the same session for a clean A/B.

**Also checked and NOT a problem** (recorded so it is not re-investigated): `PrepareResources`
does run inside our nested Draw (`ComputeSSR` -> `ExecuteForwardPasses`), and it disposes and
reallocates all nine dynamic resources on a resolution change — but it guards on
`CoreSystems.SwapChain.Resolution`, which we do **not** swap, so it is inert for us. There is
no per-frame realloc thrash. The `DepthHierarchy` mip count *is* computed from
`ScreenBuffers.PreUpscaleResolution` (ours during our pass), but both 1024 and 2560 clamp to
the same `min(..., 7)`, so that is harmless too.

### H2 — the framing was wrong; the reservoirs were never shared

`RTGIContext` — `TemporalResources` (the ReSTIR reservoirs), `PreviousScreenDepth`,
`PreviousScreenNormals`, `DiffuseProbes`, `Specular`, `DiffuseDirect` — lives on
`DrawContextManager.RTGIContext`, and **ours is already a separate context.** So "two cameras
writing one reservoir set" was never true.

What IS shared is the **world-space IR cache**, and the interesting part is the direction:
`IRCacheTraceJob` runs in `RaytracingPrepare` = **stage 30, which we skip**. So our render
never populates the cache; it samples the one the player's frame maintains. If that cache is a
volume scrolled around the PLAYER (`EnableIRCacheScrolling`), then a feed camera far from the
player samples outside it — which would look like ambient that is present but wrong, and that
is the reported symptom. **Unmeasured.** This is now the cheapest remaining ambient lead.

**H3 — Our rays hit a structure that does not contain our surroundings.** Plausible, and the
expensive one. *Test first, build second:* make the RT probe positionable by enumerating
`_rootEntityToIndex` keys and reporting how many RT roots lie within N metres of the feed
camera. If that number is ~0 while the feed is looking at a ship, H3 is confirmed and the fix
is a second `RayTracingSceneManager`.

**H4 — Ambient/probe terms are the player's.** Partly handled: `wholeSceneOwnProbes = 1` means
we own the probe manager. But `EnvironmentProbeManager::UpdateLocalLightAmbient` reads the
global `RenderView`, and stage 27 (`PrepareProbes`) is deliberately NOT skipped because we own
them. Worth a read-back before assuming it is clean.

---

## Order of work

1. ~~H1~~ **DONE** — `wholeSceneSsrLocal`, awaiting a deploy. Grade it by toggling 0/1 live.
2. **Is the IR cache anchored to the player?** (revised H2) Read `EnableIRCacheScrolling` and
   find what the cache volume is centred on. If it follows the player's camera, a remote feed
   samples outside it and that is the ambient bug — and the fix is likely the same shape as
   the presence fix: point the anchor at `PresenceCentre`, not own a second cache. Cheapest
   remaining lead by a wide margin.
3. **Make the RT probe positionable** (`_rootEntityToIndex` keys) — how many RT roots lie
   within N metres of the feed camera. This is the measurement that decides whether H3's big
   job is needed at all, and it is one instrument rather than a day of building.
4. **Only then, if H3 survives:** own a second `RayTracingSceneManager`, parked in the
   bootstrap, installed for our render, restored on unwind — the `EnvironmentProbeManager`
   pattern from goal 4.4, so the shape is known rather than novel.

**Do NOT reach for the RT quality dials to buy budget for any of this.** They are
`RaytracingSettings` fields; see the table above.

## The rule this project keeps re-learning, applied here in advance

**"Suppress the shared write" and "own a second copy" are different fixes, and for anything
the feed needs to CONSUME, only owning works.** Skipping stage 29 stops the contamination but
costs the feed its reflections; skipping GI would cost it ambient entirely. Suppression is the
diagnostic; ownership is the fix. This was got wrong twice on 2026-08-04 (atmosphere LUTs,
then the RenderView) and both times the skip made the feed worse while the player's frame was
fixed by accident.

### ...and the exception, which the SSR fix is an instance of

`wholeSceneSsrLocal` is a suppression, and it is nonetheless the right fix. The rule needs the
sharper form:

> Suppression is wrong when it removes something the feed CONSUMES. It is right when it
> removes an ACCELERATION whose only value came from history that cannot be ours anyway.

Temporal accumulation is not a source of reflections — it is a denoiser over reflections that
already exist. Stage 29's skip and this flag look superficially similar and are opposites:
skipping the stage removes `_applyReflectionsJob` and the feed has no reflections; clearing
the flag keeps the apply and removes only the accumulation.

The tell that distinguishes the two cases, and the thing worth checking next time before
assuming a skip must be wrong: **follow the false branch to the end of the method.** If the
output still gets written on that path, suppression is a quality trade. If it does not, it is
a feature deletion. Here, `IL_0a6f` onwards still applies the reflections — read before
concluding, which is the same discipline that killed the realloc-thrash worry above.
