# Roadmap

Agreed goals beyond the POC, recorded 2026-07-28. The POC itself is achieved: a stable
30fps whole-scene render on an `[RTC]` panel with RT ambient, own shadow cascades,
planet atmospheres, and no main-world bleed.

## 1. In-game control panel (LCD UI menu)

Many of the render-layer options and the frame-rate cap should be controllable from an
on-LCD UI menu in game, not from feed-config.txt. Candidate controls, all already
live-tunable through the config layer they would sit on top of:

- feed frame rate (`wholeSceneIntervalMs` — hard floor removed, 0 = every frame)
- render layers: shadows (own/shared/off, cascade count + resolution), RT GI, bloom,
  volumetrics, decals, atmosphere, far clip
- orbit parameters (radius, period, height)
- resolution

The config file remains the persistence layer; the UI writes it. The 2s poll and the
rebuild-signature machinery already make every change safe to apply live.

## 2. Multiple camera feeds

Make the mod stable and modular enough to support several simultaneous feeds. Known
work this implies:

- Per-feed instances of what is currently static: ScreenBuffers, DrawContextManager,
  cascade set, LDR ring, panel binding, orbit state. The gate/teardown machinery
  (FeedGate) becomes per-feed with a shared render-thread pump.
- One `SceneDrawSystem.Draw` postfix dispatching N feeds round-robin — NOT N hooks.
- Panel discovery generalised from "first [RTC] panel" to a registry.

## 3. Automatic feed frame-rate management (the frame budget)

The core idea, verbatim intent: an adjustable target frame cost for RTT rendering as a
whole, defaulting to the cost of one 30fps feed. Each additional ACTIVE, ON-SCREEN feed
takes a fraction of that budget rather than adding on top, so the main world's frame
rate stays stable no matter how many feeds run.

Sketch on top of the existing gate:

- `rttBudgetMs` (config + UI): total ours-frame milliseconds allowed per second,
  default ≈ one feed at 30fps × ~28ms ≈ 840ms/s.
- Perf.cs already measures per-render cost; divide budget by measured cost to get total
  renders/sec, split across feeds (equal shares first; priority weights later).
- Off-screen / occluded / panel-off feeds cost zero (the gate already proves dormancy
  works); their share returns to the pool.
- The per-feed interval becomes derived state, not a user knob, once the budget exists.

## Perf backlog (minimal visual cost, in order)

1. Cascade resolution 1024 -> 512 for the feed (config-only; at a 512 output the shadow
   texel density is still ~1:1 — near-imperceptible).
2. Far-clip tuning below 4000 m (user-verified lossless at 4000; find the floor).
3. Amortised cascades: render the feed's shadow cascades every Nth feed frame (own
   resources persist between renders; cost ~66ms shadow latency in the feed).
4. Cascade count 3 -> 2 (mild far-shadow softening in the feed only).
5. THE BIG ONE, parked as risky: move our render AFTER present, so our GPU work
   overlaps the next frame's CPU record instead of delaying the swap. Recovers most of
   the ours-frame cost from the main world. Bootstrap change; frame-span lifetime at
   present is the hazard; needs its own careful session.
