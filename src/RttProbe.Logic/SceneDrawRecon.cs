using System.Reflection;
using System.Text;

namespace RttProbe;

// Step 1 of the attack plan: prove, in a live game, that the pieces the probe
// pass uses are reachable from a plugin.
//
// Read-only. It resolves types and reads fields; it never draws, allocates GPU
// resources, or mutates engine state. Runs on the render thread, so everything
// after the one-shot dump is a single bool test.
internal static class SceneDrawRecon
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static bool _dumped;
    private static long _probeCalls, _framePasses, _lastRateLog;

    // The jobs the probe pass drives. If these are non-null on the live instance,
    // the whole approach is reachable by reflection.
    private static readonly string[] WantedJobs =
    {
        "_indirectCullingJob", "_clusterJob", "_indirectEnvironmentPass", "_indirectPlanetEnvironmentJob",
    };

    public static void Reset()
    {
        _dumped = false;
        _probeCalls = _framePasses = _lastRateLog = 0;
        _render12Types = null;
    }

    public static void OnSceneDraw(object sceneDrawSystem, object commandList, int which)
    {
        // Hard re-entrancy guard. The whole-scene render paths
        // (ScenePreparationAndRender / Draw) drive the very passes we are hooked into,
        // so without this our hook fires again from inside our own nested render and
        // recurses until the stack or the device gives out. Cheapest single choke point:
        // every hook enters through here.
        if (CameraRender.InNestedRender) return;

        if (which == 0) System.Threading.Interlocked.Increment(ref _probeCalls);
        else System.Threading.Interlocked.Increment(ref _framePasses);

        // The camera pass rides the probe hook: same point in the frame, with a
        // command list in hand and shadows already resolved.
        //
        // But the BASE VIEW must not be read there. ExecuteEnvironmentProbeUpdate
        // renders the engine's own probe cube faces, from the probe's location, and
        // SettingsManager.RenderView reflects that while it does. Sampling it inside
        // the probe hook therefore hands us a 90-degree cube-face projection from
        // inside the ship every time our frame gate lands on a probe refresh — one
        // incoherent frame, a couple of times a second. Snapshot it from the
        // per-frame pass instead, where it is provably the main view.
        if (which == 1) CameraRender.CaptureBaseView();

        // WHICH hook the camera pass rides is now a config switch, because it is the
        // open question behind the main-view corruption.
        //
        //   probe hook (0)  ExecuteEnvironmentProbeUpdate, ~7x/frame. Where this has
        //                   always run. Shadows are resolved and a command list is in
        //                   hand — but it is INSIDE the engine's own probe work, and
        //                   the main view's post passes (ComputeExposure, ApplyBloom,
        //                   ApplyToneMapping) own state that is live at that moment.
        //                   Driving them here corrupted the player's render.
        //   frame hook (1)  DrawUnlit, once per frame, after the main pass has done
        //                   its own work. If the corruption is purely "wrong point in
        //                   the frame", the post chain is safe here.
        //
        // The camera pass is self-contained — it borrows every resource it uses — so
        // this is a dispatch change rather than a restructure.
        int want = FeedConfig.PassOnFrameHook ? 1 : 0;
        if (which == want) CameraRender.OnProbePass(sceneDrawSystem, commandList);

        if (!_dumped)
        {
            _dumped = true;
            try { Dump(sceneDrawSystem); }
            catch (Exception e) { RttLog.Error("scene draw recon", e); }
        }

        // How often does the foreign-view pass actually run? Step 3 has to live
        // inside it, so its cadence is a design input, not trivia.
        var now = Environment.TickCount64;
        if (now - _lastRateLog >= 10000)
        {
            _lastRateLog = now;
            RttLog.Line($"Cadence: probe-pass {_probeCalls} calls, per-frame pass {_framePasses} calls (cumulative).");
        }
    }

    private static void Dump(object sds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== SceneDrawSystem reconnaissance {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        sb.AppendLine();

        if (sds == null) { sb.AppendLine("instance is NULL"); Write(sb); return; }
        var t = sds.GetType();
        sb.AppendLine($"Instance type: {t.FullName}");
        sb.AppendLine($"Assembly:      {t.Assembly.GetName().Name}");
        sb.AppendLine();

        // ---- the four jobs -------------------------------------------------
        sb.AppendLine("-- Job fields (the step-3 prerequisites) --");
        var jobTypes = new List<Type>();
        foreach (var name in WantedJobs)
        {
            var f = t.GetField(name, Any);
            if (f == null) { sb.AppendLine($"  {name,-32} FIELD NOT FOUND"); continue; }
            object v = null;
            try { v = f.GetValue(sds); } catch (Exception e) { sb.AppendLine($"  {name,-32} read failed: {e.Message}"); continue; }
            sb.AppendLine($"  {name,-32} {(v == null ? "NULL" : "OK")}  ({f.FieldType.Name})");
            if (v != null) jobTypes.Add(v.GetType());
        }
        sb.AppendLine();

        // ---- exact signatures we must satisfy in step 3 ---------------------
        sb.AppendLine("-- Job entry point signatures --");
        foreach (var jt in jobTypes)
        {
            sb.AppendLine($"  {jt.FullName}");
            foreach (var m in jt.GetMethods(Any).Where(m => m.Name is "DoWork" or "DoCullingFirstPass" or "Draw"))
                sb.AppendLine($"      {m.Name}({string.Join(", ", m.GetParameters().Select(p => Describe(p)))})");
        }
        sb.AppendLine();

        // ---- every other field, so nothing is missed ------------------------
        sb.AppendLine("-- All SceneDrawSystem fields --");
        foreach (var f in t.GetFields(Any).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            object v = null; string state;
            try { v = f.GetValue(f.IsStatic ? null : sds); state = v == null ? "null" : "set"; }
            catch { state = "unreadable"; }
            sb.AppendLine($"  {(f.IsStatic ? "static " : "       ")}{f.FieldType.Name,-38} {f.Name,-42} {state}");
        }
        sb.AppendLine();

        DumpCoreSystems(sb);
        DumpTypeShapes(sb);
        Write(sb);
    }

    // The statics the probe pass borrows from. All must be live.
    private static void DumpCoreSystems(StringBuilder sb)
    {
        sb.AppendLine("-- CoreSystems statics --");
        var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
        if (cs == null) { sb.AppendLine("  CoreSystems NOT FOUND"); sb.AppendLine(); return; }

        object drawContexts = null;
        foreach (var name in new[] { "DrawContexts", "BindableTexturePool", "BindableBuffers", "Settings", "ScreenBuffers" })
        {
            var f = cs.GetField(name, Any);
            if (f == null) { sb.AppendLine($"  {name,-22} FIELD NOT FOUND"); continue; }
            object v = null;
            try { v = f.GetValue(null); } catch { }
            sb.AppendLine($"  {name,-22} {(v == null ? "NULL" : "OK")}  ({f.FieldType.Name})");
            if (name == "DrawContexts") drawContexts = v;
        }
        sb.AppendLine();

        if (drawContexts != null)
        {
            sb.AppendLine("-- DrawContextManager live contexts --");
            var dt = drawContexts.GetType();
            foreach (var name in new[] { "EnvProbeCulling", "EnvProbeClustering", "MainViewCulling",
                                         "MainViewClustering", "MainOutputGeometryBuffers",
                                         "DirectionalLightShadowResources" })
            {
                var p = dt.GetProperty(name, Any);
                object v = null;
                try { v = p?.GetValue(drawContexts); } catch { }
                string extra = v is Array arr ? $" [length {arr.Length}]" : "";
                sb.AppendLine($"  {name,-34} {(v == null ? "NULL" : "OK")}{extra}");
            }
            sb.AppendLine();

            // Constructing our own context in step 2 means knowing how.
            sb.AppendLine("-- CullingContext / ClusteringContext constructors --");
            foreach (var tn in new[] { "CullingContext", "ClusteringContext" })
            {
                var ct = FindRender12Type(tn);
                if (ct == null) { sb.AppendLine($"  {tn}: not found"); continue; }
                sb.AppendLine($"  {ct.FullName}");
                foreach (var c in ct.GetConstructors(Any))
                    sb.AppendLine($"      ctor({string.Join(", ", c.GetParameters().Select(Describe))})");
            }
            sb.AppendLine();
        }

        // The pool calls that supply depth and the output target.
        sb.AppendLine("-- Pool / buffer entry points --");
        foreach (var (tn, methods) in new (string, string[])[]
        {
            ("BindableTexturePoolManager", new[] { "BorrowRWRenderTargetTexture", "BorrowResizableDepthStencilTexture", "Return" }),
            ("BindableBufferManager", new[] { "CreateTransientConstantBuffer" }),
        })
        {
            var mt = FindRender12Type(tn);
            if (mt == null) { sb.AppendLine($"  {tn}: not found"); continue; }
            sb.AppendLine($"  {mt.FullName}");
            foreach (var m in mt.GetMethods(Any).Where(m => methods.Contains(m.Name)))
                sb.AppendLine($"      {m.Name}({string.Join(", ", m.GetParameters().Select(Describe))})");
        }
        sb.AppendLine();
    }

    // The value types we must build to describe our camera to the GPU.
    private static void DumpTypeShapes(StringBuilder sb)
    {
        sb.AppendLine("-- Camera description types --");
        foreach (var tn in new[] { "TrackedCameraSettings", "CameraSettings", "ScreenSettings", "RenderViewSlim", "RenderView" })
        {
            var t = FindRender12Type(tn);
            if (t == null) { sb.AppendLine($"  {tn}: NOT FOUND"); continue; }
            sb.AppendLine($"  {t.FullName}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine($"      field {f.FieldType.Name} {f.Name}");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.StartsWith("op_")))
                sb.AppendLine($"      {m.Name} : {m.ReturnType.Name} <- ({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
            foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine($"      ctor({string.Join(", ", c.GetParameters().Select(Describe))})");
        }
        sb.AppendLine();
    }

    // Render12 types are internal and scattered across namespaces, so resolve by
    // simple name across the assembly rather than guessing full names. The type
    // list is enumerated once — this runs inline on the render thread, and
    // walking a few thousand types per lookup would be a visible hitch.
    private static Type[] _render12Types;

    private static Type FindRender12Type(string simpleName)
    {
        if (_render12Types == null)
        {
            try
            {
                var anchor = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _render12Types = anchor?.Assembly.GetTypes() ?? Type.EmptyTypes;
            }
            catch (ReflectionTypeLoadException e)
            {
                _render12Types = e.Types.Where(t => t != null).ToArray();
            }
            catch { _render12Types = Type.EmptyTypes; }
        }
        foreach (var t in _render12Types)
            if (t.Name == simpleName) return t;
        return null;
    }

    private static string Describe(ParameterInfo p)
    {
        var t = p.ParameterType;
        string prefix = t.IsByRef ? (p.IsIn ? "in " : p.IsOut ? "out " : "ref ") : "";
        return $"{prefix}{(t.IsByRef ? t.GetElementType()?.Name : t.Name)} {p.Name}";
    }

    private static void Write(StringBuilder sb)
    {
        var path = Path.Combine(RttLog.OutDir, "scene-draw-recon.txt");
        try { File.WriteAllText(path, sb.ToString()); RttLog.Line($"Scene draw recon written to {path}"); }
        catch (Exception e) { RttLog.Error("recon write", e); }
    }
}
