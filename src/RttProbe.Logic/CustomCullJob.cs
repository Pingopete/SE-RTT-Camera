using System.Reflection;

namespace RttProbe;

// Our own CullingJob, targeting the main-view pass groups without the main-view job's
// side effects.
//
// WHY NOT THE ENGINE'S _mainViewCullingJob. Two reasons, and the second is the one that
// cost a session.
//
// 1. Pass groups. It targets [MainViewPass, MainViewDeferredTexturingPass,
//    MainViewCountVolumeSegments, MainViewGatherVolumeSegments] and NOT Indirect. Our
//    visible image is drawn from the Indirect group, so using that job alone deletes the
//    feed. Co-culling solves that, and this class is compatible with it.
//
// 2. LOD TRANSITIONS. CullingJob.DoWork does not take a LODTransitionContext — it reads
//    the global:
//
//        ldsfld   CoreSystems.DrawContexts
//        callvirt DrawContextManager.get_LODTransitions()
//
//    That is the parameter test failing: a pass reading state it was not handed.
//    LODTransitionContext is per-view temporal state tracking which objects are mid
//    LOD crossfade. Culling from a second camera at a different distance writes different
//    transition state for the same objects, the player's view reads it, and their geometry
//    pops between LOD levels. That is the ship-light flicker, and it is why the flicker
//    survived giving the cull private visibility lists AND private geometry buffers — it
//    was never either of those.
//
// THE FIX. CullingGeometryJob.DoWork null-guards every use of lodTransitions, and the
// engine's own assert states the contract outright:
//
//     "lodTransitions != null || (_forcedLODMethod.HasValue &&
//                                 _forcedLODMethod != LODMethod.TransitionTimeBased)"
//
// So a forced LOD method that is not TransitionTimeBased makes the transition context
// legitimately optional. We force LODMethod.SingleLevel — value 1, exactly what the
// engine's own _indirectCullingJob forces, and that job has never disturbed the player's
// view. Paired with nulling the global for the duration of our pass (see
// CameraRender.InstallNoLodTransitions), every write lands on a null guard instead of on
// the player's state.
//
// Losing LOD crossfade costs us nothing worth having: it is a sub-pixel nicety on a
// 512x512 feed, and the alternative is corrupting the main render.
//
// FLAGS. Mirrors the indirect job everywhere it is safe to, since that configuration is
// proven not to disturb the player:
//
//                          indirect      main view     ours
//     targetPasses         [12]          [0,2,3,4]     [0,2,3,4]
//     forcedLODMethod      SingleLevel   null          SingleLevel
//     twoPassCulling       false         true          false
//     isForMainView        false         true          false
//     geometryOnly         true          false         false
//
// geometryOnly follows the main-view job rather than the indirect one because it decides
// whether the entity-proxy job runs for all proxy types or only GPUEntityProxyType 4, and
// the main-view pass groups need the full set. It is the one flag not copied from the
// proven-safe side, so it is the first thing to suspect if this misbehaves.
//
// TASKS. The ctor takes a List<Task> the sub-jobs add shader compilation work to. The
// engine collects and awaits its own during load; ours is fresh, so nothing would await
// it. Ready gates on every task in our list having completed, so the job is not used
// until its shaders exist — a job used early would either no-op or fault.
internal static class CustomCullJob
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object _job;

    // Keen.VRage.Library.Threading.Task, NOT System.Threading.Tasks.Task. The ctor's
    // parameter is List<Keen…Task>, and handing it a BCL list throws ArgumentException at
    // Invoke — which is how this was found. It is a struct with a public IsCompleted, so
    // the list is held as a non-generic IList and polled by reflection.
    private static System.Collections.IList _tasks;
    private static PropertyInfo _piIsCompleted;
    private static int _state;          // 0 untried, 1 built, -1 unavailable
    private static bool _readyLogged;

    public static object Job => _state == 1 ? _job : null;

    // Built AND its shaders compiled. Callers must check this, not Job != null.
    public static bool Ready
    {
        get
        {
            if (_state != 1) return false;
            if (_tasks == null) return true;
            try
            {
                lock (_tasks.SyncRoot ?? new object())
                {
                    foreach (var t in _tasks)
                    {
                        if (t == null) continue;
                        _piIsCompleted ??= t.GetType().GetProperty("IsCompleted", Any);
                        if (_piIsCompleted?.GetValue(t) is bool done && !done) return false;
                    }
                }
            }
            catch { return true; }   // unreadable: do not deadlock the pass on a poll

            if (!_readyLogged)
            {
                _readyLogged = true;
                RttLog.Line($"Custom cull job: ready — {_tasks.Count} shader task(s) completed.");
            }
            return true;
        }
    }

    public static void Reset()
    {
        if (_job is IDisposable d) { try { d.Dispose(); } catch { } }
        _job = null;
        _tasks = null;
        _piIsCompleted = null;
        _state = 0;
        _readyLogged = false;
    }

    public static bool Ensure()
    {
        if (_state != 0) return _state == 1;
        _state = -1;
        try
        {
            var anchor = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var asm = anchor?.Assembly;
            var jobType = asm?.GetTypes().FirstOrDefault(t => t.Name == "CullingJob");
            var groupType = FindType(asm, "PassGroupType");
            var lodType = Type.GetType("Keen.VRage.Render.Data.LODMethod, VRage.Render")
                          ?? FindTypeAnywhere("LODMethod");
            if (jobType == null || groupType == null || lodType == null)
            {
                RttLog.Line($"Custom cull job: types not found (CullingJob={jobType != null}, " +
                            $"PassGroupType={groupType != null}, LODMethod={lodType != null}).");
                return false;
            }

            var ctor = jobType.GetConstructors(Any).FirstOrDefault(c => c.GetParameters().Length == 7);
            if (ctor == null)
            {
                RttLog.Line("Custom cull job: CullingJob ctor(7 args) not found.");
                return false;
            }

            // Take the element type from the ctor signature rather than assuming it.
            // Assuming System.Threading.Tasks.Task is exactly what failed here — the
            // engine has its own Keen.VRage.Library.Threading.Task and Invoke rejects the
            // BCL one outright.
            var tasksParam = ctor.GetParameters()[5].ParameterType;
            var taskElem = tasksParam.IsGenericType ? tasksParam.GetGenericArguments()[0] : null;
            if (taskElem == null)
            {
                RttLog.Line($"Custom cull job: tasks parameter is {tasksParam.Name}, not a List<T>.");
                return false;
            }

            // Which pass groups to cull for. Configurable because the engine's main-view
            // job takes all four and only two of them are any use to us:
            //
            //   0  MainViewPass                   GBufferPassJob draws this
            //   2  MainViewDeferredTexturingPass  DeferredTexturingJob draws this
            //   3  MainViewCountVolumeSegments    volumetric, phase 1
            //   4  MainViewGatherVolumeSegments   volumetric, phase 2
            //
            // 3 and 4 are a two-phase volumetric algorithm whose consuming passes we do
            // not run. Culling for them fills the volume instance buffers with commands
            // nothing then interprets correctly, and unowned draw commands rasterise as
            // large stretched wedges at changing angles — which is exactly what appeared
            // in the feed the first time all four were used.
            var listType = typeof(List<>).MakeGenericType(groupType);
            var groups = (System.Collections.IList)Activator.CreateInstance(listType);
            foreach (var v in FeedConfig.CoCullPassGroups)
                groups.Add(Enum.ToObject(groupType, v));
            if (groups.Count == 0)
            {
                RttLog.Line("Custom cull job: coCullPassGroups is empty — nothing to cull for.");
                return false;
            }

            // SingleLevel = 1. Not TransitionTimeBased, which is what lets the transition
            // context be null without tripping CullingGeometryJob's assert. Null mirrors
            // the main-view job and is what corrupts the player's LOD crossfade state —
            // available only to re-confirm that diagnosis, never as a setting.
            var nullableLod = typeof(Nullable<>).MakeGenericType(lodType);
            var forced = FeedConfig.CoCullForceSingleLod
                ? Activator.CreateInstance(nullableLod, Enum.ToObject(lodType, 1))
                : Activator.CreateInstance(nullableLod);

            _tasks = (System.Collections.IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(taskElem));
            _job = ctor.Invoke(new object[]
            {
                groups,                            // targetPasses
                forced,                            // forcedLODMethod
                FeedConfig.CoCullTwoPass,          // isUsedWithTwoPassCulling
                FeedConfig.CoCullForMainView,      // isForMainView
                FeedConfig.CoCullGeometryOnly,     // geometryOnly
                _tasks,                            // tasks
                false,                             // isLocalLights
            });

            _state = _job != null ? 1 : -1;
            RttLog.Line(_state == 1
                ? $"Custom cull job: BUILT — groups [{string.Join(", ", FeedConfig.CoCullPassGroups.Select(NameGroup))}], " +
                  $"forcedLODMethod={(FeedConfig.CoCullForceSingleLod ? "SingleLevel" : "null (WILL corrupt the player's LOD state)")}, " +
                  $"twoPass={FeedConfig.CoCullTwoPass}, isForMainView={FeedConfig.CoCullForMainView}, " +
                  $"geometryOnly={FeedConfig.CoCullGeometryOnly}. {_tasks.Count} shader task(s) pending."
                : "Custom cull job: construction returned null.");
            return _state == 1;
        }
        catch (Exception e) { RttLog.Error("build custom culling job", e); return false; }
    }

    private static string NameGroup(int v) => v switch
    {
        0 => "MainViewPass",
        1 => "MainViewEffects",
        2 => "DeferredTexturing",
        3 => "CountVolumeSegments",
        4 => "GatherVolumeSegments",
        12 => "Indirect",
        _ => v.ToString(),
    };

    private static Type FindType(Assembly asm, string name)
    {
        try { return asm?.GetTypes().FirstOrDefault(t => t.Name == name); }
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
