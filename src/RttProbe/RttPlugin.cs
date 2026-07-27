using System.Reflection;
using System.Runtime.Loader;
using Keen.VRage.Core.Plugins;

namespace RttProbe;

// Static handoff between the bootstrap (loaded once, holds the Harmony patches)
// and the hot-reloadable logic assembly. The bootstrap never references logic
// types directly — that would pin the collectible load context.
public static class RttBridge
{
    // (renderer, batch, surfaceContext) — the renderer is needed to rebuild a panel's
    // screen material, which is how Phase 2 points a panel at our own render target.
    public static volatile Action<object, object, object> PanelRenderHook;
    public static volatile Action<object> TickHook;

    // Fires from inside the render frame with the live SceneDrawSystem and the
    // command list the pass was given. The int identifies which patched method
    // fired: 0 = ExecuteEnvironmentProbeUpdate (the foreign-view pass we imitate),
    // 1 = a per-frame pass.
    public static volatile Action<object, object, int> SceneDrawHook;

    // Fires from inside the UI stage, right after the engine has legally copied
    // into an offscreen render target — the one point in the frame where that
    // resource is in the right state to be written.
    public static volatile Action<object[]> OffscreenUiDrawHook;

    // (sceneDrawSystem, finalLDRBuffer) — fires AFTER the engine's whole frame.
    //
    // SceneDrawSystem.Draw is the top of the pipeline: public, and it takes both its
    // destination buffer and (through that buffer's Resolution) its render size as
    // parameters. Everything else it needs comes from CoreSystems statics, and those
    // are public FIELDS rather than readonly properties — so a second render is a
    // matter of swapping them around a second call, not of finding a second renderer.
    //
    // POSTFIX, not prefix. After the engine's frame the temporal state is settled and
    // we are conceptually between frames; running ahead of it would interleave our
    // render with the one the player sees.
    //
    // Draw has ZERO managed callers — it is invoked from engine glue — which is what
    // makes this a usable site at all. The probe hook could never host a second Draw
    // because it already sits inside one.
    //
    // Re-entrancy is the logic side's problem: our own nested Draw will fire this hook
    // again, and the handler must return immediately when it does.
    public static volatile Action<object, object> WholeSceneHook;
}

public sealed class RttPlugin : IPlugin
{
    private const string LogicPath = @"D:\SE2Rtt\RttProbe.Logic.dll";
    private const string LogPath = @"D:\Projects\Space Engineers Stuff\RTT Camera\output\rtt.log";

    private AssemblyLoadContext _logicContext;
    private MethodInfo _tick;
    private DateTime _loadedStamp;

    public RttPlugin(PluginHost host)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
        // Append, never truncate: relaunching after a crash must not destroy the
        // log that explains the crash.
        File.AppendAllText(LogPath, $"{Environment.NewLine}=== RttProbe bootstrap {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        Log("Bootstrap constructed. Hot-reload watching " + LogicPath);
        ApplyPatches();
        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RttProbeBootstrap" };
        worker.Start();
    }

    public RttPlugin() : this(null) { }

    private static void ApplyPatches()
    {
        try
        {
            var harmony = new HarmonyLib.Harmony("rttprobe.bootstrap");

            // The panel content recorder. Its IDrawBatch targets that panel's own
            // offscreen render target — which is exactly where the blit under test
            // has to land.
            var renderer = Type.GetType("Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdContentRendererSessionComponent, Game2.Client");
            var render = renderer?.GetMethod("Render", BindingFlags.Public | BindingFlags.Instance);
            if (render != null)
            {
                var post = typeof(RttPlugin).GetMethod(nameof(PanelRenderPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(render, postfix: new HarmonyLib.HarmonyMethod(post));
                Log("Patched LcdContentRendererSessionComponent.Render.");
            }
            else Log("FAILED: LcdContentRendererSessionComponent.Render not found.");

            // Per-frame tick, outside panel content recording. Creating our own
            // render target and drawing into it happens here rather than inside
            // Render, so we are never recording two batches at once.
            var rc = Type.GetType("Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd.LcdPanelSurfaceRenderComponent, Game2.Client");
            var tick = rc?.GetMethod("TickFsrMask", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tick != null)
            {
                var post = typeof(RttPlugin).GetMethod(nameof(TickPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(tick, postfix: new HarmonyLib.HarmonyMethod(post));
                Log("Patched LcdPanelSurfaceRenderComponent.TickFsrMask.");
            }
            else Log("FAILED: TickFsrMask not found.");

            PatchSceneDraw(harmony);
            PatchOffscreenUi(harmony);
        }
        catch (Exception e) { Log("Patching FAILED: " + e); }
    }

    // The UI stage's offscreen renderer. Its signature is not known ahead of time,
    // so the postfix takes __args and the logic side inspects them.
    private static void PatchOffscreenUi(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.UIStage.OffscreenUIRenderer, VRage.Render12");
            if (t == null) { Log("OffscreenUIRenderer type not found."); return; }

            var mi = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                  BindingFlags.Instance | BindingFlags.Static)
                      .FirstOrDefault(m => m.Name == "DrawOne");
            if (mi == null) { Log("OffscreenUIRenderer.DrawOne not found."); return; }

            var post = typeof(RttPlugin).GetMethod(nameof(OffscreenUiPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log($"Patched OffscreenUIRenderer.DrawOne({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}).");
        }
        catch (Exception e) { Log("Patching OffscreenUIRenderer FAILED: " + e.Message); }
    }

    private static void OffscreenUiPostfix(object[] __args)
    {
        try { RttBridge.OffscreenUiDrawHook?.Invoke(__args); } catch { }
    }

    // SceneDrawSystem lives in VRage.Render12 and is internal, so everything here
    // goes through reflection. The postfixes only capture `this` — the reconnaissance
    // itself runs in the hot-reloadable logic assembly.
    private static void PatchSceneDraw(HarmonyLib.Harmony harmony)
    {
        var sds = Type.GetType("Keen.VRage.Render12.Core.Systems.SceneDrawSystem, VRage.Render12");
        if (sds == null) { Log("SceneDrawSystem type not found — is VRage.Render12 loaded yet?"); return; }
        Log("SceneDrawSystem found: " + sds.FullName);

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // The foreign-view pass we intend to model on, plus a per-frame pass as a
        // fallback in case probe updates are rare.
        foreach (var (name, id, hook) in new (string, int, string)[]
        {
            ("ExecuteEnvironmentProbeUpdate", 0, nameof(ProbePassPostfix)),
            ("DrawUnlit", 1, nameof(FramePassPostfix)),
        })
        {
            try
            {
                var mi = sds.GetMethod(name, Any);
                if (mi == null) { Log($"SceneDrawSystem.{name} not found."); continue; }
                var post = typeof(RttPlugin).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
                Log($"Patched SceneDrawSystem.{name} (id {id}).");
            }
            catch (Exception e) { Log($"Patching SceneDrawSystem.{name} FAILED: {e.Message}"); }
        }

        // Draw is the TOP of the pipeline, and the only site where a second whole-scene
        // render can be driven. Patched separately from the loop above because it takes
        // a different argument (the final LDR buffer, not a command list) and because it
        // is the one hook that calls back into the method it patches.
        try
        {
            var draw = sds.GetMethod("Draw", Any);
            if (draw == null)
            {
                Log("SceneDrawSystem.Draw not found — the whole-scene route has no hook site.");
            }
            else
            {
                var post = typeof(RttPlugin).GetMethod(nameof(WholeScenePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(draw, postfix: new HarmonyLib.HarmonyMethod(post));
                Log($"Patched SceneDrawSystem.Draw({string.Join(", ", draw.GetParameters().Select(p => p.ParameterType.Name))}) " +
                    "— the whole-scene render hook.");
            }
        }
        catch (Exception e) { Log("Patching SceneDrawSystem.Draw FAILED: " + e.Message); }
    }

    // __0 is the ResizableRWRenderTargetTexture the engine just rendered the player's
    // frame into. We do not touch it — it is passed through so the logic side can read
    // its format and resolution, which is what a second target has to match.
    private static void WholeScenePostfix(object __instance, object __0)
    {
        try { RttBridge.WholeSceneHook?.Invoke(__instance, __0); } catch { }
    }

    // __0 is the DirectCommandList both passes take as their first parameter.
    // Running in the postfix means the engine has finished with that pass, so the
    // list is still open but its work is recorded.
    private static void ProbePassPostfix(object __instance, object __0)
    {
        try { RttBridge.SceneDrawHook?.Invoke(__instance, __0, 0); } catch { }
    }

    private static void FramePassPostfix(object __instance, object __0)
    {
        try { RttBridge.SceneDrawHook?.Invoke(__instance, __0, 1); } catch { }
    }

    // __instance is the LcdContentRendererSessionComponent — required by
    // LcdPanelSurfaceContext.SetNewScreenMaterialHandle, which is how a panel is
    // pointed at a different render target.
    private static void PanelRenderPostfix(object __instance, object __0, object __1)
    {
        try { RttBridge.PanelRenderHook?.Invoke(__instance, __0, __1); } catch { }
    }

    private static void TickPostfix(object __instance)
    {
        try { RttBridge.TickHook?.Invoke(__instance); } catch { }
    }

    private void WorkerLoop()
    {
        Thread.Sleep(8000);
        while (true)
        {
            try { ReloadLogicIfChanged(); }
            catch (Exception e) { Log("ERROR worker: " + e.Message); }
            Thread.Sleep(2000);
        }
    }

    private void ReloadLogicIfChanged()
    {
        if (!File.Exists(LogicPath))
        {
            if (_tick == null) Log("Waiting for logic dll to appear...");
            return;
        }
        var stamp = File.GetLastWriteTimeUtc(LogicPath);
        if (_tick != null && stamp == _loadedStamp) return;

        try
        {
            var old = _logicContext;
            var ctx = new AssemblyLoadContext("RttProbeLogic_" + stamp.Ticks, isCollectible: true);
            Assembly asm;
            using (var ms = new MemoryStream(File.ReadAllBytes(LogicPath)))
            {
                var pdbPath = Path.ChangeExtension(LogicPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
                    asm = ctx.LoadFromStream(ms, pdb);
                }
                else asm = ctx.LoadFromStream(ms);
            }
            var entry = asm.GetType("RttProbe.LogicEntry");
            var install = entry?.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
            if (install == null)
            {
                Log("Logic dll loaded but RttProbe.LogicEntry.Install not found — keeping previous logic.");
                ctx.Unload();
                return;
            }
            install.Invoke(null, null);
            _logicContext = ctx;
            _tick = install;
            _loadedStamp = stamp;
            Log($"Logic loaded (build stamp {stamp:HH:mm:ss}). Hot-reload active.");
            old?.Unload();
        }
        catch (Exception e)
        {
            Log($"ERROR loading logic dll: {e.Message} — keeping previous logic.");
        }
    }

    private static readonly object LogGate = new();
    private static void Log(string msg)
    {
        try { lock (LogGate) File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [boot] {msg}{Environment.NewLine}"); } catch { }
    }
}
