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

    // PER-FEED (phase C1a). Liveness is the definition of a feed: "panel A was ground
    // down, feed B is untouched" is this gate and nothing else. See FeedInstance.cs for
    // why these are properties rather than instance fields reached through a parameter.
    private static long _lastPanelMs
    { get => Feeds.Cur.LastPanelMs; set => Feeds.Cur.LastPanelMs = value; }
    private static bool _active
    { get => Feeds.Cur.GateActive; set => Feeds.Cur.GateActive = value; }
    private static bool _everActive
    { get => Feeds.Cur.GateEverActive; set => Feeds.Cur.GateEverActive = value; }
    private static int _cycles
    { get => Feeds.Cur.GateCycles; set => Feeds.Cur.GateCycles = value; }

    // PROCESS-GLOBAL. The pause marker is a file on disk that stops the WHOLE mod — it
    // is the safety protocol's lever, not a per-feed property — and the poll cadence is
    // one filesystem stat shared by everyone.
    private static bool _paused;
    private static long _lastPollMs;

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

        // THE THROTTLE COVERS THE FILE STAT ONLY — not the per-feed decision below.
        //
        // This used to be `if (now - _lastPollMs < 250) return;` at the top, throttling the
        // whole method. That was correct with one feed and BROKE THE SECOND ONE the instant
        // feedCount went to 2: _lastPollMs is process-global, so whichever feed polled first
        // consumed the window and every other feed returned here without ever computing its
        // own `alive`. Feed 1's gate therefore never went ACTIVE, the whole-scene hook's
        // `if (!FeedGate.Active) return` fired on its every slot, and it never built or
        // rendered anything — while feed 0 carried on looking perfectly healthy.
        //
        // Observed 2026-07-30 on the first two-feed run. The split is the fix: the expensive
        // part is the filesystem stat, which genuinely is shared and stays throttled; the
        // per-feed part is two timestamp comparisons and must run on every call, for every
        // feed. Cost is nil and it removes a whole class of "feed N is silently dormant".
        if (now - _lastPollMs >= 250)
        {
            _lastPollMs = now;
            PollPauseMarker();
        }

        PollFeed(now);
    }

    // Process-global: the pause marker is one file that stops the WHOLE mod.
    private static void PollPauseMarker()
    {
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

    }

    // PER-FEED, every call. Two timestamp comparisons — cheap enough that throttling it was
    // never buying anything, and expensive in a way nothing measured: a feed whose gate is
    // never evaluated is a feed that never runs.
    private static void PollFeed(long now)
    {
        bool alive = !_paused && _lastPanelMs != 0 && (now - _lastPanelMs) < FeedConfig.PanelIdleMs;
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

    // PER-FEED: each feed counts down to its own teardown. -1 = not armed. The default
    // lives on FeedInstance so a newly created feed starts disarmed like this one did.
    private static int _teardownIn
    { get => Feeds.Cur.TeardownIn; set => Feeds.Cur.TeardownIn = value; }

    private static bool _pendingStartupLog
    { get => Feeds.Cur.PendingStartupLog; set => Feeds.Cur.PendingStartupLog = value; }

    // EVERY feed's countdown, every engine frame. Call from OUTSIDE the render-slot scope.
    //
    // THE BUG THIS FIXES, and it is the most expensive one the two-feed work has produced.
    // This used to be called from inside `using (Feeds.Enter(Feeds.NextForRender()))`, so
    // only the feed holding the render slot counted down. AdvanceSlot() runs only after a
    // render COMPLETES — and a dormant feed never completes one. So the moment both feeds
    // went dormant the slot froze, whichever feed it happened to be pointing at reached zero
    // and released, and every other feed's countdown stayed pinned at 30 forever.
    //
    // Observed 2026-07-30 19:20:51, two feeds dormant together:
    //
    //     [feed 1] Feed gate: releasing resources now.
    //     [feed 1] Whole-scene Reset: VRAM 12847 MB -> 12720 MB (-127 MB)
    //     ...and nothing at all from feed 0.
    //
    // The next gate cycle then rebuilt BOTH feeds — allocating a fresh ScreenBuffers and
    // DrawContextManager for feed 0 on top of the set that was never freed. VRAM went
    // 12.78 -> 13.70 GB across that one cycle and stayed flat there, which is retention and
    // not churn. AvailableVRAM was 13.61 GB. The device was removed 40 seconds later.
    //
    // It reads as a VRAM-ceiling problem and it is not: two feeds cost +580 MB and fit with
    // ~850 MB to spare. What did not fit was two feeds plus an orphaned copy of one of them.
    //
    // The principle, which is the part worth keeping: a teardown countdown is per-feed
    // BOOKKEEPING, not per-feed RENDERING. Scheduling it on the render slot tied the freeing
    // of resources to the very activity that had just stopped.
    //
    // ForEachSlot, not ForEach: a slot dropped out of Count by a feedCount change or the
    // VRAM cap is the one most in need of releasing, and it is invisible to every
    // Count-bounded sweep from the instant it is retired.
    public static void PumpAll() => Feeds.ForEachSlot(PumpOne);

    // INSIDE the per-feed scope, both halves of it. Startup() writes GateCycles and
    // GateEverActive — per-feed state — so draining a global flag here would have written
    // feed 0's counters on every feed's behalf.
    private static void PumpOne()
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

        // 3a. Release our runtime screen material through the engine's own path BEFORE
        //     forgetting the binding — this is what stops the "Can't remove material"
        //     deferred assert at every gate cycle. Must precede PanelBinding.Reset, which
        //     drops the weak references Unbind needs.
        Try("panel material unbind", PanelBinding.Unbind);

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
