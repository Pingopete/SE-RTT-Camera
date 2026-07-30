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
    public static bool WholeSceneOwnFlares { get; private set; }

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

            // ---- the whole-scene route -------------------------------------------
            string before = WholeSceneSignature();

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
            WholeSceneNativeScaling = Bool(kv, "wholeSceneNativeScaling", WholeSceneNativeScaling);
            WholeSceneNoBloom       = Bool(kv, "wholeSceneNoBloom", WholeSceneNoBloom);
            WholeSceneLdrResize     = Bool(kv, "wholeSceneLdrResize", WholeSceneLdrResize);
            WholeSceneSubmitEarly   = Bool(kv, "wholeSceneSubmitEarly", WholeSceneSubmitEarly);
            WholeSceneFarClip       = Dbl(kv, "wholeSceneFarClip", WholeSceneFarClip);
            WholeSceneOwnDrawContexts   = Bool(kv, "wholeSceneOwnDrawContexts", WholeSceneOwnDrawContexts);
            WholeSceneOwnShadows        = Int(kv, "wholeSceneOwnShadows", WholeSceneOwnShadows);
            WholeSceneCascadeResolution = Int(kv, "wholeSceneCascadeResolution", WholeSceneCascadeResolution);
            WholeSceneCascadeCount      = Int(kv, "wholeSceneCascadeCount", WholeSceneCascadeCount);
            WholeScenePlanetEnv         = Bool(kv, "wholeScenePlanetEnv", WholeScenePlanetEnv);
            WholeSceneOwnFlares         = Bool(kv, "wholeSceneOwnFlares", WholeSceneOwnFlares);
            PanelFsrMask                = Bool(kv, "panelFsrMask", PanelFsrMask);
            PanelMipRegen               = Bool(kv, "panelMipRegen", PanelMipRegen);
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
            string after = WholeSceneSignature();
            if (_firstPoll || before != after) RttProbe.WholeSceneRender.Reset();
            else RttProbe.WholeSceneRender.Rearm();
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
                         WholeSceneCamera, WholeSceneIntervalMs, WholeSceneToPanel,
                         WholeSceneCameraRebuild, WholeSceneAAMode, WholeSceneExposure,
                         WholeSceneNativeScaling, WholeSceneNoBloom, WholeSceneLdrResize, WholeSceneDisableRaytracing,
                         WholeSceneDisableEyeAdaptation, WholeSceneDisableProbeUpdates,
                         WholeSceneOwnDrawContexts, WholeSceneOwnShadows,
                         WholeSceneCascadeResolution, WholeSceneCascadeCount,
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
