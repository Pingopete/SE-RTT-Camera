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
            object savedCam = null;
            bool camSwapped = false;

            _inOurRender = true;
            try
            {
                _sbField.SetValue(null, _ourScreenBuffers);
                ScopeSharedState();
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
                // Unconditional, and in reverse install order: camera, then every scoped
                // settings group, then the buffers the engine's next frame renders into.
                if (camSwapped) RestoreCamera(savedCam);
                RestoreScoped();
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

            if (_scopeWarned.Add(settingsTypeName + ":ok"))
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
        if (FeedConfig.WholeSceneDisableRaytracing)
            ScopeOff("RaytracingSettings", "raytracing",
                     "Enabled", "EnableTemporalReSTIR", "EnableSpatialReSTIR",
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
