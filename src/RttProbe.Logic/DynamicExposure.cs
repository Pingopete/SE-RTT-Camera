namespace RttProbe;

// Auto-exposure for the feed, computed from our own camera.
//
// WHY THIS IS NEEDED AT ALL. The feed's harshness was blamed on a missing ambient term
// for a long time, and that was wrong. In space there IS almost no ambient — the IBL cube
// is nearly black and a sunlit rock against empty sky genuinely has no fill light. What
// the player's view has that ours does not is an EXPOSURE COMPUTED PER FRAME. We hand the
// tonemap a fixed number, so a bright asteroid clips to white and a dark scene crushes,
// and no lighting pass can fix either.
//
// WHY NOT THE ENGINE'S. SceneDrawSystem.ComputeExposure drives EyeAdaptationJob, which
// ping-pongs `_autoExposures` and advances the temporal adaptation the MAIN view depends
// on. Running it a second time per frame against our camera corrupted the player's render
// and then took the process with it — that is the one pass this project has confirmed as
// genuinely unsafe. So we do not reuse it, and we do not construct a second one either:
// its downsample chain sizes itself from CoreSystems.SwapChain.Resolution, which is the
// player's, and its own history would still be a shared-pool resource.
//
// WHAT THIS DOES INSTEAD. The luminance estimate is derived on the CPU from what the
// camera can see, not read back from the GPU. A GPU readback would either stall the
// render thread or arrive a frame late through a staging buffer, and neither is worth it
// for a value that only has to be approximately right and smoothly varying.
//
// The estimator is deliberately crude: scene brightness in space is dominated by how much
// of the frame is sunlit surface versus empty sky, and that is a function of the target's
// apparent size and how close the orbit is to looking into the sun. Both are things we
// already know exactly, because we place the camera.
//
// The smoothing is the part that matters more than the accuracy. Real eye adaptation is
// slow and asymmetric — quick to darken, slow to brighten — because that is what reads as
// natural. A jumpy exposure looks far worse than a slightly wrong steady one.
internal static class DynamicExposure
{
    private static double _current = double.NaN;
    private static long _lastMs;
    private static long _lastLogMs;


    public static void Reset()
    {
        _current = double.NaN;
        _lastMs = 0;
        _lastLogMs = 0;

    }

    // The linear exposure multiplier the tonemap should apply. Converted to a log2 EV
    // offset by the caller, because that is what the shader actually consumes.
    public static double Linear()
    {
        double target = FeedConfig.ExposureValue;

        if (FeedConfig.AutoExposure)
        {
            double t = EstimateTarget();
            long now = Clock.Ms;

            if (double.IsNaN(_current)) { _current = t; _lastMs = now; }
            else
            {
                double dt = Math.Max(0.0, (now - _lastMs) / 1000.0);
                _lastMs = now;

                // Asymmetric, and in LOG space so the rate is perceptually even: a stop is
                // a stop whether the scene is bright or dark. Darkening fast and
                // brightening slowly is what real eye adaptation does and what reads as
                // natural; the reverse looks like a fault.
                double speed = t < _current ? FeedConfig.AutoExposureDownSpeed
                                            : FeedConfig.AutoExposureUpSpeed;
                double k = 1.0 - Math.Exp(-Math.Max(0.01, speed) * dt);
                double logNow = Math.Log2(Math.Max(1e-6, _current));
                double logTarget = Math.Log2(Math.Max(1e-6, t));
                _current = Math.Pow(2.0, logNow + (logTarget - logNow) * k);
            }
            target = _current;
        }

        // Clamp the AUTO value only.
        //
        // This used to clamp unconditionally, which silently floored a hand-set
        // exposureValue to autoExposureMin — so a deliberate 2000x test was quietly
        // turned into a 50x one and the log showed "constant 0.0005" next to
        // "linear 0.020x". A manual value is an instruction, not a suggestion; the bounds
        // exist to stop the ESTIMATOR running away, and have no business overriding a
        // number a human typed to probe the pipeline.
        if (FeedConfig.AutoExposure)
            target = Math.Clamp(target, FeedConfig.AutoExposureMin, FeedConfig.AutoExposureMax);

        // PERIODIC, not one-shot and not change-detected.
        //
        // The first version logged only on a changed value, which combined with a
        // constant estimator produced exactly one line — and that single line is what
        // made it look like nothing was being logged at all. A rate-limited line keeps
        // reporting for as long as tuning takes, and prints the SUN TERM alongside the
        // result so it is obvious whether the input is moving or the output is stuck.
        long nowMs = Clock.Ms;
        if (nowMs - _lastLogMs >= 1000)
        {
            _lastLogMs = nowMs;
            string lit = double.IsNaN(_lastLit) ? "n/a (sun direction unavailable)" : $"{_lastLit:+0.00;-0.00}";
            RttLog.Line($"Exposure: {(FeedConfig.AutoExposure ? "auto" : "fixed")} {target:F3}x " +
                        $"(EV {Math.Log2(Math.Max(1e-6, target)):F2})  sunFacing={lit}  " +
                        $"[+1 = looking down-sun, -1 = into the sun]");
        }
        return target;
    }

    // How bright the frame is likely to be, from what we already know about the shot.
    //
    // The first version of this used only the subject's apparent angular size
    // (extent / orbitRadius) and was WRONG IN THE ONE CASE WE ACTUALLY RUN: on a circular
    // orbit both terms are constant, so the exposure never moved. It logged one value at
    // startup and then nothing, which is exactly what was observed. A per-frame estimator
    // whose inputs cannot vary is not an estimator.
    //
    // What genuinely changes as the camera pans is the SUN ANGLE. Orbiting a ship, the
    // shot swings between:
    //
    //   down-sun   sun behind the camera, the subject's near face fully lit, frame bright
    //   backlit    sun beyond the subject, near face in shadow against bright space
    //
    // That is several stops between the extremes and it is the dominant variation in a
    // space scene, where there is no ambient to soften either end. It is also free: we
    // know the camera transform because we place it, and the sun direction is reachable
    // at SettingsManager.Light.Sun.Normal.
    //
    // Still an estimate rather than a measurement — a real one means reading back the
    // rendered frame's luminance, which costs either a render-thread stall or a frame of
    // latency through a staging buffer. Worth doing if this proves not good enough, but
    // the smoothing matters more than the accuracy and this varies correctly.
    private static double EstimateTarget()
    {
        var target = CameraFeed.Current;
        double radius = Math.Max(1.0, FeedConfig.OrbitRadius);
        double extent = target != null && target.Extent > 0.0 ? target.Extent : 10.0;

        // FILL — how much of the frame is surface rather than empty space. Constant on a
        // circular orbit, so it sets the baseline rather than the variation.
        double subtend = Math.Clamp(extent / radius, 0.02, 1.2);
        double fillK = Math.Clamp((subtend - 0.02) / (1.2 - 0.02), 0.0, 1.0);
        const double emptyEv = 0.0;    // 1.0x  — mostly sky
        const double fullEv = -2.0;    // 0.25x — mostly sunlit surface
        double ev = emptyEv + (fullEv - emptyEv) * fillK;

        // SUN — the term that actually moves. +1 when looking directly down-sun (subject
        // fully lit, needs the least exposure), -1 when looking straight into it.
        double lit = CameraRender.SunFacing();
        if (!double.IsNaN(lit))
            ev += -FeedConfig.AutoExposureSunRange * lit;

        _lastLit = lit;
        return Math.Pow(2.0, ev + FeedConfig.AutoExposureBias);
    }

    private static double _lastLit = double.NaN;
    public static double LastSunFacing => _lastLit;
}
