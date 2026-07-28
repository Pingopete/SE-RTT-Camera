using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace RttProbe;

// A second EyeAdaptationJob, so our render stops overwriting the player's exposure.
//
// THE MECHANISM, from IL rather than inference. ComputeExposure is stage 4 and is NOT
// skippable — its out-params feed bloom and tonemap, and skipping it NREs. Scoping
// PostProcessSettings.EyeAdaptation off (which we already do) only chooses WHICH branch
// runs; both branches run on the same shared object:
//
//     EyeAdaptationJob.ConstantExposure(commandList):
//         _outputBuffers.Reset(commandList)           <- SHARED readback buffers
//         SetRTV(_autoExposures[...]); ScreenQuadJob.Draw(...)   <- SHARED ping-pong pair
//         CopySubresource(... _outputBuffers.StatsBuffer ...)
//         _outputBuffers.PrepareReadback(commandList)
//
// SceneDrawSystem holds exactly ONE _eyeAdaptationJob, built in its constructor. So ten
// times a second our 512x512 orbit view overwrites the auto-exposure textures the
// player's DynamicExposure ping-pongs against, and re-primes the readback the player's
// histogram is read from. Their next frame then adapts to the luminance of a completely
// different camera.
//
// That is the whole-image brightness oscillation reported as "flashing in the main
// world", and it is why it reads as ambient or reflection: everything moves together,
// because exposure is a global multiplier.
//
// The fix is the own-the-object pattern this project has now used for ScreenBuffers,
// DrawContextManager, the culling contexts, the geometry buffers and the shadow
// cascades. The constructor is public and self-contained — it creates its own
// _autoExposures, _histogram and _outputBuffers — so a second instance owns a second set
// of everything that was being trampled.
//
// THIS CTD'D ON ITS FIRST ATTEMPT, and the failure is instructive. Device removed ~700ms
// after "SECOND EyeAdaptationJob built ... waiting on 1 initialisation task(s)" — and the
// install line never printed, so the job was never swapped in. The fault came from
// CONSTRUCTING it. DRED: PageFaultVA 0x0, breadcrumb 971/1432 in "ScenePreparation +
// Render", stopping at the ClearRenderTargetView after EnvProbe_Blending, i.e. inside the
// PLAYER'S frame, not ours.
//
// What makes this constructor different from every other object this project owns:
//
//     EyeAdaptationJob..ctor(List<Task> initializationTasks):
//         ... CreateRenderTarget / CreateRWBuffer / StatReadbackBuffers ...
//         InitializeAsync()            <- compiles pipeline states on ANOTHER THREAD
//         initializationTasks.Add(that task)
//
// The engine builds this in SceneDrawSystem's constructor, at startup, and waits on the
// task list before any frame is drawn. We build it inside the Draw bracket, so PSO
// compilation runs concurrently with the render thread recording the player's frame. The
// waiting logic below is correct as far as it goes — it just cannot help, because the
// damage is done by the time there is anything to wait for.
//
// RULE: objects whose constructor kicks off async initialisation cannot be built inside
// a Draw. ScreenBuffers and DrawContextManager have no async half, which is exactly why
// building those mid-frame has always been safe and this was not.
//
// The diagnosis in the header stands; the construction path is what needs redesigning.
// Two options, neither built yet:
//   1. Construct once from a hook outside the Draw bracket, and install only after the
//      init task has been complete for several frames.
//   2. Do not construct a job at all: swap only the shared job's _autoExposures array
//      (and its _outputBuffers) for targets borrowed from BindableTexturePool — the same
//      pool path CameraRender has used safely every frame for weeks. Cheaper, and it
//      never compiles anything.
// Option 2 is the better shape: it needs no PSOs, so it has no async half to race.
//
// STILL LEAKING, and not fixable from here: ComputeExposure ends with
// RenderOutputContracts.ExposureChanged(exposureData), which Game2.Client's
// ToggleStatByExposureComponent subscribes to. Our render therefore still publishes its
// exposure to a gameplay component ten times a second. Suppressing that needs a Harmony
// patch on ComputeExposure itself — a bootstrap change, hence a restart — and it is a
// gameplay signal rather than a rendering one, so it is recorded, not chased.
internal static class OwnExposure
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object _ourJob;
    private static FieldInfo _field;        // SceneDrawSystem._eyeAdaptationJob
    private static IList _initTasks;
    private static int _state;              // 0 untried, 1 built, -1 unavailable
    private static bool _readyLogged, _installLogged;

    public static void Reset()
    {
        // Holds render targets, an RWBuffer and readback buffers. Dropping it on a hot
        // reload without disposing is exactly the leak that cost a session's frame rate.
        if (_ourJob is IDisposable d)
        {
            try { d.Dispose(); }
            catch (Exception e) { RttLog.Error("own exposure: dispose LEAKED", e); }
        }
        _ourJob = null;
        _field = null;
        _initTasks = null;
        _state = 0;
        _readyLogged = _installLogged = false;
    }

    // Build once. Returns true when the job exists AND its async initialisation has
    // finished — using it before the PSOs compile would bind a null pipeline state.
    private static bool Ensure(object sceneDrawSystem)
    {
        if (_state == -1) return false;
        try
        {
            if (_state == 0)
            {
                _state = -1;
                _field = sceneDrawSystem.GetType().GetFields(Any)
                    .FirstOrDefault(f => f.Name == "_eyeAdaptationJob");
                if (_field == null)
                {
                    RttLog.Line("Own exposure: SceneDrawSystem._eyeAdaptationJob not found — " +
                                "the player's exposure stays shared with our render.");
                    return false;
                }

                var jobType = _field.FieldType;
                var ctor = jobType.GetConstructors(Any).FirstOrDefault(c => c.GetParameters().Length == 1);
                if (ctor == null)
                {
                    RttLog.Line($"Own exposure: no single-argument constructor on {jobType.Name}.");
                    return false;
                }

                // List<Keen.VRage.Library.Threading.Task>, NOT System.Threading.Tasks.Task.
                // Assuming the BCL type here is what made the custom culling job throw an
                // ArgumentException from inside Invoke, so read it off the signature.
                var listType = ctor.GetParameters()[0].ParameterType;
                _initTasks = Activator.CreateInstance(listType) as IList;
                if (_initTasks == null)
                {
                    RttLog.Line($"Own exposure: could not construct {listType.Name} for the init tasks.");
                    return false;
                }

                _ourJob = ctor.Invoke(new object[] { _initTasks });
                _state = 1;
                RttLog.Line($"Own exposure: SECOND {jobType.Name} built — its constructor creates its own " +
                            "auto-exposure ping-pong targets, histogram buffer and readback buffers, so our " +
                            "render stops overwriting the player's. Waiting on " +
                            $"{_initTasks.Count} initialisation task(s).");
            }

            if (_ourJob == null) return false;

            // Poll the init tasks reflectively; the element type is the engine's own Task.
            for (int i = 0; i < _initTasks.Count; i++)
            {
                var t = _initTasks[i];
                if (t == null) continue;
                var done = t.GetType().GetProperty("IsCompleted", Any)?.GetValue(t);
                if (done is bool b && !b) return false;
            }

            if (!_readyLogged)
            {
                _readyLogged = true;
                RttLog.Line("Own exposure: initialisation complete — our exposure job is live.");
            }
            return true;
        }
        catch (Exception e)
        {
            _state = -1;
            RttLog.Error("own exposure: build", e);
            return false;
        }
    }

    // Swap for the duration of our render. Returns the engine's job so the caller can
    // put it back, or null if we are not taking over this pass.
    public static object Install(object sceneDrawSystem)
    {
        if (!FeedConfig.WholeSceneOwnExposure || sceneDrawSystem == null) return null;
        if (!Ensure(sceneDrawSystem)) return null;
        try
        {
            var saved = _field.GetValue(sceneDrawSystem);
            if (ReferenceEquals(saved, _ourJob)) return null;   // already ours: do not stack
            _field.SetValue(sceneDrawSystem, _ourJob);

            if (!_installLogged)
            {
                _installLogged = true;
                RttLog.Line("=== OWN EXPOSURE ACTIVE: ComputeExposure now writes OUR auto-exposure targets " +
                            "and OUR readback buffers. The player's adaptation history is no longer " +
                            "overwritten ten times a second by a 512x512 view of somewhere else. ===");
            }
            return saved;
        }
        catch (Exception e) { RttLog.Error("own exposure: install", e); return null; }
    }

    public static void Restore(object sceneDrawSystem, object saved)
    {
        if (saved == null || _field == null || sceneDrawSystem == null) return;
        try { _field.SetValue(sceneDrawSystem, saved); }
        catch (Exception e) { RttLog.Error("own exposure: restore", e); }
    }
}
