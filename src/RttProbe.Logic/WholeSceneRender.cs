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

    // For the bootstrap's log-only probes (CopyJob / InitializeBuffers): lets a patched
    // engine method say whether it fired inside our nested Draw. ThreadStatic read is
    // correct — the probes fire on the render thread, same as the guard.
    public static bool InOurRender => _inOurRender;

    // Last observed FinalLDR "Resolution|MaxResolution", for the resolution tripwire.
    private static string _lastLdrRes;

    // ---- the FinalLDR resize -----------------------------------------------------
    //
    // The ScreenBuffers CTOR sizes FinalLDRTexture from the player's swapchain, and our
    // InitializeBuffers(512) re-initialises only the pre-upscale chain — so our "512"
    // render has been upscaling 512 -> 3840x2160 into a display-resolution texture every
    // frame since the route first worked, and the panel blit then scaled it back down.
    // Caught by the blit identity log (src=3840, 2160 on a 512 build).
    //
    // Resize is the engine's own designed per-frame path for these textures (HighlightJob
    // and friends resize borrowed targets mid-frame routinely), so this creates nothing.
    // It fixes the wasted 4K upscale and the engine's deferred
    // "Source and destination should have the same resolution" assert. NOTE the limit:
    // Resize changes the CURRENT resolution, not MaxResolution — the pool key — so if the
    // ghost is pool-key aliasing this alone will not clear it. The tripwire above and the
    // bootstrap probes decide that question.
    //
    // Called from the probe hook (render thread, live DirectCommandList — which IS a
    // CopyCommandList by inheritance). One attempt per rebuild.
    // PER-FEED (phase C1a): one resize attempt per feed per rebuild.
    private static bool _ldrResized
    { get => Feeds.Cur.LdrResized; set => Feeds.Cur.LdrResized = value; }

    public static void EnsureFinalLdrSize(object commandList)
    {
        // A/B gate. The resize killed the phantom bleed, but frame times stepped worse at
        // the same moment (>50ms frames 5-9 -> 28-34 per window, CPU submit UNCHANGED — so
        // GPU-side). Flipping this off on a live feed re-runs the rebuild at swapchain
        // size, which separates "the resize costs GPU somehow" from "something else
        // drifted". If off turns out faster AND the ghost stays gone, the ghost was the
        // upscale WRITE, not the texture size — worth knowing either way.
        if (!FeedConfig.WholeSceneLdrResize) return;
        if (_ldrResized || _ourScreenBuffers == null || commandList == null) return;
        _ldrResized = true;
        try
        {
            var ldr = _ourScreenBuffers.GetType().GetProperty("FinalLDRTexture", Any)?.GetValue(_ourScreenBuffers);
            if (ldr == null) return;

            var resProp = ldr.GetType().GetProperty("Resolution", Any);
            var cur = resProp?.GetValue(ldr);
            int w = FeedConfig.WholeSceneWidth, h = FeedConfig.WholeSceneHeight;
            if (cur != null && cur.ToString().Contains($"X:{w}") && cur.ToString().Contains($"Y:{h}"))
                return;     // already right — nothing to log, nothing to do

            var v2i = cur?.GetType();                 // Vector2I, taken from the live value
            var target = v2i == null ? null : Activator.CreateInstance(v2i);
            v2i?.GetField("X")?.SetValue(target, w);
            v2i?.GetField("Y")?.SetValue(target, h);

            var resize = ldr.GetType().GetMethod("Resize", Any);
            if (target == null || resize == null)
            {
                RttLog.Line("FinalLDR resize: Resize/Vector2I unreachable — the 4K upscale stays.");
                return;
            }

            resize.Invoke(ldr, new[] { commandList, target });
            // The prose used to hardcode "512->512", written when 512 was the only
            // resolution — misleading log text is how the ghost hunt lost a day, so it
            // prints the live values now.
            RttLog.Line($"FinalLDR resized: {cur} -> {w}x{h}. Our render now runs {w}x{h}->{w}x{h} " +
                        "(no upscale) instead of ->3840x2160, and the panel blit scales from that. " +
                        "MaxResolution (the pool key) is unchanged by design — watch the tripwire " +
                        "for Draw resizing it back.");
        }
        catch (Exception e) { RttLog.Error("FinalLDR resize", e); }
    }

    private static string LdrRes(object ldr)
    {
        try
        {
            var t = ldr.GetType();
            var res = t.GetProperty("Resolution", Any)?.GetValue(ldr);
            var max = t.GetProperty("MaxResolution", Any)?.GetValue(ldr);
            return $"{res}|max {max}";
        }
        catch { return "?"; }
    }

    // PER-FEED (phase C1a): 0 untried, 1 observed, -1 unavailable. This is the route's
    // health, and per-feed is the graceful-cut contract (goal 7): one feed faulting
    // must mark ITSELF unavailable and leave the others rendering.
    private static int _state
    { get => Feeds.Cur.RouteState; set => Feeds.Cur.RouteState = value; }

    private static long _lastLogMs;
    private static int _hookCount;
    private static bool _describedTarget;

    // Our own screen buffers. NOT the engine's with textures swapped inside it, which is
    // what CameraRender does today — a whole second instance. ScreenBuffers has a public
    // parameterless constructor, and it owns depth, the GBuffer array, the final LDR
    // texture and the pre-upscale resolution, so owning one separates most of the
    // per-view state in a single move.
    // PER-FEED (phase C1a). Rule 25 governs every object below: our teardown may
    // dispose only what THIS instance allocated.
    private static object _ourScreenBuffers
    { get => Feeds.Cur.OurScreenBuffers; set => Feeds.Cur.OurScreenBuffers = value; }
    private static bool _sbBuilt
    { get => Feeds.Cur.SbBuilt; set => Feeds.Cur.SbBuilt = value; }

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
    private static object _ourDrawContexts
    { get => Feeds.Cur.OurDrawContexts; set => Feeds.Cur.OurDrawContexts = value; }

    // The fresh objects our manager's ctor made, kept so the dispose swap puts each
    // side's own back before anything is released.
    private static object _ourFreshShadowResources
    { get => Feeds.Cur.OurFreshShadowResources; set => Feeds.Cur.OurFreshShadowResources = value; }
    private static object _ourFreshFlares
    { get => Feeds.Cur.OurFreshFlares; set => Feeds.Cur.OurFreshFlares = value; }

    private static bool _dcBuilt
    { get => Feeds.Cur.DcBuilt; set => Feeds.Cur.DcBuilt = value; }
    private static object _panelSourceTex
    { get => Feeds.Cur.PanelSourceTex; set => Feeds.Cur.PanelSourceTex = value; }

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

    // Set when Reset() is asked to run while our render is on the stack. Drained in
    // RunSecondRender's finally, once the swaps have been unwound.
    private static bool _resetPending;
    private static bool _deferLogged;

    public static void Reset()
    {
        // ==================================================================
        // NEVER TEAR DOWN WHILE OUR RENDER IS ON THE STACK.
        // ==================================================================
        //
        // FeedConfig.Poll() runs from the camera pass, which is INSIDE the player's Draw
        // and therefore inside our nested render. A config change that alters the rebuild
        // signature calls Reset() from there — and Reset() nulls the very statics the
        // in-flight render's `finally` blocks need to unwind their swaps.
        //
        // CONFIRMED, and it cost two device removals on 2026-07-30 (flipping
        // wholeSceneOwnProbes live). The mod's own log caught the real cause the second
        // time, after the first diagnosis — "disposing GPU textures mid-frame" — turned out
        // to be a plausible story about the wrong line:
        //
        //     ERROR restore probe manager: NullReferenceException at RunSecondRender:928
        //     ERROR ... at WholeSceneRender.RestoreScoped() line 1118
        //
        // Reset() nulled _probeField, so the finally's `_probeField.SetValue(null, saved)`
        // threw — and the ENGINE'S EnvironmentProbeManager was never put back. OUR manager
        // stayed installed in CoreSystems, its textures were then released, and the
        // player's next culling pass bound null: DRED EventStack [CullingProxies,
        // MainViewCulling[FirstPass]], PageFaultVA 0x0, zero existing and zero freed.
        // RestoreScoped() failed identically on _settingsObj, which would have left the
        // player's settings scoped to OUR values as well.
        //
        // This was never a probe bug. It is EVERY finally block in the render, and it has
        // been latent for as long as Poll() has been called from inside the render — the
        // probe flip is simply the first change big enough to make it fatal rather than
        // cosmetic. Deferring is the correct fix precisely because it protects all of them
        // at once, rather than hardening one restore path and leaving the rest.
        if (_inOurRender)
        {
            _resetPending = true;
            if (!_deferLogged)
            {
                _deferLogged = true;
                RttLog.Line("Whole-scene: Reset requested from INSIDE our render — deferred to the " +
                            "end of this render. Tearing down here would null the statics the " +
                            "in-flight finally blocks use to restore the engine's ScreenBuffers, " +
                            "DrawContextManager, probe manager and scoped settings, leaving OUR " +
                            "objects installed in the player's frame.");
            }
            return;
        }

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
        _sbBuilt = false;
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

            // And the FIELD-level version of the same hazard, which the context-level
            // restore above cannot see: with wholeSceneOwnFlares the mirrored DEFINITION
            // members inside _ourFreshFlares are the ENGINE'S objects, and FlaresContext.
            // Dispose reaches all of them — it disposes _flaresBuffer (null-checked, but the
            // mirror made it non-null), iterates _flaresByGuid calling
            // _flareDefinitionsAllocator.Free per entry, and walks _texturePinsByGuid.
            // Two CTDs on 2026-07-29 came from exactly this: the teardown freed the
            // player's flare buffer and the engine's next flare pass dereferenced it.
            // Restoring the ctor originals (captured at first mirror) makes Dispose release
            // precisely what our context allocated and nothing else.
            ScrubMirroredFlareRefs();

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
        // Same class of latch as everything else cleared here: a surviving _engineFlares
        // would point at a disposed context after a rebuild, and a surviving
        // _flareMirrorLogged would swallow the log line that proves the mirror took.
        _engineFlares = null; _flareMirrorLogged = false; _flaresReady = false;
        _flareOriginals = null;   // belt-and-braces: scrub self-clears, but a stale capture
                                  // applied to a NEW context would write another context's
                                  // objects into it, which is this same bug wearing a hat

        // OUR PROBE MANAGER — KEPT. NOT DISPOSED, NOT QUEUED, NOT HANDED ANYWHERE.
        //
        // THREE device removals bought this one sentence, on 2026-07-30, all from flipping
        // wholeSceneOwnProbes on a live feed, and all with the same DRED breadcrumb:
        // EventStack [CullingProxies, MainViewCulling[FirstPass], ScenePreparation +
        // Render], PageFaultVA 0x0, zero existing AND zero freed allocations — a NULL BIND
        // in the PLAYER'S culling pass. Steady-state own-probes had already soaked an hour
        // clean before any of them, so the feature was never the problem; the teardown was.
        //
        //   Attempt 1 — dispose here. Reset() runs from FeedConfig.Poll on the RENDER
        //     THREAD, inside the player's frame. Freeing GPU textures mid-frame is the
        //     fault family this project has recorded more than any other.
        //   Attempt 2 — defer it to the LCD tick, off the render thread. Same crash. It
        //     did fix a real and SEPARATE bug (the NRE that left our manager installed
        //     after Reset nulled the finally blocks' statics — that fix is kept, see the
        //     deferred-Reset guard at the top of this method), which is why attempt 3
        //     arrived with a clean mod log and nothing to blame.
        //   Attempt 3 — same crash again, and the conclusion: OFF THE RENDER THREAD IS NOT
        //     THE SAME AS OUTSIDE A FRAME. The LCD tick runs while the render thread is
        //     rendering. There is no safe moment to free these while the renderer is live.
        //
        // So it is simply kept, and keeping it costs nothing structural: the manager is
        // independent of the ScreenBuffers and DrawContextManager this Reset rebuilds, its
        // textures are sized by ProbeSettings rather than by our resolution, and
        // constructing it was always free. Turning the feature off just stops InstallProbes
        // swapping it in. WholeSceneOwnProbes is out of the rebuild signature too, so
        // flipping it no longer reaches this path at all.
        //
        // ==================================================================
        // ...AND "KEPT" WAS A LIE ACROSS HOT RELOADS. CTD 2026-07-30 18:46.
        // ==================================================================
        //
        // This comment used to end: "eight cube textures stay resident until the game
        // restarts. That is VRAM, not correctness." BOTH HALVES WERE WRONG, and the game
        // died proving it:
        //
        //     Assertion Failure: Out of the descriptor heap
        //       at DescriptorHeapPool.BorrowRTV()
        //       at RenderTargetCubeTexture.FaceMips.Initialize()
        //       at EnvironmentProbeManager.RecreateProbes()
        //       at WholeSceneRender.InstallProbes()
        //     [Watchdog]: application froze, RenderThreadFreeze. Capturing dump.
        //
        // WRONG #1 — it is not VRAM, it is RTV DESCRIPTORS. Those live in a small fixed
        // pool and exhaust long before memory does. VRAM sat flat at 12.2 GB for the whole
        // session while this accumulated, which is precisely why every instrument we had
        // looked healthy right up to the crash.
        //
        // WRONG #2 — it is not "until the game restarts", it is ONCE PER HOT RELOAD. The
        // logic assembly is COLLECTIBLE. _ourProbes lives in it (on FeedInstance since
        // C1a, which changes nothing here — that static is replaced either way), so every
        // reload starts null, the ??= below builds a FRESH manager, and the previous one is
        // unreachable from any code that could free it. Not disposing it was a deliberate
        // choice; losing the reference to it was not. Four reloads in one session was
        // enough to run the pool dry.
        //
        // The reasoning error is worth naming: "we must not dispose this" was established
        // by three device removals and is still correct. "Therefore it costs nothing to
        // keep" did not follow, and was never tested — it is an assumption that rode in on
        // the back of a well-evidenced conclusion. A fix's PROVEN part does not confer
        // confidence on the sentence next to it.
        //
        // THE FIX: park the manager in the BOOTSTRAP assembly, which is not collectible and
        // survives logic reloads, so the reference outlives the code that made it and
        // "kept" finally means kept — one manager per process, created once, never orphaned.
        // Disposal stays forbidden; this is about not LOSING it, not about freeing it.
        //
        // If the memory ever genuinely needs reclaiming, the only defensible place remains a
        // QUIESCED renderer — gate shutdown with the feed already dormant — not "some other
        // thread".
        _probeState = 0; _probeLogged = false;
        _dcBuilt = false;
        // The failure budget resets with the thing it was counting. A gate cycle IS the
        // documented retry for a feed that gave up, so leaving this at 3 would make that
        // retry a no-op.
        _dcFailures = 0;
        _dcField = null;
        _cascFld = _charCascFld = null;
        _ownShadowsLogged = _cascadeSettingsLogged = false;
        _planetEnvGroup = null;
        _fPeFrustum = _fPeSetupsData = _fPeSetupsCbs = _fPeFirst = _fPeFirstData = null;
        _fPeSpheres = _fPeSpheresData = null;
        _miPeFillSetups = _miPeFillSlim = _miPeSetMatrix = _miPeCreateCb = null;
        _pPeModifiersCtx = null;
        _peBufMgr = null;
        _peSaved = null;
        _planetEnvState = 0;
        _planetEnvLogged = _planetEnvCountsLogged = _peEmptyLogged = false;
        // Rearm() cleared these and Reset() did not, which is backwards: Reset is the
        // heavier path, taken precisely when the configuration changed.
        _scopeWarned.Clear();
        _skippedLogged.Clear();
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
        _lastLdrRes = null;
        _ldrResized = false;
        _earlyRan = _earlyOwnsThisFrame = false;
        _renderCount = 0;

        // Arm the settle window. Reset is exactly the event that forces the shared probe
        // manager to reprocess, so this is where the countdown belongs — it then covers a
        // config save, a hot reload and a gate restart alike, not just the one case that
        // happened to crash.
        _settleFrames = SettleFrames;

        // DO NOT force a panel rebind from here. Tried 2026-07-29 and it CRASHED THE GAME:
        // the panel showed its test pattern (a fresh target, rebound before any feed frame
        // had landed) and then the process died.
        //
        // Why it is unsafe: this Reset runs from FeedConfig.Poll on the RENDER THREAD, and
        // clearing PanelBinding._bound makes the next panel tick call
        // SetNewScreenMaterialHandle — which is ReleaseScreenMaterialHandle plus
        // CreateRuntimeLcdMaterial, i.e. destroying and building a runtime material from
        // inside the frame. That is the same family as every other "create engine resources
        // mid-frame" fault this project has recorded (Rule 11).
        //
        // The freeze it was meant to fix is real but NOT diagnosed — the causal link to
        // this Reset was inferred, never proven, and the crash suggests the inference was
        // wrong. A gate cycle clears it; that is the workaround until it is understood.
        // Suspects to test properly: whether BlitProbe.FeedTarget actually changes across a
        // WholeSceneRender.Reset (it should not — the panel binds to that, not to our
        // ScreenBuffers), and whether CameraRender's cached _feedTexture/_resolvedPanelId
        // go stale independently.
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

        // Stage 21 and wholeSceneOwnFlares are the same kind of pair. Owning the
        // FlaresContext is pointless if RenderFlares never runs, and the ONLY reason 21 is
        // in the default skip list is that we used to install the ENGINE'S context — where
        // running the pass would have advanced the player's occlusion readback twice a
        // frame. With our own context installed that hazard is gone, so refuse the skip
        // rather than let a stale skip list silently produce "own flares, no flares".
        // Stage 2 and wholeSceneOwnProbes, same pair as 3/ownShadows and 21/ownFlares. Owning
        // a probe manager and filling its queue is pointless if RenderEnvironmentProbe never
        // runs — the queue would just be discarded, and we would pay PrepareProbes and the
        // eight cube textures for nothing. Gated on _probeState, not on the config flag,
        // because that is the lesson stage 21 taught: intent is not readiness.
        if (id == 2 && FeedConfig.WholeSceneOwnProbes && _probeState > 0)
        {
            if (Array.IndexOf(stages, 2) >= 0 && _skippedLogged.Add(-2))
                RttLog.Line("Whole-scene: stage 2 (RenderEnvironmentProbe) is in the skip list but " +
                            "wholeSceneOwnProbes is on and our probe manager is installed — running it " +
                            "anyway. The skip existed because our queue was always empty and the atlas " +
                            "was the player's; both are now ours. Drop 2 from wholeSceneSkipStages.");
            return false;
        }

        // _flaresReady, NOT the config flag. See the comment on _flaresReady: force-running
        // this on intent alone dereferenced a null _flaresBuffer and took the game down.
        if (id == 21 && FeedConfig.WholeSceneOwnFlares && _flaresReady)
        {
            if (Array.IndexOf(stages, 21) >= 0 && _skippedLogged.Add(-21))
                RttLog.Line("Whole-scene: stage 21 (RenderFlares) is in the skip list but " +
                            "wholeSceneOwnFlares is on and our FlaresContext has a definition " +
                            "buffer — running it anyway. The skip existed only because we shared " +
                            "the engine's context; we now own ours, so the readback we advance is " +
                            "our own. Drop 21 from wholeSceneSkipStages.");
            return false;
        }

        // Own-flares requested but our context is not ready. Say so ONCE and fall through to
        // the skip list, which keeps 21 skipped. Silence here is what turned a missing buffer
        // into a crash.
        if (id == 21 && FeedConfig.WholeSceneOwnFlares && !_flaresReady && _skippedLogged.Add(-121))
            RttLog.Line("Whole-scene: wholeSceneOwnFlares is on but our FlaresContext has no " +
                        "_flaresBuffer yet — stage 21 stays SKIPPED this render rather than " +
                        "dereferencing null in GetFlareConstants. Normal for the first renders " +
                        "after a reload or rebuild; if it never clears, the definition mirror is " +
                        "failing and the feed will simply have no flares.");

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
        26 => "CloudJob.DoWork — stop disposing/recreating the SHARED CloudAccumulateLightAlpha " +
              "temporal resource at our half-resolution (confirmed device removal; costs the feed " +
              "volumetric clouds only, not planet atmospheres)",
        27 => "EnvironmentProbeManager.PrepareProbes — stop advancing the SHARED probe state " +
              "machine a second time per frame (confirmed device removal at 30fps; costs nothing, " +
              "stage 2 already discards the queue)",
        28 => "LocalLightsManager.FlushUpdates — stop draining the SHARED local-light shadow " +
              "update queue in our render (the feed uses the player's local-light shadows)",
        29 => "ScreenSpaceReflections.DoWork — THE PHANTOM BLEED. Stop writing our scene's " +
              "radiance into the SHARED SSR temporal history (AverageRadianceHistory / " +
              "VarianceHistory / SampleCountHistory live on SceneDrawSystem._screenSpaceReflectionsJob, " +
              "which we do not swap), so the player's reflective surfaces stop showing the feed",
        _ => "unknown",
    };

    // Fires after the engine has finished the player's frame.
    //
    // THE RENDER-THREAD PUMP (phase C1b). This hook and the prefix below are
    // SCHEDULER-driven: nothing in the engine's call names a feed, so we pick one and scope
    // every piece of per-feed state the frame touches to it. The scope must wrap the WHOLE
    // body, not just the render — ShouldSkipStage is called from deep inside the nested
    // Draw with no arguments identifying a feed, so it can only read the ambient, and the
    // ambient has to still be set when it does.
    public static void OnWholeScene(object sceneDrawSystem, object finalLdrBuffer)
    {
        if (_inOurRender) return;               // our own nested Draw — do nothing

        // OUTSIDE the render-slot scope, and deliberately so. The render thread is the only
        // place the gate may RELEASE anything (disposing from the LCD tick raced the frame
        // recorder and page-faulted), but WHICH feed gets released must not depend on which
        // feed holds this frame's render slot — the slot stops advancing the moment feeds go
        // dormant, which is exactly when the countdowns need to run. See FeedGate.PumpAll:
        // scheduling teardown on the render slot orphaned a whole feed's resources per gate
        // cycle and cost a device removal.
        FeedGate.PumpAll();

        using (Feeds.Enter(Feeds.NextForRender()))
            OnWholeSceneScoped(sceneDrawSystem, finalLdrBuffer);
    }

    private static void OnWholeSceneScoped(object sceneDrawSystem, object finalLdrBuffer)
    {
        // The gate is polled HERE because this hook is the one that fires every engine
        // frame regardless of what else is switched on. Polled before the disabled-state
        // check so a dormant mod still notices the panel coming back.
        FeedGate.Poll();
        if (!FeedGate.Active) return;

        if (_state == -1) return;

        try
        {
            _hookCount++;

            // STAGE 1: observe. The engine's own final target tells us exactly what a
            // second one has to match — format and resolution are the two things the
            // earlier attempt got wrong.
            //
            // THE STATE TRANSITION AND THE LOG LATCH ARE SEPARATE, and conflating them cost
            // the whole first two-feed evening.
            //
            // This used to be one block: `if (!_describedTarget) { _describedTarget = true;
            // _state = 1; ...log... }`. _describedTarget is process-global — correctly, it
            // describes the ENGINE'S final target, which is the same for everyone — but
            // _state is PER-FEED. So feed 0 ran first, claimed the latch, and set ITS OWN
            // _state to 1. Feed 1 never entered the block, its _state stayed 0 forever, and
            // PanelSource requires _state == 1 — so feed 1's source view was permanently
            // null, its copy failed with wholeSceneSrv=False, it never parked a frame, and
            // its panel was black. Everything else about feed 1 was healthy: own target, own
            // 1024x1024 buffers, own LDR ring, rendering and settling normally.
            //
            // This is EXACTLY the hazard the C1a inventory called out — "a log latch that
            // also gates behaviour" — written down, and then walked into anyway, because the
            // gating was one line inside something that reads as pure diagnostics.
            //
            // The rule this earns: when splitting state into per-feed and global, the
            // question is not "is this field per-feed" but "is every ASSIGNMENT to it
            // reachable by every feed".
            if (finalLdrBuffer != null) _state = 1;

            if (!_describedTarget && finalLdrBuffer != null)
            {
                _describedTarget = true;
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

            // Which end of Draw owns the render was decided by the prefix at the top of
            // THIS frame (see OnWholeSceneEarly). Read the recorded decision rather than
            // the config: FeedConfig.Poll runs from the camera pass, which fires INSIDE
            // the player's Draw — i.e. between our prefix and our postfix — so re-reading
            // the flag here could see it flip mid-frame and render twice in one frame.
            bool oursRan = _earlyRan;
            _earlyRan = false;
            if (!_earlyOwnsThisFrame) oursRan = TryRender(sceneDrawSystem);

            Perf.NoteFrame(oursRan);

            long now = Clock.Ms;
            if (now - _lastLogMs >= 5000)
            {
                _lastLogMs = now;
                RttLog.Line($"Whole-scene hook: {_hookCount} frame(s), " +
                            $"ourScreenBuffers={(_ourScreenBuffers == null ? "not built" : "BUILT")}, " +
                            $"secondRenders={_renderCount}, camera={(FeedConfig.WholeSceneCamera ? "OURS" : "player's")}, " +
                            $"submit={(_earlyOwnsThisFrame ? "START-of-frame" : "end-of-frame")}.");
            }
        }
        catch (Exception e) { _state = -1; RttLog.Error("whole-scene hook", e); }
    }

    // Set by the prefix each frame; read by the postfix. Not ThreadStatic — both hooks
    // fire on the render thread, and the prefix always precedes the postfix within a frame.
    private static bool _earlyRan, _earlyOwnsThisFrame;

    // START-OF-FRAME SUBMISSION. The targeted fix for the session drift.
    //
    // Measured 2026-07-28: our render's true GPU work is only ~3 ms, but an ours-frame
    // costs ~30 ms because the GPU sits IDLE waiting — and that idle grows with engine
    // session age (10 ms of bubbles at ~50 min, none when dormant) and survives a full
    // teardown of everything we own. Only a process restart resets it, so the reservoir is
    // engine-side; our render is just the thing that pays for it, because it sits between
    // the player's recorded work and the present copy.
    //
    // Recording our render HERE instead puts our commands ahead of the player's, so the
    // GPU executes them while the CPU is still recording the player's frame — time the GPU
    // spent idle anyway. It does not shrink the bubbles; it moves them somewhere they cost
    // nothing. Today's ~10-13 ms of gaps fit inside the player's ~15 ms record window.
    //
    // The feed image is one frame older than it would otherwise be. At 30 fps that is
    // ~33 ms of extra latency on a slowly orbiting camera — irrelevant.
    public static void OnWholeSceneEarly(object sceneDrawSystem, object finalLdrBuffer)
    {
        if (_inOurRender) return;               // our own nested Draw
        using (Feeds.Enter(Feeds.NextForRender()))
            OnWholeSceneEarlyScoped(sceneDrawSystem);
    }

    // Scoped to the SAME feed the postfix will pick, because NextForRender only advances
    // when a render completes — so the prefix and postfix of one engine frame always agree
    // on whose frame it is. That invariant is what lets _earlyRan / _earlyOwnsThisFrame stay
    // plain per-frame statics rather than becoming per-feed state.
    private static void OnWholeSceneEarlyScoped(object sceneDrawSystem)
    {
        // Cleared HERE, at frame start, not just after the postfix reads it. If the postfix
        // bails early — gate went dormant mid-frame, _state faulted — a stale true would
        // survive into the next frame and mis-bucket a Perf sample. That histogram is the
        // instrument this whole change is judged by, so it does not get to lie.
        _earlyRan = false;

        // Recorded unconditionally, before any early-out, so the postfix always has a
        // coherent answer for this frame even when we decline to render.
        _earlyOwnsThisFrame = FeedConfig.WholeSceneSubmitEarly;
        if (!_earlyOwnsThisFrame) return;       // the postfix owns it

        // The gate is polled and the buffers are built by the POSTFIX. On the very first
        // frames that leaves nothing to render from, so we simply decline and pick it up
        // next frame — one frame of startup latency, no special case.
        if (!FeedGate.Active || _state == -1 || _ourScreenBuffers == null) return;

        try { _earlyRan = TryRender(sceneDrawSystem); }
        catch (Exception e) { _state = -1; RttLog.Error("whole-scene early hook", e); }
    }

    // The settle countdown, the rate gate and the render itself. Called from EXACTLY ONE
    // of the two hooks per engine frame — whichever owns this frame — because both the
    // countdown and the rate stamp are per-frame state that must not tick twice.
    private static bool TryRender(object sceneDrawSystem)
    {
        // SETTLE AFTER A REBUILD, and this one cost a device removal to find.
            //
            // Reset() disposes and re-creates our ScreenBuffers and DrawContextManager, and
            // creating a DrawContextManager trips the engine's context-reset path — which
            // sets EnvironmentProbeManager._forceReprocess (OnResetContext is one of its two
            // writers). The engine's next frame therefore force-reprocesses EVERY probe: the
            // DRED dump from the crash showed a long queue of EnvProbe_Blending passes still
            // outstanding, and the fault was a null bind (PageFaultVA 0x0, zero existing and
            // zero freed allocations) inside that batch, while the probe cube textures were
            // being recreated.
            //
            // Rendering a second whole scene inside that window is what faulted. The trigger
            // was raising wholeSceneIntervalMs to 33 on a LIVE feed: at 100 ms there were
            // ~3 engine frames of slack and we never landed in the window, at 33 ms we landed
            // in it on the very first render — 2.1 s after the config save, at
            // secondRenders=1. Proven not to be a rate problem by booting straight into 33 ms
            // with no mid-session Reset: stable indefinitely at ~27 renders/sec.
            //
            // So: after any (re)build, let the engine have a few frames to itself. Frames,
            // not milliseconds — the thing being waited for is engine frames completing, and
            // during a mass probe reprocess those frames are long.
            if (_settleFrames > 0)
            {
                _settleFrames--;
                if (_settleFrames == 0)
                    RttLog.Line($"Whole-scene: settled after the rebuild ({SettleFrames} frames); " +
                                "second renders resume. This window exists because a rebuild forces the " +
                                "shared EnvironmentProbeManager to reprocess every probe, and rendering " +
                                "into that batch is a device removal.");
            return false;
        }

        // No hard floor any more (was Math.Max(33, ...)). The 30fps cap was a safety rail
        // from the era when a fault cost a CTD per attempt; the route is stable now and the
        // cost model is a straight trade, so the slider is the user's. Multi-feed budgeting
        // (see docs/roadmap.md) will sit on top of this same gate later.
        if (!FeedConfig.WholeSceneEnabled || _ourScreenBuffers == null) return false;
        if (Clock.Ms - _lastRenderMs < FeedConfig.WholeSceneIntervalMs) return false;

        _lastRenderMs = Clock.Ms;
        RunSecondRender(sceneDrawSystem);

        // THE SLOT ADVANCES HERE, and only here: after a render actually happened. Every
        // early return above this line — dormant gate, settling after a rebuild, rate gate,
        // route disabled — leaves the rotation where it is, so a feed that declines its turn
        // keeps it rather than forfeiting it to the next feed forever. With one feed this is
        // a no-op; with N it is the difference between fair rotation and starvation.
        Feeds.AdvanceSlot();
        return true;
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
            object savedCam = null, savedDc = null, savedProbes = null;
            object[] savedCb = null;
            bool camSwapped = false, ownShadows = false, planetEnvSwapped = false;

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

                // Re-share the flare DEFINITIONS every render (goal 4.3). Cheap — four
                // reference assignments — and it is what keeps a replaced _flaresBuffer from
                // leaving the feed permanently stale. No-op unless wholeSceneOwnFlares is on.
                if (FeedConfig.WholeSceneOwnFlares) MirrorFlareDefinitions();

                // AFTER the DrawContextManager swap, because it writes our context's
                // EnvProbesToUpdate and reads _ourDrawContexts to find the field. No-op
                // unless wholeSceneOwnProbes is on.
                savedProbes = InstallProbes();

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
                                        "read the orbit camera's projection, sky rotation and " +
                                        $"{FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} " +
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

                // After the camera install: it reads the INSTALLED view, which must be
                // ours by now for the rebuild to mean anything.
                if (camSwapped) planetEnvSwapped = RebuildPlanetEnv();

                // The exposure bleed is handled by the stage-25 Harmony override
                // (ConstantExposure becomes read-only for our render), not by owning a
                // second EyeAdaptationJob. Two attempts at owning one removed the device:
                // constructing the job ran InitializeAsync's PSO compile against the live
                // recorder, and creating 1x1 targets put resources outside the engine's
                // AutoResourceState tracking. Both are recorded in docs/whole-scene-status.md.

                if (_renderCount == 0)
                    RttLog.Line($"=== WHOLE-SCENE RENDER: calling SceneDrawSystem.Draw a second time, " +
                                $"into our own {FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} " +
                                $"ScreenBuffers. Camera is {(camSwapped ? "OURS" : "the player's")}. ===");

                // RESOLUTION TRIPWIRE. The blit identity log caught our FinalLDRTexture at
                // 3840x2160 — the PLAYER'S resolution — after being built at 512. If that
                // resize happens across ONE nested Draw, our own Draw's upscale tail is
                // doing it from player display state; if it happens elsewhere, an engine
                // path is touching our instance. Either way this names the frame it flips.
                // MaxResolution is logged too because it is the POOL KEY: the moment ours
                // says 3840x2160 our textures share borrow keys with the player's pool,
                // and the aliasing that was "excluded" is excluded no longer.
                string before = LdrRes(ourLdr);

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _miDraw.Invoke(sceneDrawSystem, new[] { ourLdr });
                Perf.NoteOurDraw((System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                                 * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                _renderCount++;

                string after = LdrRes(ourLdr);
                if (before != after || (_lastLdrRes != null && _lastLdrRes != before))
                    RttLog.Line($"!!! FinalLDR resolution moved: lastRender='{_lastLdrRes ?? "n/a"}' " +
                                $"beforeDraw='{before}' afterDraw='{after}' (render #{_renderCount}). " +
                                (before != after
                                    ? "OUR nested Draw resized it — the upscale tail is using player display state."
                                    : "It moved BETWEEN our renders — an engine-side path touched our instance."));
                _lastLdrRes = after;

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
                if (ownShadows) EndOwnShadows();
                if (savedCb != null) { try { CameraCbSwap.Restore(savedCb); } catch (Exception e) { RttLog.Error("whole-scene CB restore", e); } }
                if (camSwapped) RestoreCamera(savedCam);
                // After RestoreCamera and before anything else: the rebuild reads the
                // installed view, and this run regenerates the planet sort, the
                // weather-modifier culling and all the setup CBs from the PLAYER'S view.
                if (planetEnvSwapped) RestorePlanetEnv();
                RestoreScoped();
                // Before the DrawContextManager goes back, so the ordering mirrors install.
                // The FieldInfo is re-read rather than trusted: a deferred Reset cannot null
                // it any more, but this restore is the one whose failure leaves OUR probe
                // manager installed in the player's frame, so it carries its own guard.
                if (savedProbes != null)
                {
                    try
                    {
                        var pf = _probeField ?? Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12")
                            ?.GetField("EnvironmentProbeManager", BindingFlags.Public | BindingFlags.Static);
                        pf?.SetValue(null, savedProbes);
                    }
                    catch (Exception e) { RttLog.Error("restore probe manager", e); }
                }
                if (savedDc != null) _dcField.SetValue(null, savedDc);
                _sbField.SetValue(null, savedSb);
                _inOurRender = false;

                // Now that every swap is unwound and _inOurRender is clear, a Reset that
                // arrived mid-render can safely run.
                if (_resetPending)
                {
                    _resetPending = false;
                    RttLog.Line("Whole-scene: running the deferred Reset now that the render has unwound.");

                    // ACROSS EVERY FEED, not just the one that was rendering (phase C3
                    // prerequisite). A deferred Reset comes from a config signature change,
                    // and quality is GLOBAL by design — see docs/phase2-design.md: it is the
                    // user's VRAM throttle, not a per-feed property. So a resolution change
                    // has to rebuild ALL of them, or the feeds that did not happen to hold
                    // the render slot when Poll() fired would keep ScreenBuffers at the old
                    // size indefinitely, and nothing would ever tell us. At Count == 1 this
                    // is the single Reset it always was.
                    //
                    // ForEachSlot: Reset RELEASES, so it must reach slots that have just
                    // dropped out of Count — a signature change is exactly how feedCount
                    // shrinks, and the feed being retired is the one still holding a
                    // ScreenBuffers nothing will ask for again.
                    Feeds.ForEachSlot(Reset);
                }
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
        // NOT when we own the probes. Scoping Enable off exists solely to stop our render
        // updating the SHARED atlas; with our own manager installed there is no shared atlas
        // to protect, and leaving it off would make our own PrepareProbes do nothing — the
        // feature would silently be a no-op with no error to explain it. The two settings are
        // coupled, so the coupling is enforced here rather than left to the config file.
        if (FeedConfig.WholeSceneDisableProbeUpdates && !FeedConfig.WholeSceneOwnProbes)
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

        // BLOOM. Candidate for the phantom bleed, and the only remaining shared object in
        // the composite tail.
        //
        // BloomJob holds _tmpBloomCascadeDown / _tmpBloomCascadeUp — arrays of BORROWED
        // textures retained across calls — plus _tmpMaxCascadeResolutions, a cached
        // per-cascade resolution. The job is SceneDrawSystem._bloomJob, the singleton we do
        // not swap, so our 512x512 render and the player's 4K frame drive the same cascade
        // set with different resolutions. That is the CloudJob shape, and bloom output is
        // additive, blurry and full-screen — which is what the ghost looks like.
        //
        // Scoped rather than skipped, deliberately. ApplyBloom's signature is
        // (..., out Borrowed bloom): skipping BloomJob.DoWork would leave that out-param
        // unset, the same NRE that makes stage 4 unskippable. With this flag false the
        // engine takes its OWN disabled path, which borrows a 1x1 black bloom — designed,
        // and safe.
        //
        // Rule 11 says settings scopes leak. Checked first: PostProcessSettings.Bloom is a
        // plain field read inside ApplyBloom and feeds no shader define (the only BLOOM
        // strings in the assembly are shader FILE PATHS), so it cannot trigger the async
        // PSO rebuild that made the RaytracingSettings scopes dangerous.
        //
        // Cost to the feed while on: no bloom in the feed.
        if (FeedConfig.WholeSceneNoBloom)
            ScopeSetValues("PostProcessSettings",
                "feed bloom OFF (shared BloomJob retains its cascade borrows across renders)",
                ("Bloom", false));

        // FLARE INTENSITY, feed only. GetFlareConstants reads FlaresIntensity straight into
        // FlaresConstantData.IntensityMultiplier, so this reaches the flare pass and nothing
        // else. Only meaningful while wholeSceneOwnFlares is on — with flares off the pass
        // never runs and the scope is a no-op, which is harmless and not worth a gate on.
        // See FeedConfig.WholeSceneFlareIntensity for why this and not emissivity.
        if (FeedConfig.WholeSceneFlareIntensity >= 0)
            ScopeSetValues("LightSettings",
                $"feed flare intensity {FeedConfig.WholeSceneFlareIntensity:0.###} " +
                "(fixed feed exposure cannot pull a blown flare back, and the panel multiplies by emissivity)",
                ("FlaresIntensity", (float)FeedConfig.WholeSceneFlareIntensity));
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
            if (FeedConfig.WholeSceneCascadeResolution > 0 || FeedConfig.WholeSceneCascadeCount > 0
                || FeedConfig.WholeSceneCharacterShadowResolution > 0)
            {
                var sets = new System.Collections.Generic.List<(string, object)>();
                if (FeedConfig.WholeSceneCascadeResolution > 0)
                    sets.Add(("DirectionalLight.CascadeShadowResolution", FeedConfig.WholeSceneCascadeResolution));
                if (FeedConfig.WholeSceneCascadeCount > 0)
                    sets.Add(("DirectionalLight.CascadesCount", FeedConfig.WholeSceneCascadeCount));

                // CHARACTER SHADOWS — the third sizing field, and we were scoping only two.
                //
                // Found by the resource report, not by reading: CharacterShadows was 32 MiB
                // of a 444 MiB feed, two 2048x2048 depth sets (first- and third-person), for
                // a camera orbiting a ship at 100 m where the player's character is not in
                // shot at all.
                //
                // Same mechanism as the cascades above, confirmed in IL rather than assumed:
                //
                //     CharacterShadowsContext..ctor      -> CheckShadowSettingChanged()
                //     CharacterShadowsContext.FlushUpdates -> CheckShadowSettingChanged()
                //     CheckShadowSettingChanged reads
                //         CoreSystems.Settings.Shadow.DirectionalLight.CharacterShadowResolution
                //         and calls ResizeCascades(int) when it differs from the current size.
                //
                // Because FlushUpdates re-checks every render, scoping is enough — no need to
                // touch construction. OUR context flushes only inside OUR render and sees our
                // value; the engine's flushes in the player's frame and sees theirs. Each
                // resizes once and then stays put, so there is no per-frame thrash.
                if (FeedConfig.WholeSceneCharacterShadowResolution > 0)
                    sets.Add(("DirectionalLight.CharacterShadowResolution",
                              FeedConfig.WholeSceneCharacterShadowResolution));

                LogCascadeSettings();
                ScopeSetValues("ShadowSettings",
                    $"feed cascades {FeedConfig.WholeSceneCascadeResolution}px x {FeedConfig.WholeSceneCascadeCount}" +
                    (FeedConfig.WholeSceneCharacterShadowResolution > 0
                        ? $", character shadows {FeedConfig.WholeSceneCharacterShadowResolution}px" : ""),
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

    // ---- PLANET ENVIRONMENT REBUILD ---------------------------------------------------
    //
    // The reported symptom was precise: the feed's planet ATMOSPHERE detaches from the
    // planet and moves with the PLAYER'S aim. PlanetEnvironmentGroup.OnBeginDraw builds
    // PlanetSpheres / PlanetEnvSetupFirst / AllPlanetEnvSetups from
    // SettingsManager.RenderView — the player's camera — once per frame, before our
    // nested Draw, which then inherits them. Twenty-six jobs read these, including
    // AtmosphereAdditiveJob (the atmosphere itself), VolumeRendering, LocalFog, clouds
    // and the GBuffer/deferred-texturing passes. Same bug class as the camera constant
    // buffer: per-frame data built from the player's view, silently adopted by a nested
    // render.
    //
    // The fix leans on a property confirmed from the IL: OnBeginDraw has NO drains and
    // no cross-frame accumulation. It sorts the global planet list by camera distance,
    // fills the LUT/weather tables, culls weather modifiers, and creates TRANSIENT
    // constant buffers (the allocator CameraCbSwap has used safely for weeks). Every
    // side effect is fully regenerated by running it again — so the swap is symmetric:
    // run it under OUR view before our Draw, run it again under the PLAYER'S view after.
    // The second run does not merely restore the buffers, it restores the planet sort
    // order and the weather-modifier culling too. Nothing is created that the transient
    // allocator does not reclaim, and nothing needs field surgery.
    // TWO FAILED SHAPES, then this one. Attempt 1 invoked the whole OnBeginDraw and
    // OOR'd immediately: its output lists are append-only (the engine clears them once
    // per frame upstream), so a mid-frame re-run doubled the CB list against a replaced
    // data list. Attempt 2 cleared the three lists first — and the alignment FIX WAS
    // CONFIRMED VISUALLY — but died ~2.5 minutes later in the descriptor heap:
    //
    //     ArgumentException: An item with the same key has already been added.
    //         Key: DescriptorHeapPool+Token
    //       at DescriptorHeapPool.CreateSRV
    //       at TextureCubeTable.GetD3DGpuDescriptorHandle()
    //       at CloudShadowJob.JobSnapshot.Draw          <- the PLAYER's frame
    //
    // Clearing and refilling _atmosphereLUTTables/_weatherMapTables makes the engine
    // re-create descriptor TABLES twenty times a second — Rule 11 in disguise: we did
    // not create GPU resources, we made the ENGINE create them on our schedule, and the
    // descriptor pool's tokens eventually collided.
    //
    // So this rebuild is NARROW. The atmosphere's position lives in the SETUP CBs
    // (camera-relative planet transforms); the LUT/weather tables are camera-independent
    // texture registries. We re-run only:
    //     FillPlanetEnvironmentSetups (static; clears + refills the data list, culled by
    //                                  the given frustum)
    //     the CB-list rebuild          (transient CBs — the proven allocator)
    //     FillPlanetEnvironmentSlimSetup (writes the spheres data + CB itself)
    // and never touch SortEntities or the table fills. Skipping the sort also PRESERVES
    // the player's planet order, which is what keeps setups[i] pointing at the right
    // AtmosphereLUTTables[i].
    // v4, after v3 page-faulted at VA 0x0. Two defects found offline, no game harmed:
    //
    //   * THE EMPTY CASE. When the orbit camera's frustum culls ALL planets (it points
    //     at the ship for most of the orbit), v3 wrote null into _planetEnvSetupFirst —
    //     and a consumer reading a Nullable via GetValueOrDefault binds a DEFAULT
    //     TransientConstantBuffer, i.e. GPU address zero. PageFaultVA 0x0, surfacing
    //     frames later because the GPU executes behind the recorder. v4 never writes the
    //     swap at all when our frustum yields no planets — no planets in view means the
    //     misalignment is invisible anyway.
    //
    //   * OVERLOAD AMBIGUITY. CreateTransientConstantBuffer has TWO 2-param generic
    //     overloads — (String, in TData) and (String, ReadOnlySpan<TData>) — and v3
    //     picked whichever enumerated first. v4 selects the byref (in TData) one
    //     explicitly.
    //
    //   * RESTORE IS NOW VERBATIM. v3 re-ran the fill under the player's view, which
    //     mutated the weather-modifier fade state a second time and built a second batch
    //     of CBs. v4 snapshots the six outputs (two lists, four fields) before touching
    //     anything and puts the SAME values back — same frame, so the saved transient
    //     CBs are still valid, and late readers see bit-identical state.
    private static object _planetEnvGroup;
    private static FieldInfo _fPeFrustum, _fPeSetupsData, _fPeSetupsCbs, _fPeFirst, _fPeFirstData;
    private static FieldInfo _fPeSpheres, _fPeSpheresData;
    private static MethodInfo _miPeFillSetups, _miPeFillSlim, _miPeSetMatrix, _miPeCreateCb;
    private static System.Reflection.PropertyInfo _pPeModifiersCtx;
    private static object _peBufMgr;
    private static int _planetEnvState;      // 0 untried, 1 ok, -1 unavailable
    private static bool _planetEnvLogged, _peEmptyLogged;

    // Snapshot of the player's planet-env outputs, restored verbatim in the finally.
    private sealed class PeSaved
    {
        public object[] Cbs, Data;
        public object First, FirstData, Spheres, SpheresData;
    }
    private static PeSaved _peSaved;

    private static object[] SnapshotList(System.Collections.IList l)
    {
        var a = new object[l.Count];
        l.CopyTo(a, 0);
        return a;
    }

    private static void RefillList(System.Collections.IList l, object[] items)
    {
        l.Clear();
        foreach (var it in items) l.Add(it);
    }

    // AtmosphereAdditiveJob's loop indexes AtmosphereLUTTables[i] with the SETUPS index
    // (read from its IL), so the tables must never be shorter than the setups list. The
    // narrow rebuild leaves the tables at the full planet count and can only ever CULL
    // setups below it, so the invariant holds structurally — this guard is a tripwire in
    // case that reasoning is wrong, not a crutch it leans on.
    private static bool _planetEnvCountsLogged;

    private static void GuardPlanetEnvInvariant(string when)
    {
        try
        {
            var cbs = _fPeSetupsCbs.GetValue(_planetEnvGroup) as System.Collections.IList;
            var luts = _planetEnvGroup.GetType().GetField("_atmosphereLUTTables", Any)
                ?.GetValue(_planetEnvGroup) as System.Collections.IList;
            if (cbs == null || luts == null) return;

            if (!_planetEnvCountsLogged)
            {
                _planetEnvCountsLogged = true;
                RttLog.Line($"Planet env counts ({when}): setups={cbs.Count} lutTables={luts.Count}.");
            }
            if (luts.Count != 0 && luts.Count < cbs.Count)
            {
                RttLog.Line($"Planet env INVARIANT BROKEN ({when}): {cbs.Count} setups but only " +
                            $"{luts.Count} LUT tables — trimming setups to match rather than let " +
                            "AtmosphereAdditiveJob index out of range.");
                while (cbs.Count > luts.Count) cbs.RemoveAt(cbs.Count - 1);
            }
        }
        catch (Exception e) { RttLog.Error("planet env invariant guard", e); }
    }

    private static bool RebuildPlanetEnv()
    {
        if (!FeedConfig.WholeScenePlanetEnv || _planetEnvState == -1) return false;
        try
        {
            if (_planetEnvState == 0)
            {
                _planetEnvState = -1;
                var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                var common = core?.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(f => f.FieldType.Name.Contains("CommonResourcesManager"))?.GetValue(null);
                _planetEnvGroup = common?.GetType().GetFields(Any)
                    .FirstOrDefault(f => f.FieldType.Name == "PlanetEnvironmentGroup")?.GetValue(common);
                if (_planetEnvGroup == null)
                {
                    RttLog.Line("Whole-scene: PlanetEnvironmentGroup unreachable — the feed's planet " +
                                "atmosphere stays positioned by the player's aim.");
                    return false;
                }
                var gt = _planetEnvGroup.GetType();
                _fPeFrustum = gt.GetField("_cameraFrustum", Any);
                _fPeSetupsData = gt.GetField("_allPlanetEnvSetupsData", Any);
                _fPeSetupsCbs = gt.GetField("_allPlanetEnvironmentSetups", Any);
                _fPeFirst = gt.GetField("_planetEnvSetupFirst", Any);
                _fPeFirstData = gt.GetField("_planetEnvSetupFirstData", Any);
                _fPeSpheres = gt.GetField("_allPlanetSpheres", Any);
                _fPeSpheresData = gt.GetField("_allPlanetSpheresData", Any);
                _miPeFillSetups = gt.GetMethod("FillPlanetEnvironmentSetups", Any);
                _miPeFillSlim = gt.GetMethod("FillPlanetEnvironmentSlimSetup", Any);
                _pPeModifiersCtx = gt.GetProperty("MainViewModifiersContext", Any);
                _miPeSetMatrix = _fPeFrustum?.FieldType.GetMethod("SetMatrix", Any);

                _peBufMgr = _coreType?.GetField("BindableBuffers", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var setupType = _fPeSetupsData?.FieldType.GetGenericArguments().FirstOrDefault();
                // TWO 2-param generic overloads exist: (String, in TData) and
                // (String, ReadOnlySpan<TData>). The in-parameter one is byref; select it
                // explicitly rather than by enumeration order.
                var miCreate = _peBufMgr?.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "CreateTransientConstantBuffer" && m.IsGenericMethod
                                      && m.GetParameters().Length == 2
                                      && m.GetParameters()[1].ParameterType.IsByRef);
                if (setupType != null && miCreate != null)
                    _miPeCreateCb = miCreate.MakeGenericMethod(setupType);

                if (_fPeFrustum == null || _fPeSetupsData == null || _fPeSetupsCbs == null
                    || _fPeFirst == null || _fPeFirstData == null || _miPeFillSetups == null
                    || _fPeSpheres == null || _fPeSpheresData == null
                    || _miPeFillSlim == null || _pPeModifiersCtx == null || _miPeSetMatrix == null
                    || _miPeCreateCb == null)
                {
                    RttLog.Line("Whole-scene: planet env narrow-rebuild members not all found — " +
                                "disabled. The atmosphere stays positioned by the player's aim.");
                    return false;
                }
                _planetEnvState = 1;
            }

            if (!RebuildFromInstalledView("our view")) return false;
            if (!_planetEnvLogged)
            {
                _planetEnvLogged = true;
                RttLog.Line("=== PLANET ENV REBUILT (narrow) for our render: the setup CBs and planet " +
                            "spheres now come from the ORBIT camera — atmosphere on the planet, not on " +
                            "the player's aim. SortEntities and the LUT/weather TABLE fills are NOT " +
                            "re-run (descriptor churn there killed attempt 2), so the player's planet " +
                            "order and descriptor tables are untouched. Rebuilt from the player's view " +
                            "after our Draw. ===");
            }
            return true;
        }
        catch (Exception e) { _planetEnvState = -1; RttLog.Error("whole-scene planet env rebuild", e); return false; }
    }

    // Rebuild the setup data + CBs + spheres from the INSTALLED (our) view, after
    // snapshotting the player's outputs for a verbatim restore. Returns false — with the
    // player's state fully intact — when our frustum sees no planets.
    private static bool RebuildFromInstalledView(string label)
    {
        var settings = _coreType?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var rv = settings?.GetType().GetProperty("RenderView", Any)?.GetValue(settings);
        if (rv == null) return false;

        var viewD = rv.GetType().GetProperty("ViewD", Any)?.GetValue(rv);
        var proj = rv.GetType().GetProperty("JitteredProjection", Any)?.GetValue(rv);
        var viewProjD = proj?.GetType().GetProperty("ViewProjectionD", Any)?.GetValue(proj);
        if (viewD == null || viewProjD == null) return false;

        var data = (System.Collections.IList)_fPeSetupsData.GetValue(_planetEnvGroup);
        var cbs = (System.Collections.IList)_fPeSetupsCbs.GetValue(_planetEnvGroup);

        // Snapshot BEFORE any mutation. The restore puts these exact values back — same
        // frame, so the transient CBs inside are still live.
        _peSaved = new PeSaved
        {
            Cbs = SnapshotList(cbs),
            Data = SnapshotList(data),
            First = _fPeFirst.GetValue(_planetEnvGroup),
            FirstData = _fPeFirstData.GetValue(_planetEnvGroup),
            Spheres = _fPeSpheres.GetValue(_planetEnvGroup),
            SpheresData = _fPeSpheresData.GetValue(_planetEnvGroup),
        };

        // The frustum is group-internal scratch, re-set by the engine every OnBeginDraw.
        var frustum = _fPeFrustum.GetValue(_planetEnvGroup);
        _miPeSetMatrix.Invoke(frustum, new[] { viewProjD });
        if (_fPeFrustum.FieldType.IsValueType) _fPeFrustum.SetValue(_planetEnvGroup, frustum);

        // Static: (ref List setups, in MatrixD, in CullingFrustumD, ctx). Clears and
        // refills the data list itself; write the ref slot back in case it reallocated.
        var fillArgs = new[]
        {
            data, viewD, frustum,
            _pPeModifiersCtx.GetValue(_planetEnvGroup),
        };
        _miPeFillSetups.Invoke(null, fillArgs);
        _fPeSetupsData.SetValue(_planetEnvGroup, fillArgs[0]);
        data = (System.Collections.IList)fillArgs[0];

        // THE EMPTY CASE — the page fault in v3. No planets in our frustum means no
        // planet is visible in the feed, so the swap buys nothing: put the player's data
        // back and leave every field untouched. NEVER write a null First — a consumer
        // reading it via GetValueOrDefault binds a constant buffer at GPU address zero.
        if (data.Count == 0)
        {
            RefillList(data, _peSaved.Data);
            _peSaved = null;
            if (!_peEmptyLogged)
            {
                _peEmptyLogged = true;
                RttLog.Line("Planet env: orbit frustum sees no planets this render — swap skipped, " +
                            "player state untouched. (Normal for the part of the orbit facing away.)");
            }
            return false;
        }

        cbs.Clear();
        foreach (var item in data)
            cbs.Add(_miPeCreateCb.Invoke(_peBufMgr, new[] { "Planet Environment Setups", item }));

        _fPeFirstData.SetValue(_planetEnvGroup, data[0]);
        _fPeFirst.SetValue(_planetEnvGroup,
            _miPeCreateCb.Invoke(_peBufMgr, new[] { "planetEnvironmentSetup0", data[0] }));

        // Writes _allPlanetSpheresData AND the _allPlanetSpheres CB itself.
        _miPeFillSlim.Invoke(_planetEnvGroup, new[] { viewD });

        GuardPlanetEnvInvariant(label);
        return true;
    }

    // Verbatim put-back of the snapshot — no second fill, no second weather-culling
    // mutation, no new CBs. Runs in the finally regardless of camera-restore order
    // because it touches only the group's own outputs.
    private static void RestorePlanetEnv()
    {
        try
        {
            if (_planetEnvState != 1 || _peSaved == null) return;
            var s = _peSaved;
            _peSaved = null;

            RefillList((System.Collections.IList)_fPeSetupsCbs.GetValue(_planetEnvGroup), s.Cbs);
            RefillList((System.Collections.IList)_fPeSetupsData.GetValue(_planetEnvGroup), s.Data);
            _fPeFirst.SetValue(_planetEnvGroup, s.First);
            _fPeFirstData.SetValue(_planetEnvGroup, s.FirstData);
            _fPeSpheres.SetValue(_planetEnvGroup, s.Spheres);
            _fPeSpheresData.SetValue(_planetEnvGroup, s.SpheresData);
        }
        catch (Exception e) { RttLog.Error("whole-scene planet env restore", e); }
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

    // PER-FEED (phase C1a): this feed's rate stamp. The phase E slot scheduler replaces
    // the comparison against WholeSceneIntervalMs with "is it my turn", but the stamp
    // itself stays exactly here, one per feed.
    private static long _lastRenderMs
    { get => Feeds.Cur.LastRenderMs; set => Feeds.Cur.LastRenderMs = value; }

    // Engine frames to yield after a (re)build before the first second render. 30 is ~0.5 s
    // at 60 fps and comfortably longer than the probe reprocess measured in the crash dump,
    // while being invisible at a config save.
    private const int SettleFrames = 30;

    // PER-FEED (phase C1a). Per-feed settling is also what phase E3 needs: a global
    // quality change with N feeds live rebuilds them STAGGERED, one settle window each,
    // rather than dropping every feed into the same probe-reprocess window at once.
    private static int _settleFrames
    { get => Feeds.Cur.SettleFrames; set => Feeds.Cur.SettleFrames = value; }
    private static int _renderCount
    { get => Feeds.Cur.RenderCount; set => Feeds.Cur.RenderCount = value; }

    // Construct the second DrawContextManager. One attempt per load, hot-reloadable,
    // falls back to the shared one (current behaviour) on any failure rather than
    // disabling the route — a shared-context render is degraded, not broken.
    private static void EnsureDrawContexts()
    {
        if (_dcBuilt || _ourDrawContexts != null) return;

        // THE LATCH MEANS "SUCCEEDED", NOT "ATTEMPTED". It used to be set right here, before
        // a single line of construction ran — so the `if (t == null) return` below, and any
        // exception inside the try, left _dcBuilt = true with _ourDrawContexts = null, for
        // the rest of the session, never retried.
        //
        // That never crashed anything, which is exactly why it survived: a null context is
        // survivable by design (our render falls back to the engine's contexts — degraded,
        // not broken). But it means a transient build failure silently downgrades the feed
        // permanently, and the ONLY evidence would be the absence of a log line. This is the
        // same shape as the CopyToFeed view-lookup latch that made feed 1 render 291 frames
        // into a black panel, and it was found the same way: looking for what did NOT get
        // logged.
        //
        // Now: the success latch is set on success, and failure gets its own budget so a
        // genuinely broken build stops retrying — and SAYS SO, once, instead of going quiet.
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.Core.Systems.DrawContextManager, VRage.Render12");
            if (t == null) { RttLog.Line("Whole-scene: DrawContextManager type not found."); NoteDcFailure(); return; }

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
            // With wholeSceneOwnFlares ON we take the other route: keep OUR context and
            // share only its DEFINITION members, so the feed gets real flares while the
            // player's occlusion readback stays ours-proof. See FeedConfig.WholeSceneOwnFlares
            // for the full offline verification and the one bounded risk.
            var flareProp = t.GetProperty("LensFlares", Any);
            string flareState = "NOT shared — LensFlares unreachable, flare registration during our " +
                                "render window still lands in our empty context";
            if (flareProp != null && engineDc != null)
            {
                _ourFreshFlares = flareProp.GetValue(_ourDrawContexts);
                var engineFlares = flareProp.GetValue(engineDc);

                if (FeedConfig.WholeSceneOwnFlares)
                {
                    // Ours stays installed. Remember the engine's so MirrorFlareDefinitions
                    // can re-read the definition members before every render.
                    _engineFlares = engineFlares;
                    int copied = MirrorFlareDefinitions();
                    flareState = _ourFreshFlares == null
                        ? "OWN FLARES REQUESTED but our context is null — falling back to none"
                        : $"OURS, with {copied}/{FlareDefFields.Length} definition members shared from the " +
                          "engine (stage 21 now RUNS: the feed renders flares against our own " +
                          "occlusion buffers, so the player's readback is never advanced by us)";
                }
                else
                {
                    flareProp.SetValue(_ourDrawContexts, engineFlares);
                    _engineFlares = null;
                    flareState = ReferenceEquals(flareProp.GetValue(_ourDrawContexts), engineFlares)
                        ? "SHARED from the engine (registration cannot land in the wrong context; " +
                          "stage 21 keeps us from advancing its occlusion readback)"
                        : "SHARE FAILED — the property did not take the engine's context";
                }
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

            // ONLY here, with the manager actually constructed and configured.
            _dcBuilt = true;
            _dcFailures = 0;
        }
        catch (Exception e)
        {
            RttLog.Error("build second DrawContextManager", e);
            NoteDcFailure();
        }
    }

    // Give up after a few consecutive failures so a genuinely broken build is not retried
    // every frame forever, and say so ONCE when that happens. Silence is what made the
    // original latch invisible; a feed running on the engine's shared contexts is a real
    // degradation and the log should name it rather than leave it to be inferred.
    // PER-FEED, like _dcBuilt itself. A shared counter would let feed 0's three failures
    // latch feed 1 out of ever building its own contexts — which is precisely the bug shape
    // fixed twice already tonight (the copy budget, and the gate's startup flag).
    private static int _dcFailures
    { get => Feeds.Cur.DcFailures; set => Feeds.Cur.DcFailures = value; }

    private static void NoteDcFailure()
    {
        _ourDrawContexts = null;                 // never leave a half-built manager installed
        if (++_dcFailures < 3) return;
        _dcBuilt = true;                         // stop retrying
        RttLog.Line("!!! Whole-scene: our DrawContextManager failed to build 3 times running. Giving up " +
                    "for this feed — it will render against the ENGINE'S shared contexts, which is degraded " +
                    "(the player's visibility lists and counters get written by our render too). A gate cycle " +
                    "retries. This line exists because the previous behaviour was to fail silently and forever.");
    }

    // ---- OWN FLARES CONTEXT, SHARED DEFINITIONS (goal 4.3) ----------------------------
    //
    // The four members that carry WHAT the flares are, as opposed to the per-frame state of
    // drawing them. Verified with EngineQuery to be written only by FlaresContext..ctor and
    // UpdateFlaresBuffer, neither of which is in the render path — so pointing ours at the
    // engine's cannot be mutated by our own render.
    private static readonly string[] FlareDefFields =
    {
        "_flaresByGuid",               // Dictionary<Guid, FlareHandle> — the registry
        "_texturePinsByGuid",          // Dictionary<Guid, (ManagedTexturePin, int)>
        "_flaresBuffer",               // IManagedROBuffer — the GPU definition buffer (RO)
        "_flareDefinitionsAllocator",  // SimpleIndexAllocator — index -> buffer slot
    };

    // PER-FEED (phase C1a): the engine's context, BORROWED. Per-feed because each
    // instance's mirror is paired with its own OurFreshFlares and its own originals —
    // and mixing those pairs across feeds is precisely the Rule-25 mistake that
    // disposed the engine's flare buffer twice.
    private static object _engineFlares
    { get => Feeds.Cur.EngineFlares; set => Feeds.Cur.EngineFlares = value; }

    private static bool _flareMirrorLogged;

    // THE INVARIANT THAT WAS MISSING, and it cost a CTD on 2026-07-29.
    //
    // Stage 21 was force-run on FeedConfig.WholeSceneOwnFlares alone, while
    // MirrorFlareDefinitions returns 0 SILENTLY whenever either context is null. A hot
    // reload nulls both statics in Reset(), the next render force-ran RenderFlares before
    // the DrawContextManager rebuild had re-established them, and FlaresContext.
    // GetFlareConstants dereferenced a null _flaresBuffer:
    //
    //     NullReferenceException at FlaresContext.GetFlareConstants()
    //       at FlaresOcclusionJob.DoWork -> RenderFlares_Patch1 -> Draw_Patch1
    //
    // "Own the context" and "run the pass" are one decision, not two, and the config flag
    // only expresses the INTENT. This flag expresses the FACT: our context really does have
    // a flare definition buffer right now. Stage 21 consults this, so a mirror that fails
    // for any reason degrades to the old behaviour — no flares in the feed — instead of
    // taking the process down.
    private static bool _flaresReady
    { get => Feeds.Cur.FlaresReady; set => Feeds.Cur.FlaresReady = value; }

    // Re-read the definition members from the engine's context into ours. Called before
    // EVERY render, not once at build time, deliberately: UpdateFlaresBuffer REPLACES
    // _flaresBuffer on whichever context is installed, so a one-shot copy could go stale
    // and stay stale. Re-reading makes the worst case "one frame behind", not "wrong
    // forever". Returns how many members were successfully shared.
    // The ctor-original values of the four mirrored fields, captured before the FIRST
    // overwrite. These are what FlaresContext.Dispose is entitled to see at teardown:
    // _flaresBuffer null (the ctor never writes it; Dispose null-checks it), the empty
    // dictionaries and the allocator the ctor built. ScrubMirroredFlareRefs writes them
    // back before our DrawContextManager is disposed. Nulling instead would NRE inside
    // Dispose — it iterates _flaresByGuid unguarded (verified in IL).
    private static object[] _flareOriginals
    { get => Feeds.Cur.FlareOriginals; set => Feeds.Cur.FlareOriginals = value; }

    private static void ScrubMirroredFlareRefs()
    {
        _flaresReady = false;
        if (_ourFreshFlares == null || _flareOriginals == null) { _flareOriginals = null; return; }
        try
        {
            var ft = _ourFreshFlares.GetType();
            for (int i = 0; i < FlareDefFields.Length; i++)
            {
                var f = ft.GetField(FlareDefFields[i], Any);
                f?.SetValue(_ourFreshFlares, _flareOriginals[i]);
            }
            RttLog.Line("Whole-scene flares: mirrored ENGINE references scrubbed from our context " +
                        "(ctor originals restored) before its dispose — the teardown can no longer " +
                        "free the player's flare buffer or drain its definition allocator.");
        }
        catch (Exception e) { RttLog.Error("scrub mirrored flare refs", e); }
        finally { _flareOriginals = null; }
    }

    private static int MirrorFlareDefinitions()
    {
        _flaresReady = false;
        if (_engineFlares == null || _ourFreshFlares == null) return 0;
        int copied = 0;
        var missing = new List<string>();
        try
        {
            var ft = _ourFreshFlares.GetType();

            // Capture the ctor originals ONCE, before anything is overwritten. Not per
            // render — after the first mirror these fields hold the engine's objects, and
            // capturing those as "originals" would defeat the entire scrub.
            if (_flareOriginals == null)
            {
                _flareOriginals = new object[FlareDefFields.Length];
                for (int i = 0; i < FlareDefFields.Length; i++)
                    _flareOriginals[i] = ft.GetField(FlareDefFields[i], Any)?.GetValue(_ourFreshFlares);
            }

            foreach (var name in FlareDefFields)
            {
                var f = ft.GetField(name, Any);
                if (f == null) { missing.Add(name); continue; }
                f.SetValue(_ourFreshFlares, f.GetValue(_engineFlares));
                copied++;
            }

            // The one field the flare pass CANNOT tolerate as null: GetFlareConstants does
            // `_flaresBuffer.GPUBufferId` unguarded. Note it is legitimately null on the
            // ENGINE'S context too until the first AddFlare/UpdateFlare, so this is a real
            // runtime state and not only a mirror failure — which is exactly why the check
            // belongs here, on every render, rather than once at build time.
            var bufField = ft.GetField("_flaresBuffer", Any);
            _flaresReady = bufField != null && bufField.GetValue(_ourFreshFlares) != null;
        }
        catch (Exception e)
        {
            _flaresReady = false;
            if (!_flareMirrorLogged) { _flareMirrorLogged = true; RttLog.Error("mirror flare definitions", e); }
            return copied;
        }

        // Name the missing member rather than failing silently — a renamed private field is
        // the likeliest way this breaks on a game update, and "no flares in the feed" with
        // no explanation is the worst possible symptom.
        if (missing.Count > 0 && !_flareMirrorLogged)
        {
            _flareMirrorLogged = true;
            RttLog.Line("Whole-scene flares: could not find FlaresContext member(s) [" +
                        string.Join(", ", missing) + "] — the feed's flare definitions will be " +
                        "incomplete. Field names likely changed; re-check with tools/EngineQuery.");
        }
        return copied;
    }

    // ---- OWN ENVIRONMENT PROBES (goal 4.4) ---------------------------------------------
    //
    // The atlas is eight cube textures held per-MANAGER, and the manager is the global
    // CoreSystems.EnvironmentProbeManager. So probes cannot be rendered from the orbit camera
    // without either corrupting the player's atlas or owning a manager. This owns one.
    //
    // Construction is genuinely free: EnvironmentProbeManager..ctor() is parameterless and
    // calls only Object..ctor(). The textures appear later, in RecreateProbes, reached from
    // PrepareProbes — which is also what trips _forceReprocess, so the first PrepareProbes
    // must land inside the 30-frame settle window. It does: our first render after a rebuild
    // is gated by _settleFrames.
    //
    // Filling the queue ourselves is the other half. DrawContexts.EnvProbesToUpdate is
    // written only by DrawContextManager.OnBeginDraw, which our nested Draw never reaches, so
    // our queue is permanently empty unless we assign it. PrepareProbes() returns
    // Buffer<Request> and EnvProbesToUpdate is a field of that type, so this is one
    // assignment — and calling it on OUR instance is not Rule 8, which is about globals.
    // PER-FEED (phase C1a): probes are centred on the CAMERA, which is the whole reason
    // goal 4.4 exists — the player's atlas is right for where the player stands, not
    // where this feed looks. Two feeds at two places need two atlases, so this is
    // per-feed by definition rather than by convenience. NOT disposed on a config
    // change; three device removals settled that (see Reset).
    private static object _ourProbes
    { get => Feeds.Cur.OurProbes; set => Feeds.Cur.OurProbes = value; }

    // THE DEFERRED-DISPOSE QUEUE IS GONE — deleted 2026-07-30 during the C1 static
    // inventory, and worth recording rather than silently dropping.
    //
    // It was attempt 2 of three: Reset() queued the retired probe manager here and an
    // LCD-tick drain (DisposePendingProbes) freed its cube textures from the game thread.
    // Attempt 3 crashed identically, the manager became KEPT rather than retired, and
    // nothing has written this slot since — so the drain ran every tick, found null every
    // time, and did nothing.
    //
    // Dead code that describes a live safety mechanism is worse than no code: it says the
    // textures ARE being reclaimed off the render thread, which is the opposite of the
    // rule that actually holds (see Reset). Rule 26 — a mechanism is only real if it has
    // been observed firing — applies to teardown paths as much as to fixes.

    private static FieldInfo _probeField, _envProbesToUpdateField;
    private static MethodInfo _miPrepareProbes;
    private static int _probeState         // 0 = untried, 1 = ready, -1 = unavailable
    { get => Feeds.Cur.ProbeState; set => Feeds.Cur.ProbeState = value; }
    private static bool _probeLogged
    { get => Feeds.Cur.ProbeLogged; set => Feeds.Cur.ProbeLogged = value; }

    // Install our manager and fill our queue. Returns the manager that was there before, or
    // null if nothing was swapped — the caller restores it in the finally either way.
    //
    // Fails SOFT and permanently on the first problem: a half-installed probe manager is
    // worse than none, and this runs on the render thread with the player's frame in flight.
    private static object InstallProbes()
    {
        if (_probeState < 0 || !FeedConfig.WholeSceneOwnProbes) return null;
        try
        {
            if (_probeState == 0)
            {
                var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _probeField = core?.GetField("EnvironmentProbeManager", BindingFlags.Public | BindingFlags.Static);
                if (_probeField == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: CoreSystems.EnvironmentProbeManager not found — feature unavailable.");
                    return null;
                }

                var mgrType = _probeField.FieldType;
                _miPrepareProbes = mgrType.GetMethod("PrepareProbes", Any);
                if (_miPrepareProbes == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: EnvironmentProbeManager.PrepareProbes not found — feature unavailable.");
                    return null;
                }

                _envProbesToUpdateField = _ourDrawContexts?.GetType().GetField("EnvProbesToUpdate", Any);
                if (_envProbesToUpdateField == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: DrawContextManager.EnvProbesToUpdate not found — cannot hand our " +
                                "own queue to RenderEnvironmentProbe, so stage 2 would iterate nothing.");
                    return null;
                }

                // Parameterless ctor that allocates nothing. Deliberately created HERE rather
                // than in the DrawContextManager build: it costs nothing, so there is no
                // reason to widen that function's failure surface for it.
                //
                // REUSED across resets when one already exists. Reset() deliberately keeps
                // the manager (it cannot be disposed safely while the renderer is live), so
                // constructing unconditionally here would orphan the previous one — and its
                // eight cube textures with it — on every config change. That is Rule 10's
                // leak, arriving through the door opened to avoid a device removal.
                _ourProbes ??= Activator.CreateInstance(mgrType, nonPublic: true);
                if (_ourProbes == null)
                {
                    _probeState = -1;
                    RttLog.Line("Own probes: could not construct an EnvironmentProbeManager — feature unavailable.");
                    return null;
                }
                _probeState = 1;
            }

            var saved = _probeField.GetValue(null);
            _probeField.SetValue(null, _ourProbes);

            // PrepareProbes advances OUR state machine and, on the first call, runs
            // RecreateProbes — the eight cube textures. Inside the settle window by
            // construction, because TryRender will not call us until it expires.
            var queue = _miPrepareProbes.Invoke(_ourProbes, null);
            _envProbesToUpdateField.SetValue(_ourDrawContexts, queue);

            if (!_probeLogged)
            {
                _probeLogged = true;
                RttLog.Line("Own probes: OUR EnvironmentProbeManager installed for our render and our " +
                            "EnvProbesToUpdate filled from its own PrepareProbes(). The feed's reflections " +
                            "and ambient bounce now come from probes rendered at the ORBIT camera instead " +
                            "of the player's atlas. CloseIBL/FarIBL fall back to CommonResources.SkyboxIBL " +
                            "until the first faces land, so early frames lose local bounce rather than " +
                            "binding null. Stage 2 (RenderEnvironmentProbe) must be OUT of " +
                            "wholeSceneSkipStages for any of this to reach the screen.");
            }
            return saved;
        }
        catch (Exception e)
        {
            _probeState = -1;
            RttLog.Error("install own probes (feature DISABLED for this session)", e);
            return null;
        }
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
