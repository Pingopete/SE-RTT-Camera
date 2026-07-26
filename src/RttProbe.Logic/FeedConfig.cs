namespace RttProbe;

// Environment.TickCount64 quantises to the system timer interval — ~15.6 ms on
// Windows unless something has raised the timer resolution. That is invisible at
// 2 fps and decisive at 30: a 33 ms gate rounds up to ~47 ms, so asking for 30 fps
// delivered 20, and asking for 15 delivered 12.8. Both measured exactly.
//
// Stopwatch is backed by QueryPerformanceCounter, so the gate lands where it is put.
// Only the per-frame gates need this; the 2 s arm/config polls do not care.
internal static class Clock
{
    private static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
    public static long Ms => Sw.ElapsedMilliseconds;
}

// Live-tunable knobs, read from output\feed-config.txt and re-read every couple of
// seconds. Tuning frame rate by editing a constant and rebuilding wastes a
// hot-reload cycle per experiment; this makes it a file edit.
//
//   intervalMs = 66      camera pass period (66 ~= 15 fps, 33 ~= 30 fps)
//   orbitRadius = 100    metres from the panel
//   orbitPeriod = 30     seconds per revolution
//   orbitHeight = 15     metres above the panel
//
// Anything missing or malformed falls back to the default, so a half-written file
// cannot break the feed.
internal static class FeedConfig
{
    private static readonly string Path_ = System.IO.Path.Combine(RttLog.OutDir, "feed-config.txt");

    private static long _lastRead;
    private static long _lastStamp;

    public static int IntervalMs { get; private set; } = 66;      // ~15 fps

    // Panel update period, deliberately separate from the camera pass. RequestRender
    // makes the ENGINE run a full DrawOne (borrow, clear, replay UI batches, mipmap,
    // copy, return) for our target — so the two rates cost very different things and
    // must be testable independently. 0 = follow IntervalMs.
    public static int PanelMs { get; private set; }
    public static int EffectivePanelMs => PanelMs > 0 ? PanelMs : IntervalMs;

    // Grace period after the logic loads before any GPU work is issued. Several
    // crashes were "on world load", when the renderer is still settling — pooled
    // targets resizing, panels acquiring their render targets, streaming catching up.
    // Starting into that is asking for trouble.
    public static int StartupDelayMs { get; private set; } = 2000;

    // Phase 1 borrows from DrawContextManager.BorrowShadowCulling — the pool the
    // engine uses for SHADOW cascades. Switchable so "is the pool the problem" can be
    // answered by a file edit rather than a rebuild.
    public static bool UsePooledCulling { get; private set; } = true;

    // Explicit resource barriers around the handover copy, one switch per end.
    //
    // Adding the destination barrier killed the game on the first copy, ~0.5 s in —
    // so the engine's AutoResourceState tracker evidently transitions the CopyResource
    // destination itself, and forcing CopyDest on top of that desynchronises it. The
    // source barrier has run for hundreds of copies without incident. Default off /
    // on respectively, and switchable so the pair can be bisected in one session
    // instead of one launch per hypothesis.
    public static bool SrcTransition { get; private set; } = true;
    public static bool DestTransition { get; private set; }

    // Replace the test pattern's persistent batch with an empty one once the feed
    // takes over, so DrawOne has nothing to draw before our copy lands.
    public static bool RetireTestPattern { get; private set; } = true;

    // The CopyResource itself. Off by default while the copy is under investigation:
    // three launches died on copy #1 into a target we created, where hundreds of
    // copies into the LCD system's own target ran clean. With this off the handover
    // describes both ends into copy-diag.txt and copies nothing, so the game survives
    // and the file can be read with the session still live. Set to 1 to re-enable.
    public static bool CopyEnabled { get; private set; }

    // ---- fidelity layers, each independently switchable ----
    // Live knobs, not load-time markers: the tonemap marker was read once during the
    // dry run, so creating it mid-session did nothing and looked like the feature was
    // broken. Every layer here can be toggled while the game runs, so a layer that
    // kills the render names itself in one edit rather than one launch.
    public static bool Tonemap { get; private set; }   // exposure + tone response
    public static bool Bloom { get; private set; }     // needs Tonemap
    public static bool Sky { get; private set; }       // IndirectPlanetEnvironmentJob

    // Exposure source. On: EnvironmentProbeExposureJob.Exposure — the engine's own
    // exposure for probe-style offscreen renders. Off: ComputeExposure, which drives
    // the SHARED eye adaptation and therefore exposes our feed for the player's view
    // while feeding our HDR buffer into the adaptation the main view depends on.
    public static bool ProbeExposure { get; private set; } = true;

    // Clustering far plane, metres. Was hardcoded to 5000 — copied from the probe
    // pass, which sizes for a whole environment probe. An orbit camera 100 m from a
    // ship needs a fraction of that and the cost scales with it.
    public static double CullFarPlane { get; private set; } = 1500.0;

    // Skip ApplyBloom and hand ApplyToneMapping a flat pre-made texture instead.
    // ApplyToneMapping's bloom parameter is not optional (null throws inside the
    // engine), but there is no requirement that it be freshly computed. Bloom is a
    // multi-pass downsample/upsample chain and accounts for the first halving of the
    // frame rate — and skipping it also removes one of the three main-view post
    // passes we borrow, which is a stability win as well as a speed one.
    public static bool CheapBloom { get; private set; } = true;

    // Run the camera pass from the per-frame hook (DrawUnlit) instead of the probe
    // hook. The probe hook is inside the engine's own environment-probe work, which
    // is where borrowing the main view's post passes corrupted the player's render.
    // Off by default: the probe hook is the one with hours of proven runtime.
    public static bool PassOnFrameHook { get; private set; }

    // Orbit radius is a FLOOR, not a fixed distance: the effective radius is
    // max(orbitRadius, gridExtent * orbitClearance). Orbiting a fixed 100 m around a
    // ship whose half-diagonal is 80 m flies the camera through the hull, which is
    // what the feed was showing.
    public static double OrbitClearance { get; private set; } = 2.2;

    // Orbit the grid's centre (default) or the tagged panel itself. Panel-centred is
    // the close-up shot; grid-centred is the one that looks like a drone camera.
    public static bool OrbitGrid { get; private set; } = true;

    public static double OrbitRadius { get; private set; } = 100.0;
    public static double OrbitPeriod { get; private set; } = 30.0;
    public static double OrbitHeight { get; private set; } = 15.0;

    public static void Poll()
    {
        var now = System.Environment.TickCount64;
        if (now - _lastRead < 2000) return;
        _lastRead = now;

        try
        {
            if (!File.Exists(Path_))
            {
                // Write the defaults out once so the knobs are discoverable rather
                // than something you have to read the source to find.
                File.WriteAllText(Path_,
                    "# RTT camera feed — edit and save; picked up within ~2s.\n" +
                    $"intervalMs  = {IntervalMs}\n" +
                    $"orbitRadius = {OrbitRadius}\n" +
                    $"orbitPeriod = {OrbitPeriod}\n" +
                    $"orbitHeight = {OrbitHeight}\n");
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(Path_).Ticks;
            if (stamp == _lastStamp) return;
            _lastStamp = stamp;

            int interval = IntervalMs, panel = PanelMs, startup = StartupDelayMs;
            bool pooled = UsePooledCulling;
            bool src = SrcTransition, dst = DestTransition, retire = RetireTestPattern, copy = CopyEnabled;
            bool tone = Tonemap, bloom = Bloom, sky = Sky, probeExp = ProbeExposure, cheapBloom = CheapBloom;
            bool frameHook = PassOnFrameHook;
            double farPlane = CullFarPlane;
            double radius = OrbitRadius, period = OrbitPeriod, height = OrbitHeight, clearance = OrbitClearance;
            bool orbitGrid = OrbitGrid;

            foreach (var raw in File.ReadAllLines(Path_))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim().ToLowerInvariant();
                var val = line[(eq + 1)..].Trim();

                switch (key)
                {
                    case "intervalms":
                        if (int.TryParse(val, out var i)) interval = Math.Clamp(i, 8, 5000);
                        break;
                    case "panelms":
                        if (int.TryParse(val, out var pm)) panel = Math.Clamp(pm, 0, 5000);
                        break;
                    case "startupdelayms":
                        if (int.TryParse(val, out var sd)) startup = Math.Clamp(sd, 0, 60000);
                        break;
                    case "usepooledculling":
                        pooled = val is "1" or "true" or "yes";
                        break;
                    case "srctransition":
                        src = val is "1" or "true" or "yes";
                        break;
                    case "desttransition":
                        dst = val is "1" or "true" or "yes";
                        break;
                    case "retiretestpattern":
                        retire = val is "1" or "true" or "yes";
                        break;
                    case "copyenabled":
                        copy = val is "1" or "true" or "yes";
                        break;
                    case "tonemap":
                        tone = val is "1" or "true" or "yes";
                        break;
                    case "bloom":
                        bloom = val is "1" or "true" or "yes";
                        break;
                    case "sky":
                        sky = val is "1" or "true" or "yes";
                        break;
                    case "probeexposure":
                        probeExp = val is "1" or "true" or "yes";
                        break;
                    case "cheapbloom":
                        cheapBloom = val is "1" or "true" or "yes";
                        break;
                    case "passonframehook":
                        frameHook = val is "1" or "true" or "yes";
                        break;
                    case "cullfarplane":
                        if (double.TryParse(val, out var fp) && fp > 10.0) farPlane = fp;
                        break;
                    case "orbitradius":
                        if (double.TryParse(val, out var r)) radius = Math.Clamp(r, 1.0, 100000.0);
                        break;
                    case "orbitperiod":
                        if (double.TryParse(val, out var p) && p > 0.1) period = p;
                        break;
                    case "orbitheight":
                        if (double.TryParse(val, out var h)) height = h;
                        break;
                    case "orbitclearance":
                        if (double.TryParse(val, out var c) && c > 0.1) clearance = c;
                        break;
                    case "orbitgrid":
                        orbitGrid = val is "1" or "true" or "yes";
                        break;
                }
            }

            bool changed = interval != IntervalMs || panel != PanelMs || startup != StartupDelayMs || pooled != UsePooledCulling || radius != OrbitRadius
                        || period != OrbitPeriod || height != OrbitHeight
                        || src != SrcTransition || dst != DestTransition || retire != RetireTestPattern
                        || copy != CopyEnabled || clearance != OrbitClearance || orbitGrid != OrbitGrid
                        || tone != Tonemap || bloom != Bloom || sky != Sky || probeExp != ProbeExposure
                        || cheapBloom != CheapBloom || farPlane != CullFarPlane || frameHook != PassOnFrameHook;
            IntervalMs = interval; PanelMs = panel; StartupDelayMs = startup; UsePooledCulling = pooled; OrbitRadius = radius; OrbitPeriod = period; OrbitHeight = height;
            SrcTransition = src; DestTransition = dst; RetireTestPattern = retire; CopyEnabled = copy;
            OrbitClearance = clearance; OrbitGrid = orbitGrid;
            Tonemap = tone; Bloom = bloom; Sky = sky; ProbeExposure = probeExp;
            CheapBloom = cheapBloom; CullFarPlane = farPlane; PassOnFrameHook = frameHook;

            if (changed)
                RttLog.Line($"Config: intervalMs={IntervalMs} (~{1000.0 / IntervalMs:F0} fps) " +
                            $"orbit radius>={OrbitRadius} clearance={OrbitClearance}x grid={OrbitGrid} " +
                            $"period={OrbitPeriod}s height={OrbitHeight} " +
                            $"| copyEnabled={CopyEnabled} srcTransition={SrcTransition} " +
                            $"destTransition={DestTransition} retireTestPattern={RetireTestPattern} " +
                            $"| tonemap={Tonemap} bloom={Bloom} cheapBloom={CheapBloom} sky={Sky} " +
                            $"probeExposure={ProbeExposure} cullFarPlane={CullFarPlane} " +
                            $"hook={(PassOnFrameHook ? "per-frame" : "probe")}");
        }
        catch { /* keep the last good values */ }
    }
}
