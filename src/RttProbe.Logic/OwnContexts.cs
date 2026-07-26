using System.Reflection;
using System.Text;

namespace RttProbe;

// Phase 1: stop sharing the engine's probe culling context.
//
// Borrowing EnvProbeCulling[0] as scratch is the frame-rate ceiling — the engine
// drives it ~7x/frame for its own probe faces, so at 500 ms we rarely collide and at
// 80 ms we corrupt each other within a few frames (337+ copies stable vs 7).
//
// Constructing a CullingContext by hand does not work: it builds fine, then crashes
// within seconds, because a bare context has none of the setup the engine performs
// around the ones it owns.
//
// The engine already solves this — DrawContextManager pools culling contexts:
//
//     CullingContext BorrowShadowCulling(int rootEntityId)
//     void           ReturnShadowCulling(CullingContext context)
//
// Borrow per pass and return it afterwards. Properly initialised, correct lifecycle,
// and no contention with the probe faces.
internal static class OwnContexts
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object _drawContexts;
    private static MethodInfo _miBorrow, _miReturn;
    private static int _state;      // 0 untried, 1 available, -1 unavailable
    private static int _errLogs;

    // A borrowed context is NOT ready to be culled into, and that is what broke the feed.
    //
    // CullingContext's draw-command buffers are grown by
    //
    //     CullingContext.UpdateRanges(ComputeCommandList commandList, ref RangeStats)
    //
    // whose seed value, RangeStats.Default, is literally ONE of each count
    // (SingleFirstPass, SingleSecondPass, VolumeFirstPass, VolumeSecondPass,
    // InstancedFirstPass, InstancedSecondPass, EntityProxy). The real capacity comes
    // from the shared counters the engine reads back a frame late — so a context grows
    // to fit whatever it was last asked to draw, and a context nobody has asked to draw
    // a scene sits at a floor of one draw per category.
    //
    // The engine ranges contexts by walking its PENDING WORK QUEUES:
    // SceneDrawSystem.EnsureRangesOutputGeometryBuffers iterates CascadesToUpdate,
    // CharacterCascadesToUpdate, EnvProbesToUpdate and LocalLightsToUpdate and calls
    // UpdateRanges on the context each request names. Nothing in any of those queues
    // names the context WE borrowed, so ours is only ever ranged by accident — when a
    // local-light shadow request happens to be handed the same instance off the free
    // list.
    //
    // That is the whole regression. EnvProbeCulling[0] is ranged every frame for a full
    // probe scene cull, so while we shared it our draw commands fitted. A pooled shadow
    // context is ranged for a local light's shadow, or not at all, so most of the scene's
    // draw commands are silently dropped — intermittently, and differently each frame as
    // the free list hands back different instances. Missing asteroids and distant
    // planets, and structure flickering at certain angles, are the same defect.
    //
    // The fix is to range it ourselves, every pass, exactly as the engine does.
    private static MethodInfo _miUpdateRanges;
    private static object _defaultRangeStats;
    private static bool _rangeBlocked, _rangeLogged;

    public static bool Available => _state == 1;

    public static void Reset()
    {
        _drawContexts = null;
        _miBorrow = _miReturn = null;
        _state = 0;
        _errLogs = 0;
        _miUpdateRanges = null;
        _defaultRangeStats = null;
        _rangeBlocked = _rangeLogged = false;
    }

    public static void Resolve(object drawContexts, StringBuilder sb)
    {
        if (_state != 0) return;
        _drawContexts = drawContexts;
        sb.AppendLine();
        sb.AppendLine("-- phase 1: pooled culling context (removes the frame-rate ceiling) --");

        try
        {
            var t = drawContexts?.GetType();
            _miBorrow = t?.GetMethods(Any).FirstOrDefault(m => m.Name == "BorrowShadowCulling" && m.GetParameters().Length == 1);
            _miReturn = t?.GetMethods(Any).FirstOrDefault(m => m.Name == "ReturnShadowCulling" && m.GetParameters().Length == 1);

            _state = (_miBorrow != null && _miReturn != null) ? 1 : -1;
            sb.AppendLine($"  BorrowShadowCulling={(_miBorrow == null ? "NOT FOUND" : "ok")} " +
                          $"ReturnShadowCulling={(_miReturn == null ? "NOT FOUND" : "ok")}");
            sb.AppendLine(_state == 1
                ? "  POOLED CULLING AVAILABLE — the feed no longer shares the probe context."
                : "  pooled culling unavailable — still sharing EnvProbeCulling[0] (keep intervalMs high).");

            // Availability, not use: whether the pool is actually borrowed from is
            // FeedConfig.UsePooledCulling, decided per pass. Saying "using" here read
            // as a contradiction of a config that had it switched off.
            RttLog.Line(_state == 1
                ? $"Phase 1: pooled culling contexts available (config usePooledCulling={FeedConfig.UsePooledCulling})."
                : "Phase 1: pool unavailable; still sharing the probe context (keep intervalMs high).");
        }
        catch (Exception e) { _state = -1; RttLog.Error("resolve culling pool", e); }
    }

    // rootEntityId is very likely the POOL KEY, not just a culling filter.
    //
    // We passed -1 ("cull everything"), which is the same value the engine's own shadow
    // cascade culling would use — so we were being handed the context it was actively
    // refilling. Sharing EnvProbeCulling[0] gave an intermittent frame from the probe's
    // viewpoint; sharing the cascade context instead gives objects popping in and out as
    // our culling results are overwritten. Same bug, different pool.
    //
    // A distinct id should map to a context nobody else asks for. Configurable because
    // this is a guess about the pool's keying, and the wrong guess is one edit away from
    // the right one rather than a rebuild.
    public static object Borrow()
    {
        if (_state != 1) return null;
        try { return _miBorrow.Invoke(_drawContexts, new object[] { FeedConfig.CullRootEntityId }); }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("borrow culling", e); return null; }
    }

    // Grow the borrowed context's draw-command buffers to fit a full scene cull.
    //
    // Must run on the same command list, before DoCullingFirstPass. RangeStats.Default
    // is passed exactly as the engine passes it — it is a floor, not a request; the
    // actual growth comes from the shared counters, so this converges over a frame or
    // two rather than immediately, and is cheap once the ranges already fit.
    public static void EnsureRanges(object cullingContext, object commandList)
    {
        if (_rangeBlocked || cullingContext == null || commandList == null) return;
        if (!FeedConfig.RangeCulling) return;
        try
        {
            if (_miUpdateRanges == null)
            {
                _miUpdateRanges = cullingContext.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "UpdateRanges" && m.GetParameters().Length == 2);
                var rsType = _miUpdateRanges?.GetParameters()[1].ParameterType;
                if (rsType != null && rsType.IsByRef) rsType = rsType.GetElementType();
                _defaultRangeStats = rsType?.GetField("Default", BindingFlags.Public | BindingFlags.Static)
                                           ?.GetValue(null);

                if (_miUpdateRanges == null || _defaultRangeStats == null)
                {
                    _rangeBlocked = true;
                    RttLog.Line($"Culling ranges: UpdateRanges={(_miUpdateRanges == null ? "NOT FOUND" : "ok")} " +
                                $"RangeStats.Default={(_defaultRangeStats == null ? "NOT FOUND" : "ok")} — " +
                                "the borrowed context stays at its one-draw floor and the feed will be missing geometry.");
                    return;
                }
            }

            // Invoke copies the boxed struct in and out; we do not care about the value
            // that comes back, only that the context grew.
            _miUpdateRanges.Invoke(cullingContext, new[] { commandList, _defaultRangeStats });

            if (!_rangeLogged)
            {
                _rangeLogged = true;
                RttLog.Line("Culling ranges: UpdateRanges now called on the borrowed context each pass. " +
                            "Without it the context sits at RangeStats.Default — ONE draw per category — " +
                            "because the engine only ranges contexts its own pending-work queues name.");
            }
        }
        catch (Exception e)
        {
            _rangeBlocked = true;
            RttLog.Error("culling UpdateRanges", e);
        }
    }

    public static void Return(object ctx)
    {
        if (_state != 1 || ctx == null) return;
        try { _miReturn.Invoke(_drawContexts, new[] { ctx }); }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("return culling", e); }
    }
}
