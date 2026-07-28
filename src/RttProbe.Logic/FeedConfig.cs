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
// seconds. Tuning frame rate by editing a constant and rebuilding wastes a
// hot-reload cycle per experiment; this makes it a file edit.
//
//   intervalMs = 66      camera pass period (66 ~= 15 fps, 33 ~= 30 fps)
//   orbitRadius = 100    metres from the panel
//   orbitPeriod = 30     seconds per revolution
//   orbitHeight = 15     metres above the panel
//
// Anything missing or malformed falls back to the default, so a half-written file
// cannot break the feed.
internal static class FeedConfig
{
    private static readonly string Path_ = System.IO.Path.Combine(RttLog.OutDir, "feed-config.txt");

    private static long _lastRead;
    private static long _lastStamp;

    public static int IntervalMs { get; private set; } = 66;      // ~15 fps

    // Panel update period, deliberately separate from the camera pass. RequestRender
    // makes the ENGINE run a full DrawOne (borrow, clear, replay UI batches, mipmap,
    // copy, return) for our target — so the two rates cost very different things and
    // must be testable independently. 0 = follow IntervalMs.
    public static int PanelMs { get; private set; }
    public static int EffectivePanelMs => PanelMs > 0 ? PanelMs : IntervalMs;

    // Grace period after the logic loads before any GPU work is issued. Several
    // crashes were "on world load", when the renderer is still settling — pooled
    // targets resizing, panels acquiring their render targets, streaming catching up.
    // Starting into that is asking for trouble.
    public static int StartupDelayMs { get; private set; } = 2000;

    // Phase 1 borrows from DrawContextManager.BorrowShadowCulling — the pool the
    // engine uses for SHADOW cascades. Switchable so "is the pool the problem" can be
    // answered by a file edit rather than a rebuild.
    public static bool UsePooledCulling { get; private set; } = true;

    // rootEntityId handed to BorrowShadowCulling.
    //
    // This was guessed to be the POOL KEY, and it is not. The IL is unambiguous:
    //
    //     TryPop(_unusedShadowCulling)          <- a plain LIFO free-list, keyed by nothing
    //     if empty: new CullingContext(...)
    //     context.RootEntityId = rootEntityId   <- SET ON the context, not looked up by it
    //
    // So the pool already hands out a context nobody else holds, and passing a distinct
    // id bought nothing — which is why the flicker survived it. Worse, RootEntityId is a
    // real culling parameter: GeometryContext.UpdateRanges is its one consumer, so a
    // bogus entity id is a live suspect for geometry going missing.
    //
    // -1 is what the engine's own probe and cascade paths pass, and is the default again.
    public static int CullRootEntityId { get; private set; } = -1;

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

    // AtmosphereMultiplyJob.DoWork(cl, rtView) — the extinction half of atmosphere.
    //
    // IndirectPlanetEnvironmentJob, which we already run, is BlendState.Additive: it adds
    // in-scatter and nothing else, so geometry never receives aerial-perspective
    // extinction and there is no atmospheric sun disc. This is that half.
    //
    // Needs swapCameraCb (it reads the global camera CB) and our own render's depth,
    // which the pass installs and restores around the single call.
    public static bool AtmosphereMultiply { get; private set; }

    // Draw the sun as a LAYER on top of the sky, rather than as a sky MODE.
    //
    // skyMode is a choice, so setting it to 2 replaced IndirectPlanetEnvironmentJob —
    // the pass supplying the starfield and the planetary atmosphere — which is why the
    // sun arrived and both of those left. DrawSkybox WRITES SkyLight; the planet pass is
    // BlendState.Additive. So the sun goes down first and the atmosphere adds on top.
    //
    // REQUIRES gbufferSwap: the shader writes motion vectors to SV_Target1 =
    // ScreenBuffers.GBuffer[Motion], and without the swap that is the PLAYER'S FSR input.
    // The pass refuses to run rather than corrupt their view.
    //
    // Note the starfield ends up drawn twice — once here, once additively by the planet
    // pass, which ignores HideSkybox by design ("environment probe use skybox always").
    // Stars will read brighter than in the main view.
    public static bool SunPass { get; private set; }

    // Cull with _mainViewCullingJob instead of _indirectCullingJob.
    //
    // THE blocker for the deferred route. CullingJob is constructed with a list of target
    // PASS GROUPS; the indirect job targets one (Indirect), the main-view job targets four
    // including MainViewPass and MainViewDeferredTexturingPass. GBufferPassJob and
    // DeferredTexturingJob draw those groups, so culling with the indirect job leaves them
    // nothing to draw and the GBuffer comes out empty.
    //
    // It is also why voxel texturing could never improve: the Indirect group draws terrain
    // through TriplanarGIGlobal, a 52-line shader that samples NO textures — just
    // GetColorFar3(), one flat colour per material. The real 172-line triplanar shader
    // lives in the MainView groups.
    //
    // Not free: this instance is built with isUsedWithTwoPassCulling and isForMainView, so
    // it may want an OcclusionContext (we pass null) and a second culling pass.
    public static bool MainViewCulling { get; private set; }

    // Run CullingJob.DoCullingSecondPass after the first.
    //
    // The main-view job is built with isUsedWithTwoPassCulling, and the engine calls
    // MainViewCulling(cl, isFirstPass) TWICE per frame: the first pass culls coarsely and
    // primes occlusion, the second generates the final draw commands. Running only the
    // first yields a partial command set — which is what a single tiny fragment in the
    // GBuffer looks like.
    //
    // Only applies when mainViewCulling is on; the indirect job is single-pass.
    public static bool CullSecondPass { get; private set; } = true;

    // Build our OWN visibility-list and occlusion contexts instead of borrowing the
    // player's. Construct-only at first: it reports whether they build and does not wire
    // them into culling, because the last shared-context attempt corrupted the player's
    // view rather than merely failing.
    public static bool PrivateCullContexts { get; private set; } = true;

    // Use our OWN OutputGeometryBufferContext for the cull and the draws.
    //
    // DrawContexts.MainOutputGeometryBuffers has eighteen engine readers, and Borrow() on
    // it is only an _isBorrowed mutex flag handing back the same physical buffers — so our
    // draw commands land in the buffers the player's frame executes. Survivable with the
    // indirect culling job, which writes a group the engine re-culls anyway; it corrupted
    // the player's view outright with the main-view job.
    //
    // Flipping this ALONE should produce no visible change whatsoever. That is the point:
    // it proves the private-context mechanism before the mechanism is asked to carry a
    // feature. SurfelGenerationJob constructs its own for exactly this reason.
    public static bool PrivateGeomBuffers { get; private set; }

    // Run the main-view culling job ALONGSIDE the indirect one, into a second context.
    //
    // mainViewCulling swaps the job, and that can never work. The pass-group lists are
    // disjoint where it matters:
    //
    //     _indirectCullingJob  -> [12]           Indirect
    //     _mainViewCullingJob  -> [0, 2, 3, 4]   MainViewPass, DeferredTexturing,
    //                                            Count/GatherVolumeSegments
    //
    // The visible image is drawn by IndirectEnvironmentPassJob from the Indirect group,
    // so swapping does not break the feed — it deletes the draw commands the feed is made
    // of. That is the "black with a small blob in the centre" this route produced twice,
    // and it was never a bug: it is what the configuration asks for.
    //
    // Co-culling gets both. One OutputGeometryBufferContext can carry the output of
    // several culls into different pass groups — the engine does exactly that, culling
    // the probe's Indirect group into MainOutputGeometryBuffers, the same context the
    // main view writes MainViewPass into. A second CullingContext is still needed because
    // GeometryContext is where the per-group ranges live and it is what GBufferPassJob is
    // handed.
    //
    // Requires privateCullContexts (the main-view job dereferences the visibility and
    // occlusion contexts) and is ignored when mainViewCulling is on, since that already
    // put the main-view job in the primary slot.
    public static bool CoCullMainView { get; private set; }

    // Which pass groups the co-cull targets. Comma-separated PassGroupType values.
    //
    //   0  MainViewPass                   GBufferPassJob draws this
    //   2  MainViewDeferredTexturingPass  DeferredTexturingJob draws this
    //   3  MainViewCountVolumeSegments    volumetric, phase 1
    //   4  MainViewGatherVolumeSegments   volumetric, phase 2
    //
    // The engine's own main-view job takes all four, and copying that wholesale put large
    // white wedges through the feed at changing angles. 3 and 4 are a two-phase volumetric
    // algorithm whose consuming passes we never run, so their commands sit in the volume
    // instance buffers uninterpreted — and uninterpreted draw commands rasterise as
    // stretched garbage. Default is the two we actually consume.
    public static int[] CoCullPassGroups { get; private set; } = { 0, 2 };

    // The custom culling job's three remaining ctor flags.
    //
    // The first build mirrored the INDIRECT job on all of them, on the reasoning that it
    // is the configuration proven not to disturb the player. That was the wrong instinct:
    // the indirect job's flags go with the Indirect pass group, and we are emitting
    // MainViewPass. The GBuffer came out as large flat polygons radiating from a point —
    // the signature of garbage instance transforms, i.e. a malformed draw-command layout.
    //
    // isForMainView in particular is handed to DrawCommandsGenerationJob, which is what
    // generates the indirect draw arguments. Emitting main-view commands with it false is
    // a very plausible way to get the wrong layout.
    //
    // These now default to MIRRORING THE MAIN-VIEW JOB, because forcedLODMethod is the
    // only flag we have an actual reason to deviate on (it is what keeps our cull out of
    // the player's LOD crossfade state). Both of its old hazards are already handled:
    // the LOD transition global is nulled for our pass, and the two-pass prologue's
    // Assert.NotNull is gated on CullingSetup.IsOcclusionCullingAllowed, which is false
    // because we disable HZBO.
    public static bool CoCullForMainView { get; private set; } = true;
    public static bool CoCullTwoPass { get; private set; } = true;
    public static bool CoCullGeometryOnly { get; private set; }

    // The one deliberate deviation from the main-view job. Off would restore the
    // player's-LOD-state corruption, so this exists to CONFIRM that diagnosis on demand,
    // not as a tuning knob.
    public static bool CoCullForceSingleLod { get; private set; } = true;

    // Clear our GBuffer and depth immediately before the GBuffer pass.
    //
    // GBufferPassJob's own clearRenderTargets parameter is gated on
    // GlobalDebugSettings.EnabledDebugDraw, so passing true has never cleared anything.
    // Our GBuffer array is held for the session, so it had been accumulating since launch.
    //
    // Depth is the bigger half: gbufferAfterEnv runs the environment pass first (the
    // GBuffer pass takes no camera and inherits whatever is bound), and the env pass fills
    // depth with the whole scene. The GBuffer pass then draws the same scene through
    // different pass groups and every fragment fails the depth test against geometry
    // already at that depth.
    //
    // Off restores the old behaviour if something later in the pass turns out to need the
    // env pass's depth.
    public static bool ClearGBufferBeforePass { get; private set; } = true;

    // ---------------------------------------------------------------- whole-scene route
    //
    // Drive SceneDrawSystem.Draw a second time from our camera into our own target,
    // instead of reassembling the main pipeline pass by pass. See WholeSceneRender and
    // docs/second-view-hunt.md.
    //
    // Staged deliberately. Every one of these defaults OFF, and each is meant to be
    // proven on its own before the next is switched on — the deferred route's failures
    // were unattributable precisely because three things moved at once.

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

    // Stop rendering the probe scene once the whole-scene route feeds the panel.
    //
    // The probe pipeline was the original route and is now scaffolding: it still runs a
    // full cull, GBuffer, deferred texturing, environment and lighting pass at 30 Hz,
    // then an exposure and tonemap chain — and CopyToFeed discards all of it in favour of
    // the whole-scene image. The only thing the second render actually needs from that
    // pass is the ORBIT TRANSFORM, which is CPU-side and computed before any GPU work.
    //
    // Self-disabling: the strip only applies while WholeSceneRender.PanelSource is
    // non-null, so the probe render comes straight back if the route is switched off or
    // errors out.
    public static bool WholeSceneStripProbe { get; private set; }

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

    // Extra margin on top of the sizing basis. ZERO by default, and deliberately.
    //
    // The first guess here was 50%, on the theory that the report describes a cull from
    // the player's viewpoint while ours runs from another and might need more. Measurement
    // killed that theory: the report sits at exactly 325508 singles across every sample,
    // unchanged while the player's camera moves. It is a SCENE TOTAL, not a frustum count
    // — which is the entire purpose of CullCapacityTrackingManager, sizing for the worst
    // case rather than the current view. So our viewpoint cannot exceed it.
    //
    // And the margin is already there twice over: we size from max(reported, the engine's
    // live capacity), and the engine's capacity carries its own slack from growing in
    // 1.2x steps — 358400 held against 325508 needed. On top of that
    // DrawInstanceBuffers.EnsureCapacity runs CalculateCapacity, which is
    // ceil(max(needed, Capacity * 1.2) / 1024) * 1024. Adding 50% here would have
    // multiplied a ~180 MB allocation by another half for no reason.
    //
    // Left configurable because it is cheap insurance if a scene ever proves the
    // scene-total reading wrong.
    public static double GeomRangeHeadroom { get; private set; }

    // Absolute minimum capacity per category, regardless of what was reported.
    //
    // Guards the first pass and any frame where the readback has not caught up. Small
    // enough to be free — these are per-draw-command structures, not per-pixel.
    public static int GeomRangeFloor { get; private set; } = 1024;

    // Disable occlusion culling for the duration of our pass.
    //
    // The engine sequences main-view culling as first pass -> depth prepass -> build HiZ ->
    // second pass. We run both passes back to back with no HiZ rebuild, so the second one
    // tests visibility against the PLAYER'S depth pyramid and rejects geometry our camera
    // can plainly see. The tell was one correct frame followed by near-total culling.
    //
    // CullingSetup.IsOcclusionCullingAllowed reads SettingsManager.HZBO live, so this is a
    // scoped set/restore. Costs some overdraw, which at 512x512 is nearly free.
    public static bool DisableOcclusionCulling { get; private set; } = true;

    // Auto-exposure for the feed, from our own camera. See DynamicExposure.
    //
    // The engine's ComputeExposure is off-limits — it advances the temporal adaptation the
    // MAIN view depends on, and driving it a second time corrupted the player's render.
    // This is our own, and it never touches engine state.
    public static bool AutoExposure { get; private set; } = true;

    // Speeds are per-second exponential rates, applied in LOG space so a stop takes the
    // same time whether the scene is bright or dark. Darkening fast and brightening slowly
    // is what real eye adaptation does; the reverse reads as a fault.
    public static double AutoExposureDownSpeed { get; private set; } = 3.0;
    public static double AutoExposureUpSpeed { get; private set; } = 0.8;

    // How many STOPS the sun angle swings the exposure. The dominant variation in a space
    // scene: looking down-sun the subject is fully lit, backlit it is a silhouette against
    // bright space. Negate this if the feed brightens where it should darken — SunSettings
    // .Normal has no documented sign convention and the log reports the raw value.
    public static double AutoExposureSunRange { get; private set; } = 1.5;

    // Bounds and a manual trim, all LINEAR multipliers.
    public static double AutoExposureMin { get; private set; } = 0.02;
    public static double AutoExposureMax { get; private set; } = 4.0;
    public static double AutoExposureBias { get; private set; }

    // DeferredTexturingJob — the pass pair (PassType 23/24) that textures voxel surfaces.
    //
    // The material states the main view uses for terrain all declare DeferredTexturing;
    // TriplanarGIGlobal, which our indirect path draws through, does not — it calls itself
    // "Low quality master terrain material used for GI. Uses Far3 colors." This is what
    // turns a coarse Far3-coloured surface into a textured one.
    //
    // Needs gbufferSwap + gbufferPass: it reads the GBuffer that GBufferPassJob writes.
    public static bool DeferredTexturing { get; private set; }

    // Put one of OUR GBuffer slots on the panel instead of the rendered image.
    //
    // 0 = off. 1=BaseColor/Emissivity, 2=Normal, 3=Metalness/Roughness/AO, 4=Parallax,
    // 5=MotionVectors. Needs gbufferSwap + gbufferPass.
    //
    // "GBufferPassJob ran without throwing" has never meant "wrote correct pixels" here —
    // the range-culling bug completed cleanly and silently dropped most of its output.
    // The whole deferred path stands on this buffer, so it is worth one switch to SEE it.
    public static int DebugGBuffer { get; private set; }

    // Hand the sun/skybox pass OUR depth instead of the engine's.
    //
    // Correct in principle — the sky must fill only where geometry did not draw — but
    // it produced PATCHES of sky rather than a full one, which is partial depth/stencil
    // rejection. Nothing writes our stencil unless GBufferPassJob runs, and that is gated
    // on gbufferPass. Off until the stencil is actually populated.
    public static bool SunPassDepth { get; private set; }

    // Recompute the real bloom on one pass in N and reuse it in between.
    //
    // ApplyBloom is a multi-pass downsample/upsample chain — it is what halved the
    // frame rate when bloom was first tried. But bloom is inherently LOW FREQUENCY, a
    // blurred copy of the bright parts, so recomputing it 28 times a second buys almost
    // nothing on a camera that orbits once every 30 seconds. This turns its cost into
    // cost/N for a result that is at most N frames stale.
    //
    // 1 = every pass (full cost, the original behaviour). Needs bloom = 1 and
    // cheapBloom = 0, since the cheap stand-in short-circuits before this.
    public static int BloomEveryN { get; private set; } = 1;

    // Call CullingContext.UpdateRanges on the borrowed context each pass.
    //
    // A context from BorrowShadowCulling is ranged for RangeStats.Default — ONE draw per
    // category — until something grows it, and the engine only grows the contexts named
    // by its own pending-work queues (CascadesToUpdate, CharacterCascadesToUpdate,
    // EnvProbesToUpdate, LocalLightsToUpdate). Nothing names ours.
    //
    // That is why geometry went missing the moment we stopped sharing EnvProbeCulling[0],
    // which the engine ranges every frame for a full probe scene cull. Default ON: the
    // alternative is a knowingly under-ranged context. 0 reproduces the broken behaviour.
    public static bool RangeCulling { get; private set; } = true;

    // Which OutputGeometryBufferContext the pass writes its draw commands into.
    //
    // Borrow() on one of these is an `_isBorrowed` mutex flag, not an allocation — the
    // same physical draw-command buffers come back every time. MainOutputGeometryBuffers
    // is read by EIGHTEEN engine methods across the frame; MainOutputEffectGeometryBuffers
    // by one (RenderHighlightsAndTransparentUnlit).
    //
    // This is the other half of the flash/flicker pair. Sharing BOTH the culling context
    // and this buffer was self-consistent — the pass simply drew the engine's probe-view
    // data, which is the single-frame flash from the player's position. Taking a private
    // culling context fixed the viewpoint but split the pair: our culling results in our
    // context, our draw commands in a buffer the engine rewrites around us.
    public static bool EffectGeomBuffers { get; private set; }

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

    // ---- fidelity layers, each independently switchable ----
    // Live knobs, not load-time markers: the tonemap marker was read once during the
    // dry run, so creating it mid-session did nothing and looked like the feature was
    // broken. Every layer here can be toggled while the game runs, so a layer that
    // kills the render names itself in one edit rather than one launch.
    public static bool Tonemap { get; private set; }   // exposure + tone response
    public static bool Bloom { get; private set; } = true;   // real ApplyBloom, amortised by BloomEveryN
    public static bool Sky { get; private set; }       // legacy flag; SkyMode is the real one

    // Which sky pass. IndirectPlanetEnvironmentJob (1) is the PROBE pipeline's sky and
    // takes its orientation from cube-face state rather than the view we pass — the
    // original spin, and now a sky that is too zoomed and rotates far faster than the
    // orbit. DrawSkybox (2) takes only a command list and a target, so it uses the BOUND
    // camera CB, which the env pass has already set to ours.
    //   0 = no sky, 1 = IndirectPlanetEnvironmentJob, 2 = DrawSkybox
    public static int SkyMode { get; private set; } = 1;

    // Exposure source. On: EnvironmentProbeExposureJob.Exposure — the engine's own
    // exposure for probe-style offscreen renders. Off: ComputeExposure, which drives
    // the SHARED eye adaptation and therefore exposes our feed for the player's view
    // while feeding our HDR buffer into the adaptation the main view depends on.
    public static bool ProbeExposure { get; private set; } = true;

    // Clustering far plane, metres. Was hardcoded to 5000 — copied from the probe
    // pass, which sizes for a whole environment probe. An orbit camera 100 m from a
    // ship needs a fraction of that and the cost scales with it.
    public static double CullFarPlane { get; private set; } = 1500.0;

    // Skip ApplyBloom and hand ApplyToneMapping a flat pre-made texture instead.
    // ApplyToneMapping's bloom parameter is not optional (null throws inside the
    // engine), but there is no requirement that it be freshly computed. Bloom is a
    // multi-pass downsample/upsample chain and accounts for the first halving of the
    // frame rate — and skipping it also removes one of the three main-view post
    // passes we borrow, which is a stability win as well as a speed one.
    public static bool CheapBloom { get; private set; }

    // Run the camera pass from the per-frame hook (DrawUnlit) instead of the probe
    // hook. The probe hook is inside the engine's own environment-probe work, which
    // is where borrowing the main view's post passes corrupted the player's render.
    // Off by default: the probe hook is the one with hours of proven runtime.
    public static bool PassOnFrameHook { get; private set; }

    // Take an exposure texture the engine already computed instead of calling
    // ComputeExposure. Passive read versus a write to the histogram, _autoExposures
    // and the temporal adaptation the main view depends on. This is the bisect that
    // separates ApplyToneMapping (possibly a pure blit) from ComputeExposure (a
    // certain mutator of shared state) — they were only ever tested together.
    public static bool ReuseExposure { get; private set; } = true;

    // Constant exposure for the feed, as a live-tunable number. 0 disables it and
    // falls back to reusing the engine's (player-adapted, wrong) exposure texture.
    // Lower = darker. Start low and raise it: the failure mode we are fixing is
    // blown highlights, so overshooting downward is the safe direction.
    public static double ExposureValue { get; private set; } = 0.25;

    // Step 2 of the GBuffer work: swap ScreenBuffers.GBuffer to our own array for the
    // duration of the camera pass, and restore it immediately. Drives NO passes on its
    // own — it exists to prove the swap mechanism in isolation before lighting goes on
    // top, so a failure names the mechanism rather than the mechanism plus a pass.
    public static bool GBufferSwap { get; private set; }

    // Step 3: drive GBufferPassJob into our swapped-in GBuffer. Additive — the
    // forward-ish environment pass still produces the visible image, so this changes
    // nothing on the panel. It proves the GBuffer is WRITTEN, which step 4 needs.
    // Requires owning the depth buffer too; the code refuses to run otherwise.
    public static bool GBufferPass { get; private set; }

    // Step 4: the deferred lighting chain, reading the GBuffer instead of relying on
    // the probe path's single-pass shading. Deferred REPLACES the forward pass, so
    // envPass must go off when this comes on or the scene is lit twice. Each job is
    // separately switchable: ambient is the one that should fix the harshness.
    public static bool Deferred { get; private set; }
    public static bool DeferredDirectional { get; private set; } = true;
    public static bool DeferredLocal { get; private set; } = true;
    public static bool DeferredAmbient { get; private set; } = true;
    public static bool EnvPass { get; private set; } = true;

    // AtmosphereAdditiveJob.DoWork(cl, rtView) — two arguments, no GBuffer, so by the
    // parameter test it should be reachable where the deferred lighting jobs were not.
    public static bool Atmosphere { get; private set; }

    // The engine's ENTIRE deferred lighting stage: ExecuteLighting(lBuffer). One
    // parameter, everything else global — but the GBuffer and depth globals are ours
    // during our pass, and it is the orchestrator that sets up the state the individual
    // light jobs were missing. Replaces IndirectEnvironmentPassJob (the probe path,
    // which is pre-exposed and range-compressed and is why the feed looks flat).
    // Requires gbufferSwap + gbufferPass, and a resizable colour target.
    public static bool ExecuteLighting { get; private set; }

    // Close SceneDrawSystem.IsRtxInitialized (a writable AtomicFlag) around
    // ExecuteLighting so its GI work is skipped by the engine's OWN guard. Nulling
    // _raytraceGiJob threw because ExecuteLighting never expects that field to be null;
    // clearing a flag the engine tests is a supported state rather than a surprising one.
    // GI is what polluted the player's temporal accumulator (flickering noise).
    public static bool GateGi { get; private set; } = true;

    // Render the env pass into a SCRATCH target when ExecuteLighting is producing the
    // image. The env pass is needed for its side effect — it binds our camera constant
    // buffer, which GBufferPassJob has no parameter for — but its pixels are fully-lit
    // colour, so leaving them in our target means the scene is lit TWICE and highlights
    // blow out instead of shadows filling. Keep the binding, discard the lighting.
    public static bool EnvPassToScratch { get; private set; } = true;

    // Swap ScreenBuffers.PreUpscaleResolution to OUR render resolution for the pass.
    // ExecuteLighting sizes its dispatch from it, so with the engine's 3840x2160 and our
    // 512x512 GBuffer only a small top-left corner of the lighting landed on the target.
    // Restored unconditionally — leaving the engine thinking its viewport is 512x512
    // would wreck the player's view.
    public static bool SwapResolution { get; private set; } = true;

    // Swap SettingsManager._renderView to OUR camera for the pass. GBufferPassJob and
    // ExecuteLighting take no camera parameter, so without this they rasterise and light
    // from the player's viewpoint — the feed showed the inside of the ship.
    public static bool SwapCamera { get; private set; } = true;

    // Run GBufferPassJob AFTER IndirectEnvironmentPassJob. The camera is a constant
    // buffer bound to the command list, and the env pass is the only thing we drive that
    // binds OURS (it takes cameraCb explicitly). Running the GBuffer pass first meant it
    // inherited the player's camera — the "viewpoint stuck inside the ship" symptom.
    // Requires envPass = 1 so that binding actually happens.
    public static bool GBufferAfterEnv { get; private set; } = true;

    // Null the GI jobs for the duration of our pass. ExecuteLighting includes ray-traced
    // GI, which is TEMPORAL — it accumulates across frames — so running it twice a frame
    // against a different camera pollutes the accumulator, which is the flickering noise
    // in the player's view. Suppressing it keeps ambient, directional and local, which
    // is what the feed needs; GI contributes least at 512x512. Restored unconditionally.
    public static bool SuppressGi { get; private set; } = true;

    // Supersampling multiplier on the panel resolution for the SCENE render only.
    // The feed has always rendered at exactly 512x512 with no anti-aliasing, which is
    // the blockiness and starfield smearing — not a lighting problem. Rendering larger
    // and letting the existing CopyJob blit downsample gives real AA with no new pass.
    // Cost scales with area: x2 is 4x the pixels, x4 is 16x.
    public static double RenderScale { get; private set; } = 1.0;

    // The panel binds our feed as ColorMetalTexture: RGB is colour, ALPHA IS METALNESS.
    // Our HDR source has no alpha channel, so blitting all four channels wrote ~1.0
    // into metalness — a fully metallic panel, which has almost no diffuse response and
    // flattens the range regardless of exposure. Default: RGB only, slot pre-cleared so
    // alpha reads 0 (non-metal). Flip blitAlpha to 1 to reproduce the old behaviour.
    public static bool BlitAlpha { get; private set; }
    public static bool ZeroMetalness { get; private set; } = true;

    // ---- the five fidelity fixes from docs/routes.md ----
    //
    // All five are now ON by default: they were flipped one at a time against a running
    // feed, all five held, and the result is the stable baseline. Defaults rather than
    // config-file entries because output\feed-config.txt is gitignored — a clean checkout
    // has to reproduce this build, not the pre-fix one. Setting any of them in the config
    // file still wins, so each remains a one-line bisect.

    // 1. Which LOD profile our own culling call asks for. We copied the probe recipe,
    //    which passes Settings.LOD.EnvironmentProbe — and the shipped config gives that
    //    profile MinLOD 8 / FloraMinLOD 8 where MainView uses 0. So the feed has always
    //    drawn every mesh at its coarsest LOD. One argument, no global touched.
    public static bool LodMainView { get; private set; } = true;

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

    // 5a. EnvironmentProbeSettings.EnableRecursiveReflections ships FALSE, which makes
    //     IndirectEnvironmentPassJob bind a flat default cubemap instead of the real
    //     CloseIBL/FarIBL — our ambient term is a constant. Read LIVE inside DoWork, so a
    //     scoped set/restore around our pass is enough and the player keeps their own.
    //     -1 leaves it alone, 0 forces off, 1 forces on.
    public static int RecursiveReflections { get; private set; } = 1;

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

    public static double OrbitRadius { get; private set; } = 100.0;
    public static double OrbitPeriod { get; private set; } = 30.0;
    public static double OrbitHeight { get; private set; } = 15.0;

    public static void Poll()
    {
        var now = System.Environment.TickCount64;
        if (now - _lastRead < 2000) return;
        _lastRead = now;

        try
        {
            if (!File.Exists(Path_))
            {
                // Write the defaults out once so the knobs are discoverable rather
                // than something you have to read the source to find.
                File.WriteAllText(Path_,
                    "# RTT camera feed — edit and save; picked up within ~2s.\n" +
                    $"intervalMs  = {IntervalMs}\n" +
                    $"orbitRadius = {OrbitRadius}\n" +
                    $"orbitPeriod = {OrbitPeriod}\n" +
                    $"orbitHeight = {OrbitHeight}\n");
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(Path_).Ticks;
            if (stamp == _lastStamp) return;
            _lastStamp = stamp;

            int interval = IntervalMs, panel = PanelMs, startup = StartupDelayMs;
            bool pooled = UsePooledCulling;
            bool src = SrcTransition, dst = DestTransition, retire = RetireTestPattern, copy = CopyEnabled;
            int cullRoot = CullRootEntityId;
            bool tone = Tonemap, bloom = Bloom, sky = Sky, probeExp = ProbeExposure, cheapBloom = CheapBloom;
            bool frameHook = PassOnFrameHook, reuseExp = ReuseExposure;
            double expValue = ExposureValue;
            bool gbSwap = GBufferSwap, gbPass = GBufferPass, defer = Deferred, envP = EnvPass;
            bool defDir = DeferredDirectional, defLoc = DeferredLocal, defAmb = DeferredAmbient;
            bool atmo = Atmosphere, execLight = ExecuteLighting, swapRes = SwapResolution, swapCam = SwapCamera, gbAfter = GBufferAfterEnv, supGi = SuppressGi, gateGi = GateGi, envScratch = EnvPassToScratch;
            int skyMode = SkyMode;
            double rScale = RenderScale;
            bool blitA = BlitAlpha, zeroMetal = ZeroMetalness;
            double farPlane = CullFarPlane;
            double radius = OrbitRadius, period = OrbitPeriod, height = OrbitHeight, clearance = OrbitClearance;
            bool orbitGrid = OrbitGrid;
            bool lodMain = LodMainView, fixScreen = FixScreenRes, fullCam = FullCameraCb, effectGeom = EffectGeomBuffers, rangeCull = RangeCulling, swapCb = SwapCameraCb, atmoMul = AtmosphereMultiply;
            int bloomN = BloomEveryN;
            bool sunPass = SunPass, sunDepth = SunPassDepth;
            int dbgGb = DebugGBuffer;
            bool defTex = DeferredTexturing, autoExp = AutoExposure, mvCull = MainViewCulling, cull2 = CullSecondPass, noOcc = DisableOcclusionCulling, privCtx = PrivateCullContexts, privGeom = PrivateGeomBuffers, coCull = CoCullMainView;
            double expDown = AutoExposureDownSpeed, expUp = AutoExposureUpSpeed;
            double expMin = AutoExposureMin, expMax = AutoExposureMax, expBias = AutoExposureBias;
            double expSun = AutoExposureSunRange;
            double emissive = Emissivity, dimDist = DimDistance;
            int recursive = RecursiveReflections;
            double geomHead = GeomRangeHeadroom;
            int geomFloor = GeomRangeFloor;
            int[] coGroups = CoCullPassGroups;
            bool coMainView = CoCullForMainView, coTwoPass = CoCullTwoPass,
                 coGeomOnly = CoCullGeometryOnly, coSingleLod = CoCullForceSingleLod;
            bool clearGb = ClearGBufferBeforePass;
            bool wsBuild = WholeSceneBuildBuffers, wsRender = WholeSceneEnabled;
            int wsW = WholeSceneWidth, wsH = WholeSceneHeight;
            bool wsCam = WholeSceneCamera;
            bool wsPanel = WholeSceneToPanel;
            int wsCamRebuild = WholeSceneCameraRebuild, wsAa = WholeSceneAAMode;
            double wsExp = WholeSceneExposure;
            bool wsNative = WholeSceneNativeScaling;
            bool wsNoEye = WholeSceneDisableEyeAdaptation, wsNoProbe = WholeSceneDisableProbeUpdates;
            int wsNoRtMode = WholeSceneDisableRaytracing;
            bool wsOwnDc = WholeSceneOwnDrawContexts;
            int wsOwnShadow = WholeSceneOwnShadows;
            bool wsStrip = WholeSceneStripProbe;
            int[] wsSkip = WholeSceneSkipStages;
            int wsInterval = WholeSceneIntervalMs;

            foreach (var raw in File.ReadAllLines(Path_))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim().ToLowerInvariant();
                var val = line[(eq + 1)..].Trim();

                switch (key)
                {
                    case "intervalms":
                        if (int.TryParse(val, out var i)) interval = Math.Clamp(i, 8, 5000);
                        break;
                    case "panelms":
                        if (int.TryParse(val, out var pm)) panel = Math.Clamp(pm, 0, 5000);
                        break;
                    case "startupdelayms":
                        if (int.TryParse(val, out var sd)) startup = Math.Clamp(sd, 0, 60000);
                        break;
                    case "usepooledculling":
                        pooled = val is "1" or "true" or "yes";
                        break;
                    case "srctransition":
                        src = val is "1" or "true" or "yes";
                        break;
                    case "desttransition":
                        dst = val is "1" or "true" or "yes";
                        break;
                    case "retiretestpattern":
                        retire = val is "1" or "true" or "yes";
                        break;
                    case "copyenabled":
                        copy = val is "1" or "true" or "yes";
                        break;
                    case "tonemap":
                        tone = val is "1" or "true" or "yes";
                        break;
                    case "bloom":
                        bloom = val is "1" or "true" or "yes";
                        break;
                    case "sky":
                        sky = val is "1" or "true" or "yes";
                        break;
                    case "probeexposure":
                        probeExp = val is "1" or "true" or "yes";
                        break;
                    case "cheapbloom":
                        cheapBloom = val is "1" or "true" or "yes";
                        break;
                    case "blitalpha":
                        blitA = val is "1" or "true" or "yes";
                        break;
                    case "zerometalness":
                        zeroMetal = val is "1" or "true" or "yes";
                        break;
                    case "renderscale":
                        if (double.TryParse(val, out var rs) && rs >= 1.0) rScale = rs;
                        break;
                    case "suppressgi":
                        supGi = val is "1" or "true" or "yes";
                        break;
                    case "gbufferafterenv":
                        gbAfter = val is "1" or "true" or "yes";
                        break;
                    case "swapcamera":
                        swapCam = val is "1" or "true" or "yes";
                        break;
                    case "swapresolution":
                        swapRes = val is "1" or "true" or "yes";
                        break;
                    case "cullrootentityid":
                        if (int.TryParse(val, out var cre)) cullRoot = cre;
                        break;
                    case "skymode":
                        if (int.TryParse(val, out var sm)) skyMode = Math.Clamp(sm, 0, 2);
                        break;
                    case "envpasstoscratch":
                        envScratch = val is "1" or "true" or "yes";
                        break;
                    case "gategi":
                        gateGi = val is "1" or "true" or "yes";
                        break;
                    case "executelighting":
                        execLight = val is "1" or "true" or "yes";
                        break;
                    case "atmosphere":
                        atmo = val is "1" or "true" or "yes";
                        break;
                    case "deferred":
                        defer = val is "1" or "true" or "yes";
                        break;
                    case "envpass":
                        envP = val is "1" or "true" or "yes";
                        break;
                    case "deferreddirectional":
                        defDir = val is "1" or "true" or "yes";
                        break;
                    case "deferredlocal":
                        defLoc = val is "1" or "true" or "yes";
                        break;
                    case "deferredambient":
                        defAmb = val is "1" or "true" or "yes";
                        break;
                    case "gbufferpass":
                        gbPass = val is "1" or "true" or "yes";
                        break;
                    case "gbufferswap":
                        gbSwap = val is "1" or "true" or "yes";
                        break;
                    case "exposurevalue":
                        if (double.TryParse(val, out var ev) && ev >= 0.0) expValue = ev;
                        break;
                    case "reuseexposure":
                        reuseExp = val is "1" or "true" or "yes";
                        break;
                    case "passonframehook":
                        frameHook = val is "1" or "true" or "yes";
                        break;
                    case "cullfarplane":
                        if (double.TryParse(val, out var fp) && fp > 10.0) farPlane = fp;
                        break;
                    case "orbitradius":
                        if (double.TryParse(val, out var r)) radius = Math.Clamp(r, 1.0, 100000.0);
                        break;
                    case "orbitperiod":
                        if (double.TryParse(val, out var p) && p > 0.1) period = p;
                        break;
                    case "orbitheight":
                        if (double.TryParse(val, out var h)) height = h;
                        break;
                    case "orbitclearance":
                        if (double.TryParse(val, out var c) && c > 0.1) clearance = c;
                        break;
                    case "orbitgrid":
                        orbitGrid = val is "1" or "true" or "yes";
                        break;
                    case "atmospheremultiply":
                        atmoMul = val is "1" or "true" or "yes";
                        break;
                    case "swapcameracb":
                        swapCb = val is "1" or "true" or "yes";
                        break;
                    case "cocullmainview":
                        coCull = val is "1" or "true" or "yes";
                        break;
                    case "wholescenebuildbuffers":
                        wsBuild = val is "1" or "true" or "yes";
                        break;
                    case "wholesceneowndrawcontexts":
                        wsOwnDc = val is "1" or "true" or "yes";
                        break;
                    case "wholesceneownshadows":
                        if (int.TryParse(val, out var wsos)) wsOwnShadow = Math.Clamp(wsos, 0, 2);
                        break;
                    case "wholescenestripprobe":
                        wsStrip = val is "1" or "true" or "yes";
                        break;
                    case "wholesceneskipstages":
                    {
                        var parsedSkip = new List<int>();
                        foreach (var tok in val.Split(new[] { (char)44 }, StringSplitOptions.RemoveEmptyEntries))
                            if (int.TryParse(tok.Trim(), out var sg) && sg >= 0 && sg <= 31) parsedSkip.Add(sg);
                        wsSkip = parsedSkip.ToArray();
                        break;
                    }
                    case "wholescenedisableprobeupdates":
                        wsNoProbe = val is "1" or "true" or "yes";
                        break;
                    case "wholescenedisableeyeadaptation":
                        wsNoEye = val is "1" or "true" or "yes";
                        break;
                    case "wholescenedisableraytracing":
                        wsNoRtMode = val is "true" or "yes" ? 1 : int.TryParse(val, out var wnr) ? Math.Clamp(wnr, 0, 2) : wsNoRtMode;
                        break;
                    case "wholescenenativescaling":
                        wsNative = val is "1" or "true" or "yes";
                        break;
                    case "wholesceneaamode":
                        if (int.TryParse(val, out var wsam)) wsAa = Math.Clamp(wsam, -1, 2);
                        break;
                    case "wholesceneexposure":
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wse)) wsExp = Math.Clamp(wse, -16.0, 16.0);
                        break;
                    case "wholescenecamerarebuild":
                        wsCamRebuild = val is "true" or "yes" ? 1
                                     : int.TryParse(val, out var wcr) ? Math.Clamp(wcr, 0, 3) : wsCamRebuild;
                        break;
                    case "wholescenetopanel":
                        wsPanel = val is "1" or "true" or "yes";
                        break;
                    case "wholescenecamera":
                        wsCam = val is "1" or "true" or "yes";
                        break;
                    case "wholesceneintervalms":
                        if (int.TryParse(val, out var wsi)) wsInterval = Math.Clamp(wsi, 16, 5000);
                        break;
                    case "wholescenerender":
                        wsRender = val is "1" or "true" or "yes";
                        break;
                    case "wholescenewidth":
                        if (int.TryParse(val, out var wsw)) wsW = Math.Clamp(wsw, 64, 4096);
                        break;
                    case "wholesceneheight":
                        if (int.TryParse(val, out var wsh)) wsH = Math.Clamp(wsh, 64, 4096);
                        break;
                    case "cleargbufferbeforepass":
                        clearGb = val is "1" or "true" or "yes";
                        break;
                    case "cocullformainview":
                        coMainView = val is "1" or "true" or "yes";
                        break;
                    case "coculltwopass":
                        coTwoPass = val is "1" or "true" or "yes";
                        break;
                    case "cocullgeometryonly":
                        coGeomOnly = val is "1" or "true" or "yes";
                        break;
                    case "cocullforcesinglelod":
                        coSingleLod = val is "1" or "true" or "yes";
                        break;
                    case "cocullpassgroups":
                    {
                        var parsed = new List<int>();
                        foreach (var tok in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            if (int.TryParse(tok.Trim(), out var g) && g >= 0 && g <= 15) parsed.Add(g);
                        if (parsed.Count > 0) coGroups = parsed.ToArray();
                        break;
                    }
                    case "privategeombuffers":
                        privGeom = val is "1" or "true" or "yes";
                        break;
                    case "geomrangeheadroom":
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out var ghd))
                            geomHead = Math.Clamp(ghd, 0.0, 8.0);
                        break;
                    case "geomrangefloor":
                        if (int.TryParse(val, out var gfl)) geomFloor = Math.Clamp(gfl, 1, 1 << 20);
                        break;
                    case "privatecullcontexts":
                        privCtx = val is "1" or "true" or "yes";
                        break;
                    case "disableocclusionculling":
                        noOcc = val is "1" or "true" or "yes";
                        break;
                    case "cullsecondpass":
                        cull2 = val is "1" or "true" or "yes";
                        break;
                    case "mainviewculling":
                        mvCull = val is "1" or "true" or "yes";
                        break;
                    case "autoexposure":
                        autoExp = val is "1" or "true" or "yes";
                        break;
                    case "autoexposuredownspeed":
                        if (double.TryParse(val, out var ed) && ed > 0) expDown = ed;
                        break;
                    case "autoexposureupspeed":
                        if (double.TryParse(val, out var eu) && eu > 0) expUp = eu;
                        break;
                    case "autoexposuremin":
                        if (double.TryParse(val, out var emn) && emn > 0) expMin = emn;
                        break;
                    case "autoexposuremax":
                        if (double.TryParse(val, out var emx) && emx > 0) expMax = emx;
                        break;
                    case "autoexposuresunrange":
                        if (double.TryParse(val, out var es)) expSun = es;
                        break;
                    case "autoexposurebias":
                        if (double.TryParse(val, out var eb)) expBias = eb;
                        break;
                    case "deferredtexturing":
                        defTex = val is "1" or "true" or "yes";
                        break;
                    case "debuggbuffer":
                        if (int.TryParse(val, out var dg)) dbgGb = Math.Clamp(dg, 0, 5);
                        break;
                    case "sunpassdepth":
                        sunDepth = val is "1" or "true" or "yes";
                        break;
                    case "sunpass":
                        sunPass = val is "1" or "true" or "yes";
                        break;
                    case "bloomeveryn":
                        if (int.TryParse(val, out var bn)) bloomN = Math.Clamp(bn, 1, 60);
                        break;
                    case "rangeculling":
                        rangeCull = val is "1" or "true" or "yes";
                        break;
                    case "effectgeombuffers":
                        effectGeom = val is "1" or "true" or "yes";
                        break;
                    case "lodmainview":
                        lodMain = val is "1" or "true" or "yes";
                        break;
                    case "fixscreenres":
                        fixScreen = val is "1" or "true" or "yes";
                        break;
                    case "fullcameracb":
                        fullCam = val is "1" or "true" or "yes";
                        break;
                    case "emissivity":
                        if (double.TryParse(val, out var em) && em >= 0.0) emissive = em;
                        break;
                    case "recursivereflections":
                        if (int.TryParse(val, out var rr)) recursive = Math.Clamp(rr, -1, 1);
                        break;
                    case "dimdistance":
                        if (double.TryParse(val, out var dd)) dimDist = dd;
                        break;
                }
            }

            bool changed = interval != IntervalMs || panel != PanelMs || startup != StartupDelayMs || pooled != UsePooledCulling || radius != OrbitRadius
                        || period != OrbitPeriod || height != OrbitHeight
                        || src != SrcTransition || dst != DestTransition || retire != RetireTestPattern
                        || copy != CopyEnabled || clearance != OrbitClearance || orbitGrid != OrbitGrid
                        || tone != Tonemap || bloom != Bloom || sky != Sky || probeExp != ProbeExposure
                        || cheapBloom != CheapBloom || farPlane != CullFarPlane || frameHook != PassOnFrameHook
                        || reuseExp != ReuseExposure || expValue != ExposureValue || gbSwap != GBufferSwap || gbPass != GBufferPass || defer != Deferred || envP != EnvPass
                        || defDir != DeferredDirectional || defLoc != DeferredLocal || defAmb != DeferredAmbient
                        || lodMain != LodMainView || fixScreen != FixScreenRes || fullCam != FullCameraCb || effectGeom != EffectGeomBuffers || rangeCull != RangeCulling || swapCb != SwapCameraCb || atmoMul != AtmosphereMultiply || bloomN != BloomEveryN || sunPass != SunPass || sunDepth != SunPassDepth || dbgGb != DebugGBuffer || defTex != DeferredTexturing || autoExp != AutoExposure || mvCull != MainViewCulling || cull2 != CullSecondPass || noOcc != DisableOcclusionCulling || privCtx != PrivateCullContexts || privGeom != PrivateGeomBuffers || coCull != CoCullMainView || clearGb != ClearGBufferBeforePass
                        || wsBuild != WholeSceneBuildBuffers || wsRender != WholeSceneEnabled
                        || wsW != WholeSceneWidth || wsH != WholeSceneHeight
                        || wsCam != WholeSceneCamera || wsInterval != WholeSceneIntervalMs || wsPanel != WholeSceneToPanel || wsCamRebuild != WholeSceneCameraRebuild || wsAa != WholeSceneAAMode || wsExp != WholeSceneExposure || wsNative != WholeSceneNativeScaling || wsNoRtMode != WholeSceneDisableRaytracing || wsNoEye != WholeSceneDisableEyeAdaptation || wsNoProbe != WholeSceneDisableProbeUpdates || !wsSkip.SequenceEqual(WholeSceneSkipStages) || wsOwnDc != WholeSceneOwnDrawContexts || wsOwnShadow != WholeSceneOwnShadows || wsStrip != WholeSceneStripProbe
                        || !coGroups.SequenceEqual(CoCullPassGroups) || expDown != AutoExposureDownSpeed || expUp != AutoExposureUpSpeed || expMin != AutoExposureMin || expMax != AutoExposureMax || expBias != AutoExposureBias || expSun != AutoExposureSunRange
                        || emissive != Emissivity || recursive != RecursiveReflections || dimDist != DimDistance
                        || geomHead != GeomRangeHeadroom || geomFloor != GeomRangeFloor
                        || atmo != Atmosphere || rScale != RenderScale || blitA != BlitAlpha
                        || zeroMetal != ZeroMetalness || execLight != ExecuteLighting || swapRes != SwapResolution || swapCam != SwapCamera || gbAfter != GBufferAfterEnv || supGi != SuppressGi || gateGi != GateGi || envScratch != EnvPassToScratch || skyMode != SkyMode || cullRoot != CullRootEntityId;
            IntervalMs = interval; PanelMs = panel; StartupDelayMs = startup; UsePooledCulling = pooled; OrbitRadius = radius; OrbitPeriod = period; OrbitHeight = height;
            SrcTransition = src; DestTransition = dst; RetireTestPattern = retire; CopyEnabled = copy;
            OrbitClearance = clearance; OrbitGrid = orbitGrid;
            Tonemap = tone; Bloom = bloom; Sky = sky; ProbeExposure = probeExp;
            CheapBloom = cheapBloom; CullFarPlane = farPlane; PassOnFrameHook = frameHook;
            ReuseExposure = reuseExp; ExposureValue = expValue; GBufferSwap = gbSwap; GBufferPass = gbPass; Deferred = defer; EnvPass = envP;
            DeferredDirectional = defDir; DeferredLocal = defLoc; DeferredAmbient = defAmb;
            Atmosphere = atmo; RenderScale = rScale; ExecuteLighting = execLight; SwapResolution = swapRes; SwapCamera = swapCam; GBufferAfterEnv = gbAfter; SuppressGi = supGi; GateGi = gateGi; EnvPassToScratch = envScratch; SkyMode = skyMode; CullRootEntityId = cullRoot; BlitAlpha = blitA; ZeroMetalness = zeroMetal;
            LodMainView = lodMain; FixScreenRes = fixScreen; FullCameraCb = fullCam; EffectGeomBuffers = effectGeom; RangeCulling = rangeCull; SwapCameraCb = swapCb; AtmosphereMultiply = atmoMul; BloomEveryN = bloomN; SunPass = sunPass; SunPassDepth = sunDepth; DebugGBuffer = dbgGb; DeferredTexturing = defTex;
            MainViewCulling = mvCull; CullSecondPass = cull2; DisableOcclusionCulling = noOcc; PrivateCullContexts = privCtx; PrivateGeomBuffers = privGeom; CoCullMainView = coCull; ClearGBufferBeforePass = clearGb;

            // Compare BEFORE assigning. The second ScreenBuffers is initialised at a
            // fixed size, so a changed resolution has to rebuild it rather than quietly
            // render at the old one — and turning buffer construction off should release
            // it rather than leave it allocated.
            bool wsChanged = wsBuild != WholeSceneBuildBuffers
                             || wsW != WholeSceneWidth || wsH != WholeSceneHeight
                        || wsCam != WholeSceneCamera || wsInterval != WholeSceneIntervalMs || wsPanel != WholeSceneToPanel || wsCamRebuild != WholeSceneCameraRebuild || wsAa != WholeSceneAAMode || wsExp != WholeSceneExposure || wsNative != WholeSceneNativeScaling || wsNoRtMode != WholeSceneDisableRaytracing || wsNoEye != WholeSceneDisableEyeAdaptation || wsNoProbe != WholeSceneDisableProbeUpdates || !wsSkip.SequenceEqual(WholeSceneSkipStages) || wsOwnDc != WholeSceneOwnDrawContexts || wsOwnShadow != WholeSceneOwnShadows;

            WholeSceneEnabled = wsRender;
            WholeSceneBuildBuffers = wsBuild;
            WholeSceneWidth = wsW;
            WholeSceneHeight = wsH;
            WholeSceneCamera = wsCam;
            WholeSceneToPanel = wsPanel;
            WholeSceneCameraRebuild = wsCamRebuild;
            WholeSceneAAMode = wsAa;
            WholeSceneNativeScaling = wsNative;
            WholeSceneExposure = wsExp;
            WholeSceneIntervalMs = wsInterval;
            WholeSceneDisableRaytracing = wsNoRtMode;
            WholeSceneDisableEyeAdaptation = wsNoEye;
            WholeSceneDisableProbeUpdates = wsNoProbe;
            WholeSceneSkipStages = wsSkip;
            WholeSceneOwnDrawContexts = wsOwnDc;
            WholeSceneOwnShadows = wsOwnShadow;
            // Deliberately NOT in wsChanged: the strip owns no buffers, so flipping it
            // must not trigger a Reset() that rebuilds the second ScreenBuffers.
            WholeSceneStripProbe = wsStrip;

            // A resolution or buffer-mode change needs a full rebuild; anything else
            // just needs the one-strike disable cleared, so an experiment is a config
            // save rather than a rebuild.
            if (wsChanged) RttProbe.WholeSceneRender.Reset();
            else RttProbe.WholeSceneRender.Rearm();

            // The custom culling job bakes its pass-group list AND its flags in at
            // construction, so any change means a rebuilt job — otherwise editing the
            // config would appear to do nothing and we would be debugging the wrong thing.
            if (!coGroups.SequenceEqual(CoCullPassGroups)
                || coMainView != CoCullForMainView || coTwoPass != CoCullTwoPass
                || coGeomOnly != CoCullGeometryOnly || coSingleLod != CoCullForceSingleLod)
            {
                CoCullPassGroups = coGroups;
                CoCullForMainView = coMainView; CoCullTwoPass = coTwoPass;
                CoCullGeometryOnly = coGeomOnly; CoCullForceSingleLod = coSingleLod;
                CustomCullJob.Reset();
            }
            GeomRangeHeadroom = geomHead; GeomRangeFloor = geomFloor;
            AutoExposure = autoExp; AutoExposureDownSpeed = expDown; AutoExposureUpSpeed = expUp;
            AutoExposureMin = expMin; AutoExposureMax = expMax; AutoExposureBias = expBias;
            AutoExposureSunRange = expSun;
            Emissivity = emissive; RecursiveReflections = recursive; DimDistance = dimDist;

            if (changed)
                RttLog.Line($"Config: intervalMs={IntervalMs} (~{1000.0 / IntervalMs:F0} fps) " +
                            $"orbit radius>={OrbitRadius} clearance={OrbitClearance}x grid={OrbitGrid} " +
                            $"period={OrbitPeriod}s height={OrbitHeight} " +
                            $"| copyEnabled={CopyEnabled} srcTransition={SrcTransition} " +
                            $"destTransition={DestTransition} retireTestPattern={RetireTestPattern} " +
                            $"| tonemap={Tonemap} bloom={Bloom} cheapBloom={CheapBloom} sky={Sky} " +
                            $"probeExposure={ProbeExposure} cullFarPlane={CullFarPlane} " +
                            $"hook={(PassOnFrameHook ? "per-frame" : "probe")} " +
                            // The rendering-path knobs, because chasing a symptom without
                            // knowing which combination produced it has cost several cycles.
                            $"| skyMode={SkyMode} gbufSwap={GBufferSwap} gbufPass={GBufferPass} " +
                            $"execLight={ExecuteLighting} gateGi={GateGi} envScratch={EnvPassToScratch} " +
                            $"swapRes={SwapResolution} swapCam={SwapCamera} " +
                            // The five routes-doc fixes, printed together so a screenshot
                            // of the log says which combination produced the image.
                            $"| lodMainView={LodMainView} fixScreenRes={FixScreenRes} " +
                            $"fullCameraCb={FullCameraCb} emissivity={Emissivity} " +
                            $"recursiveReflections={RecursiveReflections} dimDistance={DimDistance} " +
                            $"| cullRootEntityId={CullRootEntityId} effectGeomBuffers={EffectGeomBuffers} rangeCulling={RangeCulling} swapCameraCb={SwapCameraCb} atmosphereMultiply={AtmosphereMultiply} bloomEveryN={BloomEveryN} sunPass={SunPass} sunPassDepth={SunPassDepth} debugGBuffer={DebugGBuffer} deferredTexturing={DeferredTexturing} autoExposure={AutoExposure} mainViewCulling={MainViewCulling} cullSecondPass={CullSecondPass} privateGeomBuffers={PrivateGeomBuffers} privateCullContexts={PrivateCullContexts} coCullMainView={CoCullMainView} coCullPassGroups=[{string.Join(",", CoCullPassGroups)}] geomRangeHeadroom={GeomRangeHeadroom} geomRangeFloor={GeomRangeFloor}");
        }
        catch { /* keep the last good values */ }
    }
}
