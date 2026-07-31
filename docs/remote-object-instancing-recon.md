# Remote-camera object instancing — engine recon (2026-07-31)

The user's question, verbatim intent: *if a feed is on the other side of the solar system,
does the game world even exist around it — and is there a mechanic to force the world to
exist around our cameras when they are outside the player's local range?* Answered by
reading the engine (EngineQuery over the shipped assemblies), not by in-game testing.
Every claim below carries the type/method it was read from.

---

## Q1 — what exists around a remote camera TODAY

The question splits into four systems with four different answers.

### 1. Entities (grids, stations, blocks) — EXIST EVERYWHERE in singleplayer. Better than feared.

Singleplayer runs an in-process server, and its replication is **type-based, not
distance-based**:

- `ServerSceneContext.ShouldReplicate(Entity)` (VRage.Multiplayer, IL read): replay-pairing
  check, then `IsReplicatable`.
- `IsReplicatable`: `CanCompositionBeReplicated` + recurse up the parent chain. **No
  position, no distance, no observer set — none of that state exists on the context.**
- Entities are queued to the client from `OnEntityAddedToScene` — i.e. existence on the
  server IS existence on the client.

And nothing takes distant entities away at runtime:

- `IManagedWorldAreaOffloading.OffloadAsync` has exactly two callers:
  `ManagedWorldArea.PerformSave` and `RunMigrations` — **save-time serialization of
  designated mission areas, not a runtime distance unloader.**
- The `TrashRemoval` family (Game2.Simulation) covers backpacks, corpses, floating
  objects, grid bones — junk cleanup, not player structures.

**Consequence:** a station on the far side of the system is in client memory right now. Our
own render already proved the corollary from the other side: the whole-scene cull ranges
carry the ENTIRE loaded world (`single=325508 ... entityProxies=16296` in tonight's log) —
our feed camera culls against everything that exists, and everything (built) exists.

### 2. Voxel terrain MESHES (planet surface, asteroid shape) — stream around ONE camera, and it is not ours.

The chain, each link read in IL:

```
RenderSettings.CameraTransform                  <- sole writer: RenderSettings.SetCameraParameters
        |                                          (the engine sets it from the PLAYER view;
        v                                           our SetCameraParameters call is on our own
VoxelRenderUpdateSessionComponent.UpdateClipmaps    private RenderView copy, not this global)
        |   iterates _renderComponents — one VoxelRenderComponent PER VOXEL BODY
        v
TryUpdate(clipmap, moveUpdate, in camera, loadingPhase)
        v
VoxelClipmap.Update(WorldTransform view, ...)   <- ILodController: LOD follows the transform
                                                   IT IS GIVEN. Nothing about it is
                                                   intrinsically player-bound.
```

So: every voxel body's clipmap exists as long as the ENTITY exists (which is: always), but
its mesh LOD is built around the player's camera. A body 2,000 km from the player has its
clipmap at the coarsest LOD — or no resident cells at all near the feed's viewpoint.

Voxel DATA (the octree pages under the meshes) loads on demand from the clipmap's cell
requests (`ClipmapResourceLoadingJob`), so **data streaming follows whoever drives a
clipmap** — there is no separate player-bound gate on the data side visible in the types.

### 3. Procedural content (encounters, NPC traffic) — player-bound by design.

`EncounterUpdate/DespawnEncounters` (Game2.Simulation.GameSystems.Encounters): procedural
encounters spawn and DESPAWN on their own policy around players. A remote camera will see
built structures but not procedural traffic. This is game-design policy, not an engine
limitation — separate fight, if ever.

### 4. Multiplayer — UNVERIFIED, expect worse.

Everything above reads the IN-PROCESS (singleplayer) path. Real MP has a different context
family and almost certainly real interest management; the mod is SP-first, so this is
recorded as an open edge, not investigated.

---

## The corrected mental model

The user's fear was "objects only exist near the player". The truth in SP is sharper:

- **Built things exist everywhere, always.** The feed can see them at any range today
  (subject to render LOD).
- **Terrain SHAPE is the thing that follows the player.** A remote feed looking at a
  planet gets the planet entity, its atmosphere (global data — already proven at 50 km),
  but its surface mesh at the coarsest LOD the clipmap kept for that body.
- The 15 km far-plane ceiling (`wholeSceneFarClipExtend` lifts it) and this clipmap LOD
  are therefore the two REAL limits on remote fidelity — not entity existence.

## Q2 — the mechanism to force the world around a feed

### The clean route: per-feed clipmaps, using the engine's own multi-clipmap machinery

The engine already instantiates MULTIPLE clipmaps per voxel body:

- `VoxelRenderComponent.OnAddedToScene` constructs the main one;
  `VoxelRenderComponent.InstantiateLowResClipmap` constructs a SECOND, lower-quality one —
  the precedent that one body can carry several clipmaps at different qualities.
- The constructor is fully parameterised:
  `VoxelClipmap(Session, Vector3I, WorldTransform, VoxelRenderComponent,
  RenderDataBuilderBase, VoxelRenderSetup, VoxelClipmapSettingsDefinition,
  ClipmapQualityEnum?)` — **it takes the transform and a QUALITY tier at construction.**
- `ILodController` exposes `Update(in WorldTransform)`, `Unload()`, `InvalidateAll()` —
  a complete owned lifecycle. Rule 25 is satisfiable: we construct, we update with OUR
  camera, we Unload on feed teardown.

Shape of the feature (unbuilt):

```
per feed, for voxel bodies within the feed camera's far plane:
    ourClipmap[body] ??= new VoxelClipmap(..., feedCameraTransform, body's renderComponent,
                                          ..., LOW quality)
    every Nth feed frame: ourClipmap[body].Update(feedCameraTransform)
    feed teardown: Unload() each — the same graceful-cut contract as everything else
```

Why NOT the tempting shortcuts:

- **Swapping the global `RenderSettings.CameraTransform`** (scoped, like our other swaps):
  the clipmap update runs on the sim side at its own cadence — a swap scoped to our render
  would rarely intersect it, and an unscoped swap starves the PLAYER'S terrain. Single
  writer, single slot: not shareable.
- **Calling TryUpdate on the ENGINE'S clipmap with our camera**: one `LastCameraPosition`
  per clipmap — the engine's next update round snaps it back to the player. A tug-of-war
  that rebuilds LODs both directions every round, worst possible cost. Per-feed INSTANCES
  avoid the fight entirely.

### Supporting precedent

`WaterDomainComponent.AddAreaOfInterestClientSignal` (VRage.Water) — the engine already
models "client registers EXTRA areas of interest" for at least one subsystem, with
add/remove/clear signals. The concept we need is native to the codebase.

### Cost model (why the VRAM cap gains a term)

A per-feed clipmap = meshing jobs (the worker pool — note the GC-churn finding, task #18,
which this would add to) + resident cells in RAM/VRAM, scaled by the chosen
`ClipmapQualityEnum`. LOW quality per feed + a cell budget is the obvious starting shape,
and `maxResidentFeeds`' arithmetic must eventually include it.

---

## What the earlier remoteness test can now predict (and what to verify in game)

- **Prediction 1:** a feed aimed at a DISTANT BUILT GRID shows it today (entity exists;
  render LOD applies). The 50 km sweep never tested this because the orbit only looks at
  its own grid — task #17's look-at target is still the missing instrument.
- **Prediction 2:** a feed aimed at distant TERRAIN shows the coarse-LOD planet/asteroid
  shape, refining only if per-feed clipmaps get built.
- **Prediction 3:** no procedural encounters near remote feeds, ever, until the encounter
  system is separately convinced.

Verify 1 and 2 with #17; build the per-feed clipmap prototype only after they confirm.

---

## ADDENDUM 2026-07-31: trees, rocks, ground clutter — and the engine's own remote-preload API

The user's follow-up: what about vegetation and clutter? Answer: a THIRD architecture,
and chasing it surfaced the best find of the whole recon.

### How clutter actually works (SE2)

**Trees, boulders, surface ore (the interactable clutter)** are neither entities-at-rest
nor voxels. They live in **planet environment SECTORS**:

- `PlanetEnvironmentComponent` (VRage.Voxels, one per planet) owns an
  `EnvironmentClipmap2D` of surface sectors, a `PlanetEnvironmentSectorStorage`, and an
  `IEntitySpawner`.
- Sectors MATERIALIZE through the **spatial-trigger system**: the module declares trigger
  layers (`GetTriggerLayers`), and trigger volumes riding entities tagged
  `"EnvironmentLocal"` (players/characters) cause `OnMaterializeSector` — which SPAWNS
  real flora entities into the hierarchy (`AddToHierarchy`), generates ores
  (`GenerateOre`), and registers GPU render batches
  (`FloraSystemComponent.AddFloraSector` → `SceneManager.CreateFloraSectorEntity`).
- Sectors DEmaterialize when the triggers leave (`OnDematerializeSector`) — clutter around
  you is a materialized bubble, not persistent world state.

**Consequence for a remote feed today:** no trees, no rocks, no surface ore in shot —
those sectors were never materialized. This is the strongest player-binding of the four
systems, stronger than voxel LOD (the planet SHAPE at least exists coarsely; the trees do
not exist at all).

**Grass** is different again: a render-side GPU system (`GrassEntity` contracts,
`GrassSettings`, and `GrassBufferContext` hanging off the DrawContextManager — **which
each of our feeds already owns**). Within materialized sectors, grass generation is
per-render-context, so it plausibly already follows our feed camera wherever sector data
exists. Unverified visually; cheap to check once a feed is inside a materialized area.

### THE FIND: the engine ships a remote-preload API, and it is exactly shaped for us

```
Keen.VRage.Core.Game.GameSystems.SpaceProbe.ISpaceProbePreloadable
    Task PreloadAreaAsync(OrientedBoundingBoxD box, Precision precision, IPreloadCollector collector)
    Task PreloadAreaAsync(LineD line,              Precision precision, IPreloadCollector collector)
    Task PreloadAreaAsync(BoundingBoxD aabb,       Precision precision, IPreloadCollector collector)
    Task PreloadAreaAsync(BoundingBoxD aabb, Vector3D vector, Precision, IPreloadCollector)
```

- Implemented by **`VoxelStorageComponentBase`** (voxel DATA at the remote point) and
  **`PlanetEnvironmentComponent`** (environment sectors — `PreloadedSector` is an inner
  class, `PendingSectorsTag` its bookkeeping) — very likely more implementors.
- Surrounding machinery: `DirectionalDynamicSpaceProbe` (preload ALONG A LINE — built for
  something travelling), `IPreloadCollector`/`IPreloaded`, `SpaceProbeAdmin` (admin
  tools), `SpaceProbeDebugScreen`. This is a maintained, first-class system: Keen's own
  "make the world exist at a remote location" mechanism.

### The mechanism menu for feeds, now three tiers

| tier | mechanism | gives | lifecycle |
|---|---|---|---|
| 1 | `PreloadAreaAsync(box around feed)` | voxel data + environment sectors warmed at the feed | one-shot per call — re-issue as the camera moves |
| 2 | an `"EnvironmentLocal"`-style TRIGGER attached at the feed position | CONTINUOUS sector materialize/dematerialize, the same bubble players get — trees/rocks/ore spawn for real | engine-managed while the trigger exists; needs ISpatialTriggerSystem registration recon |
| 3 | per-feed `VoxelClipmap` (main recon above) | near-LOD terrain MESH around the feed | ours, Unload() on teardown |

The full remote-feed recipe stacks them: entities already exist everywhere (SP) →
tier 1/2 materializes the clutter bubble → tier 3 sharpens the terrain → grass likely
follows our own per-feed GrassBufferContext inside materialized sectors.

### Open edges, honestly

- Who calls PreloadAreaAsync today and with what Precision values — read before using
  (`callers PreloadAreaAsync`, unread; the Precision enum semantics matter for cost).
- ISpatialTriggerSystem registration surface for tier 2 — unread.
- Whether sector MATERIALIZATION is server-side only (its spawner adds entities — in SP
  in-process that is fine; MP another story).
- Cost: a materialized sector bubble is real entities + physics(?) + render batches —
  the budget/cap arithmetic gains another term, same as the clipmap tier.
