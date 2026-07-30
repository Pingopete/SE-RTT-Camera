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

    // ---- PanelBinding: the material binding --------------------------------------
    public bool Bound;
    public WeakReference BoundRenderer;
    public WeakReference BoundCtx;
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
    internal static int Count
    {
        get
        {
            int n = FeedConfig.FeedCount;
            return n < 1 ? 1 : n > MaxFeeds ? MaxFeeds : n;
        }
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
