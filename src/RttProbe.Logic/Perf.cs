using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace RttProbe;

// Frame-time instrumentation for the second-render route.
//
// This exists because the alternative was reading one-minute GPUTime averages out of
// the engine's Stats log and guessing. That is not good enough to attribute a hitch:
// the averages hide spikes, the window is sixty times longer than a frame, and the
// scene changes underneath the measurement.
//
// The one measurement that actually attributes cost is a COMPARISON, and the hook gives
// it for free. OnWholeScene fires every frame, but our second render only runs on the
// frames where the rate gate opens. So bucket the frame intervals by whether our render
// ran on that frame:
//
//   ours-ran p95 much worse than idle p95   -> our second render is the cost
//   both buckets equally bad                -> something global (VRAM paging, streaming,
//                                              the player's own frame) and NOT our Draw
//
// That distinction cannot be made from an engine-wide average, and it is the whole point.
//
// VRAM is sampled here too, because an over-budget residency set produces exactly the
// symptom this is chasing — intermittent multi-frame stalls, unrelated to the feed
// cadence, that no per-stage timing would ever explain.
internal static class Perf
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    // Two histograms of frame intervals, split by whether our render ran that frame.
    private sealed class Bucket
    {
        public readonly double[] Samples = new double[2048];
        public int Count;
        public int Written;
        public double Max;
        public double Sum;

        public void Add(double ms)
        {
            Samples[Written % Samples.Length] = ms;
            Written++;
            Count++;
            Sum += ms;
            if (ms > Max) Max = ms;
        }

        public void Clear() { Count = 0; Written = 0; Max = 0; Sum = 0; }

        public double Mean => Count > 0 ? Sum / Count : 0;

        public double Pct(double p)
        {
            int n = Math.Min(Written, Samples.Length);
            if (n == 0) return 0;
            var copy = new double[n];
            Array.Copy(Samples, copy, n);
            Array.Sort(copy);
            int i = (int)Math.Round(p * (n - 1));
            return copy[Math.Clamp(i, 0, n - 1)];
        }

        // Frames longer than this are what the eye reads as a hitch, not as a low
        // frame rate. 50 ms is two missed frames at 60 Hz.
        public int Over(double ms)
        {
            int n = Math.Min(Written, Samples.Length), c = 0;
            for (int i = 0; i < n; i++) if (Samples[i] > ms) c++;
            return c;
        }
    }

    private static readonly Bucket _oursRan = new();
    private static readonly Bucket _idle = new();
    private static readonly Bucket _ourDraw = new();     // wall time of our Draw invoke alone

    private static long _lastFrameTicks;
    private static long _lastReportTicks;
    private static long _vramAtLastReport = -1;
    private static bool _headerLogged;

    // THE PUBLISHED SNAPSHOT — what the stats panel reads.
    //
    // Published in Report(), immediately BEFORE the buckets are cleared, so the panel
    // shows exactly the window the log line shows: same numbers, same period, no second
    // sampling path to disagree with the first. Reading the live buckets instead would
    // give a partial window whose percentiles change under the reader, and would put a
    // second consumer on state that Report() wipes.
    //
    // A plain immutable field swap: the render/UI side reads a reference that never
    // mutates in place, so there is no tearing to reason about across threads.
    internal sealed class Stats
    {
        public double Fps, OursP50, OursP95, OursMax, SubmitMean, SubmitP95;
        public int OursOver50, IdleOver50, OursCount, IdleCount;
        public double VramGb, VramDeltaMb, AvailGb;
        public long StampMs;
    }

    private static volatile Stats _latest;
    internal static Stats Latest => _latest;

    // Set by RunSecondRender around the Draw invoke.
    public static void NoteOurDraw(double ms) => _ourDraw.Add(ms);

    // ---- MANAGED ALLOCATION ATTRIBUTION (the GC-spike hunt, 2026-07-30) --------------
    //
    // The user, standing still with both panels in view, sees 100-270 ms hitches every few
    // seconds. Engine stats attribute them to GC: CLRStalls 0.009 -> 12, GC churn 3-5 GB
    // per interval, sim-thread maxima ~200 ms while the render thread stays at 4 ms. Our
    // heap telemetry shows ~32 MB/s of linear growth — and the dormant-minute experiment
    // proved the churn is OURS: pausing the feed all but stopped it.
    //
    // ~32 MB/s at ~52 fps is ~600 KB per engine frame, far too much for reflection glue —
    // something per-frame is allocating ARRAYS. Guessing the site has been wrong twice
    // tonight, so these counters attribute it instead: GC.GetAllocatedBytesForCurrentThread
    // deltas around our two per-frame entry points, printed per PERF window. Wraps run on
    // the render thread only, where both entry points live.
    private static long _allocRender, _allocUi;

    public static void NoteRenderAlloc(long bytes) { if (bytes > 0) _allocRender += bytes; }
    public static void NoteUiAlloc(long bytes)     { if (bytes > 0) _allocUi += bytes; }

    // Called once per engine frame from the whole-scene hook, AFTER the render decision
    // so `oursRan` is known.
    public static void NoteFrame(bool oursRan)
    {
        long now = Stopwatch.GetTimestamp();

        if (_lastFrameTicks != 0)
        {
            double ms = (now - _lastFrameTicks) * MsPerTick;
            // A gap this long is a load screen, a breakpoint or a hot reload, not a
            // frame. Including it would poison the max and every percentile after it.
            if (ms < 2000) (oursRan ? _oursRan : _idle).Add(ms);
        }
        _lastFrameTicks = now;

        if (_lastReportTicks == 0) { _lastReportTicks = now; return; }
        double sinceReport = (now - _lastReportTicks) * MsPerTick;
        if (sinceReport < FeedConfig.PerfReportMs) return;
        _lastReportTicks = now;

        Report(sinceReport);
    }

    private static void Report(double windowMs)
    {
        try
        {
            if (!_headerLogged)
            {
                _headerLogged = true;
                RttLog.Line("PERF legend: [ours] = engine frames on which our second render ran, " +
                            "[idle] = frames it did not. Same thread, same frame loop, so the only " +
                            "difference between the buckets is our Draw. If [idle] is as bad as " +
                            "[ours], the cost is NOT our render — look at VRAM and streaming.");
            }

            long used = ReadVram("UsedVRAM"), avail = ReadVram("AvailableVRAM");
            string vram;
            double delta = 0;   // hoisted: the snapshot below publishes it too
            if (used > 0)
            {
                double gb = used / 1073741824.0;
                delta = _vramAtLastReport > 0 ? (used - _vramAtLastReport) / 1048576.0 : 0;
                _vramAtLastReport = used;
                vram = $"VRAM={gb:F2}GB ({(delta >= 0 ? "+" : "")}{delta:F0}MB) avail={avail / 1073741824.0:F2}GB";
                if (avail <= 0)
                    vram += " OVER BUDGET — the driver is paging residency over PCIe, which is a " +
                            "hitch source no amount of per-stage tuning will fix";
            }
            else vram = "VRAM=?";

            double fps = (_oursRan.Count + _idle.Count) / (windowMs / 1000.0);

            RttLog.Line(
                $"PERF {fps:F1} fps over {windowMs / 1000.0:F1}s | " +
                $"ours n={_oursRan.Count} mean={_oursRan.Mean:F1} p50={_oursRan.Pct(0.50):F1} " +
                $"p95={_oursRan.Pct(0.95):F1} max={_oursRan.Max:F1} >50ms={_oursRan.Over(50)} | " +
                $"idle n={_idle.Count} mean={_idle.Mean:F1} p50={_idle.Pct(0.50):F1} " +
                $"p95={_idle.Pct(0.95):F1} max={_idle.Max:F1} >50ms={_idle.Over(50)} | " +
                $"ourDraw(cpu submit) n={_ourDraw.Count} mean={_ourDraw.Mean:F1} " +
                $"p95={_ourDraw.Pct(0.95):F1} max={_ourDraw.Max:F1} | " +
                $"alloc render={_allocRender / windowMs * 1000 / 1048576.0:F1}MB/s " +
                $"ui={_allocUi / windowMs * 1000 / 1048576.0:F1}MB/s | {vram} | " +
                // PER-FEED RATES, on the line the watchdog already reads. Everything else
                // here is an aggregate across feeds by design (the fixed budget is the
                // invariant), which leaves "is feed 1 actually producing anything" answerable
                // only by eye. It cost an hour on 2026-08-01: the per-feed status line rides
                // a process-global 5 s timer, so sampling secondRenders per feed out of the
                // log gave an 8:1 split that was pure artefact — the real split was 26.6/25.9.
                $"feed fps {Feeds.FeedFpsLine()}");

            // Publish BEFORE clearing — see the Stats comment.
            _latest = new Stats
            {
                Fps         = fps,
                OursP50     = _oursRan.Pct(0.50),
                OursP95     = _oursRan.Pct(0.95),
                OursMax     = _oursRan.Max,
                OursOver50  = _oursRan.Over(50),
                IdleOver50  = _idle.Over(50),
                OursCount   = _oursRan.Count,
                IdleCount   = _idle.Count,
                SubmitMean  = _ourDraw.Mean,
                SubmitP95   = _ourDraw.Pct(0.95),
                VramGb      = used > 0 ? used / 1073741824.0 : 0,
                VramDeltaMb = delta,
                AvailGb     = avail > 0 ? avail / 1073741824.0 : 0,
                StampMs     = Environment.TickCount64,
            };

            _oursRan.Clear(); _idle.Clear(); _ourDraw.Clear();
            _allocRender = _allocUi = 0;
        }
        catch (Exception e) { RttLog.Error("perf report", e); }
    }

    // For one-off before/after probes around a dispose, in MB. 0 if unavailable.
    public static long SampleVramMb() { long b = ReadVram("UsedVRAM"); return b > 0 ? b / 1048576 : 0; }

    // The BUDGET, in MB, not the free space — AvailableVRAM is what the driver is willing
    // to let this process hold, and the CTD that motivated the phase E1 cap was used
    // (13.70 GB) exceeding exactly this (13.61 GB) for ~40 s. 0 if unavailable, which
    // callers must treat as "no reading" rather than "no memory".
    public static long SampleVramAvailMb() { long b = ReadVram("AvailableVRAM"); return b > 0 ? b / 1048576 : 0; }

    // CoreSystems.VideoMemoryMonitor exposes UsedVRAM / AvailableVRAM as Int64
    // properties. Resolved once and cached; a failure here must never cost a frame.
    private static object _vram;
    private static PropertyInfo _pUsed, _pAvail;
    private static int _vramState;      // 0 untried, 1 ok, -1 unavailable

    private static long ReadVram(string which)
    {
        if (_vramState == -1) return 0;

        // NEVER touch the engine before a frame has rendered.
        //
        // Reading a static field on CoreSystems forces its static constructor, and that
        // cctor reads DeterministicRuntimeConfiguration — which does not exist until
        // Render12EngineComponent.Init has loaded the configuration set. Forcing it
        // earlier throws inside the cctor, .NET marks the type permanently failed, and
        // the game dies on load with a stack trace that names the engine rather than us.
        //
        // _lastFrameTicks is only ever set from the whole-scene hook, which cannot fire
        // until the renderer is fully up, so it is an honest "engine is ready" signal
        // rather than a timer.
        if (_lastFrameTicks == 0) return 0;

        try
        {
            if (_vramState == 0)
            {
                _vramState = -1;
                var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
                _vram = core?.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(f => f.FieldType.Name == "VideoMemoryMonitor")?.GetValue(null);
                if (_vram == null) return 0;
                _pUsed = _vram.GetType().GetProperty("UsedVRAM", Any);
                _pAvail = _vram.GetType().GetProperty("AvailableVRAM", Any);
                if (_pUsed == null) { _vram = null; return 0; }
                _vramState = 1;
            }
            var p = which == "UsedVRAM" ? _pUsed : _pAvail;
            return p == null ? 0 : Convert.ToInt64(p.GetValue(_vram));
        }
        catch { _vramState = -1; return 0; }
    }
}
