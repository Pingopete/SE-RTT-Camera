using System;
using System.Linq;
using System.Reflection;

namespace RttProbe;

// Give our render its own auto-exposure targets, so it stops overwriting the player's.
//
// THE MECHANISM, and it was reported before it was understood. At a 2 s feed interval the
// symptom is unmistakable: "every time the frame updates, the whole main world darkens
// for a moment, then slowly gets lighter, like it's readjusting" — a step change in
// exposure followed by gradual adaptation, once per second render.
//
// ComputeExposure is stage 4 and is NOT skippable (its out-params feed bloom and
// tonemap). We scope PostProcessSettings.EyeAdaptation off for our render, which only
// chooses the branch — and the constant branch is the damaging one:
//
//     ConstantExposure.hlsl:  return float2(Post_.ConstantLuminance, exposure)
//
// ConstantLuminance is a FIXED 1.0. So our render does not merely write a different
// scene's luminance into the shared history, it writes a CONSTANT. The player's next
// frame's DynamicExposure adapts toward the exposure for luminance 1.0, then crawls back
// over the following frames. Exactly the reported step-then-recover.
//
// SceneDrawSystem holds one _eyeAdaptationJob, and its _autoExposures is the ping-pong
// pair both branches write and the tonemapper reads.
//
// WHY NOT A SECOND JOB. That was the first attempt and it CTD'd on construction:
// EyeAdaptationJob's constructor calls InitializeAsync() and hands the task back, so
// pipeline states compile on another thread while the render thread is recording. The
// engine builds it at startup and waits on the task list before drawing anything; we
// built it inside the Draw bracket. Device removed, PageFaultVA 0x0, inside the PLAYER's
// frame.
//
// So this creates NO job and compiles NOTHING. It makes two 1x1 render targets — the
// same call the engine's own constructor makes, synchronous, no PSOs, no async half —
// and swaps only the array field for the duration of our render. There is nothing here
// that can race a recorder.
//
// The targets are created OUTSIDE our nested Draw (Prime is called from the hook before
// RunSecondRender), which keeps even the cheap allocation off the nested path.
//
// AND IT STILL CTD'D. PageFaultVA 0x0, "ScenePreparation + Render" 371/831, last ops
// EnvProbe_Blending -> ClearRenderTargetView -> Resourcebarrier — inside the PLAYER's
// frame, at a resource barrier. The targets were created fine (the log confirms two 1x1
// R32G32_Float ones) and the swap installed fine; binding them is what fell over.
//
// The reading that fits: a texture from CreateRenderTarget is not yet part of the
// engine's per-frame resource-state machinery. RenderTargetTexture carries an
// AutoResourceState and the engine's barriers are emitted from tracked state; a target
// created outside that lifecycle and then bound as an RTV produces a transition from a
// state nothing tracks.
//
// SO: STOP CREATING GPU RESOURCES. That is now twice — once constructing a job whose
// async init raced the recorder, once creating render targets outside the resource
// lifecycle. Both looked cheap and both removed the device. This whole file is the wrong
// shape for the problem.
//
// THE RIGHT SHAPE, not yet built: a Harmony prefix on EyeAdaptationJob.ConstantExposure
// that sets __result to the job's existing Exposure view and returns false. Our render
// then reads the player's current exposure and writes nothing at all — no new textures,
// no new job, no lifecycle to get wrong, and nothing to race. It needs a bootstrap
// change, which is the only reason it is not already here.
//
// LEFT DISABLED. The diagnosis is confirmed and valuable; this implementation of it is
// not safe.
internal static class OwnExposure
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static FieldInfo _jobField;          // SceneDrawSystem._eyeAdaptationJob
    private static FieldInfo _autoField;         // EyeAdaptationJob._autoExposures
    private static object _job;
    private static Array _ourTargets;
    private static int _state;                   // 0 untried, 1 ready, -1 unavailable
    private static bool _installLogged;

    public static void Reset()
    {
        // The targets come from BindableTextureManager.CreateRenderTarget, which is a
        // plain creation rather than a pool borrow, so there is no Return to make. They
        // are 1x1; letting them go on a reload costs two pixels, and calling a Dispose we
        // have not verified would be the riskier choice.
        _jobField = _autoField = null;
        _job = null;
        _ourTargets = null;
        _state = 0;
        _installLogged = false;
    }

    // Build our exposure targets. MUST be called outside the nested Draw.
    public static void Prime(object sceneDrawSystem)
    {
        if (_state != 0 || sceneDrawSystem == null) return;
        if (!FeedConfig.WholeSceneOwnExposure) return;
        _state = -1;
        try
        {
            _jobField = sceneDrawSystem.GetType().GetFields(Any)
                .FirstOrDefault(f => f.Name == "_eyeAdaptationJob");
            _job = _jobField?.GetValue(sceneDrawSystem);
            if (_job == null) { RttLog.Line("Own exposure: _eyeAdaptationJob not reachable."); return; }

            _autoField = _job.GetType().GetFields(Any).FirstOrDefault(f => f.Name == "_autoExposures");
            if (_autoField?.GetValue(_job) is not Array theirs || theirs.Length == 0)
            {
                RttLog.Line("Own exposure: _autoExposures not reachable or empty.");
                return;
            }

            // Mirror the engine's own targets rather than assuming their shape: read the
            // format and resolution off the live ones.
            var sample = theirs.GetValue(0);
            if (sample == null) { RttLog.Line("Own exposure: engine's exposure targets are null."); return; }
            var st = sample.GetType();
            object fmt = st.GetProperty("Format", Any)?.GetValue(sample);
            object res = st.GetProperty("Resolution", Any)?.GetValue(sample);
            object clear = st.GetProperty("D3DClearColor", Any)?.GetValue(sample);
            if (fmt == null || res == null) { RttLog.Line("Own exposure: could not read target format/resolution."); return; }

            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var texMgr = core?.GetFields(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(f => f.FieldType.Name == "BindableTextureManager")?.GetValue(null);
            var create = texMgr?.GetType().GetMethod("CreateRenderTarget", Any);
            if (create == null) { RttLog.Line("Own exposure: BindableTextureManager.CreateRenderTarget not found."); return; }

            // (debugName, format, resolution, Nullable<Color> clearColor, AllocationGroup)
            var ps = create.GetParameters();
            var clearArg = clear == null ? null : Activator.CreateInstance(ps[3].ParameterType, clear);

            var ours = Array.CreateInstance(st, theirs.Length);
            for (int i = 0; i < theirs.Length; i++)
                ours.SetValue(create.Invoke(texMgr,
                    new[] { "RttFeedAutoExposure" + i, fmt, res, clearArg, null }), i);

            _ourTargets = ours;
            _state = 1;
            RttLog.Line($"Own exposure: created {theirs.Length} private auto-exposure target(s) " +
                        $"({res}, format {fmt}). Our render will no longer stamp its constant " +
                        "luminance into the player's adaptation history.");
        }
        catch (Exception e) { RttLog.Error("own exposure: prime", e); }
    }

    // Swap for the duration of our render. Returns the engine's array to restore, or null.
    public static object Install()
    {
        if (_state != 1 || !FeedConfig.WholeSceneOwnExposure) return null;
        try
        {
            var saved = _autoField.GetValue(_job);
            if (ReferenceEquals(saved, _ourTargets)) return null;   // already ours
            _autoField.SetValue(_job, _ourTargets);

            if (!_installLogged)
            {
                _installLogged = true;
                RttLog.Line("=== OWN EXPOSURE ACTIVE: ComputeExposure now writes OUR auto-exposure " +
                            "targets. The player's adaptation history is no longer stamped with a " +
                            "constant luminance ten times a second. ===");
            }
            return saved;
        }
        catch (Exception e) { RttLog.Error("own exposure: install", e); return null; }
    }

    public static void Restore(object saved)
    {
        if (saved == null || _autoField == null || _job == null) return;
        try { _autoField.SetValue(_job, saved); }
        catch (Exception e) { RttLog.Error("own exposure: restore", e); }
    }
}
