using System.Reflection;

namespace RttProbe;

// Drive the engine's WHOLE renderer a second time, from our camera, into our target.
//
// WHY THIS ROUTE. Everything before it has been an attempt to make the environment
// probe's renderer look like the main one. That renderer is not the main one turned
// down — it is a different, cheaper pipeline. Its terrain shader
// (triplanargipixel.hlsl, 52 lines) samples no textures at all, so "asteroids have no
// texture" was never a tuning problem, and the deferred route that would have fixed it
// means reassembling the main pipeline pass by pass from the middle.
//
// This attacks the top instead:
//
//     pub Void SceneDrawSystem.Draw(ResizableRWRenderTargetTexture finalLDRBuffer)
//
// Public. Takes its destination as a parameter, and derives the render resolution from
// that buffer:
//
//     IL_0043  finalLDRBuffer.get_Resolution()
//     IL_0048  ExecuteScenePreparationAndRender(Vector2I)
//
// So the output and the size are already parameterised. Everything else it needs comes
// from CoreSystems statics — and those are public FIELDS, not readonly properties. A
// second render is a matter of swapping globals around a second call, not of finding a
// second renderer. There isn't one: a sweep of all 67 shipped assemblies found no
// portal, planar reflection, mirror, minimap, split-screen or secondary-view system.
// See docs/second-view-hunt.md.
//
// WHY THE HOOK IS ON Draw ITSELF. Draw has ZERO managed callers — it is invoked from
// engine glue. That makes it the only site where a second whole-scene render can be
// driven without re-entering a frame from inside itself, which is why the probe hook
// could never host this: it already sits inside Draw.
//
// POSTFIX, so the engine's frame is complete and its temporal state settled before we
// touch anything.
//
// WHAT KILLED IT LAST TIME. Tried early in the project and abandoned on two exceptions:
//
//     KeyNotFoundException: The given key 'R11G11B10_Float' was not present in the dictionary
//     InvalidOperationException: Nullable object must have a value
//
// R11G11B10_Float is ScreenBuffers.HDR_FORMAT = 26, and Draw borrows its LBuffer as
// BindableTexturePool.Borrow("LBuffer", 26, ScreenBuffers.MaxPreUpscaleResolution, ...)
// — format and size from the GLOBAL, against a smaller target of ours. Both exceptions
// fit that mismatch. Neither is structural, and both point at the same fix: own a
// ScreenBuffers rather than patching around the engine's.
//
// STAGED, like every other risky thing in this project. Stage 1 observes and constructs
// only; nothing is swapped and no second render runs until the pieces are proven
// individually. Doing it all at once is how the deferred route produced failures nobody
// could attribute.
internal static class WholeSceneRender
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // RE-ENTRANCY. Our own Draw call fires this same postfix. Without this the first
    // frame recurses until the stack dies.
    //
    // [ThreadStatic] because Draw runs on the render thread and nothing else should be
    // able to clear another thread's guard.
    [ThreadStatic] private static bool _inOurRender;

    private static int _state;              // 0 untried, 1 observed, -1 unavailable
    private static long _lastLogMs;
    private static int _hookCount;
    private static bool _describedTarget;

    // Our own screen buffers. NOT the engine's with textures swapped inside it, which is
    // what CameraRender does today — a whole second instance. ScreenBuffers has a public
    // parameterless constructor, and it owns depth, the GBuffer array, the final LDR
    // texture and the pre-upscale resolution, so owning one separates most of the
    // per-view state in a single move.
    private static object _ourScreenBuffers;
    private static bool _sbBuilt, _sbLogged;

    // Our own DrawContextManager — the OTHER global family, and the one the stage
    // bisect pointed at by elimination.
    //
    // With every skippable stage suppressed the player's indirect-lighting flashing
    // persisted, so the cause sits in what remains: ScenePreparation, RenderMainView
    // and ExecuteLighting. All of those cull, range and read through
    // CoreSystems.DrawContexts — visibility lists, occlusion contexts, geometry
    // buffers, the shared GPU counters ScenePreparation ReadCurrent/ClearCurrent's
    // every frame, and LODTransitions. We own a second ScreenBuffers, but this entire
    // family was still shared, and the experimental branch already recorded its exact
    // signature: a second cull writing the engine's visibility lists made the player's
    // ship lights go bright and flicker.
    //
    // This was on the critical path anyway: culling from the ORBIT camera (stage 3b)
    // into the engine's contexts would corrupt the player's view far worse than the
    // current same-camera perturbation does. Fixing the flashing and unblocking the
    // camera swap are the same edit.
    //
    // DrawContextManager..ctor() is public, parameterless, and calls
    // CreateInitialContexts itself — so construction is one Activator call, exactly
    // like ScreenBuffers.
    private static object _ourDrawContexts;
    private static object _ourFreshShadowResources;
    private static object _ourFreshFlares;
    private static bool _dcBuilt;
    private static object _panelSourceTex;
    private static bool _cbSwapLogged;
    private static int _cbSwapErrs;

    // Our render's finished image, for CameraRender's blit to use as its source.
    //
    // Null whenever the whole-scene image should NOT own the panel: flag off, route
    // errored, buffers not built, or no render completed yet — the blit then falls back
    // to the probe image automatically, which makes wholeSceneToPanel a safe live A/B
    // switch between the two pipelines.
    public static object PanelSource
    {
        get
        {
            // WholeSceneEnabled is checked here, not just at render time. Without it,
            // turning the route off left this returning our last FinalLDRTexture — which
            // kept the probe strip engaged, so the probe pipeline stayed switched off and
            // the panel froze on a stale frame instead of falling back. The claim that
            // the strip is "self-disabling" was only true for a route that had errored,
            // not for one deliberately switched off, which is exactly the case a bisect
            // needs. A frozen picture costs a test round-trip to diagnose.
            if (!FeedConfig.WholeSceneEnabled || !FeedConfig.WholeSceneToPanel
                || _state != 1 || _ourScreenBuffers == null || _renderCount == 0)
                return null;
            try
            {
                _panelSourceTex ??= _ourScreenBuffers.GetType()
                    .GetProperty("FinalLDRTexture", Any)?.GetValue(_ourScreenBuffers);
                return _panelSourceTex;
            }
            catch { return null; }
        }
    }

    public static void Reset()
    {
        // Do NOT dispose the engine's. Ours is disposable and holds real GPU memory, so
        // dropping it on a hot reload would leak — the pool asserts about exactly that
        // at shutdown, which is what turned every quit into a crash report earlier in
        // this project.
        _panelSourceTex = null;

        // EVERY FAILURE HERE IS LOGGED, and the VRAM is measured across the whole reset.
        //
        // These catches used to be bare `catch { }`. That is how a leak stays invisible:
        // our DrawContextManager owns a full cascade set (hundreds of MB at the player's
        // shadow settings) plus visibility, occlusion and geometry buffers ranged to the
        // whole scene, and the logic assembly reloads on every build. A Dispose that
        // throws once per reload silently costs a cascade set each time, and the only
        // symptom is the frame rate falling apart twenty minutes later when the residency
        // set goes over budget. Ask the question at the moment it can be answered.
        // ONLY when there is something to release.
        //
        // Reading VRAM touches a static field on CoreSystems, and touching a static field
        // FORCES that type's static constructor. Reset() is called from
        // LogicEntry.Install(), which runs on plugin load — five seconds before
        // Render12EngineComponent.Init loads the engine's configurations. CoreSystems's
        // cctor reads DeterministicRuntimeConfiguration, so forcing it that early threw
        // ConfigurationNotFoundException, .NET marked the type permanently failed, and
        // every later touch got TypeInitializationException. Crash on game load, and the
        // stack trace named the engine rather than us.
        //
        // Nothing has been built at Install() time, so gating on that is both the fix and
        // the honest condition: a teardown with nothing to tear down should not be
        // reaching into the engine at all.
        bool haveResources = _ourScreenBuffers != null || _ourDrawContexts != null;
        long vramBefore = haveResources ? Perf.SampleVramMb() : 0;

        if (_ourScreenBuffers is IDisposable d)
        {
            try { d.Dispose(); }
            catch (Exception e) { RttLog.Error("whole-scene Reset: dispose our ScreenBuffers LEAKED", e); }
        }
        _ourScreenBuffers = null;
        _sbBuilt = _sbLogged = false;
        if (_ourDrawContexts != null)
        {
            // Put OUR fresh shadow resources back before disposing, so Dispose releases
            // what we created rather than the engine's live object.
            try
            {
                if (_ourFreshShadowResources != null)
                    _ourDrawContexts.GetType().GetProperty("DirectionalLightShadowResources", Any)
                        ?.SetValue(_ourDrawContexts, _ourFreshShadowResources);
            }
            catch (Exception e) { RttLog.Error("whole-scene Reset: restore our shadow resources", e); }

            // Same dispose-safety for the flares context: our manager's Dispose would
            // otherwise dispose the ENGINE'S live one.
            try
            {
                if (_ourFreshFlares != null)
                    _ourDrawContexts.GetType().GetProperty("LensFlares", Any)
                        ?.SetValue(_ourDrawContexts, _ourFreshFlares);
            }
            catch (Exception e) { RttLog.Error("whole-scene Reset: restore our flares context", e); }

            if (_ourDrawContexts is IDisposable dc)
            {
                try { dc.Dispose(); }
                catch (Exception e) { RttLog.Error("whole-scene Reset: dispose our DrawContextManager LEAKED " +
                                                   "(cascade set + scene-sized culling buffers)", e); }
            }
            else RttLog.Line("Whole-scene Reset: our DrawContextManager is NOT IDisposable — " +
                             "everything it owns leaks on every reload.");
        }

        long vramAfter = haveResources ? Perf.SampleVramMb() : 0;
        if (vramBefore > 0 && vramAfter > 0)
            RttLog.Line($"Whole-scene Reset: VRAM {vramBefore} MB -> {vramAfter} MB " +
                        $"({vramAfter - vramBefore:+#;-#;0} MB). Freeing should show a NEGATIVE delta; " +
                        "a flat or positive one across repeated reloads is the leak.");
        _ourDrawContexts = null;
        _ourFreshShadowResources = null;
        _ourFreshFlares = null;
        _dcBuilt = false;
        _dcField = null;
        _cascFld = _charCascFld = null;
        _ownShadowsLogged = _cascadeSettingsLogged = false;
        // Rearm() cleared these and Reset() did not, which is backwards: Reset is the
        // heavier path, taken precisely when the configuration changed.
        _scopeWarned.Clear();
        _skippedLogged.Clear();
        OwnExposure.Reset();
        _state = 0;
        _hookCount = 0;
        _lastLogMs = 0;
        _describedTarget = false;
        _inOurRender = false;
        _miDraw = null;
        _coreType = null;
        _sbField = _rvField = null;
        _settingsObj = null;
        _lastRenderMs = 0;
        _renderCount = 0;
    }

    // Clear the one-strike disable after a config change, WITHOUT throwing away the
    // second ScreenBuffers.
    //
    // RunSecondRender latches _state = -1 on any exception, which is right — a
    // whole-frame render that faults should not retry five times a second while the log
    // is being read. But it made every experiment cost a rebuild, because only a
    // resolution change called Reset(). Re-arming on any whole-scene config edit keeps
    // the safety and returns the fast iteration loop.
    //
    // Deliberately NOT Reset(): that disposes the ScreenBuffers, and rebuilding a full
    // set of render targets to retry a flag change is pure waste.
    public static void Rearm()
    {
        if (_state == -1)
        {
            _state = _describedTarget ? 1 : 0;
            RttLog.Line("Whole-scene: re-armed after a config change (the route had disabled itself " +
                        "on an error). Buffers kept.");
        }
        _skippedLogged.Clear();
        _scopeWarned.Clear();
    }

    // Should a Draw sub-stage be skipped right now?
    //
    // TRUE only while OUR render is running. The engine's own frame must always get
    // every stage — this suppresses work in the second render, not in the game.
    //
    // Settings flags could not reach all of these. ExecuteAccelerationStructuresBuilding
    // is called unconditionally at the top of Draw and checks only
    // EnableGPUParallelization, so clearing RaytracingSettings.Enabled never stopped it:
    // we rebuilt the raytracing acceleration structures on every second render, and
    // RayTracingSceneManager.CreateTLAS is camera-dependent and world-space shared. That
    // is the leak that survived three rounds of settings scoping.
    //
    //   0 ExecuteAccelerationStructuresBuilding    raytracing scene / TLAS
    //   1 ExecuteRaytracingPrepareAndSceneFinalize raytracing prepare
    //   2 RenderEnvironmentProbe                   shared probe atlas (ambient + reflections)
    //   3 RenderShadows                            shadow cascades
    //   4 ComputeExposure                          auto-exposure history
    //   5 UpdateSurfels                            water surfels
    public static bool ShouldSkipStage(int id)
    {
        if (!_inOurRender) return false;

        // Stage 3 and wholeSceneOwnShadows are not independent settings. Owning the
        // resources means our DirectionalLightShadowResources holds OUR cascade depth
        // table — and if RenderShadows never runs, nothing is ever drawn into it. That
        // combination is the exact state that NRE'd the shadow-mask draw the first time
        // a fresh manager was installed, which is why the engine's object got shared in
        // the first place. Refuse the skip rather than let a stale skip list produce it.
        var stages = FeedConfig.WholeSceneSkipStages;

        if (id == 3 && FeedConfig.WholeSceneOwnShadows > 0)
        {
            if (Array.IndexOf(stages, 3) >= 0 && _skippedLogged.Add(-3))
                RttLog.Line("Whole-scene: stage 3 (RenderShadows) is in the skip list but " +
                            "wholeSceneOwnShadows is on — running it anyway. Owning the cascades " +
                            "means rendering into them; drop 3 from wholeSceneSkipStages.");
            return false;
        }

        for (int i = 0; i < stages.Length; i++)
            if (stages[i] == id)
            {
                if (_skippedLogged.Add(id))
                    RttLog.Line($"Whole-scene: skipping stage {id} ({StageName(id)}) during our render.");
                return true;
            }
        return false;
    }

    private static readonly HashSet<int> _skippedLogged = new();

    private static string StageName(int id) => id switch
    {
        0 => "ExecuteAccelerationStructuresBuilding",
        1 => "ExecuteRaytracingPrepareAndSceneFinalize",
        2 => "RenderEnvironmentProbe",
        3 => "RenderShadows",
        4 => "ComputeExposure",
        5 => "UpdateSurfels",
        6 => "PrepareClusters",
        7 => "ProcessParticles",
        8 => "RenderDecals",
        9 => "ExecuteHBAO",
        10 => "ExecuteLighting",
        11 => "RenderMainView",
        12 => "ComputeDirectionalLighting",
        13 => "ComputeLocalLights",
        14 => "ComputeCloudShadows",
        15 => "UpdateAtmosphere",
        16 => "DrawUI",
        17 => "RaytraceGIJob.DoWork (the ray trace itself; ambient still runs)",
        18 => "ComputeGI (ray trace AND ambient)",
        19 => "UpsamplingJob.PrepareResources (stops us re-preparing FSR at 512 and wiping the player's TAA history)",
        20 => "force IsFSREnabledAndAllowed false for our render (no state change)",
        21 => "RenderFlares (we share the engine's FlaresContext; never advance its readback)",
        25 => "ConstantExposure -> read-only (returns the existing exposure view; stops stamping " +
              "a constant into the player's adaptation history)",
        22 => "CloudShadowJob.DoWork — stop writing the SHARED CloudShadowmap from our camera",
        23 => "CloudWeatherMapJob.DoWork — stop writing the SHARED weather map tables",
        24 => "AtmosphereLUTJob.DoWork — stop writing the SHARED per-planet atmosphere LUTs",
        _ => "unknown",
    };

    // Fires after the engine has finished the player's frame.
    public static void OnWholeScene(object sceneDrawSystem, object finalLdrBuffer)
    {
        if (_inOurRender) return;               // our own nested Draw — do nothing

        // The gate is polled HERE because this hook is the one that fires every engine
        // frame regardless of what else is switched on. Polled before the disabled-state
        // check so a dormant mod still notices the panel coming back.
        FeedGate.Poll();
        // The render thread is the only place the gate is allowed to RELEASE anything —
        // disposing from the LCD tick raced the frame recorder and page-faulted.
        FeedGate.PumpRenderThread();
        if (!FeedGate.Active) return;

        if (_state == -1) return;

        try
        {
            _hookCount++;

            // STAGE 1: observe. The engine's own final target tells us exactly what a
            // second one has to match — format and resolution are the two things the
            // earlier attempt got wrong.
            if (!_describedTarget && finalLdrBuffer != null)
            {
                _describedTarget = true;
                _state = 1;
                RttLog.Line($"Whole-scene hook: LIVE. SceneDrawSystem.Draw postfix fired with " +
                            $"{Describe(finalLdrBuffer)}. This is the top of the pipeline — the only " +
                            "site where a second whole-scene render can be driven without re-entering " +
                            "a frame from inside itself.");
                LogScreenBuffers();
            }

            if (FeedConfig.WholeSceneBuildBuffers) EnsureScreenBuffers();

            // STAGE 3: the actual second render.
            //
            // RATE GATED. Draw is a whole frame; at 53 fps an ungated second render would
            // roughly halve the game's frame rate before we have learned anything from it.
            // The gate also means a fault costs one attempt per interval rather than one
            // per frame while we work out what happened.
            // Built HERE, outside the nested Draw, and deliberately. The previous attempt
            // at owning exposure constructed an EyeAdaptationJob inside the Draw bracket
            // and its async PSO compilation raced the render thread into a device
            // removal. This creates only two 1x1 render targets, but the placement rule
            // stands regardless: engine resources get made outside our nested render.
            OwnExposure.Prime(sceneDrawSystem);

            bool oursRan = false;
            if (FeedConfig.WholeSceneEnabled && _ourScreenBuffers != null
                && Clock.Ms - _lastRenderMs >= Math.Max(33, FeedConfig.WholeSceneIntervalMs))
            {
                _lastRenderMs = Clock.Ms;
                oursRan = true;
                RunSecondRender(sceneDrawSystem);
            }

            // Split the frame-interval histogram by whether we rendered this frame. Same
            // thread, same loop — so the difference between the two buckets IS our cost,
            // and if both are equally bad the cost is somewhere else entirely.
            Perf.NoteFrame(oursRan);

            long now = Clock.Ms;
            if (now - _lastLogMs >= 5000)
            {
                _lastLogMs = now;
                RttLog.Line($"Whole-scene hook: {_hookCount} frame(s), " +
                            $"ourScreenBuffers={(_ourScreenBuffers == null ? "not built" : "BUILT")}, " +
                            $"secondRenders={_renderCount}, camera={(FeedConfig.WholeSceneCamera ? "OURS" : "player's")}.");
            }
        }
        catch (Exception e) { _state = -1; RttLog.Error("whole-scene hook", e); }
    }

    // STAGE 3: swap the globals, run a whole second frame, put them back.
    //
    // The entire route is this method. Everything else is scaffolding.
    //
    // WHAT IS SWAPPED, and deliberately how little. Stage 3a moves ONLY
    // CoreSystems.ScreenBuffers, so the second render is the PLAYER'S viewpoint at our
    // resolution. That isolates the one question worth asking first — can Draw run a
    // second time at all with substituted globals — with no camera to confound it. If
    // this works the picture is the player's view at 512x512, which is wrong but
    // verifiable, and the camera becomes one more flip (wholeSceneCamera) rather than
    // part of an unattributable failure. Three things moving at once is what made the
    // deferred route's failures impossible to read.
    //
    // RESTORE ORDERING. The restore is in a finally and runs even if Draw throws
    // mid-frame, because leaving the engine pointed at a 512x512 ScreenBuffers would
    // render the player's next frame into our buffers — visually catastrophic and not
    // obviously attributable to us. The re-entrancy guard is cleared in the same finally
    // for the same reason: a stuck guard silently disables the route forever.
    //
    // ONE STRIKE. Any exception disables the route for the session. A whole-frame render
    // that faults is not something to retry 53 times a second while reading the log.
    private static void RunSecondRender(object sceneDrawSystem)
    {
        if (sceneDrawSystem == null) return;
        try
        {
            _miDraw ??= sceneDrawSystem.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "Draw" && m.GetParameters().Length == 1);
            _coreType ??= Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            _sbField ??= _coreType?.GetField("ScreenBuffers", BindingFlags.Public | BindingFlags.Static);

            if (_miDraw == null || _sbField == null)
            {
                _state = -1;
                RttLog.Line($"Whole-scene: Draw={(_miDraw == null ? "NOT FOUND" : "ok")} " +
                            $"CoreSystems.ScreenBuffers={(_sbField == null ? "NOT FOUND" : "ok")} — route disabled.");
                return;
            }

            // Our own final target, out of our own ScreenBuffers. Draw takes its render
            // resolution from whatever it is handed, so this is what makes the second
            // render 512x512 rather than 4K.
            var ourLdr = _ourScreenBuffers.GetType()
                .GetProperty("FinalLDRTexture", Any)?.GetValue(_ourScreenBuffers);
            if (ourLdr == null)
            {
                _state = -1;
                RttLog.Line("Whole-scene: our ScreenBuffers has no FinalLDRTexture — route disabled.");
                return;
            }

            var savedSb = _sbField.GetValue(null);
            object savedCam = null, savedDc = null;
            object[] savedCb = null;
            object savedExposure = null;
            bool camSwapped = false, ownShadows = false;

            _inOurRender = true;
            try
            {
                _sbField.SetValue(null, _ourScreenBuffers);

                // Swap the context family too, when we have one. Everything our render
                // culls and ranges then lands in contexts the player's frame never
                // reads — which is both the flashing fix and the prerequisite for
                // culling from a different camera at all.
                if (FeedConfig.WholeSceneOwnDrawContexts)
                {
                    EnsureDrawContexts();
                    if (_ourDrawContexts != null)
                    {
                        _dcField ??= _coreType?.GetField("DrawContexts",
                            BindingFlags.Public | BindingFlags.Static);
                        if (_dcField != null)
                        {
                            savedDc = _dcField.GetValue(null);
                            _dcField.SetValue(null, _ourDrawContexts);
                        }
                    }
                }

                ScopeSharedState();
                if (FeedConfig.WholeSceneCamera) camSwapped = InstallCamera(out savedCam);

                // The camera CONSTANT BUFFER, not just the view. The matrix check proved
                // the installed view was rebuilt perfectly — square projection, orbiting
                // At0 pair — and the panel still rendered with the player's aspect and a
                // head-tracked sky. Shaders never read the view: they read the per-frame
                // camera CB, which the engine builds from the PLAYER'S view before Draw
                // runs, and our nested Draw inherits it. Culling and camera-relative
                // positioning read the installed view directly — which is exactly why
                // geometry orbited while the sky did not. Both halves have to be ours.
                //
                // The probe pass has done this precise swap every 33ms for weeks
                // (CameraCbSwap: restore in the same frame bracket, never null, never
                // the same buffer in both fields).
                if (camSwapped && FeedConfig.WholeSceneCameraRebuild >= 2)
                {
                    var cb = CameraRender.WholeSceneCameraCb();
                    if (cb != null)
                    {
                        savedCb = CameraCbSwap.Install(cb);
                        if (!_cbSwapLogged)
                        {
                            _cbSwapLogged = true;
                            RttLog.Line("Whole-scene camera CB: swapped in for our render — shaders now " +
                                        "read the orbit camera's projection, sky rotation and 512x512 " +
                                        "Screen.Resolution instead of inheriting the player's frame CB.");
                        }
                    }
                    else if (_cbSwapErrs++ < 2)
                    {
                        RttLog.Line("Whole-scene camera CB: build failed — feed keeps the player's " +
                                    "projection/sky until it succeeds.");
                    }
                }

                // Last, because it reads BOTH globals we just installed: FlushUpdates
                // fits the cascade frusta to CoreSystems.Settings.RenderView (ours), and
                // the resource rebuild walks CoreSystems.DrawContexts (ours).
                ownShadows = BeginOwnShadows();

                // And our own exposure job, so ComputeExposure — which we cannot skip —
                // stops trampling the player's auto-exposure history every render.
                savedExposure = OwnExposure.Install();

                if (_renderCount == 0)
                    RttLog.Line($"=== WHOLE-SCENE RENDER: calling SceneDrawSystem.Draw a second time, " +
                                $"into our own {FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} " +
                                $"ScreenBuffers. Camera is {(camSwapped ? "OURS" : "the player's")}. ===");

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _miDraw.Invoke(sceneDrawSystem, new[] { ourLdr });
                Perf.NoteOurDraw((System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                                 * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                _renderCount++;

                // The image now sits in our FinalLDRTexture. Delivery to the panel is
                // NOT done here — parking it directly was tried and CTD'd:
                // CopyCommandList.Replay threw E_INVALIDARG, because the raw
                // CopyTextureSubresource path chokes on a resizable engine-internal
                // texture where the probe ring's plain pool textures copy fine. Instead
                // CameraRender's proven blit takes PanelSource as its CopyJob source and
                // the ring/parking machinery stays untouched — the exact pattern its
                // tonemap scratch target already uses in production.

                if (_renderCount == 1)
                    RttLog.Line("=== WHOLE-SCENE RENDER SURVIVED THE FIRST CALL. The engine's entire " +
                                "renderer just ran a second time this frame, into buffers we own. ===");
            }
            finally
            {
                // Unconditional, and in reverse install order: the camera CB first (it
                // must go back inside this same frame bracket — OnEndDraw disposes
                // whatever is in the field), then camera, scoped settings groups, and
                // both global families the engine's next frame renders through.
                OwnExposure.Restore(savedExposure);
                if (ownShadows) EndOwnShadows();
                if (savedCb != null) { try { CameraCbSwap.Restore(savedCb); } catch (Exception e) { RttLog.Error("whole-scene CB restore", e); } }
                if (camSwapped) RestoreCamera(savedCam);
                RestoreScoped();
                if (savedDc != null) _dcField.SetValue(null, savedDc);
                _sbField.SetValue(null, savedSb);
                _inOurRender = false;
            }
        }
        catch (Exception e)
        {
            _state = -1;
            RttLog.Error("whole-scene render (route DISABLED for this session)", e);
        }
    }

    // Scope a settings group off for the duration of our render, and remember how to
    // put it back.
    //
    // GENERALISED ON PURPOSE. The first of these was raytracing, written bespoke; the
    // second (eye adaptation) arrived within minutes, exactly as predicted. Every
    // SettingsManager group is a STRUCT in a private backing field, so they all take the
    // same treatment: box it twice, clear the flags on one, restore the other afterwards.
    // Writing a new method per group would be five copies of this by morning.
    //
    // The saved boxes are stacked and unwound in reverse, so a group added later cannot
    // disturb the restore order of one added earlier.
    private static readonly List<(FieldInfo Field, object Saved)> _scoped = new();

    private static void ScopeOff(string settingsTypeName, string label, params string[] flags)
    {
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (settings == null) return;

            _settingsObj ??= settings;
            var field = settings.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == settingsTypeName);
            if (field == null)
            {
                if (_scopeWarned.Add(settingsTypeName))
                    RttLog.Line($"Whole-scene: SettingsManager has no {settingsTypeName} field — " +
                                $"{label} stays live during our render and will keep leaking into the " +
                                "player's frame.");
                return;
            }

            var saved = field.GetValue(settings);
            var ours = field.GetValue(settings);        // struct field: a second, independent box

            int set = 0;
            foreach (var n in flags)
                if (ClearBool(ours, n)) set++;
            if (set == 0)
            {
                if (_scopeWarned.Add(settingsTypeName))
                    RttLog.Line($"Whole-scene: no matching flags on {settingsTypeName} for {label}.");
                return;
            }

            field.SetValue(settings, ours);
            _scoped.Add((field, saved));

            // Key on the LABEL as well as the type. Keying on the type alone meant two
            // different scopes of the same settings group shared one "already logged"
            // entry — so switching wholeSceneDisableRaytracing from mode 1 to mode 2
            // silently reused mode 1's key and printed nothing, while the config bisect
            // it was there to document was the entire point of the exercise. A log that
            // cannot distinguish the thing under test is worse than no log.
            if (_scopeWarned.Add(settingsTypeName + "|" + label + ":ok"))
                RttLog.Line($"Whole-scene: {label} disabled for our render ({set}/{flags.Length} flags " +
                            $"cleared on {settingsTypeName}).");
        }
        catch (Exception e) { RttLog.Error($"whole-scene scope off {settingsTypeName}", e); }
    }

    // Clear a bool on a boxed struct, following a dotted path through nested structs.
    //
    // Needed because the interesting flags are not all at the top level:
    // EnvironmentSettings.ProbeSettings.Enable is two deep. Nested STRUCTS do not
    // behave like nested objects — GetValue on a struct field returns a COPY, so
    // mutating it changes nothing unless the copy is written back at every level on
    // the way out. That is what the recursion is for, and getting it wrong would look
    // exactly like "the flag had no effect".
    private static bool ClearBool(object box, string path)
    {
        try
        {
            int dot = path.IndexOf('.');
            if (dot < 0)
            {
                var f = box.GetType().GetField(path, Any);
                if (f == null || f.FieldType != typeof(bool)) return false;
                f.SetValue(box, false);
                return true;
            }

            var outer = box.GetType().GetField(path.Substring(0, dot), Any);
            if (outer == null) return false;

            var inner = outer.GetValue(box);            // a COPY when it is a struct
            if (inner == null) return false;
            if (!ClearBool(inner, path.Substring(dot + 1))) return false;

            outer.SetValue(box, inner);                 // write the mutated copy back
            return true;
        }
        catch { return false; }
    }

    // Set one field on a boxed settings struct, following a dotted path into nested
    // structs. Same shape as ClearBool: a struct field read through reflection is a COPY,
    // so every level has to be written back on the way out or the mutation is discarded.
    private static bool SetPath(object box, string path, object value)
    {
        if (box == null) return false;
        int dot = path.IndexOf('.');
        if (dot < 0)
        {
            var f = box.GetType().GetField(path, Any);
            if (f == null) return false;
            object v = value;
            if (f.FieldType.IsEnum && value is int iv) v = Enum.ToObject(f.FieldType, iv);
            else if (f.FieldType == typeof(float)) v = System.Convert.ToSingle(value);
            else if (f.FieldType == typeof(int)) v = System.Convert.ToInt32(value);
            else if (!f.FieldType.IsInstanceOfType(value)) return false;
            f.SetValue(box, v);
            return true;
        }

        var outer = box.GetType().GetField(path.Substring(0, dot), Any);
        if (outer == null) return false;
        var inner = outer.GetValue(box);
        if (inner == null || !SetPath(inner, path.Substring(dot + 1), value)) return false;
        outer.SetValue(box, inner);
        return true;
    }

    // Same box-copy-restore as ScopeOff, but SETS values — enums, floats, bools — so a
    // settings group can be retuned for our render rather than only having flags
    // cleared. Chained boxes compose: a group already scoped this pass is re-boxed from
    // its current (already-modified) value, and the reverse unwind restores the true
    // original last.
    private static void ScopeSetValues(string settingsTypeName, string label,
        params (string Field, object Value)[] sets)
    {
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (settings == null) return;

            _settingsObj ??= settings;
            var field = settings.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == settingsTypeName);
            if (field == null)
            {
                if (_scopeWarned.Add(settingsTypeName + label))
                    RttLog.Line($"Whole-scene: SettingsManager has no {settingsTypeName} field — {label} unavailable.");
                return;
            }

            var saved = field.GetValue(settings);
            var ours = field.GetValue(settings);        // struct field: independent box

            int applied = 0;
            foreach (var (name, value) in sets)
            {
                try { if (SetPath(ours, name, value)) applied++; }
                catch { }
            }
            if (applied == 0)
            {
                if (_scopeWarned.Add(settingsTypeName + label))
                    RttLog.Line($"Whole-scene: no matching fields on {settingsTypeName} for {label}.");
                return;
            }

            field.SetValue(settings, ours);
            _scoped.Add((field, saved));

            if (_scopeWarned.Add(settingsTypeName + label + ":ok"))
                RttLog.Line($"Whole-scene: {label} set for our render ({applied}/{sets.Length} fields on {settingsTypeName}).");
        }
        catch (Exception e) { RttLog.Error($"whole-scene scope set {settingsTypeName}", e); }
    }

    private static void RestoreScoped()
    {
        for (int i = _scoped.Count - 1; i >= 0; i--)
        {
            try { _scoped[i].Field.SetValue(_settingsObj, _scoped[i].Saved); }
            catch (Exception e) { RttLog.Error("whole-scene restore scoped setting", e); }
        }
        _scoped.Clear();
    }

    private static readonly HashSet<string> _scopeWarned = new();

    // Everything our render must not advance on the player's behalf.
    //
    // Each entry here was a visible defect in the PLAYER'S view, not ours — which is the
    // signature of this whole class of problem. Owning a ScreenBuffers isolates image
    // state; it does nothing for state that integrates in world space or across frames.
    private static void ScopeSharedState()
    {
        // RAYTRACING / GI. Draw builds acceleration structures and steps ReSTIR and the
        // IR cache, all of which integrate over frames against the WORLD. Running the
        // pipeline twice per frame advanced them twice: the player's GI went patchy and
        // shifting. Not corruption — over-integration.
        // MODE 1 clears Enabled as well, which stops RaytraceGIJob running at all — and
        // ComputeGI borrows its DiffuseGIBuffer WITHOUT a clear value:
        //
        //     BorrowResizableRWRenderTargetTexture("DiffuseGIBuffer", 26, res,
        //                                          null, 1, /* clear */ null, 0)
        //
        // Safe for the engine, because RaytraceGIJob normally overwrites every pixel.
        // With it skipped, AmbientLightJob reads a RECYCLED, UNCLEARED pool texture whose
        // contents change every frame — which is the ambient flashing on shadowed sides,
        // where the ambient term dominates.
        //
        // MODE 2 keeps Enabled TRUE so the GI job still writes those buffers, and clears
        // only the accumulators that integrate in world space across frames — which were
        // the actual cause of the player's-view GI going patchy. Best of both, in theory:
        // stable feed ambient, player's history still frozen.
        // An explicit flag list beats the presets, which only ever reached six of the
        // twenty booleans on RaytracingSettings. Mode 1 (clearing Enabled) turned out to
        // cause the BRIGHT flashing all by itself — RaytraceGIJob keys a
        // LazyJobSnapshotHandler<RTGISettings, RTGISnapshot> off these settings and builds
        // shader defines from them, so toggling the wrong one forces a pipeline rebuild
        // ten times a second. Mode 2 removed that, but left the subtle per-light flicker,
        // which points at flags no preset touches: LocalLightsInIRCache,
        // LocalLightsInRTXGI, and the EnableReSTIR master that still lets candidates be
        // written into the shared reservoirs.
        if (FeedConfig.WholeSceneRtFlags.Length > 0)
            ScopeOff("RaytracingSettings",
                     "raytracing flags [" + string.Join(",", FeedConfig.WholeSceneRtFlags) + "]",
                     FeedConfig.WholeSceneRtFlags);
        else if (FeedConfig.WholeSceneDisableRaytracing == 1)
            ScopeOff("RaytracingSettings", "raytracing (full, incl. Enabled)",
                     "Enabled", "EnableTemporalReSTIR", "EnableSpatialReSTIR",
                     "EnableTemporalFilter", "EnableIRCache", "EnableIRCacheScrolling");
        else if (FeedConfig.WholeSceneDisableRaytracing == 2)
            ScopeOff("RaytracingSettings", "raytracing accumulators only (GI buffers still written)",
                     "EnableTemporalReSTIR", "EnableSpatialReSTIR",
                     "EnableTemporalFilter", "EnableIRCache", "EnableIRCacheScrolling");

        // EYE ADAPTATION. ComputeExposure drives EyeAdaptationJob, which ping-pongs a
        // shared auto-exposure history — this project already recorded that running it a
        // second time per frame is unsafe. Our 512x512 view of the same scene has a
        // different average luminance, so the player's adaptation oscillates between the
        // two exposures: lighting flickering at exactly our render cadence.
        //
        // Exposure itself is left ON so our image is still exposed; only the TEMPORAL
        // adaptation is cut, which for a fixed-purpose camera feed is arguably correct
        // anyway.
        if (FeedConfig.WholeSceneDisableEyeAdaptation)
            ScopeOff("PostProcessSettings", "eye adaptation", "EyeAdaptation");

        // ENVIRONMENT PROBES. Reported as "reflections or ambient lighting from light
        // sources, but not the lights themselves" — which is indirect lighting exactly,
        // and that is what probes supply.
        //
        // The engine updates probe faces round-robin across frames into a SHARED atlas,
        // driven by DrawContextManager.EnvProbesToUpdate. Our second Draw calls
        // RenderEnvironmentProbe as well, so we both advance that queue at double rate
        // AND write probe faces using our settings — with raytracing already scoped off.
        // The player's frame then samples that atlas for ambient and reflections, which
        // is why the symptom is indirect light and not direct.
        //
        // ProbeSettings.Enable is two levels deep (EnvironmentSettings.ProbeSettings),
        // hence the dotted path. ApplyEnvProbe is deliberately NOT cleared: we want our
        // render to keep USING the probes for ambient, just not to update them. If the
        // feed loses its ambient term anyway, Enable gates both and this needs splitting.
        if (FeedConfig.WholeSceneDisableProbeUpdates)
            ScopeOff("EnvironmentSettings", "environment probe updates", "ProbeSettings.Enable");

        // TEMPORAL AA / UPSCALING. FSR consumes motion vectors, and ours are garbage:
        // the second view's frames are 200ms apart and its previous-frame camera state
        // was the PLAYER'S — ghost trails and edge smear on everything that moves.
        // AAMode: None=0, FXAA=1 (spatial-only, needs no motion vectors — the right AA
        // for this feed), FSR=2 (engine default). ScalingMode NativeAA=4 removes the
        // upscale entirely; sharpening off because it amplifies 512px artefacts.
        // AA MODE — and the reason our render must NOT go through FSR.
        //
        // CORRECTION. This comment used to say DRS was not switchable per-render because
        // AAMode "selects between UpscaleTargetFSR and ApplyNonFSRUpscalingAndAA" at the
        // caller, making them non-interchangeable producer/consumer branches. That model
        // was WRONG. ExecutePostPasses calls BOTH, unconditionally, in sequence:
        //
        //     PatchHoles -> ComputeExposure -> UpscaleTargetFSR -> ApplyBloom
        //         -> ApplyToneMapping -> ApplyNonFSRUpscalingAndAA -> DrawUI
        //
        // Each self-gates internally. And UpscaleTargetFSR's own head is:
        //
        //     bool work = finalLDRBuffer.Resolution != ScreenBuffers.PreUpscaleResolution
        //                 || Settings.IsFSREnabledAndAllowed;
        //     tempLDRBuffer = default; tempHDRBuffer = default;
        //     if (!work) { toneMappingOutput = finalLDRBuffer; toneMappingInput = lBuffer; return; }
        //
        // — an early-out that DOES set both out-params bloom and tonemap consume. Our
        // final target and our ScreenBuffers are both 512x512, so the resolution term is
        // false for us and this early-out is reachable the moment FSR is off.
        //
        // WHY IT MATTERS. IsFSREnabledAndAllowed is just `DRS.AAMode == 2 && debugViewOk`,
        // read off the PLAYER's settings, so today our nested render takes the FSR path:
        // it borrows a TempHDRBuffer at SwapChain.Resolution (the player's 4K, not ours)
        // and dispatches the SHARED FSR3 upscaler. That upscaler is one instance —
        // SceneDrawSystem._upsamplingJob holds a single FSR3_1, whose context, history,
        // FSR3ReactiveMask and FSR3TransparencyCompositionMask are global. Two cameras,
        // one temporal accumulator. The transparency-composition mask is exactly the
        // mechanism by which opaque geometry gets composited as partly see-through, which
        // is the reported "ship is semi-transparent and the skybox shows through it".
        //
        // AAMode: 0 none, 1 FXAA (spatial-only), 2 FSR (engine default). Anything but 2
        // takes us off the shared upscaler entirely.
        //
        // Three earlier CTDs sat behind this knob and are NOT explained away by the
        // above — but two were bundled with other changes, and the third was tested
        // against this wrong model. Worth one clean attempt now that the structure is
        // understood; if it faults again, the next suspect is ScreenBuffers.Update, which
        // also reads IsFSREnabledAndAllowed and has never run on OUR instance.
        if (FeedConfig.WholeSceneAAMode >= 0)
            ScopeSetValues("DRSSettings", $"feed AA mode {FeedConfig.WholeSceneAAMode} (off the shared FSR upscaler)",
                ("AAMode", FeedConfig.WholeSceneAAMode));

        if (FeedConfig.WholeSceneNativeScaling)
            ScopeSetValues("DRSSettings", "feed native scaling (UNSAFE — see comment)",
                ("ScalingMode", 4), ("EnableSharpening", false));

        // FEED EXPOSURE, in EV stops. Adaptation is scoped off (shared history), so the
        // feed runs ConstantExposure.hlsl, whose whole output is
        // exp2(log2(keyValue/ConstantLuminance) + LuminanceExposure) — and with
        // ConstantLuminance == 1 the first term is zero. So this field is a pure signed
        // EV offset on unity: +1 doubles, -1 halves. See FeedConfig.WholeSceneExposure.
        //
        // Gate is `!= 0`, not `> 0`: 0 is the engine's own value AND the neutral EV, so it
        // means "leave alone" for free, while negative values stay reachable. The label
        // carries the value so a re-tune re-logs (the once-per-label guard is by string).
        if (FeedConfig.WholeSceneExposure != 0)
            ScopeSetValues("PostProcessSettings", $"feed exposure {FeedConfig.WholeSceneExposure:+0.##;-0.##} EV",
                ("LuminanceExposure", (float)FeedConfig.WholeSceneExposure));
    }


    // ---- OWN SUN-SHADOW CASCADES ------------------------------------------------------
    //
    // The last big borrowed-lighting item. Until now the feed sampled the ENGINE'S cascade
    // set, which is fitted around the PLAYER: shadows soften and vanish with camera
    // distance, and once the orbit leaves the cascade volume entirely the shadow lookup
    // returns "occluded" for everything — the reported "whole ship goes dark at some points
    // in the orbit".
    //
    // The engine's own setup is DrawContextManager.OnBeginDraw, and it turns out to be
    // almost entirely per-context rather than global:
    //
    //   CascadeShadowsContext.FlushUpdates()
    //       reads  CoreSystems.Settings.RenderView   <- OURS while installed
    //       reads  Settings.Shadow.DirectionalLight, Settings.Light.Sun
    //       mutates only its OWN _cascades / _cascadePriorities / _lastCameraPosition
    //       calls  Cascade.UpdateViewSetupFull(mainView, lightDir)  -> refits every frustum
    //   DirectionalLightShadowResources.OnBeginDraw()
    //       reads  CoreSystems.DrawContexts.CascadeShadows/.CharacterShadows  <- OURS
    //       builds the depth-map Texture2DTable + the setup constant buffer
    //
    // So a second, independent cascade set needs no new machinery at all — just these two
    // calls made while our view and our contexts are installed, and stage 3 allowed to run.
    //
    // What we deliberately do NOT call is DrawContextManager.OnBeginDraw() itself, even
    // though that is the engine's entry point. It also does
    // CoreSystems.LocalLights.FlushUpdates() and EnvironmentProbeManager.PrepareProbes(),
    // both of which drain queues on GLOBAL managers that the player's frame owns. Draining
    // them a second time per frame is precisely the double-stepping class of bug this
    // project has already paid for twice (probe atlas, raytracing accumulators).
    //
    // Leaving LocalLightsToUpdate / ShadowMasksToUpdate unset on our manager is safe:
    // Buffer<T> is a STRUCT (IntPtr _data, int _count, int _capacity), so the unassigned
    // field is a zero-count buffer and RenderLocalLightShadows iterates it zero times. The
    // feed gets no local-light shadows, which is a fidelity gap, not a fault.
    private static bool BeginOwnShadows()
    {
        if (FeedConfig.WholeSceneOwnShadows <= 0) return false;
        if (_ourDrawContexts == null || _ourFreshShadowResources == null) return false;

        try
        {
            var dcType = _ourDrawContexts.GetType();

            // CASCADE COST. Our cascade set is sized from the PLAYER'S graphics settings —
            // CascadesCount cascades at CascadeShadowResolution squared, each a full depth
            // texture, allocated the moment our CascadeShadowsContext was constructed. At
            // 4096 x 8 that is half a gigabyte of VRAM and eight full geometry passes per
            // second render, to shade a 512x512 panel.
            //
            // Scoped, not global: our context only ever flushes during our render, so it
            // resizes itself to these values on the first flush and the engine's own set
            // keeps the player's settings. The two contexts are independent — that is the
            // whole point of owning them.
            if (FeedConfig.WholeSceneCascadeResolution > 0 || FeedConfig.WholeSceneCascadeCount > 0)
            {
                var sets = new System.Collections.Generic.List<(string, object)>();
                if (FeedConfig.WholeSceneCascadeResolution > 0)
                    sets.Add(("DirectionalLight.CascadeShadowResolution", FeedConfig.WholeSceneCascadeResolution));
                if (FeedConfig.WholeSceneCascadeCount > 0)
                    sets.Add(("DirectionalLight.CascadesCount", FeedConfig.WholeSceneCascadeCount));
                LogCascadeSettings();
                ScopeSetValues("ShadowSettings",
                    $"feed cascades {FeedConfig.WholeSceneCascadeResolution}px x {FeedConfig.WholeSceneCascadeCount}",
                    sets.ToArray());
            }

            // Mode 2: make every cascade re-render every time we do. The engine's policy
            // (CascadesUpdateCount per draw, priority-sorted) assumes a 60fps continuous
            // camera; ours moves in 100ms steps, so a round-robin can leave a far cascade
            // several orbit positions stale.
            if (FeedConfig.WholeSceneOwnShadows >= 2)
            {
                var casc = dcType.GetProperty("CascadeShadows", Any)?.GetValue(_ourDrawContexts);
                casc?.GetType().GetField("_forceUpdateAll", Any)?.SetValue(casc, true);
            }

            _cascFld ??= dcType.GetField("CascadesToUpdate", Any);
            _charCascFld ??= dcType.GetField("CharacterCascadesToUpdate", Any);

            // Order matters: OnBeginDraw reads the cascades' DepthTextures, and
            // CharacterShadowsContext allocates its pair lazily inside FlushUpdates.
            FlushInto(dcType, "CascadeShadows", _cascFld);
            FlushInto(dcType, "CharacterShadows", _charCascFld);

            _ourFreshShadowResources.GetType().GetMethod("OnBeginDraw", Any)
                ?.Invoke(_ourFreshShadowResources, null);

            if (!_ownShadowsLogged)
            {
                _ownShadowsLogged = true;
                RttLog.Line($"Whole-scene: OWN SHADOW CASCADES active (mode {FeedConfig.WholeSceneOwnShadows}) — " +
                            "our CascadeShadowsContext refitted every cascade frustum around the ORBIT " +
                            "camera and our DirectionalLightShadowResources rebuilt its depth table from " +
                            "them. Stage 3 renders into our textures; the engine's cascade set is " +
                            "untouched. Local-light shadow requests are empty by design.");
            }
            return true;
        }
        catch (Exception e) { RttLog.Error("whole-scene begin own shadows", e); return false; }
    }

    // Take the per-context update list and store it on our manager, where RenderShadows
    // reads it from. FlushUpdates allocates a Buffer<T> that OnEndDraw would normally
    // dispose; we dispose it ourselves in EndOwnShadows.
    private static void FlushInto(Type dcType, string contextProp, FieldInfo target)
    {
        if (target == null) return;
        var ctx = dcType.GetProperty(contextProp, Any)?.GetValue(_ourDrawContexts);
        var buf = ctx?.GetType().GetMethod("FlushUpdates", Any)?.Invoke(ctx, null);
        if (buf != null) target.SetValue(_ourDrawContexts, buf);
    }

    // Must run BEFORE the DrawContexts global is put back: OnBeginDraw read it, and the
    // symmetric teardown should see the same world.
    private static void EndOwnShadows()
    {
        try
        {
            _ourFreshShadowResources?.GetType().GetMethod("OnEndDraw", Any)
                ?.Invoke(_ourFreshShadowResources, null);

            // Buffer<T> is a struct, so GetValue boxes a COPY — but _data is an IntPtr and
            // the copy points at the same native allocation, so Dispose on the box frees
            // the real thing. Then reset the field to default (a zero-count buffer) so a
            // failed render never leaves a dangling pointer for the next one to iterate.
            DisposeBuffer(_cascFld);
            DisposeBuffer(_charCascFld);
        }
        catch (Exception e) { RttLog.Error("whole-scene end own shadows", e); }
    }

    private static void DisposeBuffer(FieldInfo f)
    {
        if (f == null || _ourDrawContexts == null) return;
        try
        {
            if (f.GetValue(_ourDrawContexts) is IDisposable d) d.Dispose();
            f.SetValue(_ourDrawContexts, Activator.CreateInstance(f.FieldType));
        }
        catch (Exception e) { RttLog.Error($"whole-scene dispose {f.Name}", e); }
    }

    private static FieldInfo _cascFld, _charCascFld;
    private static bool _ownShadowsLogged, _cascadeSettingsLogged;

    // Print what the player's shadow settings actually are, and what our set costs at
    // them, so the size of the knob is a measured number rather than an assumption.
    private static void LogCascadeSettings()
    {
        if (_cascadeSettingsLogged) return;
        _cascadeSettingsLogged = true;
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var shadow = settings?.GetType().GetProperty("Shadow", Any)?.GetValue(settings);
            var dl = shadow?.GetType().GetField("DirectionalLight", Any)?.GetValue(shadow);
            if (dl == null) { RttLog.Line("Whole-scene: could not read ShadowSettings.DirectionalLight."); return; }

            object res = dl.GetType().GetField("CascadeShadowResolution", Any)?.GetValue(dl);
            object cnt = dl.GetType().GetField("CascadesCount", Any)?.GetValue(dl);
            object upd = dl.GetType().GetField("CascadesUpdateCount", Any)?.GetValue(dl);
            object always = dl.GetType().GetField("CascadesAlwaysUpdated", Any)?.GetValue(dl);

            double mb = 0;
            if (res != null && cnt != null)
                mb = System.Convert.ToDouble(res) * System.Convert.ToDouble(res)
                     * System.Convert.ToDouble(cnt) * 4.0 / 1048576.0;

            RttLog.Line($"Whole-scene cascades: player's settings are {res}px x {cnt} cascades " +
                        $"(update {upd}/draw, {always} always) = ~{mb:F0} MB of depth textures for OUR " +
                        "set alone, plus that many geometry passes per second render — to shade a " +
                        $"{FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} panel.");
        }
        catch (Exception e) { RttLog.Error("log cascade settings", e); }
    }

    // The camera half, stage 3b. Writes SettingsManager._renderView, which is the same
    // field CameraRender.InstallOurCamera uses — a proven mechanism, not a new one.
    private static bool InstallCamera(out object saved)
    {
        saved = null;
        try
        {
            var ours = CameraRender.WholeSceneRenderView();
            if (ours == null) return false;

            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _rvField ??= settings?.GetType().GetFields(Any)
                .FirstOrDefault(f => f.Name == "_renderView");
            if (settings == null || _rvField == null) return false;

            _settingsObj = settings;
            saved = _rvField.GetValue(settings);
            _rvField.SetValue(settings, ours);
            return true;
        }
        catch (Exception e) { RttLog.Error("whole-scene camera install", e); return false; }
    }

    private static void RestoreCamera(object saved)
    {
        try { if (_rvField != null && _settingsObj != null) _rvField.SetValue(_settingsObj, saved); }
        catch (Exception e) { RttLog.Error("whole-scene camera restore", e); }
    }

    private static MethodInfo _miDraw;
    private static Type _coreType;
    private static FieldInfo _sbField, _rvField;
    private static object _settingsObj;
    private static long _lastRenderMs;
    private static int _renderCount;

    // Construct the second DrawContextManager. One attempt per load, hot-reloadable,
    // falls back to the shared one (current behaviour) on any failure rather than
    // disabling the route — a shared-context render is degraded, not broken.
    private static void EnsureDrawContexts()
    {
        if (_dcBuilt || _ourDrawContexts != null) return;
        _dcBuilt = true;
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.Core.Systems.DrawContextManager, VRage.Render12");
            if (t == null) { RttLog.Line("Whole-scene: DrawContextManager type not found."); return; }

            _ourDrawContexts = Activator.CreateInstance(t);

            // Share the ENGINE'S DirectionalLightShadowResources into our manager.
            //
            // ComputeDirectionalLighting pulls this off DrawContexts (IL_0068) and hands
            // it to the shadow-mask draw. Our fresh manager's copy has never had cascades
            // rendered into it — our pending-work queues are never filled by the game and
            // we skip RenderShadows anyway — so the mask draw died on an empty Nullable
            // the first time it ran against our contexts.
            //
            // Sharing is safe here where sharing the CONTEXTS was not: the mask draw only
            // READS the resources (cascade depth maps + setup constant buffer). The feed
            // gets the player's real sun-shadow cascades — approximately correct near the
            // player, degrading with distance since cascades are player-centred. Honest
            // limitation, revisit if remote shadows matter.
            //
            // DISPOSE SAFETY: our manager's Dispose would dispose whatever this property
            // holds — which would be the ENGINE'S live object. Reset() puts the fresh one
            // back first, so each side disposes only what it created.
            //
            // ...UNLESS wholeSceneOwnShadows is on, in which case we keep our own and
            // BeginOwnShadows fills it each render. That is the upgrade path out of the
            // limitation above: cascades fitted around OUR camera instead of the player's.
            var resProp = t.GetProperty("DirectionalLightShadowResources", Any);
            var engineDc = _coreType?.GetField("DrawContexts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            bool ownShadows = FeedConfig.WholeSceneOwnShadows > 0;
            if (resProp != null)
            {
                _ourFreshShadowResources = resProp.GetValue(_ourDrawContexts);
                if (!ownShadows && engineDc != null)
                    resProp.SetValue(_ourDrawContexts, resProp.GetValue(engineDc));
            }

            // SHARE THE ENGINE'S FLARES CONTEXT, and skip the flare pass (stage 21).
            //
            // Flare registration goes through the GLOBAL, not through whoever owns the
            // context: PointLightEntityComponent.Init / SetParameters / OnRemovedFromScene,
            // the spot and particle equivalents, and SceneManager.UpdateFlareDefinitions
            // all read CoreSystems.DrawContexts.LensFlares. Our nested Draw swaps that
            // global ten times a second, so a light created, retuned or removed inside one
            // of those windows talks to OUR context — and a SetParameters that lands on
            // the wrong one leaves the engine's copy holding stale parameters. A flare
            // stuck where the light no longer is.
            //
            // That is the best candidate for "the planet's atmosphere appears, completely
            // unattached to the planet". Sharing removes the window: whichever manager is
            // installed, registration reaches the same context.
            //
            // Sharing WITHOUT skipping stage 21 would be worse than the disease, because
            // RenderFlares calls ProcessFinishedFrame and PrepareReadback — the flare
            // occlusion readback, which integrates across frames. We read the
            // definitions; we never advance the state.
            //
            // Our own context is kept for the dispose swap. It was always empty: created
            // by CreateInitialContexts and never given a single definition, because
            // registration goes through the global and the global belongs to the engine
            // whenever a light is actually created. So the feed loses nothing it had.
            var flareProp = t.GetProperty("LensFlares", Any);
            string flareState = "NOT shared — LensFlares unreachable, flare registration during our " +
                                "render window still lands in our empty context";
            if (flareProp != null && engineDc != null)
            {
                _ourFreshFlares = flareProp.GetValue(_ourDrawContexts);
                var engineFlares = flareProp.GetValue(engineDc);
                flareProp.SetValue(_ourDrawContexts, engineFlares);
                flareState = ReferenceEquals(flareProp.GetValue(_ourDrawContexts), engineFlares)
                    ? "SHARED from the engine (registration cannot land in the wrong context; " +
                      "stage 21 keeps us from advancing its occlusion readback)"
                    : "SHARE FAILED — the property did not take the engine's context";
            }

            RttLog.Line("Whole-scene: SECOND DrawContextManager built — its ctor runs " +
                        "CreateInitialContexts, so this is the full context family (visibility " +
                        "lists, occlusion, geometry buffers, shared counters, LOD transitions) " +
                        "owned by us. The player's contexts are no longer written by our render. " +
                        "DirectionalLightShadowResources " +
                        (ownShadows
                            ? $"OURS — own cascades, mode {FeedConfig.WholeSceneOwnShadows}, fitted around the orbit camera."
                            : engineDc != null
                                ? "SHARED from the engine (read-only in the mask draw)."
                                : "NOT shared — engine manager unreachable.") +
                        " LensFlares " + flareState + ".");
        }
        catch (Exception e) { RttLog.Error("build second DrawContextManager", e); }
    }

    private static FieldInfo _dcField;

    // STAGE 2: construct a second ScreenBuffers, and do nothing with it.
    //
    // Deliberately construct-only first. The last two times a context was introduced
    // mid-pipeline the failure was either silent or landed on the player rather than on
    // us; proving construction in isolation costs one launch and removes a whole class
    // of ambiguity from the next step.
    private static void EnsureScreenBuffers()
    {
        if (_sbBuilt || _ourScreenBuffers != null) return;
        _sbBuilt = true;                        // one attempt per load, success or not
        try
        {
            var sbType = Type.GetType("Keen.VRage.Render12.Core.Systems.ScreenBuffers, VRage.Render12");
            if (sbType == null)
            {
                RttLog.Line("Whole-scene: ScreenBuffers type not found.");
                return;
            }

            var ctor = sbType.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                RttLog.Line("Whole-scene: ScreenBuffers has no parameterless constructor after all — " +
                            "the second-instance plan needs rethinking.");
                return;
            }

            var sb = ctor.Invoke(null);

            // InitializeBuffers(in Vector2I maxResolution) is internal and is what the
            // engine's own instance is set up with. Update(cl, maxRes, preUpscaleRes) is
            // the public alternative but wants a command list we do not have here, so
            // the internal one is the right call at construction time.
            var res = MakeVector2I(FeedConfig.WholeSceneWidth, FeedConfig.WholeSceneHeight);
            var init = sbType.GetMethod("InitializeBuffers", Any);
            string how;
            if (init != null && res != null)
            {
                init.Invoke(sb, new[] { res });
                how = $"InitializeBuffers({FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight})";
            }
            else
            {
                how = $"constructed only (InitializeBuffers={(init == null ? "NOT FOUND" : "ok")}, " +
                      $"Vector2I={(res == null ? "NOT BUILT" : "ok")})";
            }

            _ourScreenBuffers = sb;
            RttLog.Line($"Whole-scene: SECOND ScreenBuffers built — {how}. " +
                        $"{DescribeScreenBuffers(sb)} Nothing is wired to it; the engine still owns " +
                        "CoreSystems.ScreenBuffers.");
        }
        catch (Exception e)
        {
            RttLog.Error("build second ScreenBuffers", e);
        }
    }

    private static void LogScreenBuffers()
    {
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var sb = core?.GetField("ScreenBuffers", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            RttLog.Line($"Whole-scene: engine ScreenBuffers — {DescribeScreenBuffers(sb)} " +
                        "Draw sizes its LBuffer from MaxPreUpscaleResolution, which is why a smaller " +
                        "target alone was never going to work.");
        }
        catch (Exception e) { RttLog.Error("describe engine ScreenBuffers", e); }
    }

    private static string DescribeScreenBuffers(object sb)
    {
        if (sb == null) return "ScreenBuffers=null.";
        try
        {
            var t = sb.GetType();
            string maxPre = t.GetProperty("MaxPreUpscaleResolution", Any)?.GetValue(sb)?.ToString() ?? "?";
            string pre = t.GetProperty("PreUpscaleResolution", Any)?.GetValue(sb)?.ToString() ?? "?";
            var gbuf = t.GetProperty("GBuffer", Any)?.GetValue(sb) as Array;
            var depth = t.GetProperty("DepthStencilBuffer", Any)?.GetValue(sb);
            var ldr = t.GetProperty("FinalLDRTexture", Any)?.GetValue(sb);
            return $"maxPreUpscale={maxPre} preUpscale={pre} gbuffer={(gbuf == null ? "null" : gbuf.Length + " targets")} " +
                   $"depth={(depth == null ? "null" : "ok")} finalLDR={(ldr == null ? "null" : "ok")}.";
        }
        catch { return "ScreenBuffers unreadable."; }
    }

    private static string Describe(object tex)
    {
        try
        {
            var t = tex.GetType();
            string res = t.GetProperty("Resolution", Any)?.GetValue(tex)?.ToString() ?? "?";
            return $"{t.Name} resolution={res}";
        }
        catch { return tex?.GetType().Name ?? "null"; }
    }

    // Vector2I is a value type in Keen.VRage.Library.Mathematics; built by reflection so
    // the logic assembly keeps no compile-time reference to the engine.
    private static object MakeVector2I(int x, int y)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Library.Mathematics.Vector2I, VRage.Library")
                    ?? FindTypeAnywhere("Vector2I");
            if (t == null) return null;
            var ctor = t.GetConstructor(new[] { typeof(int), typeof(int) });
            return ctor?.Invoke(new object[] { x, y });
        }
        catch { return null; }
    }

    private static Type FindTypeAnywhere(string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = a.GetTypes().FirstOrDefault(x => x.Name == name);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }
}
