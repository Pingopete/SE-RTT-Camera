using System.Reflection;
using System.Runtime.Loader;
using Keen.VRage.Core.Plugins;

namespace RttProbe;

// Static handoff between the bootstrap (loaded once, holds the Harmony patches)
// and the hot-reloadable logic assembly. The bootstrap never references logic
// types directly — that would pin the collectible load context.
public static class RttBridge
{
    // ---- PARKED PROBE MANAGERS (goal 4.4, CTD 2026-07-30 18:46) --------------------
    //
    // Per-feed EnvironmentProbeManager instances, held HERE rather than in the logic
    // assembly. This is not a convenience — it is the only place they can live.
    //
    // The manager owns eight cube textures, each six faces of RTV descriptors. Those come
    // from DescriptorHeapPool, a small FIXED pool that exhausts long before VRAM does. The
    // manager is deliberately never disposed (three device removals established that
    // disposing it mid-session removes the device), so the design depends on "kept" really
    // meaning kept.
    //
    // It did not. The logic assembly is COLLECTIBLE. A field there is gone on every hot
    // reload, so each reload built a fresh manager and left the previous one unreachable
    // from any code that could free it. Not disposing it was the deliberate choice; losing
    // the reference to it was not. Four reloads in one session ran the pool dry:
    //
    //     Assertion Failure: Out of the descriptor heap
    //       at DescriptorHeapPool.BorrowRTV()
    //       at RenderTargetCubeTexture.FaceMips.Initialize()
    //       at EnvironmentProbeManager.RecreateProbes()
    //       at WholeSceneRender.InstallProbes()
    //     [Watchdog]: application froze, RenderThreadFreeze.
    //
    // The bootstrap is loaded once and never unloaded, so a reference here survives every
    // reload and the SAME manager is reused instead of a new one being built beside it.
    //
    // TYPED AS object ON PURPOSE. The bootstrap must not reference engine render types any
    // more than it references logic types — resolving them here would drag Render12 type
    // loading into plugin init, which has already poisoned a type once (the
    // ConfigurationNotFoundException in a CoreSystems cctor). The logic side reflects over
    // these; the bootstrap only holds them alive.
    //
    // Sized to Feeds.MaxFeeds. A mismatch is not a crash — the logic side bounds-checks —
    // but it would silently stop parking the feeds past the end, so keep them in step.
    public static readonly object[] ParkedProbeManagers = new object[4];

    // PER-FEED EYE ADAPTATION, parked for exactly the reason above.
    //
    // EyeAdaptationJob holds the auto-exposure history as INSTANCE state — a
    // RenderTargetTexture[] ping-pong pair plus a histogram RWBuffer — which is what makes
    // per-feed adaptation possible at all: give our render its own instance and its history
    // stops fighting the player's. That is the same shape as the probe manager, the cascade
    // shadows and the draw contexts.
    //
    // AND IT CARRIES THE SAME HAZARD, which is why this array exists before the feature does.
    // Logic statics die on every hot reload, so a logic-owned instance is rebuilt each time
    // and the previous one becomes unreachable from any code that could dispose it. Its
    // RENDER TARGET VIEWS leak — and RTV descriptors come from a small fixed pool that
    // exhausts long before VRAM does. That is not a prediction: own-probes CTD'd on
    // 2026-07-30 with "Assertion Failure: Out of the descriptor heap at
    // DescriptorHeapPool.BorrowRTV()" after four reloads, while VRAM sat flat at 12.2 GB.
    // Parking the instance here is the fix that made own-probes safe, and it is a
    // prerequisite for this feature rather than a hardening pass to do afterwards.
    //
    // Typed object and sized to Feeds.MaxFeeds, both for the reasons given above.
    public static readonly object[] ParkedEyeAdaptation = new object[4];

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

    // (sceneDrawSystem, finalLDRBuffer) — fires at the TOP of Draw, BEFORE the player's
    // frame is recorded. The start-of-frame submission position.
    //
    // Same patch site as WholeSceneHook, opposite end. Recording our render here puts our
    // GPU work ahead of the player's in the queue, so it EXECUTES while the CPU is still
    // recording the player's frame — instead of sitting between the player's work and the
    // present copy, where it delays the swap by its full duration.
    //
    // Safe because Draw's prefix is downstream of ALL of DrawInternal's frame prep:
    // FinalizeResources, UpdateImmediateHeap, CreateDirectCommandList, RefreshTables,
    // Settings.OnBeginDraw, CommonResources.OnBeginDraw, ScreenBuffers.Update and
    // DrawContextManager.OnBeginDraw have all run by the time we get here (verified
    // against DrawInternal's call order — Draw is the 86th call, that prep is calls
    // 13-73). Everything a nested Draw consumes is live, same frame, same frame span.
    // That is what makes this cheaper in risk than the post-present position, which
    // crosses the boundary where spans close and transients recycle.
    //
    // Re-entrancy: our own nested Draw fires this too, so the handler must return
    // immediately when it does — same rule as WholeSceneHook.
    public static volatile Action<object, object> WholeSceneEarlyHook;

    // TRUE while the logic side is inside its nested second Draw. Read by the log-only
    // probes (CopyJob / ScreenBuffers.InitializeBuffers) so their lines say which render
    // an engine call fired in. Absent/null on an old logic assembly — probes then log
    // inOurRender=false, which is still useful.
    public static volatile Func<bool> InOurRenderHook;

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

    // ---- MANAGED WORLD AREA REGISTRATIONS (goal 10 tier 2, 2026-08-01) -----------------
    //
    // (area, sessionComponent) pairs captured from ManagedWorldArea.OnRegistered — the
    // SERVER-side objects, which is the entire point. The client's mirror component
    // (ClientManagedWorldAreaSessionComponent) exposes the same area list, but calling
    // TryLoad on a client-scene area throws KeyNotFoundException from
    // Scene.FinishBefore<SpawnSyncPoint> and CRASHED THE GAME TWICE on 2026-08-01:
    // loading is a server concern and only the server scene registers the spawn sync
    // point. OnRegistered hands us the server component as a parameter, so whatever is
    // in this list is by construction from the scene where TryLoad is legal.
    //
    // Lives in the BOOTSTRAP for two reasons: registrations fire DURING world load,
    // before the logic assembly has attached its hooks, so a forwarding hook would miss
    // them all; and logic statics die on every hot reload while these references must
    // not. Entries are (area, session) object pairs, typed object for the same reason
    // ParkedProbeManagers is. Appended only — a world reload appends a new batch with a
    // NEW session component, and the logic side keys on the LAST entry's session to
    // ignore stale worlds. The lock covers the racy append-during-load window.
    public static readonly List<object[]> ManagedAreaRegistrations = new();
    public static readonly object ManagedAreaLock = new();

    // ---- PER-BODY CLIPMAP CAMERA (goal 10, the terrain fix) ---------------------------
    //
    // (voxelRenderComponent, boxedWorldTransform) -> replacement boxed WorldTransform, or
    // null to leave the engine's choice alone.
    //
    // VoxelRenderUpdateSessionComponent.UpdateClipmaps() reads ONE global camera transform
    // per frame and calls UpdateClipmap(body, camera, loading) for EVERY voxel body — so
    // terrain meshes are built around the player and nowhere else. That is the entire
    // reason a remote feed sees a smooth blob where the player sees boulders, proven by
    // positive control on 2026-08-01.
    //
    // But the camera is a PER-CALL ARGUMENT, not a global the callee re-reads. When the
    // player and the feed camera are near DIFFERENT bodies (player on Kemik, camera on
    // Verdure), each body can be driven by whichever viewer is actually near it with no
    // contention at all — this is emphatically NOT the single-slot tug-of-war that swapping
    // RenderSettings.CameraTransform would be, because each clipmap still gets exactly one
    // camera; we only change WHICH one, per body.
    //
    // Typed as object on purpose: the bootstrap stays ignorant of engine types and the
    // logic assembly (reloadable) owns every decision. The transform is a STRUCT, so it
    // arrives boxed and the logic returns a modified box.
    public static volatile Func<object, object, object> ClipmapCameraHook;

    // The VoxelRenderUpdateSessionComponent that owns the clipmap update loop, captured from
    // the patch's __instance. It is NOT reachable from the session-components entity (the
    // logic looked and it is genuinely absent from that roster), and it owns _lodDistances —
    // the 16-slot LODData array whose sharing across bodies is the current prime suspect for
    // the mid-LOD plateau. Handing it over here costs nothing and saves guessing at its home.
    public static volatile object VoxelUpdateComponent;

    // ---- THE SIM-PUMP SEAT (goal 10, the server half) ---------------------------------
    //
    // Everything the trigger census proved wants a presence entity in the SERVER scene:
    // flora sectors, managed world areas and voxel sectors all constrain on
    // DynamicTag + WorldTransform + BoundingBoxData (census 2026-08-01, constraints read
    // from the live TriggerArgs). But structural scene mutation is only safe on the thread
    // that pumps that scene — the TryLoad freeze (FinishBefore<SpawnSyncPoint> from OUR
    // thread) is the standing proof of what happens otherwise.
    //
    // So the bootstrap provides a SEAT rather than an action: a Harmony prefix on a method
    // that provably runs on every scene's own pump each frame hands the logic a callback
    // IN that seat, and the logic decides per-invocation whether this is the scene it
    // wants (it probes the job tables for SpawnSyncPoint, same discriminator as ever).
    // The bootstrap stays ignorant of what the logic does there, exactly like every other
    // hook on this bridge.
    //
    // (component) -> void, invoked at the top of SpatialTriggerSystemSessionComponent's
    // per-frame pending-trigger processing, on that component's scene's pump thread.
    public static volatile Action<object> SimPumpHook;

    // ---- FLORA SECTOR CAMERA (goal 10, the client-visibility half) --------------------
    //
    // FloraSectorEntityComponent.UpdateCameraPosition and .UpdateVisibility both read
    // CoreSystems.Settings.RenderView.CameraPosition — ONE global camera — express it in
    // the sector root's frame, and hand it to InstanceSparseOctree.UpdateCamera /
    // .UpdateVisibility. The octree culls by distance (_maxCullingDistance,
    // _minDistanceToOctree, _isVisible), so with the player 3,912 km away every flora
    // sector near a remote feed camera is marked INVISIBLE. The content exists; the
    // renderer is told to hide it. Same single-viewer disease as the clipmap.
    //
    // The hook fires as a POSTFIX, deliberately: the engine's own update runs first and
    // completely (the player can never lose flora), then the logic re-points the octree of
    // sectors it claims at the feed camera. Last write wins, nothing is suppressed, and a
    // fault in our half leaves the engine's result standing.
    //
    // PREFIX, NOT POSTFIX, and the difference is the whole feature. InstanceSparseOctree
    // .UpdateCamera early-outs on `coords == _cameraCoords`, so a postfix that overwrites
    // after the engine leaves _cameraCoords flipping between the player and the feed every
    // frame: UpdateSubdivision() re-runs forever, cells never settle, and the flora that
    // does appear is sparse (observed 2026-08-02 — thin foliage, no grass, 1.5M claims in
    // 15 s). Suppressing the engine's call for sectors we claim lets the octree settle on
    // ONE camera, which is what it is built to expect.
    //
    // (component, boxedArgs, isVisibilityJob) -> true if the logic handled this sector and
    // the original must be skipped. Typed as object so the bootstrap stays ignorant of
    // VRage.Render12 types.
    public static volatile Func<object, object[], bool, bool> FloraCameraHook;

    // ---- THE NEAREST-VIEWER DISTANCE (2026-08-02) -------------------------------------
    //
    // THE ONE NUMBER BEHIND THREE SYMPTOMS. RenderUtilities.CalculateDistanceToCamera reads
    // CoreSystems.Settings.RenderView.CameraPosition — the single global camera — and returns
    // the distance from it to an entity's bounding box. DistanceTagManagerComponent
    // .OnUpdateDistanceToCamera caches that ONE float per entity as DistanceRangeData, and a
    // whole family of jobs then reads nothing but that cached number:
    //
    //     OnUpdateRootEntityStreamingTag  -> ResourceStreamingComponent.StreamingTag
    //                                        (threshold RootResourceStreamingComponent
    //                                        .RootStreamingDistance, which is 200 m)
    //     OnUpdateImpostorTag             -> ImpostorComponent Near/FarDistanceTag
    //                                        (threshold ImpostorSettings.SwapDistance)
    //     OnUpdateShadowTrackingTag       -> ShadowSettings.LocalLights.DirtyAreaTracking...
    //     OnUpdateRaytracingTag           -> RaytracingSettings.Scene Near/FarDistance
    //     OnUpdateTag                     -> geometry-dirty tags
    //
    // So a remote feed camera 3,906 km from the player puts EVERY entity it looks at in the
    // farthest distance bucket, no matter that our camera is standing on top of them. That is
    // one mechanism producing the whole remaining fidelity gap at once: trees resolving low
    // up close, foliage thinner than local, and grass — whose model arrives solely through
    // the streaming path (GrassEntityComponent.UpdateModel(handle, materials, lod)) — never
    // appearing at all.
    //
    // THE FIX IS THE ENGINE'S OWN SEMANTIC, not a duplicate of it. "Distance to the camera"
    // with several viewers means distance to the NEAREST one; the engine already spells that
    // out elsewhere (ManagedTexturePrioritizerComponent/ClosestDistanceCollector). The hook
    // returns min(engineAnswer, ourAnswer), which is monotone: a distance can only get
    // SMALLER, so no entity the player is near can ever be demoted by us. Overlap between
    // the two viewers' bubbles is not a conflict — min() is idempotent.
    //
    // (x, y, z of the entity's world position, the engine's own answer) -> the answer to use.
    // Primitives only: the logic assembly never sees an engine type and this stays allocation
    // free on a path that runs over every root entity in the render scene.
    public static volatile Func<double, double, double, float, float> ViewerDistanceHook;

    // ---- GRASS WITHOUT HiZ, FOR OUR PASS ONLY (2026-08-02) ----------------------------
    //
    // WHY THIS RATHER THAN THE SETTING. Clearing HZBOSettings.MainViewEnabled around our
    // render whited out the feed AND made the PLAYER'S world flicker: six render paths read
    // IsOcclusionCullingAllowed and expect one value for the whole frame, and SceneFinalize
    // gates the second visible-entity update on it while RenderGBuffer still runs that pass.
    // Scoping a field the pipeline snapshots is the documented RaytracingSettings hazard
    // wearing a new hat.
    //
    // RenderGrass(DirectCommandList, bool enableHiZ) takes it as an ARGUMENT. GrassRendering
    // then picks _triplanarSingleGenNoHiZPSO over _triplanarSingleGenPSO from that argument
    // alone. So forcing the parameter false reaches exactly the grass generator and nothing
    // else — per-pass by construction, no shared state touched, and it CANNOT reproduce the
    // flicker because no other consumer sees it.
    //
    // The question it answers: grass instances are occlusion-tested against a depth pyramid.
    // If that pyramid does not match our camera, every instance is rejected and the feed has
    // no grass at all rather than thin grass — which is exactly what the feed shows.
    public static volatile Func<bool> GrassNoHiZHook;

    // Call/override counters for the above, written by the postfix and read by the logic's
    // reporter. Plain longs, incremented without interlock on purpose: this is a per-entity
    // per-frame path and an occasional lost increment costs a diagnostic nothing, while a
    // lock or an Interlocked would cost the engine real time.
    public static long ViewerDistanceCalls;
    public static long ViewerDistanceOverrides;
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
            PatchGhostProbes(harmony);
            PatchManagedAreas(harmony);
            PatchClipmapCamera(harmony);
            PatchSimPumpSeat(harmony);
            PatchFloraCamera(harmony);
            PatchViewerDistance(harmony);
            PatchGrassHiZ(harmony);
        }
        catch (Exception e) { Log("Patching FAILED: " + e); }
    }

    // Grass-without-HiZ for our pass — see RttBridge.GrassNoHiZHook.
    private static void PatchGrassHiZ(HarmonyLib.Harmony harmony)
    {
        try
        {
            var sds = Type.GetType("Keen.VRage.Render12.Core.Systems.SceneDrawSystem, VRage.Render12");
            var mi = sds?.GetMethod("RenderGrass",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("SceneDrawSystem.RenderGrass not found — grass HiZ override inactive."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(RenderGrassPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log($"Patched SceneDrawSystem.RenderGrass({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) " +
                "— grass HiZ override armed (wholeSceneGrassNoHiZ).");
        }
        catch (Exception e) { Log("Patching RenderGrass FAILED: " + e.Message); }
    }

    // __1 is the SECOND parameter (enableHiZ); __0 is the command list. Positional injection
    // rather than by name so a parameter rename in a game update cannot silently detach this.
    //
    // ref, and writable: Harmony writes a modified `ref` parameter back into the call, which
    // is what lets the original run with our value instead of the caller's. A throw or a null
    // hook leaves the caller's argument untouched, so the failure mode is "no change".
    private static void RenderGrassPrefix(ref bool __1)
    {
        var hook = RttBridge.GrassNoHiZHook;
        if (hook == null) return;
        try { if (hook()) __1 = false; } catch { }
    }

    // The nearest-viewer distance — see RttBridge.ViewerDistanceHook for the mechanism.
    //
    // A POSTFIX, and that is the safety property: the engine computes its own answer in full
    // first, so a null hook, a disabled feature or a throw all leave the engine's number
    // exactly as it was. We only ever get to lower it.
    private static void PatchViewerDistance(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.Utils.RenderUtilities, VRage.Render12");
            if (t == null) { Log("RenderUtilities not found — nearest-viewer distance inactive."); return; }
            var mi = t.GetMethod("CalculateDistanceToCamera",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) { Log("RenderUtilities.CalculateDistanceToCamera not found — nearest-viewer distance inactive."); return; }
            var post = typeof(RttPlugin).GetMethod(nameof(DistanceToCameraPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log($"Patched RenderUtilities.CalculateDistanceToCamera({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) " +
                "— nearest-viewer distance armed. This is the single input to StreamingTag, " +
                "the impostor swap, shadow tracking and the raytracing near/far tags.");
        }
        catch (Exception e) { Log("Patching CalculateDistanceToCamera FAILED: " + e.Message); }
    }

    // RUNS FOR EVERY ROOT ENTITY IN THE RENDER SCENE, on the renderer's job threads. The cost
    // budget here is a few nanoseconds, so:
    //
    //   * __0 rather than a named parameter — positional injection cannot be broken by a
    //     parameter rename in a game update.
    //   * WorldTransform TYPED, not object[] — Harmony boxes __args, and boxing a 40-byte
    //     struct per entity per frame is exactly the allocation storm this project already
    //     measured once. VRage.Core is referenced by the bootstrap, so the type costs nothing.
    //   * the second argument (LocalBoundsData) is simply not requested: Harmony injects only
    //     what the patch asks for, and that type is nested inside VRage.Render12, which the
    //     bootstrap deliberately does not reference.
    //
    // Dropping boundsData means our answer is a CENTRE distance while the engine's is a
    // bounding-BOX distance, i.e. ours over-estimates for a large entity. Under min() an
    // over-estimate can only fail to help — it can never demote anything — so the
    // approximation is safe by construction rather than by luck. For the entities this
    // feature exists to fix (trees, boulders, grass cells) the box is metres wide and the
    // difference is noise.
    private static void DistanceToCameraPostfix(Keen.VRage.Core.WorldTransform __0, ref float __result)
    {
        var hook = RttBridge.ViewerDistanceHook;
        if (hook == null) return;
        RttBridge.ViewerDistanceCalls++;
        try
        {
            var p = __0.Position;
            var r = hook(p.X, p.Y, p.Z, __result);
            if (r < __result) { __result = r; RttBridge.ViewerDistanceOverrides++; }
        }
        catch { }
    }

    // Per-body clipmap camera — see RttBridge.ClipmapCameraHook for the reasoning.
    //
    // __args rather than typed parameters: it keeps the bootstrap free of VRage.Voxels
    // types, and Harmony writes __args back for prefixes, which is what lets a boxed struct
    // argument be replaced. A prefix returning void never suppresses the original.
    private static void PatchClipmapCamera(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.Voxels.Client.Components.VoxelRenderUpdateSessionComponent, VRage.Voxels.Client");
            var mi = t?.GetMethod("UpdateClipmap",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("VoxelRenderUpdateSessionComponent.UpdateClipmap not found — per-body clipmap camera inactive."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(UpdateClipmapPrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log($"Patched VoxelRenderUpdateSessionComponent.UpdateClipmap({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) — per-body clipmap camera armed.");
        }
        catch (Exception e) { Log("Patching UpdateClipmap FAILED: " + e.Message); }
    }

    // Runs for EVERY voxel body EVERY frame. It must be cheap and it must never throw:
    // an exception here is an exception in the engine's terrain update loop.
    private static void UpdateClipmapPrefix(object __instance, object[] __args)
    {
        if (RttBridge.VoxelUpdateComponent == null) RttBridge.VoxelUpdateComponent = __instance;
        var hook = RttBridge.ClipmapCameraHook;
        if (hook == null || __args == null || __args.Length < 2) return;
        try
        {
            var replacement = hook(__args[0], __args[1]);
            if (replacement == null) return;
            // Two return shapes, so an old logic DLL keeps working against this bootstrap:
            //   boxed WorldTransform            -> replace the camera only
            //   object[]{ transform, bool }     -> replace the camera AND the loadingPhase
            //                                      flag (__args[2]) — the spawn-speed
            //                                      meshing path the sync-loader uses.
            if (replacement is object[] { Length: >= 2 } pair)
            {
                if (pair[0] != null) __args[1] = pair[0];
                if (__args.Length >= 3 && pair[1] is bool loading) __args[2] = loading;
            }
            else __args[1] = replacement;
        }
        catch { }
    }

    // Flora sector camera — see RttBridge.FloraCameraHook for the reasoning.
    //
    // Both jobs take (ref RootData, ReadOnlyEntityData<WorldTransform>) whose types are
    // nested/private to VRage.Render12, so the postfixes take __args (Harmony boxes them)
    // rather than typed parameters — the same trick the clipmap prefix uses to stay free
    // of engine types.
    private static void PatchFloraCamera(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType(
                "Keen.VRage.Render12.SceneSystem.Components.FloraSectorEntityComponent, VRage.Render12");
            if (t == null) { Log("FloraSectorEntityComponent not found — flora camera inactive."); return; }
            int n = 0;
            foreach (var (name, pre) in new[]
            {
                ("UpdateCameraPosition", nameof(FloraCameraPrefix)),
                ("UpdateVisibility",     nameof(FloraVisibilityPrefix)),
            })
            {
                var mi = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null) { Log($"FloraSectorEntityComponent.{name} not found — skipped."); continue; }
                var pm = typeof(RttPlugin).GetMethod(pre, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pm));
                n++;
            }
            Log(n > 0
                ? $"Patched {n} FloraSectorEntityComponent job(s) — per-sector flora camera armed."
                : "Flora camera FAILED: no patchable job found.");
        }
        catch (Exception e) { Log("Patching flora camera FAILED: " + e.Message); }
    }

    // Per flora sector, per throttled frame. Cheap and never throwing: this sits inside the
    // renderer's scene update. Returning false skips the engine's own update for sectors
    // the logic has claimed — see RttBridge.FloraCameraHook for why suppression rather than
    // overwriting. A throw or a null hook always falls through to the original.
    private static bool FloraCameraPrefix(object __instance, object[] __args)
    {
        var hook = RttBridge.FloraCameraHook;
        if (hook == null) return true;
        try { return !hook(__instance, __args, false); } catch { return true; }
    }

    private static bool FloraVisibilityPrefix(object __instance, object[] __args)
    {
        var hook = RttBridge.FloraCameraHook;
        if (hook == null) return true;
        try { return !hook(__instance, __args, true); } catch { return true; }
    }

    // The sim-pump seat — see RttBridge.SimPumpHook.
    //
    // THIRD HOST, and the lesson is worth its line: the trigger system's methods
    // (OnTriggerPending, OnUpdateAddedOrMovedTrigger, even UpdateStats) are all
    // conditional — jobs that a quiet, unprofiled session never schedules — and two boots
    // produced a hook that provably never fired. Scene.Tick is the pump's HEARTBEAT: the
    // one method a live scene cannot avoid calling, once per frame, on its own thread.
    // The prefix costs one volatile read when the hook is unset.
    private static void PatchSimPumpSeat(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType("Keen.VRage.DCS.Scenes.Scene, VRage.DCS");
            var mi = t?.GetMethod("Tick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("Scene.Tick not found — sim-pump seat inactive."); return; }
            var pre = typeof(RttPlugin).GetMethod(nameof(SimPumpPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log("Sim-pump seat armed on Scene.Tick — fires every frame for every scene, on that scene's own thread.");
        }
        catch (Exception e) { Log("Patching sim-pump seat FAILED: " + e.Message); }
    }

    // Runs at the top of EVERY scene's frame tick, on that scene's pump thread. Must be
    // near-free and must never throw — this is the hottest seat in the engine.
    private static void SimPumpPrefix(object __instance)
    {
        var hook = RttBridge.SimPumpHook;
        if (hook == null) return;
        try { hook(__instance); } catch { }
    }

    // Managed-area registration capture — see RttBridge.ManagedAreaRegistrations for why
    // this exists and why it must live in the bootstrap. The postfix does nothing but
    // stash references; every decision belongs to the logic side, which can be reloaded.
    private static void PatchManagedAreas(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea, VRage.Core.Game");
            var mi = t?.GetMethod("OnRegistered",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) { Log("ManagedWorldArea.OnRegistered not found — area capture inactive."); return; }
            var post = typeof(RttPlugin).GetMethod(nameof(ManagedAreaRegisteredPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, postfix: new HarmonyLib.HarmonyMethod(post));
            Log("Patched ManagedWorldArea.OnRegistered (server-side area capture).");
        }
        catch (Exception e) { Log("Patching ManagedWorldArea FAILED: " + e.Message); }
    }

    // Harmony binds `session` to OnRegistered's first parameter by name. Keep this body
    // trivial and exception-proof: it runs during world load, where a throw is a failed
    // load, not a log line.
    private static void ManagedAreaRegisteredPostfix(object __instance, object session)
    {
        try
        {
            lock (RttBridge.ManagedAreaLock)
                RttBridge.ManagedAreaRegistrations.Add(new[] { __instance, session });
        }
        catch { }
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

    // __instance is APPENDED to the argument array rather than passed through a new bridge
    // field, deliberately. The logic side already locates what it needs by type name, so a
    // longer array costs it nothing — and a logic assembly running against an OLDER
    // bootstrap simply does not find an OffscreenUIRenderer in the args and degrades to "no
    // mip regeneration" with one log line, instead of a missing-field failure that would
    // take the whole handover down with it.
    //
    // Why the instance is wanted at all: OffscreenUIRenderer._mipMapJob is the engine's own
    // mip generator for this exact target, invoked one call earlier in DrawOne. Reusing it
    // creates nothing (Rule 11) and cannot fight another system for its descriptor table,
    // which borrowing CloudShadowJob's MipMapJob would have risked.
    private static void OffscreenUiPostfix(object __instance, object[] __args)
    {
        try
        {
            var withInstance = new object[__args.Length + 1];
            Array.Copy(__args, withInstance, __args.Length);
            withInstance[__args.Length] = __instance;
            RttBridge.OffscreenUiDrawHook?.Invoke(withInstance);
        }
        catch { }
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
                var pre = typeof(RttPlugin).GetMethod(nameof(WholeScenePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                // BOTH ends of the same method. The postfix always runs the bookkeeping
                // (gate, buffers, Perf, logging); which end runs the RENDER is the logic
                // side's choice, live-switchable via wholeSceneSubmitEarly. Patching both
                // unconditionally keeps that a config flip rather than a restart.
                harmony.Patch(draw,
                    prefix: new HarmonyLib.HarmonyMethod(pre),
                    postfix: new HarmonyLib.HarmonyMethod(post));
                Log($"Patched SceneDrawSystem.Draw({string.Join(", ", draw.GetParameters().Select(p => p.ParameterType.Name))}) " +
                    "— the whole-scene render hook, BOTH ends (prefix = start-of-frame submission, " +
                    "postfix = bookkeeping and the legacy render position).");
            }
        }
        catch (Exception e) { Log("Patching SceneDrawSystem.Draw FAILED: " + e.Message); }

        PatchSkippableStages(harmony, sds);
        PatchFsrGate(harmony);
        PatchExposureGate(harmony);
    }

    // __0 is the ResizableRWRenderTargetTexture the engine just rendered the player's
    // frame into. We do not touch it — it is passed through so the logic side can read
    // its format and resolution, which is what a second target has to match.
    private static void WholeScenePostfix(object __instance, object __0)
    {
        try { RttBridge.WholeSceneHook?.Invoke(__instance, __0); } catch { }
    }

    // VOID prefix — it can never skip the original. Harmony only honours a skip from a
    // prefix returning bool, and making this one bool-returning would put the player's
    // entire frame one typo away from not being drawn.
    private static void WholeScenePrefix(object __instance, object __0)
    {
        try { RttBridge.WholeSceneEarlyHook?.Invoke(__instance, __0); } catch { }
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

        // 21 — the flare pass. Paired with sharing the engine's FlaresContext.
        //
        // Every light in the world registers its flare through the GLOBAL:
        // PointLightEntityComponent.Init / SetParameters / OnRemovedFromScene, the spot
        // and particle equivalents, and SceneManager.UpdateFlareDefinitions all read
        // CoreSystems.DrawContexts.LensFlares. Our nested Draw swaps that global ten
        // times a second, so any light created, retuned or removed inside one of those
        // windows talks to OUR context instead of the engine's — and a SetParameters
        // that lands on the wrong context leaves the engine's copy holding stale
        // parameters, i.e. a flare stuck at a position the light no longer occupies.
        //
        // That is the reported "planet's atmosphere appears, completely unattached to
        // the planet". Sharing the engine's context removes the window entirely.
        //
        // But sharing alone would be worse than the disease: RenderFlares calls
        // ProcessFinishedFrame and PrepareReadback, which advance the flare OCCLUSION
        // readback across frames. Running that twice per frame against one shared
        // context would corrupt the player's flare occlusion. So share the context AND
        // skip the pass — we read the definitions, we never advance the state.
        //
        // Costs the feed nothing it had: our own FlaresContext was created empty and
        // never received a single definition, because registration goes through the
        // global and the global is the engine's whenever a light is actually created.
        (null, null),                                        // 20 RESERVED — the FSR gate
                                                             //    (an override, not a skip)
        (null, "RenderFlares"),                              // 21

        // 22-24 — THE SHARED WORLD-SPACE WRITES.
        //
        // CommonResourcesManager owns CloudShadowmap, the per-planet AtmosphereLUTTables
        // and the WeatherMapTables. All three are world-space, shared, and written from
        // whatever camera happens to be rendering. Stages 14 (ComputeCloudShadows) and 15
        // (UpdateAtmosphere) run in our nested Draw, so ten times a second we recompute
        // the player's cloud shadows, weather maps and atmosphere LUTs FROM THE ORBIT
        // CAMERA. A cloud shadowmap is a pattern projected onto world surfaces, which is
        // very close to the reported "projection of what the camera is seeing, on the
        // walls of my ship".
        //
        // Skipping the STAGES was tried early on and page-faulted: each stage also
        // produces per-frame transients its own later consumers need. So skip the JOBS
        // instead — the stage still runs, still borrows, still clears, still produces its
        // transients, and only the write to the shared world-space resource is dropped.
        // Same shape as stage 17 for RaytraceGIJob, which worked.
        //
        // Cost to the feed: no cloud shadows and no atmosphere LUT refresh of its own —
        // it uses whatever the player's frame last computed. That is the RIGHT trade
        // while these are shared: an approximate feed beats a corrupted world.
        ("Keen.VRage.Render12.LightingStage.CloudShadowJob, VRage.Render12",     "DoWork"),   // 22
        ("Keen.VRage.Render12.LightingStage.CloudWeatherMapJob, VRage.Render12", "DoWork"),   // 23
        ("Keen.VRage.Render12.LightingStage.AtmosphereLUTJob, VRage.Render12",   "DoWork"),   // 24

        (null, null),                                        // 25 RESERVED — the exposure
                                                             //    read-only override

        // 26 — THE RESOLUTION-KEYED REALLOCATION. Confirmed cause of a device removal:
        // DRED breadcrumb [15] ForwardAndPostPasses 20/255, EventStack
        // [CloudShading, ForwardPasses, ForwardAndPostPasses], PageFaultVA 0x1B54406000
        // (a REAL address — a use-after-free, not a null bind), and 360 allocation nodes
        // in the dump of which every single one was CloudAccumulateLightAlpha.
        //
        // CloudJob.DoWork calls ValidateHalfResTemporalResource, which is:
        //
        //     var halfMax = CoreSystems.ScreenBuffers.MaxPreUpscaleResolution / 2;
        //     if (resource.PeekNext().MaxResolution != halfMax) {
        //         resource.Dispose();                                   // FREE
        //         resource = new TemporalResource<>(() =>
        //             BindableTextures.CreateRWResizableRenderTargetTexture(name, fmt, halfMax));
        //     }
        //
        // It keys off CoreSystems.ScreenBuffers — the global our render SWAPS. Ours is
        // 512x512 so halfMax is 256x256; the player's 3840x2160 gives 1920x1080. So every
        // one of our renders disposes the player's cloud history and rebuilds it at 256,
        // and the player's very next frame does it straight back. Twenty allocations and
        // frees of a multi-hundred-MB resource per second, which is also the +/-151MB
        // VRAM oscillation visible in every PERF line and a large share of the frame spike.
        //
        // This is the ONE resolution-keyed resource owner our DrawContextManager swap does
        // not already cover. VolumeRenderingContext, RTGIContext, StochasticTransparency-
        // Context and WaterContext all hang off DrawContextManager — which is ours — so
        // they resize against our resolution harmlessly. CloudJob hangs off
        // SceneDrawSystem._cloudPass, and SceneDrawSystem is a singleton we do not swap.
        //
        // Every other shared job that reads MaxPreUpscaleResolution (HBAOJob, HighlightJob,
        // TerrainBlendingJob, AtmosphereAdditiveJob) only calls Resize() on a borrowed pool
        // texture — the designed per-frame path, cheap and safe. CloudJob is alone in doing
        // a genuine Dispose + Create keyed on MaxResolution. (It also retro-explains the
        // undiagnosed stage-9 HBAO device removal: same family, same global.)
        //
        // Cost to the feed: no volumetric clouds of its own. User-confirmed as free —
        // "i dont need actual clouds rendering in the feed, just the planet atmospheres",
        // and the atmospheres come from AtmosphereAdditive/MultiplyJob plus the planet-env
        // rebuild, none of which is touched here.
        ("Keen.VRage.Render12.PostProcessStage.CloudJob, VRage.Render12", "DoWork"),         // 26

        // 27-28 — THE TWO GLOBALS INSIDE DrawContextManager.OnBeginDraw.
        //
        // Owning the DrawContextManager covers almost everything, but OnBeginDraw is:
        //
        //   (LocalLightsToUpdate, ShadowMasksToUpdate) = CoreSystems.LocalLights.FlushUpdates();
        //   CascadesToUpdate          = DrawContexts.CascadeShadows.FlushUpdates();
        //   CharacterCascadesToUpdate = DrawContexts.CharacterShadows.FlushUpdates();
        //   DrawContexts.DirectionalLightShadowResources.OnBeginDraw();
        //   EnvProbesToUpdate         = CoreSystems.EnvironmentProbeManager.PrepareProbes();
        //
        // The middle three read CoreSystems.DrawContexts, which is OURS during our render.
        // The first and last read CoreSystems statics, which are the ENGINE'S, and both are
        // drain/advance operations — so our nested Draw runs each of them a second time per
        // frame against shared state.
        //
        // 27 is a CONFIRMED device removal at wholeSceneIntervalMs=33 (2026-07-28): DRED
        // breadcrumb [13] "ScenePreparation + Render" 1010/1475, EventStack
        // [EnvironmentProbes, ScenePreparation + Render], dying on the Resourcebarrier just
        // after EnvProbe_Blending, PageFaultVA 0x0 with ExistingAllocations 0 and
        // RecentFreedAllocations 0 — a NULL BIND, the opposite signature to the CloudJob
        // use-after-free. PrepareProbes stores _lastSettings, _forceReprocess and _state,
        // calls UpdateLocalLightAmbient, and can DisposeTextures + RecreateProbes. Our
        // render advancing that state machine and then skipping stage 2 leaves the player's
        // ExecuteEnvironmentProbeUpdate binding a probe face that was never produced. At
        // 10 fps it desynced rarely enough to survive; at 30 fps it is every frame.
        //
        // Cost to the feed: NONE. Stage 2 (RenderEnvironmentProbe) is already skipped, so we
        // never consumed EnvProbesToUpdate in the first place — we were paying the shared
        // state mutation for a queue we then threw away.
        //
        // 28 is the same shape and is Rule 8's other named global, but is NOT in the default
        // skip list: it has no crash attached to it yet. Patched so it can be turned on from
        // the config without a rebuild if the probe fix alone is not enough. Its cost is that
        // the feed stops updating local-light shadows of its own and uses the player's.
        //
        // Both are parameterless and return STRUCTS (Buffer<Request>, and a ValueTuple of two
        // Buffers). A Harmony prefix returning false skips the original and leaves __result at
        // default(T) — which is a zero-count Buffer that iterates safely. That is Rule 8's
        // corollary, established when an unassigned LocalLightsToUpdate turned out to be a
        // missing feature rather than a crash. So these need no __result handling at all.
        ("Keen.VRage.Render12.LightingStage.EnvironmentProbeManager, VRage.Render12",
         "PrepareProbes"),                                                                  // 27
        ("Keen.VRage.Render12.LightingStage.LocalLightsManager, VRage.Render12",
         "FlushUpdates"),                                                                   // 28

        // 29 — THE PHANTOM BLEED. Same blind spot as CloudJob (26).
        //
        // The user's description is what identified it: the ghost is not a vague imprint,
        // it is "the scene from the feed camera including skybox, bright lights emanating
        // from planets' edges, the ship's grid and asteroids", it "moves and is animated
        // showing the perspective from that camera", and the speckles in it "are the
        // skybox". That is a full colour image of OUR render appearing on the player's
        // REFLECTIVE surfaces — which is screen-space reflections, not GI.
        //
        // Ruled out first, each by test rather than by argument:
        //   * IR cache / our GI trace — skipping 17 left the bleed untouched.
        //   * ALL of GI — skipping 18 (trace AND ambient) left it untouched too.
        //   * Inheriting the engine's contexts — ours is a fresh Activator.CreateInstance,
        //     and only DirectionalLightShadowResources and LensFlares are shared, both
        //     deliberately.
        //   * The gate A/B — fully dormant is clean, so it is definitely ours.
        //
        // ScreenSpaceReflections._dynamicResources is an INSTANCE field holding
        // AverageRadianceHistory, VarianceHistory and SampleCountHistory — the temporal
        // accumulation for the reflection denoiser. The job itself is
        // SceneDrawSystem._screenSpaceReflectionsJob, and SceneDrawSystem is a singleton we
        // do NOT swap. So that history is shared with the player, our render writes our
        // scene's radiance into it, and the player's next frame denoises its reflections
        // against our content.
        //
        // Being a temporal HISTORY is also why it can bleed at all. Within one engine frame
        // our commands are recorded AFTER the player's, so a shared write can only reach
        // them if it survives into the next frame. Accumulated history does exactly that —
        // and it explains why the ghost lingered briefly after wholeSceneCamera was flipped
        // to 0, which had looked like evidence the bleed was camera-independent.
        //
        // Cost to the feed: no screen-space reflections of its own. DoWork takes its
        // destination as a parameter, so nothing downstream loses a resource — the same
        // shape as CloudJob, which skipped cleanly. PrepareResources is deliberately NOT
        // touched: like UpsamplingJob's (see 19) it ALLOCATES, and skipping an allocator
        // is how that one removed the device.
        ("Keen.VRage.Render12.PostProcessStage.ScreenSpaceReflection.ScreenSpaceReflections, VRage.Render12",
         "DoWork"),                                                                         // 29

        // 30 — SPLITTING STAGE 1, and this one gives the feed back three things at once.
        //
        // Stage 1 is ExecuteRaytracingPrepareAndSceneFinalize, and its NAME is the whole bug:
        // it is TWO unrelated bodies behind one entry point.
        //
        //     RaytracingPrepare(cl)    world-space shared RT state — the reason 1 was skipped
        //     SceneFinalize(cl)        nothing to do with raytracing at all
        //
        // SceneFinalize, read in full, runs on OUR DrawContexts:
        //
        //     CascadeStatsJob                                        cascade shadow stats
        //     LODStateUpdateJob(DrawContexts.LODTransitions)         LOD state
        //     LODStateUpdateJob(DrawContexts.InstancedLODTransitions) INSTANCED LOD state
        //     VisibleEntitiesUpdateJob(MainViewCulling.FirstPass, MainOutputGeometryBuffers)
        //     VisibleInstancedEntitiesUpdateJob(MainViewCulling.FirstPass, ...)
        //     ...and the same two again for SecondPass when HZBO.MainViewEnabled
        //
        // So skipping stage 1 for a RAYTRACING reason silently cost the feed its LOD state
        // updates, its INSTANCED LOD state updates, and its visible-entity sets. That is a
        // one-to-one match with the three fidelity gaps that were being chased as separate
        // bugs after goal 10:
        //
        //     LOD state never updates            trees resolve to low-detail up close
        //     instanced LOD never updates        foliage thinner than the same biome locally
        //     visible-entity set never updates   RenderGrass generates from
        //                                        DrawContexts.MainViewCulling.EntityProxies,
        //                                        so grass generates for nothing and no grass
        //                                        appears AT ALL
        //
        // The grass probe is what closed it: inside our own pass Grass.Enabled=True,
        // DrawDistance=1000, Density=3, Is3DMapEnabled=False, our GrassBufferContext present
        // and MainViewCulling present. Every gate open, no grass — so the failure had to be
        // the SET being generated from, and the only thing that fills that set is the job we
        // were skipping.
        //
        // Both halves have exactly ONE caller each (checked), so patching RaytracingPrepare
        // as its own stage separates them cleanly with nothing else to consider. Put 30 in
        // wholeSceneSkipStages and take 1 OUT: the RT half stays suppressed exactly as before
        // — this is strictly LESS suppression than skipping all of stage 1 — while
        // SceneFinalize runs for our camera.
        ("Keen.VRage.Render12.Core.Systems.SceneDrawSystem, VRage.Render12",
         "RaytracingPrepare"),                                                              // 30
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

    // Id 25 — THE EXPOSURE BLEED FIX. A return-value override like 20, not a plain skip.
    //
    // Confirmed at a 2 s feed interval: the player's whole world darkens the instant our
    // render fires, then slowly re-adapts, and the bleed imprint rides the dark phase.
    // ComputeExposure runs in our nested Draw (stage 4 — not skippable, its out-params
    // feed bloom and tonemap, and skipping it NRE'd when tried). With EyeAdaptation
    // scoped off for our render it takes the ConstantExposure branch, and
    // ConstantExposure.hlsl writes float2(ConstantLuminance = a FIXED 1.0, exposure)
    // into the SHARED EyeAdaptationJob._autoExposures ping-pong — a constant stamped
    // into the player's adaptation history ten times a second. It also Resets and
    // re-primes the shared readback buffers while it is at it.
    //
    // The fix: for OUR render only, skip the method body entirely and hand back the
    // job's EXISTING Exposure view. Read, never write. No new job (async PSO compile
    // raced the recorder — device removed), no new render targets (outside the engine's
    // AutoResourceState tracking — device removed). This creates nothing, so there is
    // nothing to race and no lifecycle to get wrong.
    //
    // MUST return a valid view when skipping: ComputeExposure's callers consume the
    // out-param, so a null here is the same NRE as skipping stage 4. Hence fail-open —
    // if the getter yields nothing, the original runs and we keep today's bug over a
    // crash.
    //
    // Known trade, accepted: the feed's brightness now follows the player's live
    // adaptation rather than a constant, and the wholeSceneExposure EV knob is inert
    // while this is on (it fed the branch we now skip).
    private const int ExposureReadOnlyId = 25;
    private static MethodInfo _miExposureGetter;

    private static void PatchExposureGate(HarmonyLib.Harmony harmony)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var t = Type.GetType("Keen.VRage.Render12.PostProcessStage.EyeAdaptationJob, VRage.Render12");
            var mi = t?.GetMethod("ConstantExposure", Any);
            _miExposureGetter = t?.GetProperty("Exposure", Any)?.GetGetMethod(true);
            if (mi == null || _miExposureGetter == null)
            {
                Log("EyeAdaptationJob.ConstantExposure/Exposure not found — exposure gate unavailable.");
                return;
            }

            var pre = typeof(RttPlugin).GetMethod(nameof(ConstantExposurePrefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(pre));
            Log($"Patched EyeAdaptationJob.ConstantExposure as read-only override id {ExposureReadOnlyId}.");
        }
        catch (Exception e) { Log($"Patching the exposure gate FAILED: {e.Message}"); }
    }

    // ------------------------------------------------------------- ghost probes
    //
    // LOG-ONLY Harmony prefixes, hunting the phantom bleed. The bleed is proven to be our
    // render target reaching the player's frame (it scales with our render resolution and
    // survives every stage skip, all delivery paths, and the panel binding), so the leak
    // is in whatever MOVES or REBUILDS texture content — and CopyJob is the engine's
    // converting blit. The game's own deferred-assert log already shows
    // "Source and destination should have the same resolution" (CopyJob.DoWork) and
    // "_usedMaxResolution == Vector2.Zero" (ScreenBuffers.InitializeBuffers) firing while
    // the feed runs, so both sites get identity logging.
    //
    // Neither prefix changes behaviour: no skips, no result overrides. Each unique
    // src->dst pair logs once, capped, so the cost after warm-up is one HashSet lookup.
    private static readonly HashSet<string> _copyProbeSeen = new();
    private static int _copyProbeLines;

    private static void PatchGhostProbes(HarmonyLib.Harmony harmony)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var copy = Type.GetType("Keen.VRage.Render12.PostProcessStage.CopyJob, VRage.Render12");
            var doWork = copy?.GetMethods(Any).FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 8);
            if (doWork != null)
            {
                var pre = typeof(RttPlugin).GetMethod(nameof(CopyProbePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(doWork, prefix: new HarmonyLib.HarmonyMethod(pre));
                Log("[probe] Patched CopyJob.DoWork — every unique src->dst copy logs once as [copyprobe].");
            }
            else Log("[probe] CopyJob.DoWork(8 args) not found — copy probe unavailable.");

            var sb = Type.GetType("Keen.VRage.Render12.Core.Systems.ScreenBuffers, VRage.Render12");
            var init = sb?.GetMethod("InitializeBuffers", Any);
            if (init != null)
            {
                var pre = typeof(RttPlugin).GetMethod(nameof(InitBuffersProbePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(init, prefix: new HarmonyLib.HarmonyMethod(pre));
                Log("[probe] Patched ScreenBuffers.InitializeBuffers — every (re)initialisation logs as [initprobe].");
            }
            else Log("[probe] ScreenBuffers.InitializeBuffers not found — init probe unavailable.");
        }
        catch (Exception e) { Log("[probe] Patching the ghost probes FAILED: " + e.Message); }
    }

    // args: (commandList, destination IRenderTargetView, source ITexture2DView, ...)
    private static void CopyProbePrefix(object[] __args)
    {
        try
        {
            if (_copyProbeLines >= 80) return;
            string src = ProbeDescribe(__args.Length > 2 ? __args[2] : null);
            string dst = ProbeDescribe(__args.Length > 1 ? __args[1] : null);
            bool ours = false;
            try { ours = RttBridge.InOurRenderHook?.Invoke() == true; } catch { }

            string key = (ours ? "O|" : "P|") + src + ">" + dst;
            lock (_copyProbeSeen)
            {
                if (!_copyProbeSeen.Add(key)) return;
                if (++_copyProbeLines == 80) { Log("[copyprobe] cap reached; further unique pairs unlogged."); return; }
            }
            Log($"[copyprobe] inOurRender={ours} src={src} dst={dst}");
        }
        catch { }
    }

    private static void InitBuffersProbePrefix(object __instance, object[] __args)
    {
        try
        {
            bool ours = false;
            try { ours = RttBridge.InOurRenderHook?.Invoke() == true; } catch { }
            Log($"[initprobe] ScreenBuffers.InitializeBuffers({(__args != null && __args.Length > 0 ? __args[0] : null)}) " +
                $"instance=#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__instance):x8} inOurRender={ours}");
        }
        catch { }
    }

    // "{X:512 Y:512}#1a2b3c4d" — resolution plus the RESOURCE object's identity hash, the
    // same format the logic side prints for its own blit, so lines correlate across the
    // two logs. Uncached reflection is fine: the cap check above makes the probe free once
    // 80 unique pairs have been seen.
    private static string ProbeDescribe(object view)
    {
        if (view == null) return "null";
        try
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var res = view.GetType().GetProperty("Resource", Any)?.GetValue(view) ?? view;
            var r = res.GetType().GetProperty("Resolution", Any)?.GetValue(res);
            return $"{r}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(res):x8}";
        }
        catch { return "?"; }
    }

    private static bool ConstantExposurePrefix(object __instance, ref object __result)
    {
        try
        {
            if (RttBridge.SkipStageHook?.Invoke(ExposureReadOnlyId) != true) return true;
            __result = _miExposureGetter?.Invoke(__instance, null);
            return __result == null;    // no view -> fail open: run the original
        }
        catch { return true; }
    }

    private static void PatchSkippableStages(HarmonyLib.Harmony harmony, Type sds)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (int i = 0; i < SkippableStages.Length; i++)
        {
            var (typeName, name) = SkippableStages[i];
            if (name == null) continue;     // reserved id, patched elsewhere
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
    private static bool SkipStage21() => Skip(21);
    private static bool SkipStage22() => Skip(22);
    private static bool SkipStage23() => Skip(23);
    private static bool SkipStage24() => Skip(24);
    private static bool SkipStage26() => Skip(26);
    private static bool SkipStage27() => Skip(27);
    private static bool SkipStage28() => Skip(28);
    private static bool SkipStage29() => Skip(29);
    private static bool SkipStage30() => Skip(30);

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
