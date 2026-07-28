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

    // Returns TRUE to skip a Draw sub-stage entirely. The int identifies which — see
    // RttPlugin.SkippableStages.
    //
    // Settings flags cannot reach every stage. ExecuteAccelerationStructuresBuilding is
    // the case that forced this: it is called unconditionally at the top of Draw and
    // checks only EnableGPUParallelization, so clearing RaytracingSettings.Enabled never
    // stopped it — we rebuilt the raytracing acceleration structures on every second
    // render, and RayTracingSceneManager.CreateTLAS is camera-dependent and world-space
    // shared.
    //
    // A prefix returning false skips the original outright, which is the only lever that
    // reaches a stage the settings do not gate.
    public static volatile Func<int, bool> SkipStageHook;
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

        PatchSkippableStages(harmony, sds);
        PatchFsrGate(harmony);
    }

    // __0 is the ResizableRWRenderTargetTexture the engine just rendered the player's
    // frame into. We do not touch it — it is passed through so the logic side can read
    // its format and resolution, which is what a second target has to match.
    private static void WholeScenePostfix(object __instance, object __0)
    {
        try { RttBridge.WholeSceneHook?.Invoke(__instance, __0); } catch { }
    }

    // Draw sub-stages that a second render must be able to skip.
    //
    // These are all WORLD-SPACE or CROSS-FRAME: they update state the player's next
    // frame reads, so running them a second time per frame corrupts their view rather
    // than ours. Several cannot be reached by any settings flag —
    // ExecuteAccelerationStructuresBuilding checks only EnableGPUParallelization — which
    // is why this exists at all.
    //
    // The id is positional and is what the logic side switches on; keep the order
    // stable or the config's stage list silently means something else.
    // Type == null means SceneDrawSystem; anything else is an assembly-qualified name.
    //
    // Ids 17+ reach methods on OTHER types, and that is not decoration. The RT settings
    // route to "no ray tracing in our render" is CLOSED: RaytraceGIJob keys a
    // LazyJobSnapshotHandler<RTGISettings, RTGISnapshot> off RaytracingSettings and builds
    // SHADER DEFINES from it, so toggling any flag that reaches a define rebuilds
    // pipelines — ten times a second, which shows up as bright flashing across the
    // player's whole world. Confirmed for Enabled, and again for
    // RaytracedDiffuseGI/RaytracedSpecularGI. A Harmony prefix mutates nothing and is the
    // only lever that reaches the work without touching the settings.
    private static readonly (string Type, string Method)[] SkippableStages =
    {
        (null, "ExecuteAccelerationStructuresBuilding"),     // 0  raytracing scene / TLAS
        (null, "ExecuteRaytracingPrepareAndSceneFinalize"),  // 1  raytracing prepare
        (null, "RenderEnvironmentProbe"),                    // 2  shared probe atlas (ambient + reflections)
        (null, "RenderShadows"),                             // 3  shadow cascades
        (null, "ComputeExposure"),                           // 4  auto-exposure history  (UNSAFE: out params)
        (null, "UpdateSurfels"),                             // 5  water surfels
        (null, "PrepareClusters"),                           // 6  light cluster grid
        (null, "ProcessParticles"),                          // 7  particle SIMULATION state
        (null, "RenderDecals"),                              // 8  decal atlas
        (null, "ExecuteHBAO"),                               // 9  ambient occlusion
        (null, "ExecuteLighting"),                           // 10 whole lighting stage (our image dies without it)
        (null, "RenderMainView"),                            // 11 the geometry pass (ditto)
        (null, "ComputeDirectionalLighting"),                // 12 sun light + shadow mask
        (null, "ComputeLocalLights"),                        // 13 clustered point/spot lights
        (null, "ComputeCloudShadows"),                       // 14 writes SHARED CommonResources.CloudShadowmap
        (null, "UpdateAtmosphere"),                          // 15 atmosphere LUT updates
        (null, "DrawUI"),                                    // 16 the player's HUD, baked into the feed otherwise

        // 17 — the ray trace itself, and nothing else. ComputeGI runs
        // _raytraceGiJob.DoWork behind a settings gate and then _ambientLightJob.DoWork
        // unconditionally, so skipping HERE removes the RT work and KEEPS the feed's
        // ambient term. Skipping ComputeGI (18) would take both.
        ("Keen.VRage.Render12.LightingStage.RaytraceGIJob, VRage.Render12", "DoWork"),   // 17

        // 18 — the whole GI stage, ambient included. Blunter than 17; the feed's
        // shadowed areas go black. Kept as the fallback if 17 is not enough.
        (null, "ComputeGI"),                                 // 18

        // 19 — DO NOT USE. Kept so the ids below do not shift.
        //
        // The idea was to stop our render disposing the player's FSR history:
        //
        //     UpsamplingJob.PrepareResources:
        //       switch (Settings.DRS.AAMode) {
        //         case Bilinear: _bilinear.PrepareResources(); _fsr3_1.DisposeResources();
        //         case FSR:      _fsr3_1.PrepareResources(maxRes, displayRes);
        //                        _bilinear.DisposeResources();
        //       }
        //
        // With DRSSettings.AAMode scoped to 0 for our render, ScenePreparation takes the
        // Bilinear branch and disposes the SHARED FSR3 resources — ten times a second —
        // so the player's TAA restarts every frame and never accumulates. That much was
        // right, and it IS the fine-detail shimmer.
        //
        // But skipping it is not the fix, because PrepareResources does not only
        // dispose: each branch ALLOCATES its own side. Skip it and our render runs the
        // bilinear path with nothing allocated, because the player's frame disposed
        // bilinear when it prepared FSR. Device removed inside Upsampling, PageFaultVA
        // 0x0, on world load.
        //
        // Only ONE resource set is alive at a time, chosen by AAMode. That is also the
        // real mechanism behind the three original wholeSceneAAMode CTDs, which is worth
        // saying plainly: the model that got retracted as "wrong" was pointing here.
        //
        // The answer is not to skip anything in Upsampling — it is to stop scoping
        // AAMode at all, and disable FSR for our render at the only place that decides
        // it. See stage 20.
        ("Keen.VRage.Render12.PostProcessStage.Upsampling.UpsamplingJob, VRage.Render12",
         "PrepareResources"),                                // 19  DO NOT USE
    };

    // Stage 20 is NOT a skip — it is a return-value override, so it lives outside the
    // table above.
    //
    // IsFSREnabledAndAllowed is `DRS.AAMode == 2 && debugViewOk`, and it is what
    // UpscaleTargetFSR, ExecuteForwardPasses and RenderMainView consult to decide
    // whether to run FSR and write its masks. Forcing it FALSE for the duration of our
    // render gets us off the shared upsampler — which is what stopped our geometry being
    // composited see-through — while changing NO state at all:
    //
    //   * AAMode keeps the player's value, so PrepareResources stays on the FSR branch
    //     and the FSR resources are neither disposed nor left unallocated.
    //   * UpscaleTargetFSR takes its own early-out, which correctly sets the
    //     toneMappingInput/toneMappingOutput out-params bloom and tonemap consume.
    //   * Nothing is written back, so nothing can leak into a stage we did not consider.
    //
    // Three settings scopes in a row leaked into code they were not aimed at (Enabled ->
    // RaytraceGIJob's shader defines, RaytracedDiffuseGI -> the same, AAMode ->
    // UpsamplingJob's resource lifetime). A patch that only changes what one caller SEES,
    // and only while our render is on the stack, cannot do that.
    private const int FsrDisableId = 20;

    private static void PatchFsrGate(HarmonyLib.Harmony harmony)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var sm = Type.GetType("Keen.VRage.Render12.Core.Systems.SettingsManager, VRage.Render12");
            var mi = sm?.GetMethod("get_IsFSREnabledAndAllowed", Any);
            if (mi == null) { Log("IsFSREnabledAndAllowed not found — FSR gate unavailable."); return; }

            var post = typeof(RttPlugin).GetMethod(nameof(FsrAllowedPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log($"Patched SettingsManager.IsFSREnabledAndAllowed as override id {FsrDisableId}.");
        }
        catch (Exception e) { Log($"Patching the FSR gate FAILED: {e.Message}"); }
    }

    // Fail-open in the strictest sense: if the hook throws or is absent, the engine's own
    // answer stands.
    private static void FsrAllowedPostfix(ref bool __result)
    {
        try { if (RttBridge.SkipStageHook?.Invoke(FsrDisableId) == true) __result = false; }
        catch { }
    }

    private static void PatchSkippableStages(HarmonyLib.Harmony harmony, Type sds)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (int i = 0; i < SkippableStages.Length; i++)
        {
            var (typeName, name) = SkippableStages[i];
            try
            {
                var owner = sds;
                if (typeName != null)
                {
                    owner = Type.GetType(typeName);
                    if (owner == null) { Log($"Skippable stage {typeName} not found."); continue; }
                }

                var mi = owner.GetMethod(name, Any);
                if (mi == null) { Log($"Skippable stage {owner.Name}.{name} not found."); continue; }

                // One prefix per id. Harmony cannot pass extra arguments to a shared
                // prefix, so each stage gets its own tiny method rather than a lookup by
                // stack inspection.
                var pre = typeof(RttPlugin).GetMethod("SkipStage" + i,
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (pre == null) { Log($"No SkipStage{i} prefix for {name}."); continue; }

                harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
                Log($"Patched {owner.Name}.{name} as skippable stage {i}.");
            }
            catch (Exception e) { Log($"Patching skippable stage {name} FAILED: {e.Message}"); }
        }
    }

    // A prefix returning false skips the original. Any exception must fall through to
    // "run it" — silently skipping an engine stage because our hook threw would be a
    // very hard failure to attribute.
    private static bool Skip(int id)
    {
        try { return RttBridge.SkipStageHook?.Invoke(id) != true; }
        catch { return true; }
    }

    private static bool SkipStage0() => Skip(0);
    private static bool SkipStage1() => Skip(1);
    private static bool SkipStage2() => Skip(2);
    private static bool SkipStage3() => Skip(3);
    private static bool SkipStage4() => Skip(4);
    private static bool SkipStage5() => Skip(5);
    private static bool SkipStage6() => Skip(6);
    private static bool SkipStage7() => Skip(7);
    private static bool SkipStage8() => Skip(8);
    private static bool SkipStage9() => Skip(9);
    private static bool SkipStage10() => Skip(10);
    private static bool SkipStage11() => Skip(11);
    private static bool SkipStage12() => Skip(12);
    private static bool SkipStage13() => Skip(13);
    private static bool SkipStage14() => Skip(14);
    private static bool SkipStage15() => Skip(15);
    private static bool SkipStage16() => Skip(16);
    private static bool SkipStage17() => Skip(17);
    private static bool SkipStage18() => Skip(18);
    private static bool SkipStage19() => Skip(19);

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
