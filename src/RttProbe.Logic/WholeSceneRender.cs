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

            long now = Clock.Ms;
            if (now - _lastLogMs >= 5000)
            {
                _lastLogMs = now;
                RttLog.Line($"Whole-scene hook: {_hookCount} frame(s) so far, " +
                            $"ourScreenBuffers={(_ourScreenBuffers == null ? "not built" : "BUILT")}, " +
                            $"rendering={FeedConfig.WholeSceneEnabled}. Nothing is swapped yet.");
            }
        }
        catch (Exception e) { _state = -1; RttLog.Error("whole-scene hook", e); }
    }

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
