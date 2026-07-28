namespace RttProbe;

// Dispatches the two SceneDrawSystem pass hooks to the camera pass.
//
// This was SceneDrawRecon: a one-shot reflection dump that proved the probe pass was
// reachable from a plugin, with the dispatch bolted on beside it. The dump answered its
// question a long time ago (its output is in output/scene-draw-recon.txt) and the
// EngineQuery tool answers the same questions offline now, without a running game. Only
// the dispatch was still load-bearing, so only the dispatch is left.
internal static class ScenePassHook
{
    private static long _probeCalls, _framePasses, _lastRateLog;

    public static void Reset() => _probeCalls = _framePasses = _lastRateLog = 0;

    public static void OnSceneDraw(object sceneDrawSystem, object commandList, int which)
    {
        if (which == 0) System.Threading.Interlocked.Increment(ref _probeCalls);
        else System.Threading.Interlocked.Increment(ref _framePasses);

        // The BASE VIEW must not be read from the probe hook.
        // ExecuteEnvironmentProbeUpdate renders the engine's own probe cube faces from the
        // probe's location, and SettingsManager.RenderView reflects that while it does.
        // Sampling it there hands us a 90-degree cube-face projection from inside the ship
        // every time our frame gate lands on a probe refresh — one incoherent frame, a
        // couple of times a second. Snapshot it from the per-frame pass instead, where it
        // is provably the main view.
        if (which == 1) CameraRender.CaptureBaseView();

        // WHICH hook the camera pass rides:
        //
        //   probe hook (0)  ExecuteEnvironmentProbeUpdate, ~7x/frame. Shadows are resolved
        //                   and a command list is in hand, but it is INSIDE the engine's
        //                   own probe work.
        //   frame hook (1)  DrawUnlit, once per frame, after the main pass.
        //
        // The camera pass is self-contained — it borrows every resource it uses — so this
        // is a dispatch change rather than a restructure.
        int want = FeedConfig.PassOnFrameHook ? 1 : 0;
        if (which == want) CameraRender.OnProbePass(sceneDrawSystem, commandList);

        var now = Environment.TickCount64;
        if (now - _lastRateLog >= 10000)
        {
            _lastRateLog = now;
            RttLog.Line($"Cadence: probe-pass {_probeCalls} calls, per-frame pass {_framePasses} calls (cumulative).");
        }
    }
}
