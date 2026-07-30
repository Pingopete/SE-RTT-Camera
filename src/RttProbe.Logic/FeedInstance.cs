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

// The registry.
//
// C1a: exactly one instance, so Cur is a constant and BOTH THREADS SEE THE SAME OBJECT
// — which is precisely what a static field did. That is the whole reason this stage is
// separated from C1b: the transform introduces no threading change of any kind, so a
// parity failure at C2 can only be the field mapping.
//
// C1b replaces Cur with a per-thread ambient the pump sets around each feed's work.
// The render thread and the LCD tick will then legitimately be on different feeds at
// the same instant, and that is the moment ThreadStatic becomes load-bearing rather
// than decorative.
internal static class Feeds
{
    private static readonly FeedInstance Solo = new FeedInstance(0);

    internal static FeedInstance Cur => Solo;

    internal static int Count => 1;
}
