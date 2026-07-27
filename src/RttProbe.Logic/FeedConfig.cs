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
    public static double Emissivity { get; private set; } = 500.0;

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
            bool defTex = DeferredTexturing, autoExp = AutoExposure, mvCull = MainViewCulling, cull2 = CullSecondPass, noOcc = DisableOcclusionCulling, privCtx = PrivateCullContexts, privGeom = PrivateGeomBuffers;
            double expDown = AutoExposureDownSpeed, expUp = AutoExposureUpSpeed;
            double expMin = AutoExposureMin, expMax = AutoExposureMax, expBias = AutoExposureBias;
            double expSun = AutoExposureSunRange;
            double emissive = Emissivity, dimDist = DimDistance;
            int recursive = RecursiveReflections;

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
                    case "privategeombuffers":
                        privGeom = val is "1" or "true" or "yes";
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
                        || lodMain != LodMainView || fixScreen != FixScreenRes || fullCam != FullCameraCb || effectGeom != EffectGeomBuffers || rangeCull != RangeCulling || swapCb != SwapCameraCb || atmoMul != AtmosphereMultiply || bloomN != BloomEveryN || sunPass != SunPass || sunDepth != SunPassDepth || dbgGb != DebugGBuffer || defTex != DeferredTexturing || autoExp != AutoExposure || mvCull != MainViewCulling || cull2 != CullSecondPass || noOcc != DisableOcclusionCulling || privCtx != PrivateCullContexts || privGeom != PrivateGeomBuffers || expDown != AutoExposureDownSpeed || expUp != AutoExposureUpSpeed || expMin != AutoExposureMin || expMax != AutoExposureMax || expBias != AutoExposureBias || expSun != AutoExposureSunRange
                        || emissive != Emissivity || recursive != RecursiveReflections || dimDist != DimDistance
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
            MainViewCulling = mvCull; CullSecondPass = cull2; DisableOcclusionCulling = noOcc; PrivateCullContexts = privCtx; PrivateGeomBuffers = privGeom;
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
                            $"| cullRootEntityId={CullRootEntityId} effectGeomBuffers={EffectGeomBuffers} rangeCulling={RangeCulling} swapCameraCb={SwapCameraCb} atmosphereMultiply={AtmosphereMultiply} bloomEveryN={BloomEveryN} sunPass={SunPass} sunPassDepth={SunPassDepth} debugGBuffer={DebugGBuffer} deferredTexturing={DeferredTexturing} autoExposure={AutoExposure} mainViewCulling={MainViewCulling} cullSecondPass={CullSecondPass}");
        }
        catch { /* keep the last good values */ }
    }
}
