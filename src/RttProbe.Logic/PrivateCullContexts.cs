using System.Reflection;

namespace RttProbe;

// Our OWN visibility-list and occlusion contexts, so main-view culling can run without
// touching the player's.
//
// WHY. The main-view culling job is the only route to the MainViewPass and
// MainViewDeferredTexturingPass groups, and those are the only groups where terrain draws
// through the real 172-line triplanar shader instead of TriplanarGIGlobal's 52-line
// flat-colour one. But unlike the indirect job it dereferences a VisibilityListBufferContext
// and an OcclusionContext rather than tolerating nulls.
//
// The first attempt handed it DrawContexts.MainVisibilityListBuffers and
// DrawContexts.Occlusion — copied straight from SceneDrawSystem.MainViewCulling without
// asking whether they were spare. They are not: they are the main view's working state,
// so our cull wrote its results into the buffers the engine then read for the player's
// screen. The player's ship lights went bright and flickered, which is what a corrupted
// visibility list looks like. Same class of mistake as MainOutputGeometryBuffers, which
// this project had already learned about once.
//
// So these are constructed rather than borrowed. Both turn out to be far simpler than
// CullingContext, whose hand-construction defeated an earlier attempt:
//
//     VisibilityListBufferContext(AllocationGroup)          + Borrow/Return/EnsureRanges
//     OcclusionContext(string debugName, StatKey? parent)
//
// and the engine's own recipe in DrawContextManager.CreateInitialContexts passes
// CoreSystems.MemoryHierarchy.DefaultScene as the allocation group, which is a public
// field on a public static.
//
// DELIBERATELY CONSTRUCT-ONLY AT FIRST. Nothing here is wired into the pass until the
// construction itself is proven in isolation, because the last two times a context was
// introduced mid-pass the failure was silent (an empty GBuffer) or landed on the player
// rather than on us. Verify, then use.
internal static class PrivateCullContexts
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object _visibility, _occlusion, _geomBuffers;
    private static MethodInfo _miVisEnsureRanges, _miVisBorrow, _miVisReturn;
    private static MethodInfo _miGeomEnsureRanges, _miGeomBorrow, _miGeomReturn;
    private static object _defaultRangeStats;
    private static int _state;          // 0 untried, 1 built, -1 unavailable
    private static bool _rangesLogged;

    public static bool Available => _state == 1;
    public static object Visibility => _state == 1 ? _visibility : null;
    public static object Occlusion => _state == 1 ? _occlusion : null;

    // Our own draw-command buffers.
    //
    // THE remaining shared state, and the cause of the player's ship lights flickering.
    // We were passing DrawContexts.MainOutputGeometryBuffers to both the cull and the
    // GBuffer pass — the MAIN VIEW's draw-command buffers, with eighteen engine readers,
    // where Borrow() is only an _isBorrowed mutex flag handing back the same physical
    // buffers. With the indirect culling job that was survivable because the engine
    // re-culls those groups anyway; with the main-view job we were writing MainViewPass
    // commands into the exact buffers the engine executes for the player's screen.
    //
    // SurfelGenerationJob is the engine's own precedent for a private second-view cull and
    // it constructs its own, so this is a sanctioned shape rather than a workaround:
    //
    //     BorrowShadowCulling(rootEntityId)
    //     CullingContext.UpdateRanges(cl, rangeStats)
    //     OutputGeometryBufferContext.EnsureRanges(rangeStats, cl)
    //     OutputGeometryBufferContext.Borrow()
    //     CullingJob.DoCullingFirstPass(...)
    //     ... rasterize ...
    //     OutputGeometryBufferContext.Return()
    //     ReturnShadowCulling(ctx)
    public static object GeomBuffers => _state == 1 ? _geomBuffers : null;

    public static void Reset()
    {
        // Both are IDisposable and hold GPU buffers. Dropping them on a hot reload would
        // leak, and the pool asserts about exactly that at shutdown — the defect that made
        // every quit a crash report earlier today.
        foreach (var c in new[] { _visibility, _occlusion, _geomBuffers })
        {
            if (c is IDisposable d) { try { d.Dispose(); } catch { } }
        }
        _visibility = _occlusion = _geomBuffers = null;
        _miGeomEnsureRanges = _miGeomBorrow = _miGeomReturn = null;
        _defaultRangeStats = null;
        _miVisEnsureRanges = _miVisBorrow = _miVisReturn = null;
        _state = 0;
        _rangesLogged = false;
    }

    // Build both, or report exactly which step failed. Returns true only when BOTH exist.
    public static bool Ensure()
    {
        if (_state != 0) return _state == 1;
        _state = -1;
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var memory = core?.GetField("MemoryHierarchy", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var group = memory?.GetType().GetField("DefaultScene", Any)?.GetValue(memory);
            if (group == null)
            {
                RttLog.Line("Private contexts: CoreSystems.MemoryHierarchy.DefaultScene not reachable.");
                return false;
            }

            var visType = FindType("VisibilityListBufferContext");
            var occType = FindType("OcclusionContext");
            if (visType == null || occType == null)
            {
                RttLog.Line($"Private contexts: types not found " +
                            $"(visibility={visType != null}, occlusion={occType != null}).");
                return false;
            }

            _visibility = visType.GetConstructors(Any)
                .FirstOrDefault(c => c.GetParameters().Length == 1)?.Invoke(new[] { group });

            // OcclusionContext(string debugName, Nullable<StatKey> parentStatKey). The stat
            // key is nullable and only feeds the profiler's stats tree; null keeps us out of
            // it entirely, which also avoids colliding with an engine stat name.
            var occCtor = occType.GetConstructors(Any).FirstOrDefault(c => c.GetParameters().Length == 2);
            _occlusion = occCtor?.Invoke(new object[] { "RttCamera", null });

            if (_visibility == null || _occlusion == null)
            {
                RttLog.Line($"Private contexts: construction failed " +
                            $"(visibility={_visibility != null}, occlusion={_occlusion != null}).");
                return false;
            }

            _miVisEnsureRanges = visType.GetMethods(Any).FirstOrDefault(m => m.Name == "EnsureRanges");
            _miVisBorrow = visType.GetMethods(Any).FirstOrDefault(m => m.Name == "Borrow" && m.GetParameters().Length == 0);
            _miVisReturn = visType.GetMethods(Any).FirstOrDefault(m => m.Name == "Return" && m.GetParameters().Length == 0);

            // Our own draw-command buffers — same ctor shape, same allocation group.
            var geomType = FindType("OutputGeometryBufferContext");
            _geomBuffers = geomType?.GetConstructors(Any)
                .FirstOrDefault(c => c.GetParameters().Length == 1)?.Invoke(new[] { group });
            if (_geomBuffers == null)
            {
                RttLog.Line("Private contexts: OutputGeometryBufferContext could not be constructed — " +
                            "the cull would have to share the player's draw-command buffers, which is " +
                            "what made their ship lights flicker.");
                return false;
            }

            // EnsureRanges(in RangeStats, in ComputeCommandList) — note RangeStats comes
            // FIRST here, unlike CullingContext.UpdateRanges(cl, rangeStats).
            _miGeomEnsureRanges = geomType.GetMethods(Any)
                .FirstOrDefault(m => m.Name == "EnsureRanges" && m.GetParameters().Length == 2);
            _miGeomBorrow = geomType.GetMethods(Any).FirstOrDefault(m => m.Name == "Borrow" && m.GetParameters().Length == 0);
            _miGeomReturn = geomType.GetMethods(Any).FirstOrDefault(m => m.Name == "Return" && m.GetParameters().Length == 0);

            var rsType = _miGeomEnsureRanges?.GetParameters()[0].ParameterType;
            if (rsType != null && rsType.IsByRef) rsType = rsType.GetElementType();
            _defaultRangeStats = rsType?.GetField("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            _state = 1;
            RttLog.Line($"Private contexts: BUILT — visibility={_visibility.GetType().Name} " +
                        $"occlusion={_occlusion.GetType().Name} geometry={_geomBuffers.GetType().Name} " +
                        $"(allocation group DefaultScene; visEnsureRanges={(_miVisEnsureRanges == null ? "NO" : "ok")} " +
                        $"geomEnsureRanges={(_miGeomEnsureRanges == null ? "NO" : "ok")} " +
                        $"rangeStats={(_defaultRangeStats == null ? "NOT FOUND" : "ok")}). " +
                        "Nothing is wired to them yet.");
            return true;
        }
        catch (Exception e) { RttLog.Error("build private cull contexts", e); return false; }
    }

    // Size the visibility buffers before culling into them.
    //
    // Straight from the lesson that cost us the asteroids: a freshly constructed context
    // starts at a floor, and the engine only grows the ones its own pending-work queues
    // name. Nothing names ours. CullingContext needed UpdateRanges for exactly this reason
    // and this is the same shape.
    public static void PrepareForPass(object commandList)
    {
        if (_state != 1 || commandList == null) return;
        try
        {
            _miVisBorrow?.Invoke(_visibility, null);
            _miVisEnsureRanges?.Invoke(_visibility, new[] { commandList });

            // Range the geometry buffers BEFORE borrowing, which is the order
            // SurfelGenerationJob uses. RangeStats.Default is a floor rather than a
            // request — the real capacity comes from the shared counters a frame later,
            // exactly as it does for CullingContext.
            if (_miGeomEnsureRanges != null && _defaultRangeStats != null)
                _miGeomEnsureRanges.Invoke(_geomBuffers, new[] { _defaultRangeStats, commandList });
            _miGeomBorrow?.Invoke(_geomBuffers, null);

            if (!_rangesLogged)
            {
                _rangesLogged = true;
                RttLog.Line("Private contexts: visibility and geometry buffers borrowed and ranged for our pass.");
            }
        }
        catch (Exception e) { _state = -1; RttLog.Error("prepare private cull contexts", e); }
    }

    // Range and borrow ONLY the geometry buffers.
    //
    // Kept separate from PrepareForPass so the geometry context can be swapped in on its
    // own, without the visibility/occlusion pair. That is what lets it be tested as a
    // change which should produce no visible difference at all — one variable, not three.
    public static void PrepareGeomForPass(object commandList)
    {
        if (_state != 1 || commandList == null) return;
        try
        {
            // EnsureRanges BEFORE Borrow, the order SurfelGenerationJob uses. A private
            // context nobody else ranges starts at a floor, which is the defect that made
            // the asteroids vanish — applied here before it can cost anything.
            if (_miGeomEnsureRanges != null && _defaultRangeStats != null)
                _miGeomEnsureRanges.Invoke(_geomBuffers, new[] { _defaultRangeStats, commandList });
            _miGeomBorrow?.Invoke(_geomBuffers, null);
        }
        catch (Exception e) { _state = -1; RttLog.Error("prepare private geometry buffers", e); }
    }

    public static void EndGeomPass()
    {
        if (_state != 1) return;
        try { _miGeomReturn?.Invoke(_geomBuffers, null); }
        catch (Exception e) { _state = -1; RttLog.Error("return private geometry buffers", e); }
    }

    public static void EndPass()
    {
        if (_state != 1) return;
        try { _miVisReturn?.Invoke(_visibility, null); _miGeomReturn?.Invoke(_geomBuffers, null); }
        catch (Exception e) { _state = -1; RttLog.Error("return private visibility context", e); }
    }

    private static Type FindType(string simpleName)
    {
        try
        {
            var anchor = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            foreach (var t in anchor?.Assembly.GetTypes() ?? Type.EmptyTypes)
                if (t.Name == simpleName) return t;
        }
        catch { }
        return null;
    }
}
