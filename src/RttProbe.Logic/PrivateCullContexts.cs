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
    private static Type _rangeStatsType;
    private static int _state;          // 0 untried, 1 built, -1 unavailable
    private static bool _rangesLogged, _sizeLogged, _declineLogged;

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
        _rangeStatsType = null;
        _miVisEnsureRanges = _miVisBorrow = _miVisReturn = null;
        _state = 0;
        _rangesLogged = _sizeLogged = _declineLogged = false;
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
            _rangeStatsType = rsType;
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

    // Size and borrow the VISIBILITY buffers before culling into them.
    //
    // Straight from the lesson that cost us the asteroids: a freshly constructed context
    // starts at a floor, and the engine only grows the ones its own pending-work queues
    // name. Nothing names ours. CullingContext needed UpdateRanges for exactly this reason
    // and this is the same shape. VisibilityListBufferContext.EnsureRanges takes only a
    // command list — it has no RangeStats to get wrong.
    //
    // Geometry is deliberately NOT touched here. It used to be, and that made this method
    // and PrepareGeomForPass both borrow the same context: with mainViewCulling and
    // privateGeomBuffers on together the second Borrow would trip EnsureRanges'
    // `!_isBorrowed` assert, and an assert tripped mid-session turns the next quit into a
    // crash report via DiagnosticReporter. One owner per resource.
    public static void PrepareForPass(object commandList)
    {
        if (_state != 1 || commandList == null) return;
        try
        {
            _miVisBorrow?.Invoke(_visibility, null);
            _miVisEnsureRanges?.Invoke(_visibility, new[] { commandList });

            if (!_rangesLogged)
            {
                _rangesLogged = true;
                RttLog.Line("Private contexts: visibility buffers borrowed and ranged for our pass.");
            }
        }
        catch (Exception e) { _state = -1; RttLog.Error("prepare private cull contexts", e); }
    }

    // Range and borrow ONLY the geometry buffers.
    //
    // Kept separate from PrepareForPass so the geometry context can be swapped in on its
    // own, without the visibility/occlusion pair. That is what lets it be tested as a
    // change which should produce no visible difference at all — one variable, not three.
    //
    // `sceneStats` is the boxed RangeStats that CullingContext.UpdateRanges just reported
    // for this pass — see OwnContexts.LastRangeStats. Passing it, rather than
    // RangeStats.Default, is the fix for the instant device removal this route hit twice.
    // OutputGeometryBufferContext's constructor creates every buffer at capacity ONE and
    // its EnsureRanges is a plain resize to whatever it is handed, with no counter lookup
    // of its own, so Default (all 1s) sized the buffers for a single draw and the cull
    // then wrote a whole scene into them.
    //
    // `engineGeom` is DrawContexts.MainOutputGeometryBuffers — the context the engine has
    // already sized for a full-scene main-view cull, this frame, for the player's camera.
    // Mirroring its capacities is the sturdiest source we have, and it is needed because
    // sceneStats alone may not be trustworthy: GeometryContext.UpdateRanges sizes from
    // CullCapacityTrackingManager.GetTotalMaterialUsagesForRoot(RootEntityId), and that is
    // a plain dictionary lookup returning Span.Empty on a miss. rootEntityId -1 was assumed
    // to mean "everything"; it is a key, so if nothing tracks -1 the report comes back
    // small and sizing from it would remove the device exactly as Default did.
    //
    // So we take the MAX of the two, which is safe whichever assumption is wrong.
    //
    // Returns true only when the buffers were actually ranged and borrowed; false means
    // the caller should fall back to the shared buffers for this pass.
    public static bool PrepareGeomForPass(object commandList, object sceneStats, object engineGeom)
    {
        if (_state != 1 || commandList == null) return false;
        if (_miGeomEnsureRanges == null) return false;
        try
        {
            var stats = SizeWithHeadroom(sceneStats, engineGeom);
            if (stats == null)
            {
                if (!_declineLogged)
                {
                    _declineLogged = true;
                    RttLog.Line("Private geometry buffers: neither the engine's main-view capacities nor a " +
                                "RangeStats report were readable, so the shared buffers stay in use. " +
                                "Sizing a private context from a guess is what removed the device — " +
                                "OutputGeometryBufferContext is built at capacity ONE and its EnsureRanges " +
                                "resizes to exactly what it is told.");
                }
                return false;
            }

            // EnsureRanges BEFORE Borrow, the order SurfelGenerationJob uses — and it
            // asserts !_isBorrowed, so the other order trips a deferred-fatal assert that
            // turns the next quit into a crash report.
            _miGeomEnsureRanges.Invoke(_geomBuffers, new[] { stats, commandList });
            _miGeomBorrow?.Invoke(_geomBuffers, null);

            if (!_sizeLogged)
            {
                _sizeLogged = true;
                RttLog.Line($"Private geometry buffers: ranged for {OwnContexts.DescribeRangeStats(stats)}. " +
                            $"Engine main view holds {DescribeCapacities(engineGeom)}; " +
                            $"UpdateRanges reported {OwnContexts.DescribeRangeStats(sceneStats)}. " +
                            $"Max of the two, +{FeedConfig.GeomRangeHeadroom:P0} headroom, " +
                            $"floor {FeedConfig.GeomRangeFloor}.");
            }
            return true;
        }
        catch (Exception e) { _state = -1; RttLog.Error("prepare private geometry buffers", e); return false; }
    }

    private static readonly string[] RangeFields =
    {
        "SingleFirstPassCount", "SingleSecondPassCount",
        "VolumeFirstPassCount", "VolumeSecondPassCount",
        "InstancedFirstPassCount", "InstancedSecondPassCount",
        "EntityProxyCount",
    };

    // The six DrawInstanceBuffers on an OutputGeometryBufferContext, in RangeStats order,
    // then the entity-proxy buffer. Straight off the constructor's field list.
    private static readonly string[] BufferFields =
    {
        "_instanceBuffersFirstPass", "_instanceBuffersSecondPass",
        "_volumeInstanceBuffersFirstPass", "_volumeInstanceBuffersSecondPass",
        "_instancedInstanceBuffersFirstPass", "_instancedInstanceBuffersSecondPass",
    };

    // Take the larger of what the engine's main view holds and what UpdateRanges reported,
    // scale for headroom, floor each field.
    //
    // Headroom because both sources describe a cull that ALREADY happened, from the
    // player's viewpoint; ours is about to run from a different one. EnsureCapacity only
    // ever grows (CalculateCapacity rounds up and it never contracts unless a contract is
    // scheduled), so over-asking costs one allocation and under-asking costs the device.
    // The asymmetry is the whole argument.
    private static object SizeWithHeadroom(object sceneStats, object engineGeom)
    {
        if (_rangeStatsType == null) return null;

        var reported = OwnContexts.ReadRangeStats(sceneStats);
        var engine = ReadCapacities(engineGeom);
        if (reported == null && engine == null) return null;

        double scale = Math.Max(1.0, FeedConfig.GeomRangeHeadroom + 1.0);
        int floor = Math.Max(1, FeedConfig.GeomRangeFloor);

        // Box a fresh copy rather than mutating either source — EnsureRanges takes it by
        // ref, and handing back the same box every pass would let it edit in place.
        object boxed = Activator.CreateInstance(_rangeStatsType);
        for (int i = 0; i < RangeFields.Length; i++)
        {
            int basis = Math.Max(reported == null ? 0 : reported[i], engine == null ? 0 : engine[i]);
            long want = (long)Math.Ceiling(basis * scale);
            int sized = (int)Math.Clamp(Math.Max(want, floor), 1, int.MaxValue);
            _rangeStatsType.GetField(RangeFields[i], BindingFlags.Public | BindingFlags.Instance)
                          ?.SetValue(boxed, sized);
        }
        return boxed;
    }

    // Read an OutputGeometryBufferContext's live capacities — what the engine has actually
    // sized itself to. All three buffer classes expose a public Capacity getter, and the
    // entity-proxy buffer exposes ElementCount, which is exactly what EnsureRanges compares
    // RangeStats.EntityProxyCount against.
    public static int[] ReadCapacities(object geomContext)
    {
        if (geomContext == null) return null;
        try
        {
            var t = geomContext.GetType();
            var v = new int[RangeFields.Length];
            for (int i = 0; i < BufferFields.Length; i++)
            {
                var buf = t.GetField(BufferFields[i], Any)?.GetValue(geomContext);
                var cap = buf?.GetType().GetProperty("Capacity", Any)?.GetValue(buf);
                if (cap is not int c) return null;
                v[i] = c;
            }
            var proxy = t.GetField("_entityProxyOutputBuffer", Any)?.GetValue(geomContext);
            var count = proxy?.GetType().GetProperty("ElementCount", Any)?.GetValue(proxy);
            v[6] = count is int pc ? pc : 0;
            return v;
        }
        catch { return null; }
    }

    public static string DescribeCapacities(object geomContext)
    {
        var v = ReadCapacities(geomContext);
        return v == null
            ? "unreadable"
            : $"single={v[0]}/{v[1]} volume={v[2]}/{v[3]} instanced={v[4]}/{v[5]} entityProxies={v[6]}";
    }

    public static void EndGeomPass()
    {
        if (_state != 1) return;
        try { _miGeomReturn?.Invoke(_geomBuffers, null); }
        catch (Exception e) { _state = -1; RttLog.Error("return private geometry buffers", e); }
    }

    // Visibility only — EndGeomPass owns the geometry side, and returning it twice is the
    // mirror of the double-borrow PrepareForPass used to cause.
    public static void EndPass()
    {
        if (_state != 1) return;
        try { _miVisReturn?.Invoke(_visibility, null); }
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
