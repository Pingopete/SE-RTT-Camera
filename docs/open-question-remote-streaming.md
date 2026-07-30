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
