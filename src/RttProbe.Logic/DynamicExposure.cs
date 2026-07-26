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
    private static string _lastLog = "";
    private static int _logs;

    public static void Reset()
    {
        _current = double.NaN;
        _lastMs = 0;
        _lastLog = "";
        _logs = 0;
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

        target = Math.Clamp(target, FeedConfig.AutoExposureMin, FeedConfig.AutoExposureMax);

        // Change-detection logging, not one-shot: a one-shot line here would print the
        // startup value and then never mention that the exposure had moved, which is the
        // exact trap that made the old constant look inert.
        if (_logs < 40)
        {
            string s = $"{target:F3}";
            if (s != _lastLog)
            {
                _lastLog = s;
                _logs++;
                RttLog.Line($"Exposure: {(FeedConfig.AutoExposure ? "auto" : "fixed")} -> {s}x linear " +
                            $"(EV {Math.Log2(Math.Max(1e-6, target)):F2})");
            }
        }
        return target;
    }

    // How bright the frame is likely to be, from what we already know about the shot.
    //
    // Two terms, both derived from the camera we placed rather than measured off the GPU:
    //
    //   FILL — how much of the frame is lit surface rather than empty space. The target's
    //   apparent angular size against our FOV. A ship filling the frame needs far less
    //   exposure than the same ship as a speck against black, which is exactly the case
    //   the fixed value gets wrong at both ends.
    //
    //   SUN — whether we are looking anywhere near the sun. Not modelled yet; the orbit
    //   camera always faces the target, so the sun is only ever incidental. Left as a
    //   named gap rather than a silent assumption.
    private static double EstimateTarget()
    {
        var target = CameraFeed.Current;
        double radius = Math.Max(1.0, FeedConfig.OrbitRadius);
        double extent = target != null && target.Extent > 0.0 ? target.Extent : 10.0;

        // Fraction of the half-FOV the subject subtends. Clamped well short of 1 because
        // a subject that fills the frame still has sky around the edges at these framings.
        double subtend = Math.Clamp(extent / radius, 0.02, 1.2);

        // Empty space wants a lot of exposure to show anything; a frame full of sunlit
        // hull wants very little. Interpolated in log space so the ends are not lopsided.
        const double emptyEv = 0.0;    // 1.0x  — mostly sky
        const double fullEv = -2.5;    // 0.18x — mostly sunlit surface
        double k = Math.Clamp((subtend - 0.02) / (1.2 - 0.02), 0.0, 1.0);
        double ev = emptyEv + (fullEv - emptyEv) * k;

        return Math.Pow(2.0, ev + FeedConfig.AutoExposureBias);
    }
}
