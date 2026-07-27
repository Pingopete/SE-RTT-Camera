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

    public static void Reset()
    {
        // Do NOT dispose the engine's. Ours is disposable and holds real GPU memory, so
        // dropping it on a hot reload would leak — the pool asserts about exactly that
        // at shutdown, which is what turned every quit into a crash report earlier in
        // this project.
        if (_ourScreenBuffers is IDisposable d) { try { d.Dispose(); } catch { } }
        _ourScreenBuffers = null;
        _sbBuilt = _sbLogged = false;
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

    // Fires after the engine has finished the player's frame.
    public static void OnWholeScene(object sceneDrawSystem, object finalLdrBuffer)
    {
        if (_inOurRender) return;               // our own nested Draw — do nothing
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
            if (FeedConfig.WholeSceneEnabled && _ourScreenBuffers != null
                && Clock.Ms - _lastRenderMs >= Math.Max(33, FeedConfig.WholeSceneIntervalMs))
            {
                _lastRenderMs = Clock.Ms;
                RunSecondRender(sceneDrawSystem);
            }

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
            object savedCam = null, savedRt = null;
            bool camSwapped = false;

            _inOurRender = true;
            try
            {
                _sbField.SetValue(null, _ourScreenBuffers);
                savedRt = InstallNoRaytracing();
                if (FeedConfig.WholeSceneCamera) camSwapped = InstallCamera(out savedCam);

                if (_renderCount == 0)
                    RttLog.Line($"=== WHOLE-SCENE RENDER: calling SceneDrawSystem.Draw a second time, " +
                                $"into our own {FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight} " +
                                $"ScreenBuffers. Camera is {(camSwapped ? "OURS" : "the player's")}. ===");

                _miDraw.Invoke(sceneDrawSystem, new[] { ourLdr });
                _renderCount++;

                if (_renderCount == 1)
                    RttLog.Line("=== WHOLE-SCENE RENDER SURVIVED THE FIRST CALL. The engine's entire " +
                                "renderer just ran a second time this frame, into buffers we own. ===");
            }
            finally
            {
                // Unconditional, and in reverse install order: camera, then raytracing,
                // then the buffers the engine's next frame will be rendered into.
                if (camSwapped) RestoreCamera(savedCam);
                RestoreRaytracing(savedRt);
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

    // Keep our second render out of the player's GLOBAL ILLUMINATION.
    //
    // Reported from the game: with stage 3a running, the player's world render went
    // patchy and shifting in its GI. That is not a rendering bug in our second frame —
    // it is our second frame's side effects landing on theirs.
    //
    // Draw's first and last acts are global and temporal, and none of them live in
    // ScreenBuffers, so owning a ScreenBuffers does not isolate them:
    //
    //     IL_003c  ExecuteAccelerationStructuresBuilding()      raytracing scene
    //     IL_008d  ExecuteRaytracingPrepareAndSceneFinalize()
    //
    // and RaytracingSettings is full of accumulators — EnableTemporalReSTIR,
    // EnableTemporalFilter, EnableIRCache, EnableIRCacheScrolling. Those integrate over
    // frames in WORLD space, so running the pipeline a second time per frame advances
    // them twice. Patchy and shifting is precisely what a temporal accumulator looks
    // like when it is stepped at the wrong rate.
    //
    // RaytracingSettings is a STRUCT and SettingsManager._raytracing is the backing
    // field, so this is the InstallNoOcclusion shape: box it twice, mutate one, restore
    // the other. Clearing Enabled gates the whole RT path rather than picking at
    // individual accumulators.
    //
    // Costs our feed raytraced GI, which is a scope item that was excluded anyway — and
    // a feed without RT GI is worth more than a main view with corrupted GI.
    private static object InstallNoRaytracing()
    {
        if (_rtBlocked || !FeedConfig.WholeSceneDisableRaytracing) return null;
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (settings == null) return null;

            _rtField ??= settings.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == "RaytracingSettings");
            if (_rtField == null)
            {
                _rtBlocked = true;
                RttLog.Line("Whole-scene: SettingsManager RaytracingSettings field not found — our second " +
                            "render will keep advancing the player's RT/GI accumulation twice per frame.");
                return null;
            }

            _rtSettingsObj = settings;
            var saved = _rtField.GetValue(settings);
            var ours = _rtField.GetValue(settings);     // struct field: a second, independent box

            int set = 0;
            foreach (var n in new[] { "Enabled", "EnableTemporalReSTIR", "EnableSpatialReSTIR",
                                      "EnableTemporalFilter", "EnableIRCache", "EnableIRCacheScrolling" })
            {
                var f = ours.GetType().GetField(n, Any);
                if (f != null && f.FieldType == typeof(bool)) { f.SetValue(ours, false); set++; }
            }
            if (set == 0) { _rtBlocked = true; RttLog.Line("Whole-scene: no raytracing flags found."); return null; }

            _rtField.SetValue(settings, ours);
            if (!_rtLogged)
            {
                _rtLogged = true;
                RttLog.Line($"Whole-scene: raytracing disabled for our render ({set} flags cleared). " +
                            "Draw builds acceleration structures and steps ReSTIR/IR-cache accumulators " +
                            "that integrate over frames in WORLD space — running the pipeline twice per " +
                            "frame advanced them twice, which is what made the player's GI patchy.");
            }
            return saved;
        }
        catch (Exception e) { _rtBlocked = true; RttLog.Error("whole-scene disable raytracing", e); return null; }
    }

    private static void RestoreRaytracing(object saved)
    {
        if (saved == null || _rtField == null || _rtSettingsObj == null) return;
        try { _rtField.SetValue(_rtSettingsObj, saved); }
        catch (Exception e) { RttLog.Error("whole-scene restore raytracing", e); }
    }

    private static FieldInfo _rtField;
    private static object _rtSettingsObj;
    private static bool _rtBlocked, _rtLogged;

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
