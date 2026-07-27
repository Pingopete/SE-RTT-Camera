# Whole-scene render — status and plan (2026-07-27)

The POC works: the panel shows the engine's full renderer from the orbit camera —
textures (incl. the probe-route-impossible asteroid triplanar), deferred lighting, sun
and flares, atmosphere both halves, correct 1:1 aspect, orbit-locked sky. Stable, with
the player's view intact. This file is the audit of what remains, agreed with the user.

## The feed today

| state | detail |
|---|---|
| ✓ in | MainView geometry + real triplanar terrain, deferred lighting on our own cluster grid, skybox/stars/planets/belts, sun + glare, atmosphere (in-scatter + extinction), volumetrics, transparency, bloom, tonemap |
| ⚠ degraded | sun shadows = player-centred cascades (read-only share; fade/fail with distance); ambient + reflections = player-positioned probe atlas; exposure fixed (adaptation scoped off); texture streaming driven by player position |
| ✗ out (fixable) | HBAO (stage 9 skip), decals (stage 8 skip), particles (stage 7 — needs sim-stepping investigation before unskipping) |
| ✗ out (by scope) | raytraced GI/reflections, probe updates, water surfels, HUD (deliberate) |

## User-observed glitches → diagnosis

| observation | diagnosis | fix tier |
|---|---|---|
| blocks flash in brightness | decals missing (test: unskip 8) OR probe-reflection snap on metals | now / later |
| in-shadow areas flash bright↔dark | shared probe atlas re-captured round-robin from the PLAYER's position — each face update snaps the ambient the feed samples | later (own probe set) |
| whole ship/objects go fully dark at orbit points | camera exits the player-centred cascade volume — outside it everything samples "in shadow" | later (own cascades) |
| ghosting/smearing (expected) | FSR temporal AA fed garbage motion vectors: prev-frame camera fields are the PLAYER's, frames 200ms apart | now (AA scope + prev-frame fix) |

## Agreed plan

1. **NOW — unskip decals (8)**, then HBAO (9) after verification. Owned contexts changed
   the safety calculus; each is one config flip.
2. **NOW — temporal hygiene**: scope DRSSettings for our render (AAMode FXAA=1 —
   spatial-only AA, no motion vectors; ScalingMode NativeAA=4; sharpening off), and fix
   our RenderView's LastFrameCameraPosition fields to OUR previous orbit position
   instead of the player's.
3. **NOW — feed exposure knob**: scope PostProcessSettings.LuminanceExposure to a
   config value during our render.
4. **LATER — own the borrowed lighting** (fixes the flash/darkening tier): render our
   own shadow cascades into our own DirectionalLightShadowResources; own or
   double-buffer a probe set. Same own-the-object pattern proven five times.
5. **LATER — player-view flicker** (parked): own EyeAdaptation ping-pong / cloud map.
6. **LATER — performance**: raise rate (interval knob), strip the probe pipeline to
   transform-only (it still renders 30 Hz nobody sees), measure.

## Standing rules (hard-won, do not relearn)

- Own the contexts → RUN the prepare stages; producers (4, 6, 14, likely 15) cannot be
  skipped.
- View feeds culling, camera CB feeds shaders — swap both.
- Never let the installed view's `_resolution` diverge (race → CTD); mode 2 handles it.
- Resizable engine textures go to the panel via the CopyJob blit, never the raw copy.
- CTD leaves `output/handover-live.marker` → delete or the feed is blank.
