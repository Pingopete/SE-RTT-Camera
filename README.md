# RTT Camera

Feasibility study for a render-to-texture / picture-in-picture camera feed onto
LCD panels in **Space Engineers 2**.

> **Working end to end.** A true second 3D scene view — rendered from mod code by
> a camera orbiting 100 m from a tagged LCD panel — displayed on that panel,
> in game, stably. See [`docs/attack-plan.md`](docs/attack-plan.md) for the
> seven-stage pipeline and every constraint found along the way.
>
> The project opened with the conclusion that this was architecturally
> impossible. That was wrong: the engine already renders scene geometry from
> foreign viewpoints into non-screen targets several times a frame for
> environment probes, and following that template is what made it work.

**Verdict depends on the access you have.**

*Through the public API:* **no.** Offscreen render targets are a 2D-only surface,
and nothing in `RenderContracts` or `RenderSystem` takes a camera and a target.

*With plugin-level access* (Harmony on internals, reflection onto private state):
**yes — and more cheaply than expected.**

`SceneDrawSystem.ExecuteEnvironmentProbeUpdate` already renders scene geometry
from an arbitrary viewpoint into a non-screen target, six times a frame, and it
does so **without mutating a single global**. The camera is passed as a transient
constant buffer, depth is borrowed from a pool, output is an ordinary render
target view, and culling runs against a dedicated context. There is no view state
to save or restore, because the engine's own second-view path never touches the
first view's.

A camera feed is that same sequence with a 2D target, a camera-derived
`RenderViewSlim`, and its own culling context.

- [`docs/attack-plan.md`](docs/attack-plan.md) — the engine's recipe, reconstructed
  from IL, and a staged order of attack
- [`docs/feasibility.md`](docs/feasibility.md) — the full evidence chain, including
  what *isn't* possible through the public API

## Layout

| Path | What it is |
|---|---|
| `src/RttProbe` | Bootstrap plugin. Loaded once at game start; applies Harmony patches and hosts the logic assembly in a collectible load context. |
| `src/RttProbe.Logic` | The plumbing spike. Hot-reloads into a running game in ~2 s. |
| `tools/RenderRecon` | Offline reflection/IL analysis of the engine's render surface. Produces `docs/rtt-recon.md`. |
| `docs/feasibility.md` | The verdict and the reasoning behind it. |
| `docs/rtt-recon.md` | Generated raw evidence — API dumps and IL. |
| `output/` | Runtime log and the arming marker (git-ignored). |

## Running alongside Grid Schematics

`PluginHost.LoadPlugins` splits its argument on `;`, so SE2 loads any number of
bootstraps at once. The two mods coexist: different Harmony IDs (so their
postfixes on the shared LCD methods stack rather than collide), different panel
tags (`[GS]` / `[RTT]`), different log files, and byte-identical `0Harmony.dll`
in both deploy directories.

**Steam launch options win over anything a script passes**, so set them there:

```
-plugins:D:\SE2Rtt\RttProbe.dll;D:\SE2Probe\GridProbe.dll
```

Steam → Library → Space Engineers 2 → Properties → General → Launch Options.
Point at the **deploy** copies as above, not `bin\Release` — each has
`0Harmony.dll` beside it, and rebuilding never fights a file the running game
holds open.

With launch options set, start from Steam. `scripts\launch-se2.bat` does the same
thing for an empty-launch-options setup, and loads both by default
(`set RTT_ONLY=1` for a single-mod run).

### Checking what actually loaded

Launching with the bat is not evidence a plugin loaded — Steam can relaunch the
exe with its own arguments. This reads the real command line off the running
process and tails both mods' logs:

```
powershell -ExecutionPolicy Bypass -File scripts\check-plugins.ps1
```

### Iterating

Only bootstrap changes need a game restart. Both mods hot-reload their logic
assemblies within ~2 s of a rebuild, so day-to-day you rebuild and keep playing.

## Step 1 — scene-draw reconnaissance (built, read-only)

Proves in a live game that the pieces the probe pass uses are reachable from a
plugin. It resolves types and reads fields; it never draws, allocates GPU
resources, or mutates engine state, so it cannot destabilise the renderer.

Harmony postfixes on `SceneDrawSystem.ExecuteEnvironmentProbeUpdate` and
`.DrawUnlit` capture the live instance. On the first call it writes
`output\scene-draw-recon.txt` with:

- whether `_indirectCullingJob`, `_clusterJob`, `_indirectEnvironmentPass` and
  `_indirectPlanetEnvironmentJob` are present and non-null on the live instance
- the exact `DoWork` / `DoCullingFirstPass` signatures those jobs expose — the
  parameter lists step 3 has to satisfy
- every other `SceneDrawSystem` field, so nothing is missed
- the `CoreSystems` statics the pass borrows from (`DrawContexts`,
  `BindableTexturePool`, `BindableBuffers`), and the live `DrawContextManager`
  contexts including `EnvProbeCulling`'s array length
- constructors for `CullingContext` / `ClusteringContext` (needed in step 2)
- field layouts for `TrackedCameraSettings`, `CameraSettings`, `ScreenSettings`
  and `RenderViewSlim` — how to describe our camera to the GPU

It also logs the **cadence** of both passes to `output\rtt.log` every 10 s. How
often the probe pass runs is a design input for step 3, not trivia.

```
scripts\build.bat
```

Then launch with `scripts\launch-se2.bat` and load any world. No panel tag is
needed — the recon runs on its own. Send me `output\scene-draw-recon.txt` and the
cadence lines, and that settles what step 2 has to build.

## Steps 2 & 3 — the second scene render

`src/RttProbe.Logic/CameraRender.cs` runs the engine's own cull → cluster → draw
sequence a second time, into our own render target, modelled directly on
`ExecuteEnvironmentProbeUpdate`. It rides that method's postfix, so it gets a live
`DirectCommandList` at the right point in the frame with shadows already resolved,
and throttles itself to one render every 500 ms (the hook fires ~430×/second).

Two phases, because a wrong guess here means bad GPU work on the render thread:

**Phase A — dry run. Always runs, no GPU work.** Resolves every argument (jobs,
contexts, LOD settings, pool methods, formats, camera types, and the render-target
/ depth-stencil view accessors) and writes `output\camera-dryrun.txt` saying
whether the full call could be assembled. If anything reads `NOT FOUND` or `NULL`,
phase B never runs.

**Phase B — armed only.** Issues the real passes. Gated on a marker file:

```
type nul > "D:\Projects\Space Engineers Stuff\RTT Camera\output\camera-armed.marker"
```

Then rebuild the logic assembly (`scripts\build.bat`) to hot-reload — arming is
read at install. Nothing else needs restarting.

The first render deliberately uses the engine's **current main view** rather than a
camera block. If our target shows the world, the pipeline works; pointing it
somewhere else is a separate change that shouldn't be debugged at the same time as
the plumbing.

### Reading the result

The crash, if any, lands during command replay well after the call returns, so
survival is the real signal:

- `CAMERA PASS SUBMITTED` — the sequence was issued without throwing
- `CAMERA PASS SURVIVED 20 submissions` — **it works**
- Game died, `output\camera-live.marker` still present — the replay rejected it.
  On next load the probe sees the marker, refuses to re-arm, and says so. Delete
  it to try again.

## The plumbing spike

**This does not advance the 3D question** — it settles the *last* link in the
chain, not the missing one. Can a mod create its own offscreen render target,
draw into it, and blit it onto an LCD panel? Grid Schematics left that open.

It is worth having only because that link is common to every RTT design: if a
per-view scene render ever appears, this is how its output reaches the glass.
The blocker remains getting 3D *into* a target, which no amount of blitting
solves. Four stages, each logged before it is attempted so a hard crash names its
own cause.

| Stage | What it does | Risk |
|---|---|---|
| 1 | Resolve `RenderContracts` / `UISystem` off the LCD render component | none |
| 2 | `CreateOffscreenTarget("RttProbe", 512×512)` | none |
| 3 | `CreateImmediateBatchFor` → draw an animated test pattern → `Submit` | none |
| 4 | `batch.DrawImage(rt.TextureHandle, …)` onto the panel | **can crash the game** |

Stage 4 is the real unknown. A render target's texture handle is *generated*
(backed by a `RenderId`), not the file-backed guid handle the UI recorder
normally sees — and `UISystemComponent.GetTexture` asserts `IsGuid()`. If the
recorder rejects it, the throw happens inside the render thread's command replay
where nothing can catch it.

### Running it

```
scripts\build.bat
```

Then start the game with `scripts\launch-se2.bat`, load a world, and place an LCD
panel. Arming is driven from the panel text so nothing risky happens by merely
loading the mod:

- **`[RTT]`** — stages 1–3 only. Safe. Watch `output\rtt.log` confirm the target
  was created and painted.
- **`[RTT!]`** — also arms stage 4, blitting our target onto the panel.

Stage 4 reports itself two ways, because the failure is *deferred*: our postfix
returns cleanly and the crash happens later during replay. So the probe writes
`output\blit-armed.marker` before recording the blit and deletes it after 120
surviving frames.

- Log says `STAGE 4 CONFIRMED` and the marker is gone → **RT-to-RT blit works.**
- Game died and the marker is still there → the replay rejected the handle. On
  the next load the probe sees the marker, refuses to re-arm, and says so.
  Delete the marker to try again.

Rebuilding `RttProbe.Logic` while the game runs hot-reloads it — no restart.
Changing the bootstrap needs a restart.

## Re-running the recon

Needs the .NET 9 SDK and a local SE2 install. The game path lives in
`Directory.Build.props` (`SE2Dir`).

```bash
dotnet run -c Release --project "tools/RenderRecon"
```

Override the game directory without editing anything:

```bash
dotnet run -c Release --project "tools/RenderRecon" -- --game "C:\path\to\SpaceEngineers2\Game2"
```

Worth re-running after any SE2 patch, and especially once a public mod API
appears — a camera-to-target render entry point is exactly what that might add.

## Related

The sibling **Grid Schematics 2** project renders live vector geometry into LCD
panel render targets at 60 fps. Its `docs/architecture.md` documents the LCD
render hook, the texture streamer's behaviour, and the measured cost of the 2D
canvas — all of which this study builds on.
