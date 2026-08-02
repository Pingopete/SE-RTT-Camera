# OPEN QUESTION — is the feed TRULY remote, or only as remote as the client's streaming?

**Raised 2026-07-30. Unanswered. This is the highest-value open question in the project,
because it decides what the camera-block mod (goal 6) actually IS.**

Two products hide behind the same feature:

- If the feed can render anywhere: a **system-wide remote camera**. Put a camera on a station
  in another orbit and watch it from your ship. That is a headline capability.
- If the feed can only render what the client has already streamed: a **local security
  camera**, useful within a few km of the player and blank beyond it. Still worth building,
  but a completely different pitch, a different UI, and different user expectations.

Nothing about the mod changes between those two outcomes. The difference lives entirely in
the engine's streaming layer, which this project does not touch.

## What is already settled: the RENDER is genuinely camera-relative

This is not a trick anchored to the player's viewpoint. Every view-dependent input to the
second `Draw` is derived from OUR camera:

| system | evidence it follows our camera |
|---|---|
| culling / visibility | our own `DrawContextManager` — owning it was required precisely because culling from a second camera into the engine's shared lists corrupted the player's view |
| shadow cascades | `wholeSceneOwnShadows` fits every cascade frustum around the orbit camera; that fixed "the ship goes dark at some orbit points" |
| environment probes | `wholeSceneOwnProbes` exists *because* the player's atlas is centred on the player and therefore wrong for the camera |
| planet atmosphere | `wholeScenePlanetEnv` rebuilds `PlanetEnvironmentGroup`'s setup CBs from the orbit camera; that fixed the detached-atmosphere bug |
| far plane | `wholeSceneFarClip` applies to OUR view only, and deliberately leaves `VeryFarClipping` alone so planets keep rendering |

So there is no known player-position dependency in the render path.

## What is NOT settled: what the client has resident to draw

The renderer can only draw what is already in client memory. That is the simulation and
streaming layer:

- **Voxels / asteroids** stream through the octree around the player
  (`UseOctreeRegionStreaming`, `UsePlanetTextureStreaming` in the engine's core config).
  Far from any player, the data is likely not resident.
- **Grids** exist client-side only if replicated to this client. A ship on the far side of
  the system is probably not loaded at all.
- **Planets, sun, skybox** are the exception — rendered from global data at any range, which
  is why the 2500 m far clip does not remove them.

**PREDICTION (inference, NOT observation):** a camera across the system shows planets and
stars correctly, and empty space where local asteroids and ships should be — because that
geometry is not in memory to cull against, not because our render failed.

## Why this is currently untested

Every render this project has ever done has been from ~100 m from the player's own grid: the
orbit camera orbits the tagged panel's grid by construction, and `wholeSceneFarClip` is 2500 m.
Nothing at distance has ever been exercised.

## The cheap first test (config only, no rebuild)

1. Raise `wholeSceneFarClip` hard (50000+) — live knob.
2. Push `orbitRadius` out progressively: 500 m, 2 km, 10 km.
3. Watch what survives as the camera pulls away.

Reading the result:

- distant grids and voxels stay visible -> streaming is more generous than expected, and the
  system-wide product is on the table
- they vanish while planets remain -> the streaming limit is confirmed, and its RADIUS is
  the number that defines the product

Either way the answer is visible in one minute of config edits, and the radius at which
things disappear is itself the specification.

## If streaming IS the limit, the follow-up questions

- Can a client be made to stream a region it has no player in? Is there a subscription or
  interest-management API? (An `EngineQuery` sweep for streaming/interest/replication
  entry points would answer this offline, without a running game.)
- Does the answer differ singleplayer vs multiplayer, where replication is server-driven?
- Is there a cheap "distant grid impostor" representation the engine already keeps for
  something else?
- Would a bounded compromise work — force-stream a small region around an active camera,
  paid for out of the same budget as the feed itself?

That last one is the likeliest shape of a real fix, and it would make the VRAM cap even more
load-bearing: a remote camera would then cost geometry residency as well as render targets.

## Status

**Untested. Do not design goal 6's UI, terminal options or marketing around system-wide
range until the radius test above has been run.**

---

## FIRST RUN 2026-07-30 22:34 — partial, and it corrects the test's own premise

### Finding 1: the render does not care about distance. At all.

Orbit radius swept 100 m -> 500 m -> 2 km -> 10 km, live, no gate cycle:

| orbit radius | fps | CPU submit | VRAM |
|---|---|---|---|
| 100 m (baseline) | 53.0 | 2.3 ms | 12.32 GB |
| 500 m | 53.8 | 2.2 ms | 12.36 GB |
| 2 km | 54.1 | 2.3 ms | 12.33 GB |
| **10 km** | **53.9** | **2.2 ms** | **12.34 GB** |

Flat. No errors, no stutter, no VRAM movement. Whatever the streaming answer turns out to
be, **rendering from 10 km away costs exactly what rendering from 100 m costs**, which is
itself a real result: the route has no distance-dependent cost term.

### Finding 2: `wholeSceneFarClip` CANNOT extend the far plane. It is min-only.

```csharp
private static float FarClip(float engineFar)
    => clip > 0 && engineFar > clip ? (float)clip : engineFar;
```

It takes the SMALLER of our value and the engine's. Step 1 of the test above — "raise
`wholeSceneFarClip` hard (50000+)" — is therefore **impossible as written**: setting 50000
silently yielded the engine's own `FarClipping` of **15000 m**, which the log states plainly
(`Feed far clip: 15000 m`) and which nobody had read closely because the knob had only ever
been used to pull the plane IN as a perf lever.

**So there is a RENDER-side ceiling at ~15 km that has nothing to do with streaming.**
`VeryFarClipping` stays at 1,000,000 m and is deliberately untouched, which is why planets
and sky are exempt — exactly as the far-clip comment says.

This matters for how the remaining question is asked: **beyond 15 km, "nothing is drawn" is
OUR far plane, not the engine's streaming.** The two limits have to be separated before any
disappearance can be attributed. Extending the plane means RAISING `FarClipping` on our
render view — a code change, and one with a real cost term behind it (the far plane is what
culling reads, so widening it widens the cull).

### Still open

The visual verdict at 10 km, and everything past 15 km. The revised sequence:

1. Change `FarClip` to allow raising the plane, behind its own config flag so the perf lever
   keeps its current min-only behaviour by default.
2. Re-sweep with the plane genuinely open: 20 km, 50 km, 100 km.
3. Only THEN is a disappearance attributable to streaming.

Until step 1 exists, this document's "one minute of config edits" claim is wrong and should
not be repeated.

## SECOND RUN 2026-07-30 22:39 — the plane opened to 50 km, camera out to 50 km

`wholeSceneFarClipExtend` was built (ac7a9f6) so the plane could genuinely be pushed out,
then the camera was swept to a **50 km orbit with a 50 km far plane**.

| | fps | GPU time | frame time | CPU submit | VRAM |
|---|---|---|---|---|---|
| 100 m orbit, 2.5 km plane | 53.0 | — | — | 2.3 ms | 12.32 GB |
| **50 km orbit, 50 km plane** | **54.0** | **18.3 ms** | **18.5 ms** | **2.3 ms** | 12.32 GB |

Engine's own STATISTICS row, not just our counters. **Distance and plane width are free.**

**What the feed actually showed at 50 km** (window capture, `output/frames/`): a ringed
planet, the sun, the Milky Way band and a full starfield — rendering cleanly, no black
screen, no artefacts. The global-data layers behave exactly as predicted: they are not
streamed, so they render at any range.

### What this does NOT answer, stated plainly

**Nothing streamed was ever in frame.** The orbit camera looks inward at the tagged panel's
grid, and at 50 km that grid subtends almost nothing; no other ship, station or asteroid
happened to lie along the view. So "we saw only planets and stars" is consistent with BOTH
hypotheses and distinguishes neither:

- streaming dropped the local geometry, or
- there was no local geometry in that direction to begin with.

`ourDraw` cannot arbitrate either: it stayed 2.2-2.3 ms from 100 m to 50 km, because submit
is dominated by the whole-scene CULL (proportional to world entity count, unchanged) rather
than by the drawn subset. **Submit is not a visibility proxy — do not use it as one.**

### The test that would actually settle it

Point a camera at a KNOWN distant object and vary the distance to it:

1. Park a second grid (or note an asteroid) at a known position.
2. Aim the feed camera at it — needs an aim mode, since the orbit always looks inward at its
   own grid. This is the real blocker, and it is a small feature: a config-set look-at target.
3. Fly the PLAYER away from it in steps, leaving the camera on it. The distance at which it
   disappears from the feed is the streaming radius, and it is the product specification.

Step 3 is the important inversion: it is the PLAYER's distance from the content that drives
streaming, not the camera's. Every test so far moved the camera while the player stayed put
next to everything — which is precisely why nothing has ever disappeared.

---

## 2026-07-31: THE ENGINE RECON ANSWERS MOST OF THIS — see remote-object-instancing-recon.md

Read from the shipped assemblies, not tested in game. The short version:

- **Entities (grids/stations) exist client-side EVERYWHERE in singleplayer.** In-process
  replication is type-based (`ServerSceneContext.ShouldReplicate` → `IsReplicatable`, IL
  read: no position, no distance, no observers), nothing unloads them at runtime
  (offloading = save-time mission areas; trash removal = junk). The "local security camera"
  fear was aimed at the wrong system.
- **Voxel terrain MESHES are the player-bound part**: every body's clipmap LODs around
  `RenderSettings.CameraTransform` — one global slot, sole writer `SetCameraParameters`,
  fed by the engine with the player view.
- **The fix has an engine-native shape**: per-feed `VoxelClipmap` instances (the ctor takes
  a WorldTransform + a quality tier; `InstantiateLowResClipmap` is the multi-clipmap
  precedent; `Unload()` completes the owned lifecycle). Do NOT time-share the engine's
  clipmap or the global camera slot — single-slot tug-of-war, both directions rebuilt
  every round.
- Procedural encounters remain player-bound by design; multiplayer unverified.

The radius test this document proposed is superseded by sharper predictions: distant BUILT
grids should be visible TODAY (needs #17's look-at target to aim at one); distant TERRAIN
stays coarse until per-feed clipmaps exist.

---

## 2026-08-01: THE POPPING BUG AND GOAL 10 ARE THE SAME PROBLEM

Chasing the user-reported "objects and terrain LODs flipping back and forth, objects
popping in and out" ended up in this document rather than a bug report, because the
mechanism turned out to be the one this file exists to ask about: **the engine models
exactly ONE viewer.** Today the feed deals with that by briefly *impersonating* it. That
poisons anything reading the viewer while we hold it, and still leaves our camera with no
standing of its own. Both halves of the symptom fall out of that single fact.

### What was measured in game (not inferred)

| experiment | what it moved | result |
|---|---|---|
| `feedsDisabled=1`, gate DORMANT | the feed, entirely | **no popping** — we cause it |
| `wholeSceneIntervalMs` 0 -> 500 | render rate 39/s -> 2/s (verified: PERF `ours n=164` -> `n=9`) | **no change** |
| `orbitPeriod` 30 -> 100000 (frozen) | camera POSITION only; VRAM and render rate held | **much less popping** |
| `orbitPeriod` back to 30 (A-B-A) | camera position moving again | **popping returns** |

VRAM was never exhausted at any point (12.43 GB used / 13.89 GB available at dormancy;
966 MB headroom with the feed up). The earlier "it is VRAM eviction" claim was an overreach
on circumstantial evidence and is retired.

The variable that tracks the symptom is **camera POSITION** — not render count, not memory.

Two user observations pinned it further, and both deserve recording because neither came
from an instrument:

- With the feed OFF, a platform grid sat *stably* at low-tier textures, and **pausing the
  game resolved it to full LOD at once.** Pausing frees no memory — it stops demand from
  CHANGING. The streaming manager is failing to CONVERGE under churn.
- With the orbit FROZEN, stepping behind a wall made objects the camera could plainly see
  **unload**. Player state deciding feed content, directly observed.

### Mechanism A — texture tiers: we poison the distance collector

`ManagedTexturePrioritizerComponent.OnCollectStandardsRoot`, read from IL:

```
if (!CoreSystems.Settings.Streaming.EnableCollectingMaterialDistances) return;
Vector3D camPos = CoreSystems.Settings.RenderView.CameraPosition;   // <-- SHARED GLOBAL
... CollectStandards(ref relativePosition, ...)
```

Texture tier is a min-distance reduction (`ClosestDistanceCollector`) over scene ENTITIES
against ONE camera position, taken from the shared `CoreSystems.Settings.RenderView` —
which `WholeSceneRender.InstallCamera` overwrites for the duration of our nested Draw.

**Our own code already contains the assumption this breaks.** `CameraRender.cs`, "MODE 2 —
THE RACE FIX": *"a view whose RESOLUTION diverges from the player's poisons whatever
buffer-size math the unlucky reader feeds. The baseline survives because camera-position
divergence never sizes anything."* Correct as far as it goes — position divergence sizes
nothing. It STEERS the streaming distance collector, which nobody had looked for. Mode 2
neutralised resolution divergence and deliberately left position divergence in place. That
is the gap.

Ruled OUT by the same read, so nobody re-treads them:

- **Texel density is not ours.** `GetPixelsPerSurfaceMeterBase` reads
  `CoreSystems.SwapChain.ResolutionF.Y` — the player's swapchain. Our 1024x1024 target is
  invisible to it.
- **Our draw calls contribute nothing.** Demand comes from an entity iteration, not from
  what we draw. Submit is not a demand proxy (this file already warned it is not a
  visibility proxy either).
- **The voxel clipmap slot is clean.** `RenderSettings.CameraTransform` has sole writer
  `RenderSettings.SetCameraParameters`; our `SetCameraParameters` call goes to our own
  private RenderView copy, and `grep CameraTransform src/` returns nothing. The single-slot
  tug-of-war this project predicted for terrain has NOT been committed.

Note on the negative result: the render-rate test did **not** exonerate a race. Our
per-render cost ROSE at low cadence (26 ms -> 46-130 ms, cold caches), so wall-clock
exposure fell only ~85% -> ~12%, not 20x. If one poisoned collection pass demotes a large
set and promotion is throughput-limited (`StreamingLoadingTaskDeadline`), 12% still
saturates recovery. Rate-independence is CONSISTENT with position poisoning, not evidence
against it.

### Mechanism B — objects unloading: flora sectors ride PLAYER entities

Already recorded in `remote-object-instancing-recon.md`, and the likely cause of the
disappearances: `PlanetEnvironmentComponent` materializes surface sectors through the
spatial-trigger system, on **volumes riding `EnvironmentLocal`-tagged entities — players**.
Sectors spawn flora entities and GPU batches on materialize and REMOVE them on
dematerialize. The feed camera carries no tag, so it gets no vote.

This reframes "I hid behind a wall and things unloaded": hiding required MOVING, which moved
the trigger volume. The competing reading is genuine occlusion culling. They are separable
by a one-minute test — **move the same distance in the open, clear line of sight.** If the
same objects unload, it is position-driven sector dematerialization.

Supporting structure: `GPUModelEntity.LastVisibleFrame` and
`GPUInstancedModelEntity.LastVisibleFrame` are per-entity and scene-wide, written by
`ComposeModelEntity` and by `FloraSectorEntityComponent.UploadEntitiesOnGpu`, with NO
managed readers — they are consumed GPU-side by culling shaders. Meanwhile
`OcclusionContext`, `HiZContext`, `MainVisibilityListBuffers`, `MainViewCulling` and both
`LODTransitionContext`s all hang off `DrawContextManager`, which we own per feed. So our
visibility work is isolated from the player's — which is what protects the player's view,
and is ALSO why our camera's visibility can never count toward keeping anything resident.
The isolation cuts both ways.

### Why this closes the loop with goal 10

The fix for the bug and the feature are the same work, differing only in sign:

- **As a bug**, we want our camera to STOP steering shared viewer state it should not own.
- **As a feature**, we want our camera to legitimately BE a viewer, so the world
  materializes around it.

Impersonating the single slot delivers neither cleanly. Registering as a real second viewer
delivers both. The three-tier recipe from `remote-object-instancing-recon.md`
(`PreloadAreaAsync` warm-up / environment trigger at the feed position / per-feed
`VoxelClipmap`) is therefore not only goal 10's plan — tier 2 is also the principled repair
for Mechanism B.

### Candidate repair for Mechanism A, with its trap stated

Scope `StreamingSettings` around our render, the same pattern already used for shadow
settings. **Do not deploy blind:** setting `EnableCollectingMaterialDistances=false` during
our window means any collection pass landing there collects NOTHING, and `RAW_UNUSED`
exists — an empty pass could mass-demote, i.e. worse than the disease. Establish first
whether collection is per-frame or throttled, and whether a partial pass is reachable. A
safer shape is holding the collector's view at the PLAYER's `CameraPosition`, if the render
can tolerate it.

Other levers on `StreamingSettings`: `SkipMipLevels`, `MinTextureStreamingBytes`,
`TargetUnusedVRAMMult`, `AvailableStreamingSizeOverride`, `EnableCaching`.

### The last no-build control, not yet run

`wholeSceneCamera = 0` renders the feed from the PLAYER's camera: same second render, same
VRAM, same buffers, only the divergence removed. If popping stops, divergence is proven. It
is in the REBUILD SIGNATURE, so it needs the dormant-first protocol — a live edit of a
signature knob is what removed the device at 15:08 today.

### Tooling

`ilscan` — a Mono.Cecil call-graph / IL scanner over the shipped assemblies, built on the
`Mono.Cecil.dll` that ships in `Game2`. Commands: `find`, `members`, `callers`, `reach`,
`writes`, `il`; `--mod=` keeps the graph build to seconds instead of minutes. Every
structural claim above came from it rather than from guessing at symbol names — which is
the direct answer to the two greps earlier today that reported "no sun symbols" when what
was missing was the `strings` command.

---

## 2026-08-01 (evening): THE TERRAIN GAP IS THE CLIPMAP, PROVEN BY POSITIVE CONTROL

Three measurements, run with the player standing on KEMIK while the feed camera looked
at Verdure 4,000 km away — the first time this project has had the player and the camera
on different planets, which is what made the controls clean.

### 1. Data residency: PreloadAsync WORKS, and our camera can drive it

`SpaceProbeSessionComponent.PreloadAsync(BoundingBoxD, Precision)` is wired to the feed
camera (`preloadAroundCamera` / `preloadRadius` / `preloadIntervalMs` / `preloadPrecision`,
all outside the rebuild signature). Attribution was measured, not assumed:

| state | VRAM |
|---|---|
| preload OFF at the Verdure base, 90 s | 13.49 GB -> 13.49 GB (flat) |
| anchor moved to the antipode (never visited), still OFF | 13.49 GB |
| **preload ON**, High precision, 2 km cube, 90 s | **13.76 GB (+270 MB)** |

Flat for 90 s and across the anchor move, then +270 MB within 90 s of enabling it at a
site no player has ever been. **A remote camera can pull world data into residency
anywhere in the solar system, on demand.** That capability is real and banked.

### 2. It produces NO visible change, and the IL says why

40 preload calls at High/1000 m over the Verdure base changed nothing in the feed: same
smooth mottled surface, zero discrete relief. The voxel provider's `PreloadAreaAsync` body
is box maths plus `PerformPinRequest` on streamable voxel RESOURCES — it pins **data**.
Nothing in it generates geometry. Residency and meshing are different systems, and only
the first one is ours to drive.

### 3. THE POSITIVE CONTROL — the same renderer, both ways

Clearing `orbitAnchor` puts the camera back on the panel's own grid, i.e. exactly where the
player is standing. Same feed, same 1024x1024 target, same settings, same everything —
only proximity to the player differs:

| camera AT the player | camera REMOTE |
|---|---|
| individual boulders, crevices, sharp ridgelines | smooth mottled gradient |
| shadowed surface relief, readable rock texture | zero discrete features |
| layered mountains receding into haze | featureless horizon band |

**So the render path is not the limitation.** Culling, contexts, shadows, atmosphere and
delivery all display fully detailed terrain the moment the terrain EXISTS. Remote views are
coarse for exactly one reason: `VoxelRenderUpdateSessionComponent.UpdateClipmaps()` reads
`RenderContracts.GetSettings().CameraTransform` ONCE per frame and drives EVERY voxel
body's clipmap from that single transform. Meshes are built around the player and nowhere
else.

### The build that follows, and it is fully scoped

`VoxelRenderComponent.InstantiateLowResClipmap` shows a second clipmap being constructed on a
clipmap on a body, and every constructor argument is reachable from the existing clipmap
and component:

```
new VoxelClipmap(Session, Clipmap.Size, Clipmap.LocalToWorld,
                 renderComponent,                    // the mesher
                 CreateRenderDataBuilder(target),    // component method
                 CreateVoxelRenderSetup(),           // component method
                 Clipmap.SettingsDefinition, quality)
```

Nothing unreachable. Two things to settle before writing it:

- **THE PRECEDENT IS WEAKER THAN IT FIRST LOOKED, and this correction matters before anyone
  starts building.** `EnableLowResClipmap` is true only when environment probes AND
  raytraced diffuse GI are enabled, so the engine's second clipmap serves **GI** — a
  different PURPOSE at coarser quality, matching `VoxelRenderTarget.GI` — and
  `UpdatePosition` moves it with the same body. It is NOT a second VIEWPOINT. What survives
  is that a second `VoxelClipmap` is CONSTRUCTIBLE with reachable arguments, which is the
  mechanically useful part. What does not survive is any claim that two clipmaps centred in
  DIFFERENT PLACES is a configuration Keen already exercises. Our use is novel.
- **Overlap.** `VoxelRenderTarget` is `Model | GI | Shadow | GBuffer | None` — it selects the
  rendering PURPOSE, not the viewer. A second clipmap's cells land in the shared geometry
  data and frustum culling decides who sees them, so two clipmaps covering the same ground
  at different LODs is a double-geometry risk. The engine's own low-res clipmap sets
  `FreezeClipmap`, which is probably how it avoids exactly this.
- **Budget.** Clipmaps allocate GPU resources, and the feed VRAM cap is already the binding
  constraint (13.7 of 14.2 GB with one feed). Per-feed clipmaps add a term to E1's
  arithmetic, and the cap gets more load-bearing, not less.

### What is ruled OUT, so nobody re-treads it

- **Observers.** `IObservers` is a genuine multi-viewer registry (5 slots, 14 tags, three
  observers coexisting on `Planet` right now) and registration via `ObserverComponent` is
  thread-safe by construction. But terrain's tag `VoxelObserver` is consumed by
  `TryGetFirstTransform` into ONE `_observerEntity` — registering a second one races the
  player for his terrain rather than earning our own. Right mechanism, wrong consumer.
- **Swapping the global `CameraTransform`.** The single-slot tug-of-war the recon predicted,
  and the same class as the LOD popping bug. Deliberately not attempted.
- **`ManagedWorldArea.TryLoad` off-thread.** Three CTDs and a sim freeze. `FinishBefore
  <SpawnSyncPoint>` plants a scheduler dependency only the sim pump can clear.

---

## 2026-08-01 (night): THE TRIGGER CENSUS — every constraint decoded, one archetype rules them all

The camera-marker null result (ClientTriggerTag entity at a virgin site, four minutes,
nothing) forced the question down a level: what do the triggers actually TEST? The answer
came from a new instrument, not from more theory.

### The instrument

`triggerCensus = 1` → `output/trigger-census.txt`: every `EntityTrigger` in every reachable
scene's `SpatialTriggerSystemSessionComponent` — debug name, bounds, live occupancy, and
`TriggerArgs.TypeConstraints` decoded to names through the engine's own reverse registry
(`RuntimeDataInfo.Of(int)`). The SERVER scene is reached through the captured
`ManagedWorldAreaSessionComponent.Session` (scene `#03261e87`, the one whose job tables
carry `SpawnSyncPoint`).

Constraint layout, read from `TypeConstraintBuilder.AsSpan(out mustHaveCount)` rather than
guessed: each `int[]` is `[count, ...count MustHave ids, ...MustNot ids]`. The first
decode treated the count as a component id and read nonsense — worth remembering, because
"must:3+..." looks exactly like a TypeId list.

### What the live constraints say

| trigger (debug name) | scene | MUST have |
|---|---|---|
| `PlanetEnvironmentPrimary/Secondary/Blocking` (flora sectors) | server | DynamicTag + WorldTransform + BoundingBoxData |
| `PlanetEnvironmentPrimary/Secondary` | client | ClientTriggerTag + WorldTransform + BoundingBoxData |
| `Voxel : Block` / `Voxel : Prediction` (voxel data sectors) | client | DynamicTag + WorldTransform + BoundingBoxData |
| `ManagedWorldArea_trigger/_blocking` (POI grids → TryLoad) | server | DynamicTag + WorldTransform + BoundingBoxData |
| `Contract/EncounterPlanetEnvironmentTrigger` | server | InstanceBind\<CharacterComponent\> + WT + BBD |

MustNot on all of them: `ProcedurallyGeneratedTag`, `ManagedByWorldAreaTag` (or
`IgnoredByWorldAreaTag`), `StagingTag`, `ConcurrentInit` — content must not re-trigger
content, and half-initialized entities do not count.

**One archetype — `DynamicTag + WorldTransform + BoundingBoxData` — is the presence input
for everything except encounters (which demand a real character).** Sectors are
ref-counted (`RefCountingSectorsInfo`, per-sector entity counts in `SectoredTrigger`), so
overlapping bubbles are the DESIGNED case: in multiplayer every dynamic physics object is
a materialization source. That answers the overlap-safety question with the engine's own
architecture: no duplicate spawns, whoever arrives first materializes, the count keeps it
alive until the last leaves.

It also reframes the endgame for free: a camera block on an RC ship needs NOTHING from
this machinery — a moving ship is a DynamicTag carrier by construction, and the server
materializes around it natively. What we are building is the same presence for a camera
that has no ship.

### The seat, and the two presence entities

Server-scene structural mutation is only legal on that scene's own pump (the TryLoad
freeze remains the standing proof). The bootstrap's `SimPumpHook` — a Harmony prefix on
the trigger system's per-frame methods — hands the logic a callback IN each scene's pump;
the SpawnSyncPoint probe picks the server one. On that seat:

- `serverPresenceEntity = 1`: DynamicTag+WT+BBD at the camera, in the server scene —
  flora sectors, managed areas, everything the table above lists as server-side.
- `cameraTriggerEntity = 1` (+ `cameraTriggerDynamicTag`): the client marker now carries
  ClientTriggerTag AND DynamicTag — client flora triggers and client voxel-data sectors.

### Still open

- Why the ClientTriggerTag-only marker sat inside ZERO triggers despite satisfying the
  client flora constraint on paper (`_containingTriggers` says the trigger system tracked
  exactly 3 entities). Candidates: the entity-index DCS signals, sector-tracking scope, or
  trigger volumes that simply are not there until something generates the sector layout.
  The census `inside` column with the new markers armed is the direct test.
- Terrain MESH remains the clipmap override's job (client half, proven); voxel DATA may
  now arrive via the marker's DynamicTag driving `Voxel : Block` sectors — if so, manual
  preload becomes redundant. A/B: marker on, preload off, watch VRAM at a virgin site.

---

## 2026-08-02 — THE STAGE WE SKIPPED WAS THE BUG (stage 1 split)

Three fidelity gaps were left after goal 10 and were being chased as three separate bugs:
trees resolving low up close, foliage thinner than local, and no grass at all. They share
one cause, and the cause was **ours**, not the engine's.

`wholeSceneSkipStages` contained id 1 = `ExecuteRaytracingPrepareAndSceneFinalize`. The
name is the whole bug: **two unrelated bodies behind one entry point.**

    RaytracingPrepare(cl)   world-space shared RT state — the reason 1 was skipped
    SceneFinalize(cl)       nothing to do with raytracing whatsoever

`SceneFinalize`, read in full, runs on **our** DrawContexts:

    CascadeStatsJob
    LODStateUpdateJob(DrawContexts.LODTransitions)             <- LOD state
    LODStateUpdateJob(DrawContexts.InstancedLODTransitions)    <- INSTANCED LOD state
    VisibleEntitiesUpdateJob(MainViewCulling.FirstPass, MainOutputGeometryBuffers)
    VisibleInstancedEntitiesUpdateJob(MainViewCulling.FirstPass, ...)
    ...and both again for SecondPass when HZBO.MainViewEnabled

Mapping onto the symptoms:

| skipped job | symptom |
|---|---|
| LOD state never updates | trees stay low-detail however close the camera gets |
| instanced LOD never updates | foliage thinner than the same biome is locally |
| visible-entity set never updates | `RenderGrass` generates from `DrawContexts.MainViewCulling.EntityProxies`, so grass generates for nothing |

**Fix**: `RaytracingPrepare` becomes skippable stage **30**; config swaps `1` for `30`.
Both halves have exactly one caller each, so they separate cleanly. This is strictly
*less* suppression than before — the RT half stays off exactly as it was.

### How it was found, and the lesson

Every link in the grass chain looked correct on paper. What closed it was printing the
live gate **from inside our own pass** (`grassProbe = 1`) instead of reasoning about it:

    Grass.Enabled=True DrawDistance=1000 Density=3 MaxInclination=35 AngleCull=42
    Is3DMapEnabled=False
    GrassBufferContext=GrassBufferContext MainViewCulling=present

Every gate open, still no grass — which leaves only the *set* being generated from, and
the one job that fills it was the one we were skipping. **Reading code says what should
happen; the probe said what did.**

### The control that invalidates the grass claim

Pointing the feed at the player's own grid (`orbitAnchor` empty) put the feed and the
player's own view of the same ground in one screenshot. **The player is standing in bare
sandstone desert — no grass, no flora of any kind.**

So this save has *no local grass to compare against*, and every "grass is missing from
the feed" conclusion here has rested on an untested premise: that the filmed site has
blade grass at all. The remote alpine anchor has moss, lichen, boulders and trees.
**The grass gap is UNPROVEN, not confirmed.** Do not re-litigate it without a site
verified grassy on foot.

The same invalidity infects the density instrument: `FLORA CAMERA`'s "DENSITY
like-for-like" line compares the nearest sector to the FEED (31 instances, alpine) with
the nearest to the PLAYER (16, desert). Different biomes. It has been printing all day
and reads as like-for-like when it is nothing of the sort — the same class of error as
the blind grass reader, only quieter, because a plausible number attracts less suspicion
than a zero.

### Measured while hunting (keep — these were all guessed at before)

- `RootResourceStreamingComponent.RootStreamingDistance` = **200 m**
- `ImpostorSettings.SwapDistance` = 500 m, with `EnableImpostorSwitching` **false** by default
- `GrassSettings.DrawDistance` = **1000 m** — distance was never what culled our grass
- `GrassSettings.MaxInclination` = 35, `AngleCullingThreshold` = 42
- `VoxelCell.MAX_LOD_WITH_GRASS` gates which clipmap LODs get a grass entity at all
- Grass does **not** arrive via resource streaming: `VoxelCell.CreateModelEntity(model,
  materials, hasGrassMaterial, ...)` builds it alongside the cell's model

### The nearest-viewer distance (commit 0c5c53a) — real, firing, not the headline

`RenderUtilities.CalculateDistanceToCamera` reads the single global
`Settings.RenderView.CameraPosition`; `DistanceTagManagerComponent` caches that one float
per entity and the whole tag family (`StreamingTag`, impostor near/far, shadow tracking,
RT near/far, geometry-dirty) reads nothing else. A postfix returns
`min(engineAnswer, ourAnswer)` — monotone downward, so the player can never be demoted,
and idempotent, so overlapping viewer bubbles are not a conflict.

Firing and measured: 673 overrides/window at r=200, ~11,500 at r=1000 (so render-scene
roots are dense enough for the bubble to bite), fps flat, VRAM +121 MB. But it is **not**
what was holding the three symptoms back, and no visual difference could honestly be
called from its A/B. It stays because tag-family parity is still the right thing.

**Not covered by it**: `ManagedTexturePrioritizerComponent.OnCollectStandardsRoot` reads
`Settings.RenderView.CameraPosition` *directly* for texture mip priority — a second,
independent single-camera decision. Open.

### CORRECTION (same day): the remote site DOES have grass — the negative stands

The control above proved there is no *local* grass in this save to compare against. That
is true and worth keeping, but I used it to walk the grass claim back further than the
evidence supports. Correcting:

`VoxelCell.CreateModelEntity(model, materials, **hasGrassMaterial**, immediateUpdate)`
branches on that flag — `IL_00b6 ldarg.3 / brfalse.s IL_0114` jumps the whole grass block.
So `_grassEntity` exists **only** when the cell's materials include a grass material.

The census reading **43–85 valid `_grassEntity` per LOD 0–4 around the remote camera** is
therefore positive proof those cells carry grass material. The site has grass geometry and
the feed draws no blades. **The remote grass negative is real.**

What the desert control actually establishes is narrower: there is no local A/B available,
so the *comparison* has to be made against the remote site's own geometry counts rather
than against the player's view.

### And a third blind reader, caught before it was believed

A per-cell distance readout added to the grass sweep reported the nearest clipmap cell
**867 m** from a camera sitting 15 m above terrain rendering in full detail, with a dozen
cells at an *identical* distance. Both impossible: `VoxelCell._worldTransform` is a shared
root/ring transform, not per-cell.

The sweep now checks both tells (most cells sharing one distance; nearest cell implausibly
far) and **withholds the verdict** rather than printing it with a caveat. That is the
point: a zero announces itself, but 867 m is plausible enough to be believed, and a
plausible wrong number costs more than an obvious one. Three blind readers in two days —
the `CellData` wrapper, the desert-vs-alpine "like-for-like" density line, and this — and
**all three produced output that looked reasonable.**

### 2026-08-02, later: the marker's bounding box does NOT set the materialization radius

User observation from a raised orbit camera: scatter objects stop at a hard circle around
the anchor, bare ground beyond. That looked like the presence marker's extents, because the
PlanetEnvironment triggers are `SECTORED` and `SectoredTrigger.UpdateEntity` takes the
entity's OBB.

**Tested and refuted.** `cameraTriggerExtent` 1.375 → 25 → 100 m, live, orbit frozen,
markers verifiably rebuilt at each step (the creation log now prints the real half-extent —
it previously hardcoded "2.75 m box" and so could not have shown this either way). User:
"foliage is still limited to the previous range, I don't see any additional foliage spawning
further away." VRAM stayed flat at 12.4 GB throughout, so it is not a residency limit either.

The knob stays (it is correct that the extent be settable, and the default is back to 1.375),
but it is **not** the radius lever.

Candidates left, in order:
1. **Sector size vs box size.** `SectorArgs` carries the sector size; if a sector is much
   larger than our box, every extent below one sector activates exactly one sector and the
   knob can do nothing. Primary reports `occupiedSectors=68800` over a 61 km-radius planet,
   which is order-800 m per sector — bigger than every extent tested. Read `SectorArgs` live
   before testing an extent above it.
2. **`SectoredTrigger.TrackSectorPerEntity`.** If false, an entity is assigned to the single
   sector containing it regardless of its box.
3. **A per-layer view distance** in the environment component, which is the shape that
   produces a hard circle most naturally.

Also still unexplained and parked at the user's request: **shadows of trees and foliage with
no object present.** That proves the object exists and is in the shadow pass's set while the
main view drops it, which points at main-view culling — and the HZBO second pass
(`SceneFinalize` runs the visible-entity update again for `MainViewCulling.SecondPass` gated
on `HZBO.MainViewEnabled`) is the obvious suspect, since `RenderGrass` takes the same
`enableHiZ` flag and has explicit NoHiZ PSO variants. One mechanism would explain both the
missing foliage and the missing grass.
