using System.Globalization;

namespace RttProbe;

// Environment.TickCount64 quantises to the system timer interval — ~15.6 ms on
// Windows unless something has raised the timer resolution. That is invisible at
// 2 fps and decisive at 30: a 33 ms gate rounds up to ~47 ms, so asking for 30 fps
// delivered 20, and asking for 15 delivered 12.8. Both measured exactly.
//
// Stopwatch is backed by QueryPerformanceCounter, so the gate lands where it is put.
// Only the per-frame gates need this; the 2 s arm/config polls do not care.
internal static class Clock
{
    private static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
    public static long Ms => Sw.ElapsedMilliseconds;
}

// Live-tunable knobs, read from output\feed-config.txt and re-read every couple of
// seconds. Tuning by editing a constant and rebuilding wastes a hot-reload cycle per
// experiment; this makes it a file edit.
//
// Anything missing or malformed falls back to the default, so a half-written file
// cannot break the feed.
//
// This used to carry 106 knobs. Sixty-two were bisect switches for the probe scene
// render, which no longer exists — each had a default nobody had changed in weeks and
// a consumer that has been deleted. They are gone, along with the 300-character
// boolean chain that compared them all: the whole-scene group now change-detects on a
// signature string, which is shorter and cannot be forgotten when a knob is added.
internal static class FeedConfig
{
    private static readonly string Path_ = System.IO.Path.Combine(RttLog.OutDir, "feed-config.txt");

    private static long _lastRead;
    private static long _lastStamp;
    private static bool _firstPoll = true;

    public static int EffectivePanelMs => PanelMs > 0 ? PanelMs : IntervalMs;

    public static int IntervalMs { get; private set; } = 66;      // ~15 fps

    // Panel update period, deliberately separate from the camera pass. RequestRender
    // makes the ENGINE run a full DrawOne (borrow, clear, replay UI batches, mipmap,
    // copy, return) for our target — so the two rates cost very different things and
    // must be testable independently. 0 = follow IntervalMs.
    public static int PanelMs { get; private set; }

    // Grace period after the logic loads before any GPU work is issued. Several
    // crashes were "on world load", when the renderer is still settling — pooled
    // targets resizing, panels acquiring their render targets, streaming catching up.
    // Starting into that is asking for trouble.
    public static int StartupDelayMs { get; private set; } = 2000;

    // Point the engine's two per-frame camera constant buffers at ours for the pass.
    //
    // SettingsGroup._jitteredCameraSettings / _nonjitteredCameraSettings are read by ~92
    // methods. Passes that take a cameraCb parameter already use ours; everything else
    // reads these. The immediate defect it fixes is ClusteringJob.DoWork, which builds
    // our cluster grid from the PLAYER'S frustum and therefore bins every clustered local
    // light into the wrong screen-space cluster.
    //
    // It is also the prerequisite for AtmosphereMultiplyJob, AmbientLightJob and HBAOJob.
    // Off by default until proven: if the restore is ever skipped, the engine's remaining
    // passes render the player's screen from our 512x512 orbit camera.
    public static bool SwapCameraCb { get; private set; }

    // Stage 2: construct a second ScreenBuffers and do nothing with it. Proves the
    // public parameterless constructor and InitializeBuffers work before anything is
    // swapped. Costs a second set of screen-sized targets, so not free — but far less
    // than finding out mid-render that it could not be built.
    public static bool WholeSceneBuildBuffers { get; private set; }

    // Stage 3: swap the globals and actually call Draw. The real experiment.
    //
    // Named Enabled rather than WholeSceneRender because that is the CLASS that does the
    // work, and a property with the same name shadows it inside this type — which is
    // exactly the kind of collision that produces a confusing compile error at the worst
    // moment.
    public static bool WholeSceneEnabled { get; private set; }

    // Resolution of the second render. The whole point of this route is that Draw takes
    // its size from the buffer it is handed, so this is a real knob rather than a wish.
    public static int WholeSceneWidth { get; private set; } = 512;

    public static int WholeSceneHeight { get; private set; } = 512;

    // Stage 3b: swap the camera too, so the second render is OUR viewpoint.
    //
    // Off for the first test on purpose. With it off the second render is the player's
    // view at our resolution — wrong, but visually verifiable and with only ONE global
    // moved. Turning it on is then a single attributable change rather than part of a
    // failure nobody can read.
    public static bool WholeSceneCamera { get; private set; }

    // Disable raytracing for the duration of OUR render.
    //
    // Draw builds acceleration structures and steps ReSTIR / IR-cache accumulators that
    // integrate over frames in WORLD space, none of which live in ScreenBuffers — so
    // owning a ScreenBuffers does not isolate them. Running the pipeline twice per frame
    // advances them twice, which showed up immediately as patchy, shifting GI in the
    // PLAYER'S world render.
    //
    // Costs our feed raytraced GI, which was excluded from scope anyway. A feed without
    // RT GI is worth more than a main view with corrupted GI.
    // 0 = leave raytracing alone. 1 = clear Enabled + all accumulators (stops
    // RaytraceGIJob, so ComputeGI's UNCLEARED DiffuseGIBuffer borrow reaches
    // AmbientLightJob as recycled garbage — the ambient flashing on shadowed sides).
    // 2 = clear only the world-space accumulators, keeping Enabled so the GI buffers
    // are still written. 2 is the intended setting if the player's view stays clean.
    public static int WholeSceneDisableRaytracing { get; private set; } = 1;

    // Disable EYE ADAPTATION for our render.
    //
    // ComputeExposure drives EyeAdaptationJob, which ping-pongs a shared auto-exposure
    // history — this project already recorded a second call per frame as unsafe. Our
    // 512x512 view of the same scene has a different average luminance, so the player's
    // adaptation oscillates between the two exposures and their world lighting flickers
    // at exactly our render cadence.
    //
    // Exposure itself stays ON so our image is still exposed; only the TEMPORAL
    // adaptation is cut, which for a fixed-purpose camera feed is arguably correct.
    public static bool WholeSceneDisableEyeAdaptation { get; private set; } = true;

    // Stop our render UPDATING the shared environment-probe atlas.
    //
    // The engine refreshes probe faces round-robin across frames into a shared atlas,
    // and that atlas supplies ambient and reflections. Our second Draw calls
    // RenderEnvironmentProbe too, advancing the queue at double rate and writing faces
    // with our settings. The player then samples it — which presents as indirect
    // lighting misbehaving while direct lights look fine.
    public static bool WholeSceneDisableProbeUpdates { get; private set; } = true;

    // Swap CoreSystems.DrawContexts for our own DrawContextManager during our render.
    //
    // The stage bisect ruled out every skippable stage and the flashing persisted, so
    // the cause is in what remains — and everything that remains culls and ranges
    // through DrawContexts: visibility lists, occlusion, geometry buffers, the shared
    // GPU counters ScenePreparation clears every frame, LOD transitions. The
    // experimental branch recorded the signature exactly: a second cull writing the
    // engine's visibility lists made the player's ship lights flicker.
    //
    // Also the PREREQUISITE for wholeSceneCamera — culling from the orbit camera into
    // the player's contexts would be strictly worse than today's perturbation.
    public static bool WholeSceneOwnDrawContexts { get; private set; }

    // Draw sub-stages skipped INSIDE our render only. Comma-separated ids:
    //
    //   0 ExecuteAccelerationStructuresBuilding    raytracing scene / TLAS
    //   1 ExecuteRaytracingPrepareAndSceneFinalize raytracing prepare
    //   2 RenderEnvironmentProbe                   shared probe atlas (ambient + reflections)
    //   3 RenderShadows                            shadow cascades
    //   4 ComputeExposure                          auto-exposure history  ** SEE BELOW **
    //   5 UpdateSurfels                            water surfels
    //
    // STAGE 4 IS NOT SAFELY SKIPPABLE. ComputeExposure has OUT PARAMETERS:
    //
    //     ComputeExposure(cl, lBuffer, out ITexture2DView exposure, out Nullable<..>)
    //
    // A prefix that skips it leaves those null, and ApplyBloom and ApplyToneMapping
    // consume them immediately — instant NullReferenceException. It is listed so the
    // distinction is on record: the other stages only have SIDE EFFECTS, this one
    // PRODUCES something Draw needs. Use WholeSceneDisableEyeAdaptation for the
    // exposure-history problem instead.
    //
    // This mechanism exists because settings flags cannot reach everything.
    // ExecuteAccelerationStructuresBuilding is called unconditionally at the top of Draw
    // and checks only EnableGPUParallelization, so clearing RaytracingSettings.Enabled
    // never stopped it — three rounds of settings scoping missed it for that reason.
    //
    // Default is the two raytracing stages plus the probe atlas: all world-space, all
    // read by the player's next frame, none of them needed for a camera feed.
    public static int[] WholeSceneSkipStages { get; private set; } = { 0, 1, 2 };

    // Rebuild the whole-scene RenderView through the engine's SetResolution +
    // SetCameraParameters instead of three raw field overrides. Fixes the squashed
    // 16:9-into-1:1 aspect and the stationary sky (ViewAt0/InvViewAt0) — but its first
    // outing ended in a GPU page fault in a bloom chain after ~45 renders, with three
    // sub-changes landed at once. OFF = the proven squashed-but-stable baseline;
    // flip live to re-test and bisect.
    public static int WholeSceneCameraRebuild { get; private set; }

    // AA mode for the whole-scene render: -1 leave engine (FSR: temporal, ghosts at 5fps),
    // 0 none, 1 FXAA (spatial-only — the right choice for this feed), 2 FSR. Also forces
    // ScalingMode NativeAA and sharpening off while >= 0.
    public static int WholeSceneAAMode { get; private set; } = -1;

    // Force ScalingMode NativeAA + sharpening off for our render. SEPARATE from the AA
    // mode because bundling them CTD'd in the post chain: ScalingMode selects between
    // UpscaleTargetFSR and ApplyNonFSRUpscalingAndAA, which borrow differently-sized
    // targets against our fixed-geometry ScreenBuffers. The riskier half.
    // Scope PostProcessSettings.Bloom off for our render only. BloomJob retains its
    // cascade borrows on the SHARED SceneDrawSystem._bloomJob; see ScopeSharedState.
    public static bool WholeSceneNoBloom { get; private set; }

    // Far plane for OUR view only, metres. 0 = keep the player's far plane. The second
    // render's measured cost is CPU submit (draw-command building), so culled-in draw
    // COUNT is the lever — not pixels. VeryFarClipping is untouched, so the planet/sky
    // layer still renders and only asteroid/grid geometry beyond the clip is dropped.
    // Live-tunable; not part of the rebuild signature.
    public static double WholeSceneFarClip { get; private set; }

    // Let WholeSceneFarClip EXTEND the far plane, not just pull it in.
    //
    // Off by default because the min-only behaviour is correct for the perf lever this knob
    // was built as. On, the configured value wins in both directions — which is what the
    // remoteness investigation needs, because the engine's own FarClipping (~15 km) was
    // silently capping the feed and making "nothing is drawn out there" ambiguous between
    // our far plane and the engine's streaming. See docs/open-question-remote-streaming.md.
    //
    // Costs draw count: the far plane is what culling reads, so widening it widens the cull.
    // Live-tunable; not part of the rebuild signature.
    public static bool WholeSceneFarClipExtend { get; private set; }

    // A/B gate on the FinalLDR resize (the phantom-bleed fix). In the rebuild signature
    // so flipping it forces the rebuild that re-runs (or skips) the one-shot resize.
    public static bool WholeSceneLdrResize { get; private set; } = true;

    // START-OF-FRAME SUBMISSION. Record our render in Draw's PREFIX (before the player's
    // frame) instead of its postfix (between the player's frame and the present copy), so
    // our GPU work executes while the CPU is still recording the player's frame.
    //
    // The targeted fix for the session drift: our render's true GPU work is ~3ms, but an
    // ours-frame costs ~30ms because the GPU idles waiting, and that idle grows with engine
    // session age. This moves the waiting somewhere it costs nothing.
    //
    // Deliberately NOT in the rebuild signature: no resources change, so flipping it needs
    // no rebuild and no settle window — it is a clean live A/B. Costs one frame of feed
    // latency. Off by default so adopting the new bootstrap is inert until asked.
    public static bool WholeSceneSubmitEarly { get; private set; }

    public static bool WholeSceneNativeScaling { get; private set; }

    // Feed exposure bias, in EV STOPS. Signed: +1 doubles brightness, -1 halves it.
    //
    // Confirmed against the shipped HLSL rather than guessed. With adaptation scoped off
    // the feed runs ConstantExposure.hlsl:
    //
    //     exposure = CalculateExposure(Post_.ConstantLuminance, Post_.LuminanceExposure)
    //     CalculateExposure: log2(keyValue(avgLum)/avgLum) + exposure   [Exposure.hlsli]
    //     GetExposure():     exp2(that)                                [GetExposureLinear]
    //
    // ConstantLuminance is 1, and LuminanceKeyValueCendos(1) == 1, so the base term is
    // log2(1/1) = 0 — LuminanceExposure is a pure EV offset on unity. The engine's live
    // value is 0, which is why 0 doubles as "leave it alone" here at no cost.
    //
    // Was gated `> 0`, which silently made the knob brighten-only. A 512px feed pointed
    // at a sunlit planet needs to come DOWN more often than up.
    public static double WholeSceneExposure { get; private set; }

    // ---- AUTO APERTURE (2026-08-01) ----------------------------------------------
    //
    // WholeSceneExposure alone is a FIXED stop, and a fixed stop cannot be right twice:
    // -2 EV matched the world at night and blew out over a sunlit flower meadow. The feed
    // needs to open and close like an eye — but it must NOT use the engine's eye adaptation,
    // whose history is shared with the player (that is what made feed brightness track where
    // the PLAYER stood), and owning a second EyeAdaptationJob removed the device twice.
    //
    // So: drive the stop from the SUN, on the CPU, with no new GPU resource at all. Sun
    // elevation against the subject's local up is the variable that actually separates the
    // two failing cases, we already compute planet-radial up for the orbit, and the whole
    // thing costs a dot product per render.
    //
    // What it deliberately does NOT do is meter the picture. Pointing the camera into a cave
    // will not open the aperture. That needs a real measurement (a readback of our own
    // smallest mip) and belongs behind this, once this proves the seam.
    //
    // EVERY KNOB HERE IS OUTSIDE THE REBUILD SIGNATURE, on purpose. Tuning an exposure curve
    // is exactly the kind of thing you do twenty times in a row, and the 15:08 device removal
    // was a signature key edited on a live feed. These are read fresh per render.
    // PARKED, DEFAULT OFF. Sun-driven exposure was built and then overtaken by events: with
    // stage 25 correctly skipped, LuminanceExposure is not read at all (ConstantExposure
    // returns the existing view), so nothing here can reach the image. Un-skipping 25 to make
    // it reach WOULD over-expose the player's whole world — see the note on wholeSceneExposure.
    //
    // The scaffolding and the scan diagnostics stay because the successor needs them: a real
    // per-feed aperture wants its own entry in EyeAdaptationJob._autoExposures (a COLLECTION,
    // and the engine already drives a separate _environmentProbeExposureJob), at which point
    // a curve like this one is what feeds it.
    public static bool FeedAutoExposure { get; private set; } = false;

    // EV at full day (sun overhead) and at night (sun below the horizon). Day is the more
    // negative number: a bright scene needs a smaller aperture.
    public static double FeedExposureDay { get; private set; } = -5.0;
    public static double FeedExposureNight { get; private set; } = -2.0;

    // Sun elevation, as dot(sunToward, localUp), over which the curve ramps. The default
    // spans a little below the horizon to well above it, so dawn and dusk are a glide
    // rather than a step.
    public static double FeedExposureDawnDot { get; private set; } = -0.15;
    public static double FeedExposureDayDot { get; private set; } = 0.25;

    // Seconds for the stop to travel most of the way to its target. This is the "aperture
    // has inertia" term; without it a cloud crossing the sun would step the panel.
    public static double FeedExposureAdaptSeconds { get; private set; } = 4.0;

    // Sign of the engine's sun vector. +1 if the field points TOWARD the sun, -1 if it is
    // the direction light travels. Config rather than an assumption: getting it backwards
    // makes the feed brightest at midnight, which is obvious on sight and would otherwise
    // cost a rebuild to flip.
    public static double FeedSunSign { get; private set; } = 1.0;

    // Explicit list of RaytracingSettings flags to clear during our render. Empty = use
    // the wholeSceneDisableRaytracing preset instead.
    //
    // RaytracingSettings has TWENTY booleans and the presets only ever touched six. The
    // symptom we are chasing ("subtle flashing that seems to come from light sources")
    // has two flags named for it — LocalLightsInIRCache and LocalLightsInRTXGI — that no
    // preset clears, and there is a master EnableReSTIR that the accumulator preset also
    // misses, so candidates keep getting written into the shared reservoirs.
    //
    // Bisecting twenty flags by editing an enum and rebuilding is the wrong shape. This
    // makes each hypothesis a config edit against a running game.
    //
    // CAUTION: RaytraceGIJob holds a LazyJobSnapshotHandler<RTGISettings, RTGISnapshot>
    // and builds SHADER DEFINES from those settings. Flags that feed a define will force
    // a pipeline rebuild when toggled — which is the mechanism behind the bright flashing
    // that clearing Enabled produced. Expect some flags to be free and others to cost a
    // visible rebuild.
    public static string[] WholeSceneRtFlags { get; private set; } = Array.Empty<string>();

    // Rebuild the planet environment (PlanetSpheres / PlanetEnvSetupFirst /
    // AllPlanetEnvSetups) from OUR camera before our Draw, and again from the player's
    // after. These per-frame CBs are built from the player's view in
    // PlanetEnvironmentGroup.OnBeginDraw and inherited by a nested render — which is why
    // the feed's planet atmosphere detached from the planet and moved with the player's
    // aim. Twenty-six jobs read them, atmosphere and volumetrics included.
    public static bool WholeScenePlanetEnv { get; private set; }

    // LENS FLARES IN THE FEED (roadmap goal 4.3). Default OFF — it changes which context
    // receives flare REGISTRATION, and the conservative arrangement it replaces was put
    // there deliberately.
    //
    // Today our render installs the ENGINE'S FlaresContext and skips stage 21. That is
    // correct but gives the feed no flares at all: registration goes through the global
    // (PointLightEntityComponent.Init / SetParameters / OnRemovedFromScene, the spot and
    // particle equivalents, SceneManager.UpdateFlareDefinitions), so whichever context is
    // installed receives it — and running RenderFlares against the shared context would
    // advance ProcessFinishedFrame / PrepareReadback twice per frame and corrupt the
    // player's flare occlusion.
    //
    // With this ON we keep OUR OWN FlaresContext installed and share only the four
    // DEFINITION members from the engine's: _flaresByGuid, _texturePinsByGuid,
    // _flaresBuffer and _flareDefinitionsAllocator. Verified offline with EngineQuery:
    //
    //   * those four are written ONLY by FlaresContext..ctor and UpdateFlaresBuffer —
    //     neither is in the render path, so our render cannot mutate them;
    //   * ProcessFinishedFrame and PrepareReadback touch ONLY _occlusionCounterBuffers,
    //     _drawCommandsCounterBuffers, _occlusionCount, _instancesCount and
    //     _flareDrawBuffersCapacity — all of which are then OURS, so the player's readback
    //     is untouchable;
    //   * the ctor allocates every RW buffer up front (CreateResizableRWBuffer x5 plus both
    //     counter arrays sized from FrameSpanManager.ResourceSpanCount), so our context is
    //     fully formed with no lazy path to trip over.
    //
    // KNOWN BOUNDED RISK. AddFlare / UpdateFlare / RemoveFlare call UpdateFlaresBuffer,
    // which REPLACES _flaresBuffer on whichever context is installed. Those run from
    // render-command-buffer replay, which happens before Draw and therefore outside our
    // window — but that is inference, not proof. If one ever did land inside our window it
    // would set OUR buffer and leave the engine's holding the previous one, so the player
    // would be stale by one flare until the next registration. Degraded and self-healing,
    // not broken. The definition members are re-read from the engine on EVERY render rather
    // than copied once, so nothing accumulates.
    //
    // Forces stage 21 to run, the same way WholeSceneOwnShadows forces stage 3: owning the
    // context is pointless if the pass that reads it never executes.
    //
    // ==========================================================================
    // BROKEN. DO NOT ENABLE. Two CTDs, and the SECOND one found the real flaw.
    // ==========================================================================
    //
    // Both crashes were the same NullReferenceException in FlaresContext.GetFlareConstants,
    // which does `_flaresBuffer.GPUBufferId` unguarded. The first was diagnosed as stage 21
    // being force-run before the mirror had happened, and fixed with the _flaresReady guard.
    // That fix is correct and is still in place — but it addressed the wrong half, because
    // the second CTD happened WITH the guard active.
    //
    // THE ACTUAL BUG IS LIFETIME, NOT READINESS. MirrorFlareDefinitions copies REFERENCES
    // out of the engine's context into ours, and one of them, _flaresBuffer, is a GPU
    // resource. Our FlaresContext hangs off OUR DrawContextManager. When the gate tears down
    // — a pause, a config change, a hot reload — Reset() disposes that DrawContextManager,
    // which disposes our FlaresContext, which disposes _flaresBuffer. That reference IS the
    // ENGINE'S buffer. We free the player's flare definitions, the engine's context keeps
    // pointing at the freed object, and its very next flare pass dereferences it.
    //
    // The timing is the proof. Both crashes landed immediately after a gate teardown:
    //   CTD 1  20:20:20  right after a pause/resume cycle
    //   CTD 2  20:59:52  one second after "FEED GATE: DORMANT" from a pause
    // Neither happened during steady-state rendering, which is why flares looked fine for
    // twelve minutes the first time.
    //
    // _flaresReady cannot help: it protects OUR pass from reading a null buffer. It says
    // nothing about us having freed the ENGINE'S.
    //
    // THE FIX, unbuilt: null the four mirrored fields on our context BEFORE our
    // DrawContextManager is disposed, so its dispose chain cannot reach objects we do not
    // own. That needs a teardown hook ordered strictly before the DC dispose, and getting
    // that order wrong is another crash — hence not attempted at the end of a session with
    // four CTDs in it.
    //
    // GENERAL LESSON, worth more than the feature: sharing a reference INTO an object you
    // will later dispose makes you the owner of something you did not allocate. Borrowing
    // read-only state is only safe if the borrower's lifetime is strictly shorter AND its
    // teardown cannot touch what it borrowed. Ours failed the second condition. Every other
    // shared object in this route is shared the other way round — we hand OUR resources to
    // nobody, and we read the engine's without holding them past a frame.
    public static bool WholeSceneOwnFlares { get; private set; }

    // OWN ENVIRONMENT PROBES (roadmap goal 4.4). Default OFF — this is the most expensive
    // feature on the roadmap and the first one expected to cost measurable frame time.
    //
    // WHY IT MATTERS. This is a REMOTE camera, and the feed currently samples the PLAYER'S
    // probe atlas, which is centred on the player. So the feed's reflections and ambient
    // bounce are correct for where the player is standing rather than where the camera is.
    // Subtle at a 100 m orbit, visibly wrong at real remote-camera distances, and worse the
    // further the camera goes. A correctness problem dressed as a fidelity one.
    //
    // WHY IT COULD NOT SIMPLY BE UNSKIPPED. Two facts, both verified offline:
    //   * the atlas is EIGHT cube textures held per-MANAGER on the global
    //     CoreSystems.EnvironmentProbeManager, so rendering probes without our own manager
    //     writes the PLAYER'S atlas from the orbit camera — the old "ambient and reflections
    //     from light sources but not the lights themselves" bug;
    //   * DrawContexts.EnvProbesToUpdate is written ONLY by DrawContextManager.OnBeginDraw,
    //     which runs in DrawInternal and never in our nested Draw. Our context's queue is
    //     therefore permanently empty, which is exactly why skipping stage 2 costs nothing
    //     today: we were never consuming it.
    //
    // WHAT THIS DOES. Owns the object, the pattern already proven for ScreenBuffers and
    // DrawContextManager: construct our own manager, swap the global for the duration of our
    // render, fill our own queue from our own PrepareProbes(), and let stage 2 run.
    // PrepareProbes on OUR instance is not a Rule 8 violation — Rule 8 forbids advancing a
    // GLOBAL manager's state twice per frame, and ours is shared with nobody.
    //
    // RISK, honestly. The ctor allocates NOTHING (it calls only Object..ctor), so
    // construction is free — the textures are created later in RecreateProbes, reached from
    // PrepareProbes. That is the allocation to respect, and it is also guaranteed to trip
    // _forceReprocess, so it must land inside the 30-frame settle window (Rule 19). Failure
    // is graceful rather than fatal: CloseIBL/FarIBL fall back to CommonResources.SkyboxIBL,
    // so a probe that is not ready degrades to the skybox term instead of binding null.
    //
    // Forces wholeSceneDisableProbeUpdates OFF in effect — see WholeSceneRender. Scoping
    // ProbeSettings.Enable off existed purely to stop us updating the SHARED atlas, and with
    // our own manager installed that reason is gone. Leaving it on would make PrepareProbes
    // do nothing and this feature would silently be a no-op.
    public static bool WholeSceneOwnProbes { get; private set; }

    // Flare intensity for the FEED ONLY, scoped like exposure and bloom. Negative = leave
    // the engine's value alone (default).
    //
    // Reported once flares went live: the sun flare is "extremely bright" and shows "a faint
    // square box around the edges of the square flare texture". Both are the same symptom.
    // The feed runs a FIXED exposure — eye adaptation is scoped off because its history is
    // shared, and stage 25 makes exposure read-only — so it has no auto-adaptation to pull a
    // blown highlight back, and on top of that the panel material multiplies by
    // EmissivityMultiplier (10 by default). A flare quad that saturates then shows its
    // texture's near-zero alpha border as a visible rectangle.
    //
    // FlaresIntensity is the right lever rather than emissivity: GetFlareConstants reads it
    // straight into FlaresConstantData.IntensityMultiplier, so scoping it touches ONLY the
    // flare pass, and only during our render. Lowering emissivity instead would dim the
    // whole feed, and it lives on a material shared with every other LCD in the world.
    //
    // Deliberately NOT in the rebuild signature: it is a pure per-render scope, so retuning
    // it takes effect on the next render with no rebuild and no settle window. That makes
    // finding the right value a live sweep instead of a series of gate cycles.
    public static double WholeSceneFlareIntensity { get; private set; } = -1;

    // THE STATS PANEL (goal 9 / plan phase A1). Tag a panel [RTS] to get perf numbers on
    // it in world. Draws into the panel's OWN batch — no target, no binding, no handover —
    // so it is independent of the feed and cannot interfere with it.
    public static bool StatsPanel { get; private set; } = true;

    // Repaint period. The panel is re-recorded by marking its content dirty, so this is
    // how often that happens. 500 ms is plenty for numbers and keeps the rebuild cost
    // (and its FSR-mask re-arm) negligible.
    public static int StatsPanelMs { get; private set; } = 500;

    // THE BUDGET CONSTANT (design: budget lock v2). The measured warm cost of ONE render
    // at the current global quality preset — the per-frame slice the whole phase-2
    // scheduler holds constant. The stats panel flags sustained submit above it.
    //
    // Default is today's measured reference (~2.1-2.5 ms at 1024 with probes and flares
    // on). Phase B replaces this with a per-preset table. It is a TRIPWIRE THRESHOLD and a
    // calibration number, never a per-frame gate — enforcement is structural (one render
    // slot per engine frame), because metering with skip-to-repay would manufacture the
    // bimodal frame pattern that this project already identified as the felt choppiness.
    public static double RttBudgetMs { get; private set; } = 2.5;

    // Hold the tagged panel in FSR's REACTIVE mask. Default ON — it fixes a real, long-lived
    // visual bug and the whole change is one float property plus one int field per tick.
    //
    // Without it the player's FSR accumulates temporal history over a surface whose content
    // we replace every frame, because the engine only marks a panel reactive for 5 frames
    // after RebuildSurfaceContent and our feed bypasses that path entirely. The result is
    // the accumulating smear along the stars' apparent motion, worse the further the player
    // stands back and briefly cleared by player movement. See PanelBinding.ApplyFsrMask.
    //
    // Set to 0 to get the old behaviour back — worth having as an A/B, and worth turning off
    // if the player's AA is not FSR, since the reactive mask then buys nothing while still
    // touching the shared LCD material.
    public static bool PanelFsrMask { get; private set; } = true;

    // Rebuild the panel target's mips 1..N from our frame after the handover copy.
    //
    // CopyTextureSubresource writes one subresource, so without this only mip 0 is ours and
    // the lower levels keep whatever DrawOne left on a recycled pool texture — making the
    // feed correct up close and progressively wrong as the player backs away. Default ON:
    // it uses the engine's own MipMapJob instance, creates nothing, and fails soft to the
    // previous behaviour if any member is missing. Needs the bootstrap that appends
    // __instance to the offscreen-UI hook args, so it is inert until the game is restarted.
    // See FeedHandover.RegenerateMips.
    public static bool PanelMipRegen { get; private set; } = true;

    // How long a tagged panel may go without ticking before the mod shuts itself down.
    //
    // The LCD render component ticks every panel it draws, so a panel switched off,
    // unpowered or destroyed simply stops arriving — the absence of the signal is the
    // signal. Long enough to survive a stall or a streaming hitch, short enough that
    // toggling the panel is a usable A/B against vanilla.
    public static int PanelIdleMs { get; private set; } = 1500;

    // How often Perf emits its frame-interval report. 0 disables the instrument.
    public static int PerfReportMs { get; private set; } = 5000;

    // Cascade resolution and count for OUR shadow set. 0 = use the player's settings.
    //
    // Our set is sized from the player's graphics options, which are chosen for a 4K
    // screen. At 4096px x 8 that is half a gigabyte of depth textures and eight full
    // geometry passes per second render — to shade a 512x512 panel. Scoped during our
    // render only, and our CascadeShadowsContext resizes itself to match on its next
    // flush, so the engine's own set is untouched.
    public static int WholeSceneCascadeResolution { get; private set; } = 1024;

    public static int WholeSceneCascadeCount { get; private set; } = 3;

    // Character shadow map size for OUR render only. 0 = leave the player's value alone.
    //
    // Found by the resource report: two 2048x2048 depth sets — first-person and third-person
    // — were 32 MiB of a 444 MiB feed, allocated because CharacterShadowsContext sizes itself
    // from ShadowSettings.DirectionalLight.CharacterShadowResolution and nothing scoped it.
    // The feed is an orbital camera; the player's own character is not in the shot, and the
    // first-person set is meaningless to it by definition.
    //
    // NOT zero-able: the context allocates whatever it is told, and a zero-size depth texture
    // is a device removal waiting to happen. 256 keeps the machinery valid and costs 0.5 MiB
    // instead of 16 — if a character ever IS in a feed's shot, their shadow is simply coarse.
    public static int WholeSceneCharacterShadowResolution { get; private set; } = 0;

    // HOW MANY INDEPENDENT FEEDS ARE ACTIVE (plan phase C3). 1 = the shipped behaviour.
    //
    // IN the rebuild signature, and the first attempt had this exactly backwards.
    //
    // The original reasoning was "adding a feed must not drag the OTHERS through a rebuild
    // and a 30-frame settle". That sounds right and is wrong, because changing this value
    // changes WHICH PANEL EACH FEED OWNS. Every feed caches its panel resolution — the
    // resolved panel id, the offscreen target, the handle text the handover matches on, the
    // render target itself — and re-routing underneath those caches leaves each feed holding
    // another panel's identity. Observed 2026-07-30 on the first two-feed run: routing was
    // correct in the log, both panels claimed the right feeds, and the picture froze, because
    // feed 0 was still matching the handle of the panel that had just been reassigned to
    // feed 1. drawOne(ours) went to 0.0 and copies stopped, with no error anywhere.
    //
    // A rebuild is exactly the thing that clears those caches, so this belongs in the
    // signature. The cost is one gate cycle when you change the feed COUNT, which is a rare,
    // deliberate act — not the per-frame cadence tuning A2 freed from the signature.
    //
    // Feeds.Count clamps this into the slots that exist, so a typo degrades to a valid count
    // instead of an index fault.
    //
    // A second feed needs a second tagged panel to point at. With feedCount=2 and only one
    // tagged panel, feed 1 simply never finds a target and stays dormant — which is the
    // graceful-cut contract (goal 7) doing its job, not a failure.
    public static int FeedCount { get; private set; } = 1;

    // ---- the VRAM admission cap (phase E1) ---------------------------------------
    //
    // feedCount is what the user ASKS for; these three decide what they GET. See
    // Feeds.UpdateResidentCap for the arithmetic and the crash that motivated it.

    // Hard user ceiling, independent of memory. Set it to 1 to pin single-feed behaviour
    // regardless of how much VRAM is free.
    public static int MaxResidentFeeds { get; private set; } = 4;

    // Headroom held back from the admission arithmetic. 512 MB because VRAM was measured
    // swinging +/-200 MB frame to frame during the D3 sweeps, and because the device
    // removal that motivated this happened only ~90 MB over budget — the margin between
    // "tight" and "dead" is smaller than the noise, so the reserve has to clear both.
    public static int FeedVramReserveMb { get; private set; } = 512;

    // Off = trust maxResidentFeeds alone and never consult VRAM. Kept as an escape hatch:
    // if the cap ever misjudges and refuses a feed that would have been fine, the user can
    // switch the automatic half off without losing the manual ceiling.
    public static bool FeedVramGuard { get; private set; } = true;

    // ---- the per-feed off switch (phase F4) --------------------------------------
    //
    // `feedsDisabled = 2` or `feedsDisabled = 1,3` — ONE-BASED, matching the [RTCn] tags on
    // the panels rather than the internal ids, because the number the user types on a block
    // is the number they should be able to type here.
    //
    // WHY IT IS NOT feedCount. Lowering feedCount RE-ROUTES which panel each feed owns, so
    // it has to drag every feed through a quiesced rebuild (see FeedCount's comment). This
    // knob changes nothing about routing: feed n stays feed n, it simply stops being alive.
    // That makes it the only lever that reproduces what actually happens in game when a
    // panel is destroyed or switched off — one feed leaves, the others carry on — and it
    // does it repeatably, from outside the game, without grinding a block down.
    //
    // It is also the seam the connection framework will use later: "this feed has lost its
    // link" and "this feed is switched off" both want precisely this graceful stop.
    //
    // Stored as a MASK so the hot path (FeedGate.PollFeed, every feed every frame) is a bit
    // test rather than a string parse.
    private static int _disabledMask;

    public static bool IsFeedDisabled(int feedId) =>
        feedId >= 0 && feedId < 32 && (_disabledMask & (1 << feedId)) != 0;

    // READ feedCount BEFORE THE FIRST PANEL TICK, and nothing else.
    //
    // FeedCount defaults to 1 and only becomes the configured value on the first Poll, which
    // is a couple of seconds into a load. Every tagged panel that ticks inside that window
    // sees Feeds.Count == 1, and at one feed the router sends EVERY [RTCn] to feed 0 — by
    // design, that is what "asked for a feed that is not active" means. So on every load,
    // briefly, feed 1's panel is claimed and bound by feed 0. Feed 0's next teardown then
    // restores that panel to its stock material, taking away the binding feed 1 had correctly
    // made, and the screen shows stale or torn content. Observed 2026-08-01 as a corrupted
    // static image on the second feed's panel, and earlier the same day as the second panel
    // "mirroring" the first.
    //
    // Deliberately NOT the whole of Poll. Poll calls Feeds.UpdateResidentCap, which samples
    // VRAM through engine types, and this runs during plugin install — the exact situation
    // that once threw ConfigurationNotFoundException and permanently poisoned a type (see the
    // note in Feeds). This touches a file and an int, nothing else.
    internal static void PrimeFeedCount()
    {
        try
        {
            var kv = Read();
            FeedCount = Int(kv, "feedCount", FeedCount);
            ReadDisabledFeeds(kv);
            RttLog.Global($"Config primed at install: feedCount={FeedCount}. Read before the first " +
                          "panel tick so routing is correct from the first one — at the default of 1, " +
                          "every tagged panel would briefly claim feed 0.");
        }
        catch { }   // the full Poll is moments away and will report anything real
    }

    private static void ReadDisabledFeeds(Dictionary<string, string> kv)
    {
        int mask = 0;
        foreach (int oneBased in Ints(kv, "feedsDisabled", System.Array.Empty<int>()))
        {
            int id = oneBased - 1;
            if (id >= 0 && id < 32) mask |= 1 << id;
        }
        if (mask == _disabledMask) return;

        int was = _disabledMask;
        _disabledMask = mask;

        // LOUD, in both directions. A silently disabled feed is a black panel with every
        // counter reading healthy, which is the single most expensive failure shape this
        // project has produced — so the lever that can cause it announces itself.
        RttLog.Global($"Config: feedsDisabled {Describe(was)} -> {Describe(mask)}. " +
                      "A disabled feed goes DORMANT by the ordinary path — gate off, teardown 30 " +
                      "frames later, resources released — and the remaining feeds absorb its share " +
                      "of the render slot. No rebuild, and nothing else is disturbed.");
    }

    private static string Describe(int mask)
    {
        if (mask == 0) return "(none)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0) { if (sb.Length > 0) sb.Append(','); sb.Append(i + 1); }
        return sb.ToString();
    }

    // Last EFFECTIVE feed count seen by Poll, for detecting the re-route that has to take the
    // dormant path. Starts at -1 so the first poll can never read as a change (the first poll
    // rebuilds everything anyway, from a gate that has not started).
    private static int _lastCount = -1;
    private static int _lastCountLogged = 1;

    // Render our OWN sun-shadow cascades, around OUR camera.
    //
    // 0 = off: our DrawContextManager borrows the ENGINE'S DirectionalLightShadowResources
    //     read-only and stage 3 stays skipped. The feed samples cascades fitted around the
    //     PLAYER, so shadows degrade with camera distance and whole objects read as fully
    //     shadowed once the camera leaves that volume — the "ship goes dark at some orbit
    //     points" report.
    // 1 = own: keep our manager's own resources, flush OUR CascadeShadowsContext against
    //     OUR installed view, and run stage 3. Engine-adaptive update policy
    //     (CascadesUpdateCount per render, priority-sorted, forced when the camera shifts).
    // 2 = own + force every cascade every render. Costs more; removes any dependence on
    //     an update policy tuned for 60fps continuous motion rather than a 10fps orbit.
    //
    // The cascade textures already exist either way: CascadeShadowsContext's ctor calls
    // CheckShadowSettingChanged, which sees _cascades.Length == 0 != CascadesCount and
    // allocates the full set. We have been paying that VRAM since the second manager was
    // built and rendering nothing into it.
    public static int WholeSceneOwnShadows { get; private set; }

    // Show OUR render on the panel instead of the probe feed.
    //
    // The probe pass keeps running (it drives the orbit transform); it just stops
    // parking frames while this is on, and the whole-scene render parks its
    // FinalLDRTexture instead. Flipping this off restores the probe feed within one
    // panel tick — instant A/B comparison between the two pipelines.
    public static bool WholeSceneToPanel { get; private set; }

    // Minimum gap between second renders. Draw is a WHOLE FRAME: ungated at 53 fps this
    // would roughly halve the game's frame rate before teaching us anything, and a fault
    // would repeat 53 times a second while the log is being read.
    public static int WholeSceneIntervalMs { get; private set; } = 200;

    // Explicit resource barriers around the handover copy, one switch per end.
    //
    // Adding the destination barrier killed the game on the first copy, ~0.5 s in —
    // so the engine's AutoResourceState tracker evidently transitions the CopyResource
    // destination itself, and forcing CopyDest on top of that desynchronises it. The
    // source barrier has run for hundreds of copies without incident. Default off /
    // on respectively, and switchable so the pair can be bisected in one session
    // instead of one launch per hypothesis.
    public static bool SrcTransition { get; private set; } = true;

    public static bool DestTransition { get; private set; }

    // Replace the test pattern's persistent batch with an empty one once the feed
    // takes over, so DrawOne has nothing to draw before our copy lands.
    public static bool RetireTestPattern { get; private set; } = true;

    // The CopyResource itself. Off by default while the copy is under investigation:
    // three launches died on copy #1 into a target we created, where hundreds of
    // copies into the LCD system's own target ran clean. With this off the handover
    // describes both ends into copy-diag.txt and copies nothing, so the game survives
    // and the file can be read with the session still live. Set to 1 to re-enable.
    public static bool CopyEnabled { get; private set; }

    // Run the camera pass from the per-frame hook (DrawUnlit) instead of the probe
    // hook. The probe hook is inside the engine's own environment-probe work, which
    // is where borrowing the main view's post passes corrupted the player's render.
    // Off by default: the probe hook is the one with hours of proven runtime.
    public static bool PassOnFrameHook { get; private set; }

    // Swap SettingsManager._renderView to OUR camera for the pass. GBufferPassJob and
    // ExecuteLighting take no camera parameter, so without this they rasterise and light
    // from the player's viewpoint — the feed showed the inside of the ship.
    public static bool SwapCamera { get; private set; } = true;

    // The panel binds our feed as ColorMetalTexture: RGB is colour, ALPHA IS METALNESS.
    // Our HDR source has no alpha channel, so blitting all four channels wrote ~1.0
    // into metalness — a fully metallic panel, which has almost no diffuse response and
    // flattens the range regardless of exposure. Default: RGB only, slot pre-cleared so
    // alpha reads 0 (non-metal). Flip blitAlpha to 1 to reproduce the old behaviour.
    public static bool BlitAlpha { get; private set; }

    // 2. Stamp OUR render resolution into the camera CB's Screen.Resolution.
    //    CameraSettings -> TrackedCameraSettings copies ScreenBuffers.PreUpscaleResolution
    //    (the player's 3840x2160) while we rasterise 512x512, and shaders reconstruct view
    //    rays with rcp(Screen_.Resolution) — a 7.5x error on every one of them. That is
    //    the over-zoomed sky, and also wrong view vectors and specular in the geometry pass.
    public static bool FixScreenRes { get; private set; } = true;

    // 3. Build the camera CB from a real RenderView via
    //    CameraSettings.CreateNonjitteredCameraSettings instead of the RenderViewSlim
    //    conversion, which leaves ViewTransform, InvViewTransform, TanFOV, FOVScaleFactor
    //    and CameraFlags at zero — so shaders believe the camera is at the world origin —
    //    and stamps the PLAYER's position into MainViewCameraPos, which is what planet
    //    curvature and triplanar (voxel) texturing read.
    public static bool FullCameraCb { get; private set; } = true;

    // 4. LCD material EmissivityMultiplier. 0 leaves the base material's value alone
    //    (10 on LCDScreen_On). The panel's emissive term is added to the MAIN view's HDR
    //    buffer before its bloom and tonemap, so this is the display-side gain — the one
    //    axis that can put the feed above white. Live-tunable: change it and the material
    //    is updated in place, so a sweep costs a file save.
    //
    //    DEFAULT IS STOCK (10), and deliberately. It was 500 — a 50x gain from when the
    //    feed was dim and needed rescuing at the display end — and because these statics
    //    live in the collectible load context, EVERY hot reload reset it to 500 and
    //    ApplyEmissivity ran with that value before the first Poll(). So a config of 10
    //    still produced a 50x floodlight for the first frames after every reload. A
    //    default that overrides the config file until the config is read is a bug, not a
    //    convenience: this is a SHARED LCDMaterialDefinition, so it applies to every LCD
    //    panel in the world, and the panel's emissive term lands in the MAIN view's HDR
    //    buffer before bloom and tonemap.
    public static double Emissivity { get; private set; } = 10.0;

    // 5b. EnvironmentProbeSettings.DimDistance. Pass_Pixel_Indirect.hlsli multiplies all
    //     shaded output by clamp(ZDepth/DimDistance, 0, 1) SQUARED, and it ships as 5 m —
    //     so geometry 1 m from the camera is multiplied by 0.04. It exists so a probe does
    //     not contaminate itself with the hull it sits inside.
    //
    //     Unlike 5a this canNOT be a scoped swap: DimDistance reaches the shader through
    //     CommonResources.SettingsGroup.CreateFrameSettings, which builds the frame's
    //     GlobalSettings CB once in OnBeginDraw — long before our hook runs. It has to be
    //     set persistently, which also affects the engine's OWN probes (slightly brighter
    //     ambient for the player). -1 leaves it alone.
    //
    //     Note this does nothing at the default 100 m orbit — everything is already past
    //     5 m. It matters for a camera mounted close to structure.
    public static double DimDistance { get; private set; } = -1.0;

    // Orbit radius is a FLOOR, not a fixed distance: the effective radius is
    // max(orbitRadius, gridExtent * orbitClearance). Orbiting a fixed 100 m around a
    // ship whose half-diagonal is 80 m flies the camera through the hull, which is
    // what the feed was showing.
    public static double OrbitClearance { get; private set; } = 2.2;

    // Orbit the grid's centre (default) or the tagged panel itself. Panel-centred is
    // the close-up shot; grid-centred is the one that looks like a drone camera.
    public static bool OrbitGrid { get; private set; } = true;

    // ORBIT SOMEWHERE ELSE (phase J, 2026-08-01). Empty = today's behaviour, orbit the
    // panel's OWN grid.
    //
    // Set it to part of another grid's display name (case-insensitive substring) and the
    // orbit centre moves to that grid instead. This is the instrument goal 10 has been
    // blocked on: every render this project has ever done was ~100 m from the player, so
    // "nothing ever disappeared" was never evidence of anything. Point the camera at a grid
    // the player is NOWHERE near and the streaming/materialization question answers itself.
    //
    // Run `worldGridSurvey = 1` first — it writes output/world-grids.txt with every grid's
    // name, id, position and distance, which is where the string for this knob comes from.
    //
    // DELIBERATELY OUTSIDE THE WHOLE-SCENE REBUILD SIGNATURE: moving the orbit centre
    // allocates nothing and resizes nothing, so it is safe to edit on a running feed, the
    // same way orbitRadius and orbitPeriod are. Keep it that way — a signature knob edited
    // live is what removed the device at 15:08 on 2026-08-01.
    public static string OrbitAnchor { get; private set; } = "";

    // One-shot world inventory. Self-clearing: the dump runs once and the flag is forgotten
    // until the file is edited again, so leaving `= 1` in the file does not re-dump every
    // poll. Writes output/world-grids.txt.
    public static bool WorldGridSurvey { get; private set; }
    private static bool _surveyArmedLast;

    // MATERIALIZE ONE MANAGED AREA BY NAME (goal 10 / tier 2 pathfinder, 2026-08-01).
    // `loadArea = Vallis Reach` calls TryLoad() on the matching ManagedWorldArea — the
    // engine's own load path, the one its spatial trigger fires — so the content spawns
    // exactly as if a player had walked in. Edge-triggered and consumed once, same
    // discipline as worldGridSurvey: a name left in the file must not re-fire on every
    // poll, because TryLoad is a WORLD MUTATION, not a report.
    public static string LoadAreaRequest { get; private set; } = "";
    private static string _loadAreaLast = "";

    public static string TakeLoadAreaRequest()
    {
        var r = LoadAreaRequest;
        LoadAreaRequest = "";
        return r;
    }

    private static string _loadAreaMarkerPath;
    private static string LoadAreaMarkerPath =>
        _loadAreaMarkerPath ??= System.IO.Path.Combine(RttLog.OutDir, "loadarea-consumed.marker");

    private static string ReadLoadAreaMarker()
    {
        try { return System.IO.File.Exists(LoadAreaMarkerPath)
                  ? System.IO.File.ReadAllText(LoadAreaMarkerPath).Trim() : ""; }
        catch { return ""; }
    }

    private static void WriteLoadAreaMarker(string value)
    {
        try { System.IO.File.WriteAllText(LoadAreaMarkerPath, value); }
        catch { /* an unwritable marker means a re-fire after restart; log-worthy but not fatal */ }
    }

    // Consumed-and-cleared by the surveyor, so the "run once" decision lives in ONE place
    // rather than being re-derived by every caller. Returns true at most once per edit.
    public static bool TakeWorldGridSurveyRequest()
    {
        if (!WorldGridSurvey) return false;
        WorldGridSurvey = false;
        return true;
    }

    public static double OrbitRadius { get; private set; } = 100.0;

    public static double OrbitPeriod { get; private set; } = 30.0;

    public static double OrbitHeight { get; private set; } = 15.0;


    // ---------------------------------------------------------------- polling

    public static void Poll()
    {
        var now = System.Environment.TickCount64;
        if (now - _lastRead < 2000) return;
        _lastRead = now;

        try
        {
            if (!File.Exists(Path_))
            {
                // Write the defaults out once so the knobs are discoverable rather than
                // something you have to read the source to find.
                File.WriteAllText(Path_,
                    "# RTT camera feed — edit and save; picked up within ~2s.\n" +
                    $"intervalMs   = {IntervalMs}\n" +
                    $"orbitRadius  = {OrbitRadius}\n" +
                    $"orbitPeriod  = {OrbitPeriod}\n" +
                    $"orbitHeight  = {OrbitHeight}\n");
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(Path_).Ticks;
            if (stamp == _lastStamp) return;
            _lastStamp = stamp;

            var kv = Read();
            if (kv.Count == 0) return;      // unreadable or empty: keep the last good values

            IntervalMs     = Int(kv, "intervalMs", IntervalMs);
            PanelMs        = Int(kv, "panelMs", PanelMs);
            StartupDelayMs = Int(kv, "startupDelayMs", StartupDelayMs);
            PanelIdleMs    = Int(kv, "panelIdleMs", PanelIdleMs);
            PerfReportMs   = Int(kv, "perfReportMs", PerfReportMs);

            OrbitRadius    = Dbl(kv, "orbitRadius", OrbitRadius);
            OrbitPeriod    = Dbl(kv, "orbitPeriod", OrbitPeriod);
            OrbitHeight    = Dbl(kv, "orbitHeight", OrbitHeight);
            OrbitClearance = Dbl(kv, "orbitClearance", OrbitClearance);
            OrbitGrid      = Bool(kv, "orbitGrid", OrbitGrid);

            // ANCHOR CHANGES ARE ANNOUNCED. Moving the orbit to another grid changes what
            // the feed shows completely, so a silent pickup would look like a bug in the
            // camera rather than a config edit that took. Compare before assigning.
            var anchorWas = OrbitAnchor;
            OrbitAnchor = Str(kv, "orbitAnchor", "");
            if (!string.Equals(anchorWas, OrbitAnchor, StringComparison.Ordinal))
                RttLog.Global($"Config: orbitAnchor \"{anchorWas}\" -> \"{OrbitAnchor}\". " +
                    (OrbitAnchor.Length == 0
                        ? "Empty — the orbit returns to the panel's OWN grid."
                        : "The orbit will re-centre on the first grid whose display name contains " +
                          "this, once the world walk resolves it. Watch for \"ORBIT ANCHOR:\" in the log; " +
                          "if it does not appear, the name matched nothing and the feed stays on its own grid."));

            // Edge-triggered, not level-triggered: only a false->true transition arms it.
            // Level-triggered would re-dump on every config poll for as long as the line
            // said 1, which is this project's house bug wearing a survey hat.
            var surveyWanted = Bool(kv, "worldGridSurvey", false);
            if (surveyWanted && !_surveyArmedLast) WorldGridSurvey = true;
            _surveyArmedLast = surveyWanted;

            // DURABLE consume for the area loader, not the in-memory edge the survey uses.
            // The in-memory version re-fired after a game restart — fresh statics saw the
            // name sitting in the file as a new edge and pulled the same trigger during
            // world load, which was CTD #2 of 2026-08-01. A WORLD MUTATION request must
            // survive-compare across process lifetimes, so the last consumed value lives in
            // a marker file: same value = already done, no matter how many boots ago.
            var loadWanted = Str(kv, "loadArea", "");
            if (loadWanted.Length > 0
                && !string.Equals(loadWanted, _loadAreaLast, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(loadWanted, ReadLoadAreaMarker(), StringComparison.OrdinalIgnoreCase))
            {
                LoadAreaRequest = loadWanted;
                WriteLoadAreaMarker(loadWanted);
            }
            _loadAreaLast = loadWanted;

            CopyEnabled       = Bool(kv, "copyEnabled", CopyEnabled);
            SrcTransition     = Bool(kv, "srcTransition", SrcTransition);
            DestTransition    = Bool(kv, "destTransition", DestTransition);
            RetireTestPattern = Bool(kv, "retireTestPattern", RetireTestPattern);
            BlitAlpha         = Bool(kv, "blitAlpha", BlitAlpha);
            PassOnFrameHook   = Bool(kv, "passOnFrameHook", PassOnFrameHook);

            SwapCamera   = Bool(kv, "swapCamera", SwapCamera);
            SwapCameraCb = Bool(kv, "swapCameraCb", SwapCameraCb);
            FixScreenRes = Bool(kv, "fixScreenRes", FixScreenRes);
            FullCameraCb = Bool(kv, "fullCameraCb", FullCameraCb);

            Emissivity  = Dbl(kv, "emissivity", Emissivity);
            DimDistance = Dbl(kv, "dimDistance", DimDistance);

            // NOT read here. FeedCount is part of the whole-scene signature (see the field
            // comment: re-routing panels invalidates every feed's cached panel identity), so
            // it is read inside the signature block below, between the two snapshots.

            // DELIBERATELY OUTSIDE THE SIGNATURE WINDOW (phase F4). Disabling a feed must
            // take the ordinary dormancy path — that feed's gate goes dormant, its teardown
            // runs 30 frames later, its neighbours never stop rendering — and NOT the
            // quiesced rebuild a feedCount change triggers, which stops every feed in the
            // mod. The whole point of the lever is to exercise "one of N goes away while the
            // rest keep running", so it must not be able to take the rest with it.
            ReadDisabledFeeds(kv);

            // ---- the whole-scene route -------------------------------------------
            string before = WholeSceneSignature();

            FeedCount               = Int(kv, "feedCount", FeedCount);
            MaxResidentFeeds        = Int(kv, "maxResidentFeeds", MaxResidentFeeds);
            FeedVramReserveMb       = Int(kv, "feedVramReserveMb", FeedVramReserveMb);
            FeedVramGuard           = Bool(kv, "feedVramGuard", FeedVramGuard);

            // INSIDE the signature window, and after the three knobs above are read, so a
            // cap change is detected as a signature change and re-routes panels through the
            // same rebuild a feedCount change uses. Evaluating it before the `before`
            // snapshot would make a cap move invisible to the comparison.
            Feeds.UpdateResidentCap();
            WholeSceneEnabled       = Bool(kv, "wholeSceneRender", WholeSceneEnabled);
            WholeSceneBuildBuffers  = Bool(kv, "wholeSceneBuildBuffers", WholeSceneBuildBuffers);
            WholeSceneWidth         = Int(kv, "wholeSceneWidth", WholeSceneWidth);
            WholeSceneHeight        = Int(kv, "wholeSceneHeight", WholeSceneHeight);
            WholeSceneCamera        = Bool(kv, "wholeSceneCamera", WholeSceneCamera);
            WholeSceneCameraRebuild = Int(kv, "wholeSceneCameraRebuild", WholeSceneCameraRebuild);
            WholeSceneToPanel       = Bool(kv, "wholeSceneToPanel", WholeSceneToPanel);
            WholeSceneIntervalMs    = Int(kv, "wholeSceneIntervalMs", WholeSceneIntervalMs);
            WholeSceneAAMode        = Int(kv, "wholeSceneAAMode", WholeSceneAAMode);
            WholeSceneExposure      = Dbl(kv, "wholeSceneExposure", WholeSceneExposure);
            // Auto aperture — read INSIDE the signature window is harmless because none of
            // these are put INTO the signature (see WholeSceneSignature). Tuning is free.
            FeedAutoExposure         = Bool(kv, "feedAutoExposure", FeedAutoExposure);
            FeedExposureDay          = Dbl(kv, "feedExposureDay", FeedExposureDay);
            FeedExposureNight        = Dbl(kv, "feedExposureNight", FeedExposureNight);
            FeedExposureDawnDot      = Dbl(kv, "feedExposureDawnDot", FeedExposureDawnDot);
            FeedExposureDayDot       = Dbl(kv, "feedExposureDayDot", FeedExposureDayDot);
            FeedExposureAdaptSeconds = Dbl(kv, "feedExposureAdaptSeconds", FeedExposureAdaptSeconds);
            FeedSunSign              = Dbl(kv, "feedSunSign", FeedSunSign);
            WholeSceneNativeScaling = Bool(kv, "wholeSceneNativeScaling", WholeSceneNativeScaling);
            WholeSceneNoBloom       = Bool(kv, "wholeSceneNoBloom", WholeSceneNoBloom);
            WholeSceneLdrResize     = Bool(kv, "wholeSceneLdrResize", WholeSceneLdrResize);
            WholeSceneSubmitEarly   = Bool(kv, "wholeSceneSubmitEarly", WholeSceneSubmitEarly);
            WholeSceneFarClip       = Dbl(kv, "wholeSceneFarClip", WholeSceneFarClip);
            WholeSceneFarClipExtend = Bool(kv, "wholeSceneFarClipExtend", WholeSceneFarClipExtend);
            WholeSceneOwnDrawContexts   = Bool(kv, "wholeSceneOwnDrawContexts", WholeSceneOwnDrawContexts);
            WholeSceneOwnShadows        = Int(kv, "wholeSceneOwnShadows", WholeSceneOwnShadows);
            WholeSceneCascadeResolution = Int(kv, "wholeSceneCascadeResolution", WholeSceneCascadeResolution);
            WholeSceneCascadeCount      = Int(kv, "wholeSceneCascadeCount", WholeSceneCascadeCount);
            WholeSceneCharacterShadowResolution =
                Int(kv, "wholeSceneCharacterShadowResolution", WholeSceneCharacterShadowResolution);
            WholeScenePlanetEnv         = Bool(kv, "wholeScenePlanetEnv", WholeScenePlanetEnv);
            WholeSceneOwnFlares         = Bool(kv, "wholeSceneOwnFlares", WholeSceneOwnFlares);
            PanelFsrMask                = Bool(kv, "panelFsrMask", PanelFsrMask);
            PanelMipRegen               = Bool(kv, "panelMipRegen", PanelMipRegen);
            StatsPanel                  = Bool(kv, "statsPanel", StatsPanel);
            StatsPanelMs                = Int(kv, "statsPanelMs", StatsPanelMs);
            RttBudgetMs                 = Dbl(kv, "rttBudgetMs", RttBudgetMs);
            WholeSceneFlareIntensity    = Dbl(kv, "wholeSceneFlareIntensity", WholeSceneFlareIntensity);
            WholeSceneOwnProbes         = Bool(kv, "wholeSceneOwnProbes", WholeSceneOwnProbes);
            WholeSceneDisableRaytracing    = Int(kv, "wholeSceneDisableRaytracing", WholeSceneDisableRaytracing);
            WholeSceneDisableEyeAdaptation = Bool(kv, "wholeSceneDisableEyeAdaptation", WholeSceneDisableEyeAdaptation);
            WholeSceneDisableProbeUpdates  = Bool(kv, "wholeSceneDisableProbeUpdates", WholeSceneDisableProbeUpdates);
            WholeSceneSkipStages    = Ints(kv, "wholeSceneSkipStages", WholeSceneSkipStages);
            WholeSceneRtFlags       = Strs(kv, "wholeSceneRtFlags", WholeSceneRtFlags);

            // A resolution or buffer-mode change needs a full rebuild of the second
            // ScreenBuffers and DrawContexts; anything else just needs the one-strike
            // disable cleared, so an experiment is a config save rather than a rebuild.
            //
            // The signature covers every value the rebuild depends on. Comparing one
            // string rather than thirty fields is what stops a newly-added knob silently
            // missing the comparison — the failure mode of the chain this replaced, where
            // editing the config appeared to do nothing at all.
            // SWEPT ACROSS EVERY FEED (phase C3 prerequisite). Poll() runs under whichever
            // feed holds the render slot at that instant, so a bare Reset() would rebuild
            // exactly one of them and silently leave the rest at the old resolution. Quality
            // is GLOBAL by design — the signature describes buffer identity, which every
            // feed shares — so the rebuild is global too. At Count == 1 this is identical to
            // what it replaced. (Reset defers itself when called from inside a render; the
            // drain in RunSecondRender's finally sweeps all feeds the same way.)
            string after = WholeSceneSignature();
            int countAfter = Feeds.Count;

            // A COUNT CHANGE TAKES THE DORMANT PATH. Everything else rebuilds in place.
            //
            // CTD 2026-07-30 20:54, on the cleanest baseline this project has had (52.4 fps,
            // >50ms=0, 1.5 GB of VRAM headroom): feedCount was flipped 1 -> 2 with feed 0
            // live, and the game device-removed 2.5 s later with a NULL BIND inside culling —
            // PageFaultVA 0x0, ExistingAllocations 0, RecentFreedAllocations 0, and 1.6 GB
            // still free. Not memory.
            //
            //     20:53:58.915  [feed 0] Whole-scene Reset: 12511 -> 12385 MB
            //     20:54:00.425  [feed 0] SECOND ScreenBuffers built, secondRenders=0
            //                   <<< DEVICE_REMOVED, no DC build, no settle line, no ERROR >>>
            //
            // The exact null resource was never identified, and this fix does not depend on
            // identifying it. What IS established is the window: a count change re-routes
            // every panel and rebuilds every feed AT ONCE, underneath a render that is still
            // running. This project now has CTDs from that same window on three separate
            // knobs — wholeSceneAAMode (4), wholeSceneOwnProbes (3), and now feedCount — and
            // both of the other two were ultimately resolved by refusing to change them under
            // a live render rather than by making the change safe.
            //
            // The dormant path is already proven: the relaunch after this crash came up with
            // feedCount=2 and built both feeds from a dormant gate with no trouble, and every
            // pause-protocol deploy all evening did the same. So instead of asking the
            // operator to remember the pause protocol for this knob, take it automatically.
            //
            // Deliberately scoped to COUNT changes only. Resolution and layer changes have
            // rebuilt in place many times without incident, and widening this would be
            // changing two things at once on the most crash-prone path in the codebase.
            bool countChanged = !_firstPoll && countAfter != _lastCount;
            _lastCount = countAfter;

            if (countChanged)
            {
                RttLog.Line($"Config: feed count {_lastCountLogged} -> {countAfter}. Re-routing panels " +
                            "requires rebuilding EVERY feed, so the gate goes dormant first and rebuilds " +
                            "from a quiesced renderer — changing this under a live render device-removed " +
                            "the game on 2026-07-30. The feed returns on its own in about a second.");
                _lastCountLogged = countAfter;
                FeedGate.RequestQuiescedRebuild();
                // No Reset here on purpose: the gate's Shutdown already calls
                // WholeSceneRender.Reset per feed, on the render thread, with nothing in
                // flight. Doing it here as well would be the very in-place teardown this is
                // avoiding.
            }
            // Reset RELEASES, so it sweeps EVERY slot — shrinking feedCount (or the VRAM cap
            // clamping it) is precisely the case where a slot leaves Count while still owning
            // a ScreenBuffers and a DrawContextManager, and a Count-bounded sweep would skip
            // it at the one moment it needed freeing. Rearm only re-arms latches on feeds
            // that will run, so it stays bounded by Count.
            else if (_firstPoll || before != after) Feeds.ForEachSlot(RttProbe.WholeSceneRender.Reset);
            else Feeds.ForEach(RttProbe.WholeSceneRender.Rearm);
            _firstPoll = false;

            RttLog.Line($"Config: intervalMs={IntervalMs} (~{1000.0 / Math.Max(1, IntervalMs):F0} fps) " +
                        $"orbit radius>={OrbitRadius} clearance={OrbitClearance}x grid={OrbitGrid} " +
                        $"period={OrbitPeriod}s height={OrbitHeight} " +
                        $"| emissivity={Emissivity} dimDistance={DimDistance} " +
                        $"| whole-scene: render={WholeSceneEnabled} {WholeSceneWidth}x{WholeSceneHeight} " +
                        $"@{WholeSceneIntervalMs}ms camera={WholeSceneCamera} rebuild={WholeSceneCameraRebuild} " +
                        $"ownDc={WholeSceneOwnDrawContexts} ownShadows={WholeSceneOwnShadows} " +
                        $"({WholeSceneCascadeCount}x{WholeSceneCascadeResolution}) planetEnv={WholeScenePlanetEnv} " +
                        $"aa={WholeSceneAAMode} skip=[{string.Join(",", WholeSceneSkipStages)}] " +
                        $"rtFlags=[{string.Join(",", WholeSceneRtFlags)}]");
        }
        catch { /* keep the last good values */ }
    }

    // Everything the second ScreenBuffers / DrawContexts build depends on.
    private static string WholeSceneSignature() =>
        string.Join("|", WholeSceneBuildBuffers, WholeSceneWidth, WholeSceneHeight,
                         // THE EFFECTIVE COUNT is here, not the raw feedCount. Changing it
                         // re-routes which panel each feed owns, and every feed caches its
                         // panel's identity (resolved id, offscreen target, handle text,
                         // render target). Without a rebuild those caches survive the
                         // re-route and each feed keeps matching another panel's handle —
                         // which is a silent frozen picture, not an error. See the FeedCount
                         // field comment for the observed failure.
                         //
                         // Feeds.Count rather than FeedCount so the phase-E1 VRAM cap gets
                         // the identical treatment: a cap that clamps 2 feeds to 1 is the
                         // same re-route as a user editing feedCount from 2 to 1, and it
                         // would leave the same stale caches behind if it were invisible here.
                         Feeds.Count,
                         // WholeSceneIntervalMs is deliberately ABSENT (plan phase A2).
                         // Its only consumer is TryRender's rate gate, which reads
                         // FeedConfig fresh every frame, so a change needs no rebuild:
                         // nothing about buffer or context IDENTITY depends on the render
                         // period. Leaving it in the signature meant every cadence tweak
                         // cost a full gate cycle plus a 30-frame settle — and cadence is
                         // exactly what the phase-E slot scheduler will be tuning, so it
                         // has to be free to change. Class (a), verified by consumer.
                         WholeSceneCamera, WholeSceneToPanel,
                         WholeSceneCameraRebuild, WholeSceneAAMode, WholeSceneExposure,
                         WholeSceneNativeScaling, WholeSceneNoBloom, WholeSceneLdrResize, WholeSceneDisableRaytracing,
                         WholeSceneDisableEyeAdaptation, WholeSceneDisableProbeUpdates,
                         WholeSceneOwnDrawContexts, WholeSceneOwnShadows,
                         WholeSceneCascadeResolution, WholeSceneCascadeCount,
                         // WholeSceneOwnProbes is deliberately ABSENT — see below.
                         WholeScenePlanetEnv, WholeSceneOwnFlares,
                         string.Join(",", WholeSceneSkipStages),
                         string.Join(",", WholeSceneRtFlags));

    // ---------------------------------------------------------------- parsing

    private static Dictionary<string, string> Read()
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var raw in File.ReadAllLines(Path_))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1);

                // Strip a TRAILING comment. Without this, `orbitGrid = 1  # orbit the
                // grid` parses as the literal "1  # orbit the grid", every TryParse
                // fails, and the knob silently reverts to its default — which is not a
                // hypothetical: documenting the file inline took the whole-scene route
                // down for twenty minutes, and the only visible symptom was a config log
                // line reading grid=False.
                int hash = val.IndexOf('#');
                if (hash >= 0) val = val.Substring(0, hash);

                kv[key] = val.Trim();
            }
        }
        catch { }
        return kv;
    }

    // Trimmed, never null. An absent key and an empty value mean the same thing to every
    // caller here ("not set"), so they are collapsed rather than distinguished.
    private static string Str(Dictionary<string, string> kv, string key, string fallback) =>
        kv.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    private static int Int(Dictionary<string, string> kv, string key, int fallback) =>
        kv.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var r) ? r : fallback;

    private static double Dbl(Dictionary<string, string> kv, string key, double fallback) =>
        kv.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var r) ? r : fallback;

    // Accepts 1/0, true/false, yes/no — the file has been written all three ways.
    private static bool Bool(Dictionary<string, string> kv, string key, bool fallback)
    {
        if (!kv.TryGetValue(key, out var v)) return fallback;
        v = v.Trim();
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                     || v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                     || v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }

    // An empty value is a legitimate EMPTY LIST, not "missing". `wholeSceneRtFlags =`
    // with nothing after it is how the RT scoping was turned off, and treating that as
    // absent would silently restore the previous flags.
    private static int[] Ints(Dictionary<string, string> kv, string key, int[] fallback)
    {
        if (!kv.TryGetValue(key, out var v)) return fallback;
        var outv = new List<int>();
        foreach (var tok in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(tok.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                outv.Add(n);
        return outv.ToArray();
    }

    private static string[] Strs(Dictionary<string, string> kv, string key, string[] fallback)
    {
        if (!kv.TryGetValue(key, out var v)) return fallback;
        var outv = new List<string>();
        foreach (var tok in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = tok.Trim();
            if (t.Length > 0) outv.Add(t);
        }
        return outv.ToArray();
    }
}
