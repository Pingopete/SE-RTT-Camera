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
