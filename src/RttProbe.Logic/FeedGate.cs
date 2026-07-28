using System;
using System.IO;

namespace RttProbe;

// One authority for "is this mod doing anything at all right now".
//
// The point is a clean A/B against vanilla WITHOUT restarting the game. Turn the tagged
// panel off and everything this mod does stops: no second Draw, no probe pass, no
// parking, no panel material changes, and every GPU resource we own is released. Turn it
// back on and the whole pipeline rebuilds from scratch.
//
// That matters more than convenience. Every conclusion in this project rests on
// "compared to what?", and until now the only way to get a mod-free frame was to quit,
// edit, and reload — which changes the world state, the VRAM residency and the streaming
// set at the same time. Those are exactly the variables that produced two of today's
// false diagnoses.
//
// THE SIGNAL is the tagged panel's own LCD tick. CameraFeed.OnLcdTick fires from the
// engine's LCD render component for every panel it draws, so a panel that is switched
// off, unpowered, destroyed or out of the render set simply stops ticking. No block-state
// reflection, no power API, no polling of the sim from the render thread — the absence of
// a signal IS the signal. A staleness window covers the gap between ticks.
//
// The Harmony patches stay installed while dormant. They cannot be removed safely
// mid-session, and they do not need to be: every hook consults this gate and returns
// immediately, and the stage-skip hooks already no-op outside our render. Dormant means
// the engine runs its own code with our patches present but inert.
internal static class FeedGate
{
    private static readonly string PausePath = Path.Combine(RttLog.OutDir, "feed-paused.marker");

    private static long _lastPanelMs;
    private static bool _paused;
    private static bool _active;
    private static bool _everActive;
    private static long _lastPollMs;
    private static int _cycles;

    // True while a tagged panel is alive and the mod should be doing its work.
    // Starts FALSE: until a tagged panel has ticked at least once, there is nothing to
    // draw to and no reason to build anything.
    public static bool Active => _active;

    public static void Reset()
    {
        _lastPanelMs = 0;
        _active = false;
        _everActive = false;
        _lastPollMs = 0;
        _cycles = 0;
        _teardownIn = -1;
        _pendingStartupLog = false;
        _paused = false;
    }

    // For the health watcher: a one-line machine-readable state dump.
    public static string StatusLine =>
        $"gate={(_paused ? "PAUSED" : _active ? "ACTIVE" : "dormant")} cycles={_cycles} " +
        $"teardownIn={_teardownIn}";

    // Called from CameraFeed whenever a panel carrying the tag is seen ticking.
    public static void NotePanelAlive() => _lastPanelMs = Clock.Ms;

    // Safe from ANY hook, on any thread. It only flips a bool and arms a countdown; it
    // never touches a GPU resource.
    //
    // The first version called Shutdown() straight from here, and here is reached from
    // BlitProbe.OnTick — the LCD tick. That disposed our ScreenBuffers and
    // DrawContextManager while the frame referencing them was still being recorded, and
    // the game died with PageFaultVA 0x0 partway through ScenePreparation + Render. A
    // dropped tick during any hitch was enough to trigger it, which makes it a bug that
    // fires exactly when the game is already struggling.
    //
    // Flipping Active is enough to STOP all work immediately, everywhere, because every
    // hook gates on it. Releasing what that work owned is a separate question and has to
    // happen somewhere it cannot race the recorder.
    public static void Poll()
    {
        long now = Clock.Ms;
        if (now - _lastPollMs < 250) return;
        _lastPollMs = now;

        // THE PAUSE MARKER. A file is the right mechanism here precisely because it is
        // outside the game: it can be created before a rebuild, by a script, or by a
        // human, and it takes effect without the config parser, the panel, or anything
        // else in the mod having to be healthy.
        //
        // The workflow it exists for: pause -> wait for DORMANT in the log -> swap the
        // DLL or edit anything risky -> unpause. A hot reload that lands while our nested
        // Draw is recording has caused several of tonight's crashes, and a dormant mod
        // has nothing in flight to land on.
        bool paused = false;
        try { paused = File.Exists(PausePath); } catch { }
        if (paused != _paused)
        {
            _paused = paused;
            RttLog.Line(paused
                ? "=== FEED PAUSED by marker (output/feed-paused.marker). Going dormant; safe to " +
                  "rebuild or edit anything. Delete the marker to resume. ==="
                : "=== FEED UNPAUSED (marker removed). Resuming. ===");
        }

        bool alive = !paused && _lastPanelMs != 0 && (now - _lastPanelMs) < FeedConfig.PanelIdleMs;
        if (alive == _active) return;

        _active = alive;
        if (_active)
        {
            _teardownIn = -1;              // cancel a pending teardown: we are back
            _pendingStartupLog = true;
        }
        else
        {
            // Work has already stopped, this frame. Give the frames that were mid-flight
            // when it stopped time to be submitted and retired before anything is freed.
            _teardownIn = TeardownDelayFrames;
            RttLog.Line("=== FEED GATE: DORMANT. No tagged panel has ticked for " +
                        $"{FeedConfig.PanelIdleMs} ms. All work has stopped; releasing resources in " +
                        $"{TeardownDelayFrames} frames. ===");
        }
    }

    // Called ONLY from the whole-scene hook, which is the SceneDrawSystem.Draw postfix on
    // the render thread — the same place a config change has been safely disposing and
    // rebuilding these objects all session. This is where anything owning GPU memory is
    // allowed to be released.
    private const int TeardownDelayFrames = 30;
    private static int _teardownIn = -1;
    private static bool _pendingStartupLog;

    public static void PumpRenderThread()
    {
        if (_pendingStartupLog) { _pendingStartupLog = false; Startup(); }

        if (_teardownIn < 0) return;
        if (--_teardownIn > 0) return;
        _teardownIn = -1;
        Shutdown();
    }

    private static void Startup()
    {
        _cycles++;
        RttLog.Line($"=== FEED GATE: ACTIVE (cycle {_cycles}). A tagged panel is ticking again. " +
                    "Everything rebuilds from scratch: second ScreenBuffers, second " +
                    "DrawContextManager, cascade set, LDR ring, panel binding. ===");
        _everActive = true;
    }

    // Put the game back exactly as it would be without this mod loaded.
    //
    // Order matters: stop the things that ISSUE GPU work before releasing what that work
    // reads, or a pass already in flight can be handed a disposed resource. Every step is
    // independently guarded, because a shutdown that throws half way through is worse
    // than either state.
    private static void Shutdown()
    {
        RttLog.Line("Feed gate: releasing resources now. The game should render exactly as it does " +
                    "without this mod. Turn the panel back on to restart.");

        // 1. Stop issuing work. The whole-scene route and the probe pass both gate on
        //    Active, so they are already inert by the time this runs; these calls release
        //    what they own.
        Try("whole-scene teardown", WholeSceneRender.Reset);
        Try("camera pass teardown", CameraRender.Reset);

        // 2. Stop delivering frames to the panel.
        Try("handover teardown", FeedHandover.Reset);
        Try("blit teardown", BlitProbe.Reset);

        // 3. Release the camera constant-buffer swap.
        Try("camera CB swap", CameraCbSwap.Reset);

        // 3b. Reset the panel BINDING, not just the material. Found the hard way: without
        //     this, _bound stays true across a gate cycle, so on restart BlitProbe builds
        //     a fresh offscreen target, the handover copies frames into it — every
        //     counter healthy — and the panel's screen material still points at the OLD
        //     target from the previous cycle. A black screen with park/copy rates
        //     climbing is exactly this signature.
        Try("panel binding teardown", PanelBinding.Reset);

        // 4. Undo the PERSISTENT engine mutations. These are the ones that would
        //    otherwise survive a dormant gate and quietly invalidate the comparison:
        //    the LCD material's emissive multiplier is a shared definition affecting
        //    every panel in the world, and DimDistance reaches the engine's own probes.
        Try("restore panel material", PanelBinding.RestoreEngineState);
        Try("restore probe settings", CameraRender.RestoreEngineState);

        // 5. Forget the panel, so coming back is a genuinely fresh discovery rather than
        //    a resumption with stale state.
        Try("forget panel", CameraFeed.Reset);

        RttLog.Line("Feed gate: shutdown complete." +
                    (_everActive ? "" : " (Nothing had been started yet.)"));
    }

    private static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { RttLog.Error("feed gate shutdown: " + what, e); }
    }
}
