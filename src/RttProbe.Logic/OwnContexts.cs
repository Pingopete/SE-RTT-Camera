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

    public static bool Available => _state == 1;

    public static void Reset()
    {
        _drawContexts = null;
        _miBorrow = _miReturn = null;
        _state = 0;
        _errLogs = 0;
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

    // rootEntityId -1 matches what the probe pass passes to DoCullingFirstPass:
    // cull everything rather than a single entity's subtree.
    public static object Borrow()
    {
        if (_state != 1) return null;
        try { return _miBorrow.Invoke(_drawContexts, new object[] { -1 }); }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("borrow culling", e); return null; }
    }

    public static void Return(object ctx)
    {
        if (_state != 1 || ctx == null) return;
        try { _miReturn.Invoke(_drawContexts, new[] { ctx }); }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("return culling", e); }
    }
}
