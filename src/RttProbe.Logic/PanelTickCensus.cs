using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RttProbe;

// PER-SURFACE TICK CENSUS — the discriminator for task #26.
//
// THE QUESTION IT ANSWERS. On 2026-08-06 a block with 8 surfaces had three of them tagged
// [RTT]. Surface 4 took feed 0 and ran at 46 fps; surface 1 took feed 1, which went ACTIVE and
// then DORMANT twice with "no tagged panel has ticked for 1500 ms", leaving that surface bound
// to a render target nothing fills — a black panel next to a healthy one on the same block.
//
// Two explanations fit that log equally well, and no instrument in the mod could separate
// them:
//
//   (a) THE HEARTBEAT MIS-KEYS. FeedGate._lastPanelMs is stamped by NotePanelAlive() under
//       whatever feed scope the tick arrived in. If surface 1's tick is being attributed to
//       feed 0's gate — or dropped before it reaches the gate — then feed 1 starves while its
//       panel is ticking perfectly well. The three surfaces share one block entity, and this
//       repo already knows claim keys must be per-block-unique.
//
//   (b) THE SURFACE GENUINELY STOPS. The LCD system may only tick surfaces it considers
//       visible or in range, in which case the 1500 ms window is reporting the truth and the
//       fix belongs nowhere near the gate.
//
// The difference is directly observable and needs no cleverness: record, per TAGGED SURFACE,
// when it last ticked. If surface 1's age stays near zero while feed 1 calls it dead, it is
// (a). If its age climbs past 1500 ms in step with the gate going dormant, it is (b).
//
// WHY IT RECORDS BEFORE THE POWERED CHECK. CameraFeed.OnLcdTick returns early when
// IsPanelPowered is false, and that early return is upstream of NotePanelAlive. So "ticking
// but reported unpowered" is a THIRD possible answer, and one this census can distinguish for
// free by stamping the raw tick and the powered verdict separately. The powered check has
// history: the first liveness version assumed an unpowered panel stops ticking, and it does
// not — the component keeps ticking to draw the powered-off screen.
//
// COST. One dictionary lookup and two long stores per tagged-surface tick, plus one formatted
// line every PanelTickCensusMs. Untagged surfaces never reach here. The dump is deliberately
// on the same background writer as everything else, because this file's own history says a
// synchronous write on the render thread is worth 110-178 ms.
internal static class PanelTickCensus
{
    private sealed class Entry
    {
        public long FirstTickMs;
        public long LastTickMs;          // any tick, powered or not
        public long LastPoweredTickMs;   // only ticks that passed IsPanelPowered
        public long Ticks;
        public long PoweredTicks;
        public int RoutedFeed = -1;      // what FeedRouter says this surface belongs to
        public int ScopedFeed = -1;      // the feed scope the tick actually ARRIVED in
        public bool LastPowered;
    }

    // Keyed by FeedRouter.SurfaceKey, which already encodes feed index, surface index and the
    // block entity — so two tagged surfaces on ONE block are two distinct entries. That is the
    // whole point; a census keyed on the block would be blind to exactly the case under test.
    private static readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
    private static long _lastDumpMs;
    private static readonly object _lock = new();

    // Called on every tick of a TAGGED surface, before the powered gate, so the census sees
    // the raw signal rather than the post-filter one.
    internal static void Note(string key, int routedFeed, int scopedFeed, bool powered)
    {
        if (!FeedConfig.PanelTickCensus || string.IsNullOrEmpty(key)) return;
        try
        {
            long now = Clock.Ms;
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out var e))
                    _byKey[key] = e = new Entry { FirstTickMs = now };
                e.LastTickMs = now;
                e.Ticks++;
                e.RoutedFeed = routedFeed;
                e.ScopedFeed = scopedFeed;
                e.LastPowered = powered;
                if (powered) { e.LastPoweredTickMs = now; e.PoweredTicks++; }
            }
        }
        catch { /* a diagnostic must never be able to break the tick it observes */ }
    }

    // Called from the same cadence that emits the other probes. Emits nothing until at least
    // two tagged surfaces have been seen — with one surface there is no ambiguity to resolve
    // and the line would be noise on every single-panel session.
    internal static void MaybeDump()
    {
        if (!FeedConfig.PanelTickCensus) return;
        try
        {
            long now = Clock.Ms;
            if (now - _lastDumpMs < FeedConfig.PanelTickCensusMs) return;

            string line;
            lock (_lock)
            {
                if (_byKey.Count < 2) { _lastDumpMs = now; return; }
                _lastDumpMs = now;

                var sb = new StringBuilder("PANEL TICK CENSUS: ");
                sb.Append(_byKey.Count).Append(" tagged surface(s) seen. ");
                sb.Append("age = ms since that surface last ticked AT ALL; poweredAge = since it ")
                  .Append("last passed IsPanelPowered. A surface whose age stays under ")
                  .Append(FeedConfig.PanelIdleMs)
                  .Append(" ms while ITS feed reports \"no tagged panel has ticked\" means the ")
                  .Append("heartbeat is mis-keyed, not that the panel died.");

                foreach (var kv in _byKey.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var e = kv.Value;
                    long age = now - e.LastTickMs;
                    long pAge = e.LastPoweredTickMs == 0 ? -1 : now - e.LastPoweredTickMs;
                    sb.Append("\n    \"").Append(kv.Key).Append("\" ")
                      .Append("routedFeed=").Append(e.RoutedFeed)
                      .Append(" scopedFeed=").Append(e.ScopedFeed)
                      .Append(e.RoutedFeed == e.ScopedFeed ? "" : "  <-- SCOPE MISMATCH: the tick arrived under a different feed than the router assigns this surface, so its liveness is being credited to the wrong gate")
                      .Append(" | ticks=").Append(e.Ticks)
                      .Append(" powered=").Append(e.PoweredTicks)
                      .Append(" age=").Append(age).Append("ms")
                      .Append(" poweredAge=").Append(pAge < 0 ? "never" : pAge + "ms")
                      .Append(" lastPowered=").Append(e.LastPowered);

                    if (age > FeedConfig.PanelIdleMs)
                        sb.Append("  <-- NO TICK ATTRIBUTED: the block stopped ticking, this surface's tag was ")
                          .Append("removed, or discovery stopped resolving it. On 2026-08-06 the first wording ")
                          .Append("blamed the engine when the truth was the third case — a single-winner scan. ")
                          .Append("Since the per-surface loop, every tagged surface is attributed every block ")
                          .Append("tick, so a climbing age with the tag still on now really is the block or the engine");
                    else if (pAge > FeedConfig.PanelIdleMs)
                        sb.Append("  <-- TICKING BUT UNPOWERED: IsPanelPowered is rejecting it, which is upstream of NotePanelAlive");
                }
                line = sb.ToString();
            }
            RttLog.Line(line);
        }
        catch { }
    }

    // Cleared with the rest of the per-session state so a reload does not carry stale ages
    // into a fresh session and read as "this surface has been dead for 40 minutes".
    internal static void Reset()
    {
        try { lock (_lock) { _byKey.Clear(); _lastDumpMs = 0; } } catch { }
    }
}
