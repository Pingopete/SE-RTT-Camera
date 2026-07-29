# Reference build — "every-frame baseline", 2026-07-29

**Why this is pinned.** Best performance this project has produced, and the first build to
show **no sign of the progressive GPU-starvation / frame-time drift** that dominated the
earlier session (~15 minutes of continuous play, clean). If a later change regresses
performance or reintroduces the drift, diff against this.

- Branch: `reference/every-frame-baseline`
- Tag: `ref-every-frame-2026-07-29`
- Parent branch at time of pinning: `start-of-frame-submit`

## Binaries as deployed to `D:\SE2Rtt`

| File | Size | Modified | SHA-256 |
|---|---|---|---|
| `RttProbe.dll` (bootstrap) | 23 552 | 2026-07-29 17:29 | `35d65803f37f84fa52d1bb09b3d027f1c8aa3a518b44f27dd064d694bff0a668` |
| `RttProbe.Logic.dll` (hot-reload) | 163 840 | 2026-07-29 18:42 | `e00fd67b365f031328c95b755e263861515aaed9e6239d04fbd6241a932f65de` |
| `0Harmony.dll` | 2 330 624 | 2025-11-12 | `a849b726e1f9248d71aabbed8114deaf79beb7acc25e8344ff92a27ad8ac87ab` |

The bootstrap requires a game restart to adopt; the logic assembly is hot-reloadable.

## Config

`docs/feed-config.known-good.txt`, byte-identical to the live `output/feed-config.txt` at
the time of pinning (`output/` is gitignored, hence the copy). The load-bearing values:

```
wholeSceneIntervalMs        = 0        # every engine frame — THE change that mattered
wholeSceneWidth/Height      = 512
wholeSceneSkipStages        = 0,1,2,5,7,9,16,19,20,21,24,25,26
wholeSceneOwnDrawContexts   = 1
wholeSceneOwnShadows        = 1        # 2 cascades @ 512
wholeScenePlanetEnv         = 1
wholeSceneAAMode            = -1       # do not scope AAMode at all
wholeSceneDisableRaytracing = 0        # full RT in the feed
wholeSceneFarClip           = 2500
wholeSceneLdrResize         = 1        # the phantom-bleed fix
wholeSceneSubmitEarly       = 0        # postfix, not prefix (prefix was a null result)
```

## Measured

```
PERF 65.8-66.0 fps | ours n=329-331 mean=15.1-15.3 p50=15.1-15.2 p95=17.9-18.5
                   | max=20.4-25.5 >50ms=0 | idle n=0
                   | ourDraw (CPU submit) mean=2.1 ms
copies=22.0  park#2757  secondRenders=8338
```

Verified with a **live** panel — an earlier reading of 72 fps was taken with a frozen panel
and retracted. Measured with **reduced in-game graphics settings**; the feed was not at full
fidelity, and the reduced settings are also why the world (and therefore the feed) had no
skybox. Not a max-settings number.

**The exact in-game graphics settings are transcribed in the README**, under
*Reference build → In-game graphics settings in force for that measurement*, together with
what each one interacts with mod-side. They matter because they were reduced *before* the
drift stopped being observed, so the credit between `wholeSceneIntervalMs = 0` and the
settings themselves **has not been separated**. The single cheapest experiment to separate
them: raise Texture Quality alone, keep `wholeSceneIntervalMs = 0`, and watch for the drift.

## What this build contains

- The rate-limit removal (`wholeSceneIntervalMs = 0`) — the change credited with both the
  performance and the absence of drift.
- The phantom-bleed fix: `FinalLDRTexture` one-shot resize to 512×512. It had been
  3840×2160 since birth.
- Stage 26 (`CloudJob.DoWork`) skipped — a confirmed device removal, and the ±151 MB VRAM
  oscillation.
- `PanelBinding.Unbind()` on shutdown — clears the `"Can't remove material"` deferred
  assert.
- The 30-frame settle window after any config change, which is what makes config edits safe.
- The far-clip override (2500 m, our view only) and the panel-material fix.
- A net −6100-line cleanup: the probe render route and six dead files removed.

## What this build deliberately does NOT contain

- **The forced panel rebind in `WholeSceneRender.Reset()`.** It crashed the game. `Reset`
  runs on the render thread, and clearing `PanelBinding._bound` makes the next panel tick
  destroy and rebuild a runtime material mid-frame. The comment block left in
  `WholeSceneRender.cs` records the full reasoning — **do not re-add it from there.**
- Start-of-frame submission (`wholeSceneSubmitEarly = 1`). The prefix hook is present and
  registered, but the A/B was a **null result** at equal session age (41.6 fps both arms).

## Known open issues at this pin

1. **Panel-freeze after a signature config change** — undiagnosed. Workaround: toggle the
   panel off and on.
2. **Stage 19 is enabled while the source marks it DO NOT USE** — the comment and the config
   disagree. See `docs/feed-render-layers.md` §5.
3. **Stages 22/23 still write shared world-space cloud shadow / weather maps from the orbit
   camera.**
4. **No anti-aliasing in the feed**, most likely. See `docs/feed-render-layers.md` §4.

## How to return to this state

```bash
git checkout ref-every-frame-2026-07-29
```

Then copy `docs/feed-config.known-good.txt` over `output/feed-config.txt`, rebuild, deploy to
`D:\SE2Rtt`, and **restart the game** (the bootstrap is pinned here too). Check for a
leftover `output/handover-live.marker` before launching — it blanks the feed on the next run.
