using Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.Contracts;

namespace RttProbe;

// PHASE C1a — THE INSTANCING SEAM.
//
// Everything the feed owns used to be a private static. That was correct while there
// was exactly one feed and it made the ownership rules easy to see, but it is the one
// thing standing between here and goals 3, 5 and 6: a second camera needs a second
// ScreenBuffers, a second DrawContextManager, a second LDR ring and a second panel
// binding, and none of that is expressible in a static field.
//
// WHY THIS SHAPE, and not a conventional "make the classes instance classes" refactor.
// The per-feed state is ~55 fields spread across seven files and roughly 7,800 lines,
// and it is read from several hundred call sites. Threading an instance parameter
// through all of them is a diff nobody can review and nothing can bisect: every one of
// those call sites is a chance to touch the wrong feed's state silently. So instead the
// FIELD becomes a same-named static PROPERTY over an instance field:
//
//     private static object _ourScreenBuffers;                              // before
//     private static object _ourScreenBuffers                               // after
//     { get => Feeds.Cur.OurScreenBuffers; set => Feeds.Cur.OurScreenBuffers = value; }
//
// Every existing read and write compiles and behaves identically, untouched. The whole
// refactor is then "delete a field, add a property" per item — mechanical, greppable,
// and reviewable field by field rather than site by site.
//
// WHAT IS NOT HERE, deliberately:
//
//   - Reflection caches (MethodInfo, FieldInfo, Type, PropertyInfo). These describe the
//     ENGINE'S types, not our feed. They are correctly process-global and resolving them
//     once per feed would be pure waste.
//   - Log latches and diagnostic counters (_xxxLogged, _errLogs, the diag HashSets).
//     These mean "we have already said this about the engine", which is a statement
//     about the process, not about a feed. Per-feed latches would multiply the log
//     volume by N for no information.
//   - Engine handles shared by every feed: the UISystem, the RenderContracts, the mip
//     job, the LCD material definitions that emissivity and the FSR mask are scoped
//     onto (those are SHARED DEFINITIONS — see FeedConfig.Emissivity; per-feed values
//     there are not possible without a per-feed material, which is not on the roadmap).
//   - Perf's buckets. The budget is global BY DESIGN (see docs/phase2-design.md): the
//     fixed total is the invariant, so the instrument that measures it must aggregate
//     across feeds, not per feed. Per-feed attribution is a phase E concern.
//
// C1a KEEPS EXACTLY ONE INSTANCE. That is the point: the seam lands and gets proven at
// parity (C2) before selection exists, so if the numbers move it is this transform and
// nothing else. The pump that chooses an instance per render is C1b.
internal sealed class FeedInstance
{
    // Stable identity for logs and, later, the scheduler's rotation order.
    public readonly int Id;
    public FeedInstance(int id) { Id = id; }

    // ---- WholeSceneRender: the second renderer's own globals ---------------------
    //
    // These are the objects that make the nested Draw a SECOND view rather than a
    // corruption of the player's. Rule 25 applies to every one of them: our teardown
    // may dispose only what this instance allocated.
    public object OurScreenBuffers;
    public bool SbBuilt;
    public object OurDrawContexts;
    public bool DcBuilt;

    // Consecutive failed attempts to build DcBuilt's manager. Per-feed so one feed's broken
    // build cannot latch another out of ever trying. See WholeSceneRender.NoteDcFailure.
    public int DcFailures;
    public object OurFreshShadowResources;
    public object OurFreshFlares;
    public object PanelSourceTex;
    public bool LdrResized;

    // 0 untried, 1 observed, -1 unavailable. Per-feed: one feed faulting must not take
    // the others down with it, which is precisely the graceful-cut contract in goal 7.
    public int RouteState;

    // Cadence. LastRenderMs and SettleFrames are what the phase E slot scheduler will
    // drive; RenderCount is the "has this feed ever produced an image" test PanelSource
    // depends on.
    public long LastRenderMs;
    public int SettleFrames;
    public int RenderCount;

    // Our own environment probe manager (goal 4.4). NOT disposed on a config change —
    // three device removals established that, see WholeSceneRender.Reset.
    public object OurProbes;
    public int ProbeState;
    public bool ProbeLogged;

    // The flare mirror (goal 4.3). EngineFlares is a BORROWED reference — the engine's
    // context, re-read before every render. FlareOriginals holds our context's ctor
    // values so ScrubMirroredFlareRefs can put them back before we dispose: sharing a
    // reference INTO an object you later dispose makes you the owner of something you
    // did not allocate, which is Rule 25 and cost two crashes to learn.
    public object EngineFlares;
    public bool FlaresReady;
    public object[] FlareOriginals;

    // ---- CameraRender: the view, the ring and the panel target -------------------
    public object WsRenderView;
    public object WsResolution;

    // Previous-frame camera, for motion vectors. Unambiguously per-feed: feed B's
    // previous frame is not feed A's, and mixing them is a smear artefact.
    public object WsPrevCamPos;
    public object WsPrevCameraSettings;
    public object CbRenderView;

    // The LDR ring. Session-owned, three deep so the UI stage is never handed the slot
    // we are writing. RingIndex starts at -1 = nothing handed over yet; LdrMips defaults
    // to 1 and is raised to the panel's real mip count once known (the mip-chain fix —
    // mips 1..n used to hold recycled pool content).
    public readonly object[] LdrRing = new object[3];
    public object LdrReady;
    public int RingIndex = -1;
    public int LdrMips = 1;

    // The panel we deliver to, and its shape.
    public object FeedTexture;
    public object FeedRes;
    public object FeedFormat;
    public object FeedComponent;
    public int FeedState;
    public string ResolvedPanelId;

    // Camera continuity.
    public object LastCamWorld;
    public object LastViewD;
    public Vector3D LastEye;
    public bool HaveLastEye;
    public long LastRender;
    public long FeedStartTicks;

    // ---- CameraFeed: what this feed is pointed at --------------------------------
    //
    // Volatile because the tick side publishes it and the render side reads it. A
    // reference assignment is atomic, which is why Target is a class — as a struct it
    // was torn mid-write and produced a one-frame fallback to the player's view.
    public volatile CameraFeed.Target Target;
    public object PanelRt;

    // Memoised grid bounds, keyed on BoundsGrid + BoundsAt. Two feeds on two grids
    // must not share one slot: the orbit radius is derived from Extent.
    public object BoundsGrid;
    public (Vector3D Centre, double Extent) BoundsCache;
    public long BoundsAt;

    // ---- FeedHandover: the parked frame ------------------------------------------
    public volatile object PendingFrame;      // Borrowed<T>
    public volatile object PendingResource;   // the texture itself
    public int ParkGeneration;
    public string PanelHandleText;

    // DELIVERY PROOF, per feed (phase C3). These were process-global "log once" latches,
    // classified during the C1a inventory as statements about the ENGINE. They are not —
    // "this feed's frames reached its panel" is the most feed-specific fact in the mod, and
    // as a global latch feed 0 fires it first and feed 1's copy is swallowed forever.
    //
    // That is not merely untidy: it is what made the first black-panel diagnosis a guessing
    // game. A silent feed and a working feed produced identical logs.
    public bool HandoverSurvivedLogged;
    public int Handovers;
    public bool HandoverArgsLogged;
    public bool PanelRtDiag;
    public int CopyLogs;

    // CONSECUTIVE view-lookup failures in CopyToFeed, and its own log budget. Per-feed for
    // the same reason CopyLogs is: feed 1 starts its render AFTER feed 0 is already warm, so
    // its "source not ready yet" window is a normal part of its startup and must not be
    // graded against feed 0's. A streak, not a count — one good pass zeroes it.
    public int ViewLookupFails;
    public int ViewLookupLogs;

    // ---- BlitProbe: the target and its batch -------------------------------------
    public OffscreenRenderTarget? Rt;
    public bool RtTried;
    public volatile bool FeedOwnsTarget;
    public PersistentDrawBatch PersistentBatch;
    public bool BatchRetired;

    // ---- FeedGate: this feed's liveness ------------------------------------------
    //
    // Per-feed from the start, because "panel A was ground down, feed B is untouched"
    // (goal 7 / phase F1) is exactly a per-feed gate and nothing else.
    public long LastPanelMs;
    public bool GateActive;
    public bool GateEverActive;
    public int GateCycles;
    public int TeardownIn = -1;

    // "This feed went active; log its startup from the render thread." Per-feed because
    // Startup() writes GateCycles and GateEverActive, which are per-feed — as one global
    // flag the first feed to drain it consumed every other feed's startup, so feed 1's
    // shutdown then reported "(Nothing had been started yet.)" while it was demonstrably
    // rendering. Caught by the unscoped-access detector the moment PumpAll moved outside
    // the render scope, which is precisely the job that detector exists to do.
    public bool PendingStartupLog;

    // ---- PanelBinding: the material binding --------------------------------------
    //
    // PHASE E2 FAN-OUT: a feed can display on SEVERAL panels, so the binding is a LIST
    // of weak (renderer, ctx) pairs — one entry per claiming panel — instead of the
    // single latch + pair it used to be. Weak for the same reason the pair was: a
    // destroyed panel must not keep its LCD material alive through us.
    //
    // A pair is appended at bind ATTEMPT (not success), preserving the old "one attempt
    // per panel per activation" semantics: the old code set _bound=true and stored the
    // pair before invoking the engine, so a failed bind was never retried and Unbind
    // still swept it. Same here, per panel.
    public readonly List<(WeakReference Renderer, WeakReference Ctx)> BoundPanels = new();

    // WHICH PANEL THIS FEED'S CAMERA FOLLOWS. First claimant wins: with two panels on
    // one feed, letting every tick publish the orbit target made the camera thrash
    // between the two panels' grids (last-claimant-wins, twice per frame). The feed's
    // identity — orbit target, captured panel RT, LastRenderComponent — follows the
    // panel that claimed it FIRST; later claimants are display-only mirrors. Cleared by
    // CameraFeed.Reset so a gate cycle re-elects from whatever is actually ticking.
    public string PrimaryPanelName;

    // Distinct tagged panel names currently claiming this feed. Drives WantsRepaint:
    // binding runs inside the content-render hook, which an idle panel never enters, so
    // repaints are forced while any claimant is unbound. Cleared with PrimaryPanelName —
    // a destroyed panel's stale claim heals at the next gate cycle, when only live
    // panels re-claim.
    public readonly HashSet<string> ClaimedPanelNames = new();
}

// The registry, and the ambient that decides WHOSE state `Feeds.Cur` means.
//
// PHASE C1b. C1a moved the state onto instances; this decides which instance a given hook
// call refers to. That mapping is NOT uniform across our ten entry points, and assuming it
// was is the mistake this design exists to avoid — see docs/phase2-design.md:
//
//   - PANEL-driven (BlitProbe.OnTick, CameraFeed/StatsPanel.OnLcdTick, the two
//     OnPanelRender hooks): the ENGINE hands us a specific LCD component, renderer or
//     surface context, on its own schedule. The feed is whoever OWNS that panel — a
//     LOOKUP. A rotation here would hand panel A's tick to feed B.
//   - TARGET-driven (FeedHandover.OnOffscreenUiDraw): the engine hands us the offscreen
//     target being drawn. Also a lookup.
//   - SCHEDULER-driven (the whole-scene hooks, and the probe pass nested inside them):
//     nothing external names a feed, so WE choose. Phase E1's render slot, in embryo.
//
// STILL EXACTLY ONE INSTANCE. Every lookup and every pick resolves to it, so this stage
// builds the mechanism and leaves the answer alone — it cannot change behaviour. C3 adds
// the second instance, and the mechanism starts mattering.
internal static class Feeds
{
    // THE REGISTRY (phase C3). Slots are ALLOCATED eagerly and ACTIVATED by config.
    //
    // Allocating is free — a FeedInstance is plain fields, it touches no engine type, and
    // its ctor cannot run engine code. That matters more than it looks: reading a
    // CoreSystems static forces that type's cctor, and doing so during plugin load once
    // threw ConfigurationNotFoundException and permanently poisoned the type (see the
    // comment in WholeSceneRender.Reset). So the array is built from nothing at load, and
    // FeedConfig is never consulted at static-init time.
    //
    // Count is therefore a CONFIG READ, not an array length: feedCount clamps into the
    // slots that exist. That keeps N=1 the shipped default and makes the second feed a
    // live knob to switch off if it misbehaves, rather than a rebuild to revert.
    private const int MaxFeeds = 4;

    private static readonly FeedInstance[] All = CreateAll();

    private static FeedInstance[] CreateAll()
    {
        var a = new FeedInstance[MaxFeeds];
        for (int i = 0; i < MaxFeeds; i++) a[i] = new FeedInstance(i);
        return a;
    }

    // The feed that owns work not attributable to any specific one.
    internal static FeedInstance Primary => All[0];

    // ACTIVE feed count. Clamped hard: a typo in the config must not index past the slots
    // or drop to zero, because zero feeds means NextForRender has nothing to return and
    // every lookup would have to invent an answer.
    //
    // ALSO clamped by the VRAM admission cap (phase E1) — see UpdateResidentCap. The user
    // asks for N feeds; they get min(N, what fits). Deliberately the ONLY automatic
    // throttle in the system: quality stays a manual lever, by explicit user decision
    // ("i dont want the quality setting to be adjusted as an automatic thorttle"), so the
    // one thing the mod may decide on its own is how many feeds it will hold at once.
    internal static int Count
    {
        get
        {
            int n = FeedConfig.FeedCount;
            if (n < 1) n = 1;
            if (n > MaxFeeds) n = MaxFeeds;
            return n < _residentCap ? n : _residentCap;
        }
    }

    // ---- the VRAM admission cap (phase E1) ---------------------------------------
    //
    // WHY THIS EXISTS, from measurement rather than principle. Two feeds at 1024 SSAA were
    // run on 2026-07-30 and the game device-removed 40 s later, with UsedVRAM at 13.70 GB
    // against an AvailableVRAM budget of 13.61 GB. Nothing in the mod noticed. The analytic
    // resource walk then put a feed at 384.7 MiB, so "will the next feed fit" is arithmetic
    // we can do BEFORE building it instead of a crash we discover afterwards.
    //
    // The cap only ever bounds Count. It never tears a resident feed down on its own: the
    // config asking for fewer feeds is the user's decision and is honoured instantly, but a
    // VRAM dip must not start a teardown storm on the render thread. It bites where it is
    // cheap — at the moment a feed would be ADMITTED.
    private static int _residentCap = MaxFeeds;
    private static int _capRaiseVotes;
    private static int _lastLoggedCap = MaxFeeds;

    internal static int ResidentCap => _residentCap;

    // Per-feed footprint, split by what it scales with. ScreenBuffers (60.0 MiB) and the
    // RTGI temporal histories (32.0 MiB) scale with OUR pixel count; everything else —
    // entity instance buffers, light clustering, the rest of the DrawContextManager, the
    // panel-sized LDR ring — does not.
    //
    // THE TOTAL IS THE MEASURED MARGINAL COST, NOT THE ANALYTIC SUM. The resource walk in
    // docs/feed-resources-1024-charshadow256.txt adds up to 384.7 MiB and says in its own
    // output that it is a LOWER BOUND, because it prints an UNSIZED list of types it cannot
    // measure. The observed cost of switching feedCount 1 -> 2 on 2026-07-30 was
    // 12.20 -> 12.78 GB, i.e. ~580 MB. Using the analytic figure here would let the cap
    // admit feeds ~1.5x smaller than they really are, which is the precise failure this cap
    // exists to prevent — so the number that decides admission is the one that was watched
    // happening, per Rule 26. The walk stays valuable for finding WHAT to cut; it is just
    // not the right input for "does another one fit".
    //
    // The structural remainder is the important number for the roadmap: no quality preset
    // can remove it, because owning a second culling context means owning scene-sized
    // buffers. It is the floor under max-resident-feeds.
    private const double ResScaledMbAt1024 = 92.0;
    private const double StructuralMb      = 488.0;

    private static double PerFeedMb()
    {
        double px = (double)FeedConfig.WholeSceneWidth * FeedConfig.WholeSceneHeight;
        return StructuralMb + ResScaledMbAt1024 * (px / (1024.0 * 1024.0));
    }

    // Feeds actually holding GPU resources right now. SbBuilt is the honest test — a slot
    // that is configured but has never built its ScreenBuffers costs nothing, so counting
    // configured feeds instead would reserve memory for feeds that are not there.
    private static int ResidentCount()
    {
        int n = 0;
        for (int i = 0; i < MaxFeeds; i++) if (All[i].SbBuilt) n++;
        return n;
    }

    // Called from FeedConfig.Poll (every 2 s), INSIDE the rebuild-signature window so a cap
    // change re-routes panels through exactly the same machinery a feedCount change does.
    internal static void UpdateResidentCap()
    {
        int userCap = FeedConfig.MaxResidentFeeds;
        if (userCap < 1) userCap = 1;
        if (userCap > MaxFeeds) userCap = MaxFeeds;

        if (!FeedConfig.FeedVramGuard) { ApplyCap(userCap, "guard off"); return; }

        long usedMb = Perf.SampleVramMb(), availMb = Perf.SampleVramAvailMb();

        // NO READING IS NOT ZERO HEADROOM. Perf returns 0 before the first frame and
        // whenever VideoMemoryMonitor cannot be resolved; treating that as "nothing fits"
        // would clamp every feed away during startup, when the cap has nothing useful to
        // say anyway. Fall back to the user's ceiling and let them own the decision.
        if (usedMb <= 0 || availMb <= 0) { ApplyCap(userCap, "no VRAM reading"); return; }

        int resident = ResidentCount();
        double perFeed = PerFeedMb();
        long headroom = availMb - usedMb - FeedConfig.FeedVramReserveMb;
        int extra = headroom <= 0 ? 0 : (int)(headroom / perFeed);

        int fits = resident + extra;
        if (fits < 1) fits = 1;              // never cap the last feed away
        int want = fits < userCap ? fits : userCap;

        // ASYMMETRIC HYSTERESIS. Lowering is immediate — it is the safety direction, and a
        // late clamp is the crash it exists to prevent. Raising needs three consecutive
        // polls (~6 s) to agree, because VRAM swings +/-200 MiB frame to frame (measured
        // during the failed B1/D3 sweeps) and a cap that flaps across the requested count
        // would trigger a rebuild every time it moved.
        if (want < _residentCap) { _capRaiseVotes = 0; ApplyCap(want, Why(headroom, perFeed, resident, availMb, usedMb)); }
        else if (want > _residentCap)
        {
            if (++_capRaiseVotes >= 3) { _capRaiseVotes = 0; ApplyCap(want, Why(headroom, perFeed, resident, availMb, usedMb)); }
        }
        else _capRaiseVotes = 0;
    }

    private static string Why(long headroom, double perFeed, int resident, long availMb, long usedMb) =>
        $"used {usedMb} MB of a {availMb} MB budget, reserve {FeedConfig.FeedVramReserveMb} MB, " +
        $"headroom {headroom} MB, {resident} feed(s) resident at ~{perFeed:F0} MB each";

    private static void ApplyCap(int cap, string why)
    {
        _residentCap = cap;
        if (cap == _lastLoggedCap) return;
        _lastLoggedCap = cap;

        // Loud on the way down, because a silently reduced feed count is indistinguishable
        // from a broken feed — a black panel with every counter reading healthy is the
        // single most expensive failure shape this project has produced.
        RttLog.Line($"FEED VRAM CAP: max resident feeds = {cap} ({why}). " +
                    (cap < FeedConfig.FeedCount
                        ? $"feedCount={FeedConfig.FeedCount} is being CLAMPED to {cap} — the extra feed(s) will not be built."
                        : "not currently limiting anything."));
    }

    // Enumerate the ACTIVE feeds. Anything sweeping the registry uses this, so shrinking
    // feedCount stops touching the retired slots immediately — their resources are then
    // released by the same gate-shutdown path a dormant panel uses, which is the only
    // quiesced moment the renderer offers.
    internal static FeedInstance At(int i) => All[i];

    // THE AMBIENT. ThreadStatic because the LCD tick can legitimately be on feed A while
    // the render thread is on feed B, at the same instant — which is exactly why C1a (where
    // Cur was a constant and both threads saw one object) was graded at parity BEFORE this
    // landed. Null means "no pump has claimed this thread": a bug to be found, not a state
    // to rely on. See Unscoped().
    [ThreadStatic] private static FeedInstance _ambient;

    // AGGRESSIVELY INLINED, and not cargo-culted. This sits in front of state that hot paths
    // touch — ShouldSkipStage runs per stage per render, the LDR ring is indexed through it,
    // and the render path reads a dozen of these per frame. A ThreadStatic read plus a null
    // test is a few nanoseconds and the JIT folds the accessor away, but the attribute makes
    // that a guarantee rather than a hope. Measured context: on the C1a build ourDraw held
    // 2.4-2.7 ms across a 3x swing in engine frame time, so the seam was already free — this
    // keeps it free now that a real indirection has replaced the constant.
    internal static FeedInstance Cur
    {
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get => _ambient ?? Unscoped();
    }

    // ---- the unscoped-access diagnostic ------------------------------------------
    //
    // Falling back to Primary is SAFE while Count == 1 (it is the same object every lookup
    // would return) and WRONG the moment C3 adds a feed, because an unscoped path would
    // silently operate on feed 0's state whichever feed it actually belongs to.
    //
    // So the fallback is deliberately both things at once. It keeps the mod running — a null
    // here would NRE on the render thread every frame, and "never device-remove" outranks
    // "fail loudly" — and it reports itself. The FIRST occurrence captures a stack trace,
    // which turns "some path is unscoped" into the exact file and line needing a scope.
    // That log line IS the C3 to-do list, written by the code instead of by me guessing
    // which of the ten entry points I missed.
    private static bool _unscopedLogged;
    private static int _unscopedCount;
    private static bool _selfTesting;
    internal static int UnscopedCount => _unscopedCount;

    private static FeedInstance Unscoped()
    {
        _unscopedCount++;
        if (!_unscopedLogged)
        {
            _unscopedLogged = true;
            string where;
            try { where = new System.Diagnostics.StackTrace(1, true).ToString(); }
            catch { where = "(stack unavailable)"; }
            RttLog.Line(_selfTesting
                ? "Feeds: (SELF-TEST) deliberate unscoped access — this is the diagnostic proving " +
                  "it can fire, NOT a real finding. The stack below is the self-test's own.\n" + where
                : "Feeds: per-feed state touched with NO ambient instance set — falling " +
                  "back to feed 0. HARMLESS while there is one feed, since every lookup " +
                  "returns that same object; WRONG as soon as there are two, because this " +
                  "path would operate on feed 0 whichever feed it belongs to. Scope it " +
                  "before C3. Logged once; running total in Feeds.UnscopedCount.\n" + where);
        }
        return All[0];
    }

    // PROVE THE DIAGNOSTIC WORKS, every load, before trusting its silence.
    //
    // At Count == 1 a scoped access and an unscoped one are behaviourally IDENTICAL — both
    // resolve to feed 0 — so "the log is clean" is equally consistent with "every path is
    // scoped" and with "the detector is broken". Trusting the second reading is how C3 would
    // start with false confidence, and this project has already paid for the general version
    // of that mistake: a mechanism is only real once it has been observed FIRING (Rule 26,
    // which is also what condemned the dead probe-dispose queue).
    //
    // So: call this from Install, which runs BEFORE any pump has claimed the thread and is
    // therefore genuinely unscoped. It exercises the whole path — the null test, the counter,
    // the StackTrace capture and RttLog — then re-arms so a real unscoped access still gets
    // reported with its own stack.
    internal static void SelfTest()
    {
        _selfTesting = true;
        int before = _unscopedCount;
        _ = Cur;
        bool fired = _unscopedCount > before;
        _selfTesting = false;

        _unscopedLogged = false;
        _unscopedCount = 0;

        RttLog.Line(fired
            ? "Feeds: unscoped-access diagnostic SELF-TEST PASSED — an unscoped read was " +
              "detected and reported, so a SILENT log from here on is real evidence that every " +
              "per-feed access is properly scoped. Counter re-armed."
            : "Feeds: unscoped-access diagnostic SELF-TEST FAILED — an access with no ambient " +
              "set was NOT detected. The detector is broken, so its silence means nothing and " +
              "C3 must not rely on it. Fix this before adding a second feed.");
    }

    // ---- scoping -----------------------------------------------------------------

    // Restores the PREVIOUS ambient rather than null, so nesting is safe — the probe pass
    // fires inside the whole-scene render, which is already scoped.
    internal readonly struct Scope : IDisposable
    {
        private readonly FeedInstance _prev;
        internal Scope(FeedInstance f) { _prev = _ambient; _ambient = f; }
        public void Dispose() => _ambient = _prev;
    }

    internal static Scope Enter(FeedInstance f) => new Scope(f ?? All[0]);

    // Run body once per ACTIVE feed, each under its own ambient. For whole-registry sweeps
    // — config rebuilds, teardowns. Snapshots Count first so a config edit landing mid-sweep
    // cannot change the bound underneath the loop.
    internal static void ForEach(Action body)
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            using (Enter(All[i]))
                body();
    }

    // EVERY slot, active or not. The distinction is not cosmetic:
    //
    //   sweeping to RUN something covers the ACTIVE feeds  -> ForEach
    //   sweeping to RELEASE something covers ALL slots     -> ForEachSlot
    //
    // A slot that has just been retired (feedCount 2 -> 1, or the VRAM cap clamping) is
    // precisely the one still holding a ScreenBuffers and a DrawContextManager that nothing
    // will ever ask for again. Sweeping it with ForEach skips it at exactly the moment its
    // resources became garbage, and there is no later pass that would catch it — the feed is
    // outside Count from then on, so it is invisible to every subsequent sweep.
    //
    // Untouched slots cost nothing to visit: their state is null and their countdowns are
    // -1, so every release path returns immediately.
    internal static void ForEachSlot(Action body)
    {
        for (int i = 0; i < MaxFeeds; i++)
            using (Enter(All[i]))
                body();
    }

    // ---- the two selectors --------------------------------------------------------

    // THE RENDER SLOT (phase E1, in embryo): at most one render per engine frame, strict
    // cyclic rotation. Peek and advance are SEPARATE on purpose — the rotation must move
    // when a render actually happens, not on every frame, or a feed that declines its turn
    // (dormant, settling, rate-gated) would hand its slot away permanently.
    private static int _slot;

    internal static FeedInstance NextForRender() => All[_slot % Count];

    // Modulo the LIVE Count, so shrinking feedCount cannot strand the rotation on a slot
    // that is no longer active.
    internal static void AdvanceSlot() => _slot = (_slot + 1) % Count;

    // LOOKUP for the panel- and target-driven hooks (phase C3). The C1b stubs returned
    // All[0]; these now resolve real ownership. See FeedRouter for why claims are made on
    // the FIRST tick rather than settling, and why they are keyed on the panel NAME rather
    // than on a component reference the engine recreates.
    internal static FeedInstance ForPanel(object renderComponent) =>
        Count == 1 ? All[0] : FeedRouter.ForComponent(renderComponent);

    internal static FeedInstance ForTarget(object targetComponent) =>
        Count == 1 ? All[0] : FeedRouter.ForTargetComponent(targetComponent);

    // The panel-render hook is handed a surface context, which has no name to parse — it is
    // claimed during discovery instead. See FeedRouter.ClaimSurface.
    internal static FeedInstance ForSurface(object surfaceCtx) =>
        Count == 1 ? All[0] : FeedRouter.ForSurface(surfaceCtx);
}
