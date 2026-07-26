# Scope and goals

Written 2026-07-26, after a week of exploratory work, to replace an accumulated pile of
per-session notes with one statement of what this POC is trying to prove.

## The claim being proved

**Space Engineers 2 can render the 3D world a second time, from an arbitrary camera, and
display it on an in-world LCD panel.** Not a synthesised vector overlay, not a grid
schematic, not a static image — a real second render of the game world.

That claim is **already proved**. What remains is making it good enough to be worth
shipping.

## Definition of done

| # | Requirement | Status |
|---|---|---|
| 1 | A true second 3D render of the game world, not a synthesised approximation | **done** |
| 2 | Displayed on a real in-world LCD panel, sampled by the panel's own material | **done** |
| 3 | Stable — hours, not minutes; no CTD, no progressive slowdown | **done** |
| 4 | **30 fps sustained** | **done at 26–29 fps** with the current pass set |
| 5 | 512×512 is sufficient resolution | accepted; not a goal to exceed |
| 6 | **Visually resembles the full-screen world render** | **partly** — see the baseline below |
| 7 | Does not degrade the player's own view | **conditionally** — see below |

## The known-good baseline (2026-07-26)

Five fixes went in one at a time and all five held. They are now **code defaults**, not
config-file entries, because `output\feed-config.txt` is gitignored and a clean checkout
has to reproduce this build. Setting any of them in the config still wins, so each stays
a one-line bisect.

| Fix | What it corrected |
|---|---|
| `lodMainView` | we passed `Settings.LOD.EnvironmentProbe` to our own culling call — `MinLOD 8`, the coarsest mesh for everything. `MainView` is `MinLOD 0`. |
| `fixScreenRes` | the camera CB carried the player's 3840×2160 in `Screen.Resolution` while rasterising 512×512 — a 7.5× error on every `rcp(Screen_.Resolution)` view-ray reconstruction, in the sky *and* the geometry pass. |
| `fullCameraCb` | the `RenderViewSlim` conversion left `ViewTransform`/`InvViewTransform`/`TanFOV`/`FOVScaleFactor` at zero and stamped the *player's* position into `MainViewCameraPos`. Also fixed an unnoticed aspect-ratio squash: the slim path copied the player's 16:9 projection into a square target. |
| `recursiveReflections` | ships `false`, binding a flat default cubemap instead of the real `CloseIBL`/`FarIBL` — the ambient term was a constant. |
| `emissivity = 50` | display side. The panel's emissive term reaches the main view's HDR buffer ahead of its own bloom, so it is the only axis on which the feed can exceed white. |

Plus one regression fix that had to land first: **`rangeCulling`**. A `CullingContext`
borrowed from `BorrowShadowCulling` is ranged for `RangeStats.Default` — one draw per
category — and the engine only grows the contexts its own pending-work queues name.
Nothing names ours, so most of the scene's draw commands were silently dropped. That was
the missing asteroids and the flickering structure, and it appeared the moment we stopped
sharing `EnvProbeCulling[0]` (which the engine ranges every frame).

Still open on requirement 6: no screen-space AO, atmosphere is in-scatter only (the
multiply half is missing), no real anti-aliasing, shadows still come from the player's
cascades rather than our frustum.

Raw pixel resolution is explicitly **not** a goal. Ray tracing is explicitly **out of
scope**. Requirement 6 is the whole remaining project.

## What requirement 6 actually means

The feed today is `IndirectEnvironmentPassJob` — the environment-probe pipeline. Named
gaps, in the user's words and then in engine terms:

| Complaint | In engine terms |
|---|---|
| "very limited clamped tonal range" | no ambient/indirect term, and the panel treats the feed as **albedo** so it cannot exceed white |
| "missing planetary atmospheres" | atmospheric scattering is set up per-frame for the main view; our pass has no atmosphere contribution |
| "no ambient occlusion" | AO needs GBuffer normals; the probe path writes colour + depth only |
| "some textures" missing | unconfirmed — hypotheses are streaming residency keyed to the main camera, material tables, or impostors substituting for meshes |

The single structural cause behind most of it: **the probe path is the flattest shading
path in the engine, by design.** Environment probes are the *input* to ambient lighting,
so they deliberately render without indirect light, to avoid feeding themselves. We
adopted the cheapest path in the engine because it was the one that proved the plumbing.

## Constraints on any solution

1. **The player's view is sacred.** Corrupting it — geometry stitching, flickering
   lights, black lines — is a hard fail even if the panel looks perfect. Several
   otherwise-promising passes have been rejected on this alone. Verification means
   watching the *player's* screen, not the panel.
2. **One change per test.** Batching changes has repeatedly produced ambiguous results
   and cost extra sessions. Every candidate needs a single flip with an unambiguous
   pass/fail signal, driven from `output/feed-config.txt` while the game runs.
3. **A CTD is an acceptable cost of an experiment.** It is information, not a setback.
4. **Grid Schematics 2 is read-only reference.** Separate active project.
5. Everything runs by reflection and Harmony against internal `VRage.Render12` types,
   in a hot-reloadable `AssemblyLoadContext`. No engine source; the shipped assemblies
   are the only specification.

## The rule that decides what is addable

Established the hard way, and it has held up across every test since:

> **Does the pass read or write anything it was not handed as a parameter?**
> If yes, that state must be ours before the pass can run. If it only touches what it
> is given, it is safe to call from a second view.

Job classes that take explicit contexts pass. `SceneDrawSystem` methods shaped
`(commandList, buffer)` fail — the buffer is the only parameter, and the histograms,
eye-adaptation state, `ScreenBuffers` and per-frame scratch they also touch belong to
the frame the engine is already rendering.

Corollaries worth stating, because each cost a session:

- **Invoking cleanly is not the same as doing something.** `ExecuteLighting`,
  `DrawSkybox` and `AtmosphereAdditiveJob` all run without error and contribute nothing.
- **An intermittent, rate-dependent GPU death is a validity bug until proven
  otherwise**, not a race. A 10-mip destination copied from a 1-mip source produced
  months of apparent "contention curves".
- **Mid-frame global swaps are survivable.** Swapping `ScreenBuffers.GBuffer` and
  `_depthStencilBuffer` around our pass ran ~630 times at 29 fps with the player's view
  clean. This is the door that "own the state the pass reads" goes through.

## Where the effort goes next

Two independent axes, and they are worth separating because progress on either is
visible on its own:

- **Render** — get more of the engine's shading into the second view.
- **Display** — get what we already render onto the panel with real dynamic range.
  The panel binds our texture as `PBRMaterialDefinition.ColorMetalTexture`, i.e. as
  albedo, so `displayed = our_pixel × light_falling_on_the_LCD`. Nothing can exceed
  white and the panel sits in a dim interior. Sweeping our exposure 12× was visibly
  inert, which is consistent with the diagnosis.

A ranked set of candidate routes for both axes follows in
[architecture-plan.md](architecture-plan.md).
