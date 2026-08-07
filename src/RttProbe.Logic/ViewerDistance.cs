using System;
using Keen.VRage.Library.Mathematics;

namespace RttProbe;

// THE NEAREST-VIEWER DISTANCE — one number, three symptoms.
//
// After goal 10 landed (terrain, then trees and boulders in a feed 3,900 km from the player)
// three fidelity gaps were left, and they were being chased as three separate bugs:
//
//     * trees resolve to low-detail versions even with the camera right beside them
//     * foliage looks thinner in the feed than the same biome does locally
//     * grass never appears at all, though the geometry provably exists on our cells
//       (43-85 valid _grassEntity per clipmap LOD, measured 2026-08-02)
//
// They are one bug. RenderUtilities.CalculateDistanceToCamera reads
// CoreSystems.Settings.RenderView.CameraPosition — the single global camera — and returns the
// distance from it to an entity's bounding box. DistanceTagManagerComponent
// .OnUpdateDistanceToCamera caches that ONE float per entity as DistanceRangeData, and then a
// whole family of jobs decides everything from nothing but that cached number:
//
//     OnUpdateRootEntityStreamingTag  StreamingTag, threshold RootStreamingDistance = 200 m
//     OnUpdateImpostorTag             Near/FarDistanceTag, threshold Impostor.SwapDistance
//     OnUpdateShadowTrackingTag       ShadowSettings.LocalLights.DirtyAreaTracking...
//     OnUpdateRaytracingTag           Raytracing.Scene Near/FarDistance
//     OnUpdateTag                     geometry-dirty tags
//
// (The thresholds are enumerated in DistanceThresholdContainer.FullRefresh, which is the
// single place to look if a game update changes the list.)
//
// With the player 3,906 km away, every entity our camera is standing on top of lands in the
// farthest bucket. Grass is the loudest casualty because its model arrives SOLELY through the
// streaming path — GrassEntityComponent.UpdateModel(modelResourceHandle, materials, lod) — so
// a grass entity outside the streaming bubble has geometry and no model, which is exactly the
// "it exists but does not draw" reading the census produced.
//
// THE FIX IS THE ENGINE'S OWN SEMANTIC. "Distance to the camera", once there is more than one
// camera, means distance to the NEAREST one; the engine already names that idea elsewhere
// (ManagedTexturePrioritizerComponent/ClosestDistanceCollector). So the postfix returns
// min(engineAnswer, ourAnswer).
//
// WHY min() IS SAFE RATHER THAN MERELY PLAUSIBLE. It is monotone downward: a distance can only
// get smaller, never larger. No entity the player is near can be demoted by us, no matter what
// this class computes or how wrong it is. Two viewers whose bubbles overlap are not in
// conflict either — min() is idempotent, which is the same reason the engine's ref-counted
// sectors are overlap-safe. The failure mode of a bug in here is "the feed does not improve",
// not "the player's world degrades".
//
// THE BUBBLE IS DELIBERATELY THE PLAYER'S OWN SIZE. Radius defaults to 200 m, which is
// RootStreamingDistance exactly: the feed camera is granted the same streaming bubble the
// player already carries, and not one metre more. That is the fidelity parity the brief asks
// for — match local first, then cull back deliberately — and it bounds the extra resident set
// at "one more player's worth" rather than "however much world the far clip can see".
internal static class ViewerDistance
{
    // Immutable, published by one reference write. The engine calls Nearest() from the
    // renderer's job threads while the camera pass writes from ours; a struct or loose fields
    // would let a reader see an X from one frame and a Z from the next, and a torn position is
    // a bubble in the wrong place. Swapping a whole object cannot tear.
    private sealed class Viewer
    {
        public double X, Y, Z;
        public double R;          // half-extent, for the cheap axis rejects
        public double R2;         // radius squared, for the distance test
        public long ExpiresAt;    // Environment.TickCount64 lease — see Nearest()

        // ---- THE DISTANCE CURVE (task #42) — precomputed so the hot path stays cheap ----
        // Inside Full the answer is the true distance, 1:1. Between Full and R the reported
        // distance is inflated by a smoothstep gain rising to FarBias, so the engine's own
        // LOD/mip/impostor ladders step content DOWN before the bubble boundary — by the
        // time an entity crosses R it is already reporting FarBias x its true distance, so
        // handing it back to the player-distance answer crosses far fewer tier thresholds.
        // The cliff is removed at its cause, and the knob DIRECTION is cheaper, not richer:
        // bias up = mid-range degrades earlier. BiasM1 == 0 (bias 1.0) short-circuits to
        // exactly the old behaviour.
        public double F;          // full-fidelity radius (<= R)
        public double InvSpan;    // 1 / (R - F), 0 when F == R
        public double BiasM1;     // FarBias - 1, 0 = curve off
    }

    private static volatile Viewer _viewer;

    // How long a published position stays valid without renewal. The camera pass renews every
    // intervalMs (33 ms by default), so this only ever expires when the feed has actually
    // stopped — a dormant gate, a torn-down feed, a hot reload mid-flight. Without the lease a
    // stopped feed would leave a patch of world pinned at full resolution with nothing looking
    // at it, and this project has been bitten before by exactly this shape: a latch that keeps
    // reporting healthy after the thing it represents has gone (the handover crash marker).
    private const long LeaseMs = 5000;

    // Renewed from the camera pass. Position is the ORBIT SUBJECT CENTRE, not the camera eye:
    // the eye swings around it every orbit, and a bubble that swings with it would re-tag
    // entities on both edges continuously. A stationary centre puts the same set of entities in
    // the near bucket for the whole orbit, which is what the tag jobs are built to expect and
    // what keeps this from becoming another source of the popping in task #32.
    internal static void Publish(Vector3D centre, double radius, double full, double farBias)
    {
        if (radius <= 0.0) { Clear(); return; }

        // Sanitise HERE, once per camera pass, not in the 120k/s hot path. full <= 0 means
        // "no curve zone" (the pre-curve behaviour: 1:1 all the way to the boundary), and a
        // bias below 1 is refused outright — a gain under 1 would REDUCE reported distances,
        // which breaks the monotone-safety argument the whole hook rests on.
        if (full <= 0.0 || full > radius) full = radius;
        if (double.IsNaN(farBias) || farBias < 1.0) farBias = 1.0;

        _viewer = new Viewer
        {
            X = centre.X, Y = centre.Y, Z = centre.Z,
            R = radius, R2 = radius * radius,
            F = full,
            InvSpan = radius > full ? 1.0 / (radius - full) : 0.0,
            BiasM1 = radius > full ? farBias - 1.0 : 0.0,
            ExpiresAt = Environment.TickCount64 + LeaseMs,
        };
    }

    internal static void Clear() => _viewer = null;

    // Corrections made by the swap guard. Reported so "the fix is installed" and "the fix is
    // actually catching poisoned reads" stay separate claims — this project has confused those
    // before, and a zero here with the guard armed would mean the discriminator is wrong.
    private static long _swapCorrections, _lastSwapCorrections;
    private static long _inWindowCalls, _lastInWindowCalls;
    private static long _inWindowOurThread, _lastInWindowOurThread;

    // The instrument must confess when it is not installed. On 2026-08-03 this text reported
    // "0 calls landed inside our swap window -> the theory is WRONG" for a session where
    // viewerDistance=0 AND fixLodCycling=0 at boot — the postfix was never PATCHED, the
    // counters were reading an uninstalled instrument, and the confident refutation was a
    // lie. A zero is only evidence when the patch demonstrably ran. (The same lesson as the
    // POOL CENSUS FieldInfo bug: check the instrument before believing a negative.)
    private static System.Reflection.FieldInfo _fCallsForGuard;
    private static bool _guardBridgeLooked;

    internal static string SwapGuardText()
    {
        var n = _swapCorrections - _lastSwapCorrections;
        _lastSwapCorrections = _swapCorrections;
        var inWin = _inWindowCalls - _lastInWindowCalls;
        _lastInWindowCalls = _inWindowCalls;
        var ours = _inWindowOurThread - _lastInWindowOurThread;
        _lastInWindowOurThread = _inWindowOurThread;

        if (!_guardBridgeLooked)
        {
            _guardBridgeLooked = true;
            _fCallsForGuard = Type.GetType("RttProbe.RttBridge, RttProbe")?.GetField("ViewerDistanceCalls");
        }
        var totalCalls = _fCallsForGuard != null && _fCallsForGuard.GetValue(null) is long tc ? tc : -1;
        if (totalCalls == 0)
            return "swap guard: INERT — the CalculateDistanceToCamera postfix has made ZERO calls this " +
                   "session, meaning the patch was never applied (viewerDistance and fixLodCycling were " +
                   "both 0 at boot). Every zero here is the uninstalled instrument, NOT evidence of no " +
                   "overlap. Set either knob and RESTART to make this instrument mean something.";

        return $"swap guard: {n} corrected, {inWin} distance call(s) landed INSIDE our swap window " +
               $"({ours} of them on our own render thread; postfix alive, {totalCalls} calls total)" +
               (inWin == 0
                   ? "   <-- NO OVERLAP: the engine never queries entity distance while our camera is " +
                     "installed, so CalculateDistanceToCamera is not the vector on this path."
                   : ours == inWin
                       ? "   <-- ALL OURS: every query inside the window came from our own draw, which " +
                         "SHOULD see our camera. No race here either."
                       : "   <-- RACE CONFIRMED: queries from other threads are reading our camera for " +
                         "player-side entities, and the guard is rewriting them.");
    }

    // ---- UNINSTALLING THE HOOK, not merely emptying it -------------------------------
    //
    // Clear() alone is NOT a cost saving, and finding that out is the point of this comment.
    // It nulls _viewer so Nearest() returns on its first line — but the bridge delegate is
    // still installed, so DistanceToCameraPostfix keeps running for EVERY root entity in the
    // render scene: ~107,000 times a second, each one reading the volatile hook field,
    // incrementing a shared static counter from a job thread (cache-line contention, and
    // racy on top), and paying a delegate dispatch, all to reach a method that immediately
    // returns its argument.
    //
    // Turning the feature off has to REMOVE the delegate, and then the postfix's own
    // `if (hook == null) return;` costs one static read and a predicted branch.
    //
    // Driven from the config poll rather than from Install(), because the knob can change at
    // any time and a hook that could only be installed at load would be exactly the shape of
    // bug this project keeps meeting: state that can only change while the thing it controls
    // is already running. SetHook is idempotent and cheap, so the poll can call it every time
    // without tracking transitions itself.
    private static bool? _hookInstalled;

    internal static void SetHook(bool wanted)
    {
        if (_hookInstalled == wanted) return;
        try
        {
            var f = Type.GetType("RttProbe.RttBridge, RttProbe")?.GetField("ViewerDistanceHook");
            if (f == null) return;                       // older bootstrap: nothing to install
            f.SetValue(null, wanted ? (Func<double, double, double, float, float>)Nearest : null);
            _hookInstalled = wanted;
            RttLog.Line(wanted
                ? "VIEWER DISTANCE: hook INSTALLED — CalculateDistanceToCamera is now postfixed for " +
                  "every root entity in the render scene."
                : "VIEWER DISTANCE: hook REMOVED — the postfix now early-outs on a null delegate. " +
                  "That is ~107,000 calls a second on renderer job threads no longer paying a " +
                  "delegate dispatch and a contended counter increment. Clearing the bubble alone " +
                  "did NOT do this; the delegate had to come out.");
        }
        catch { }
    }

    internal static bool Active => _viewer != null;

    // ---- THE REPORT ------------------------------------------------------------------
    //
    // Called from the same camera pass that renews the lease, so it runs exactly when the
    // feature is armed and stops when it is not — no separate clock to go stale.
    //
    // It reports the OVERRIDE COUNT, not merely that the hook is installed, because those are
    // different claims and this project has confused them before. "Armed" says a delegate is
    // in a field; "N of M calls overridden" says the engine actually asked us about entities
    // inside the bubble and took our answer. A window of zero overrides with a non-zero call
    // count is the interesting negative: the patch fires, the bubble is real, and nothing is
    // near it — which points at the camera position, not at the mechanism.
    private static long _reportTicks;
    private static long _lastCalls, _lastOverrides;
    private static System.Reflection.FieldInfo _fCalls, _fOverrides;
    private static bool _bridgeLooked;

    internal static void Report()
    {
        var now = Environment.TickCount64;
        if (now - _reportTicks < 15000) return;
        _reportTicks = now;

        if (!_bridgeLooked)
        {
            _bridgeLooked = true;
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            _fCalls = bridge?.GetField("ViewerDistanceCalls");
            _fOverrides = bridge?.GetField("ViewerDistanceOverrides");
            if (_fCalls == null || _fOverrides == null)
                RttLog.Line("VIEWER DISTANCE: this bootstrap has no distance counters — restart the game to " +
                            "adopt the new one. The OVERRIDE ITSELF is also part of that bootstrap, so until " +
                            "the restart viewerDistance has NO EFFECT, however it is set.");
        }
        if (_fCalls == null || _fOverrides == null) return;

        var calls = (long)_fCalls.GetValue(null);
        var over = (long)_fOverrides.GetValue(null);
        var dCalls = calls - _lastCalls;
        var dOver = over - _lastOverrides;
        _lastCalls = calls; _lastOverrides = over;

        var v = _viewer;
        RttLog.Line($"VIEWER DISTANCE: {dOver} of {dCalls} distance queries answered from the CAMERA this window " +
                    $"({over} of {calls} cumulative); bubble r={v?.R ?? 0:F0} m at " +
                    $"{v?.X ?? 0:F0},{v?.Y ?? 0:F0},{v?.Z ?? 0:F0}. " +
                    (dCalls == 0
                        ? "ZERO CALLS means the postfix is not running at all — the patch failed or the bootstrap is stale."
                        : dOver == 0
                            ? "ZERO OVERRIDES with calls flowing means the mechanism works and NOTHING IS IN THE BUBBLE — " +
                              "suspect the published position, not the patch."
                            : "Those entities are now measured from the camera, which is the one input to StreamingTag, " +
                              "the impostor swap, shadow tracking and the raytracing tags."));
    }

    // THE HOT PATH: called once per root entity in the render scene, per distance-update
    // frame, on the renderer's job threads. Everything about the ordering below is a cost
    // decision, not a style one.
    //
    // The three axis rejects run before any multiply because the overwhelming majority of
    // entities in a loaded solar system are nowhere near our bubble, and those exit after one
    // subtract and one compare. Only what survives all three pays for the squares.
    //
    // The lease is checked LAST, after the bubble test, for the same reason: an entity outside
    // the bubble gets the engine's answer whether or not our position is stale, so a stale
    // viewer cannot change any result there — and checking it first would put a clock read on
    // every entity in the scene instead of the handful inside 200 m.
    // ---- THE SWAP GUARD: THE MAIN-WORLD LOD CYCLING FIX -------------------------------
    //
    // THE BUG, in one sentence: while our nested Draw holds OUR camera in
    // CoreSystems.Settings.RenderView, any engine job that calls CalculateDistanceToCamera
    // for a PLAYER-side entity measures it against a camera 3906 km away.
    //
    // AND IT IS NOT A TRANSIENT. DistanceTagManagerComponent.OnUpdateDistanceToCamera CACHES
    // that one float per entity as DistanceRangeData, and the whole tag family downstream
    // (StreamingTag, impostor Near/Far, shadow tracking, raytracing near/far, geometry-dirty)
    // decides from nothing but the cached number. So a single poisoned read demotes an entity
    // and it STAYS demoted until something recomputes it — which is exactly the user's
    // long-standing symptom: main-world objects drop to coarse LOD and later recover.
    //
    // MEASURED: our swap is installed 12.4% OF WALL CLOCK (581 installs / 15 s, mean 3.20 ms).
    // Roughly one in eight of those reads is poisoned.
    //
    // WHY THIS DISCRIMINATOR IS EXACTLY RIGHT. _inOurRender is [ThreadStatic] and is set only
    // on the render thread inside our nested Draw. So:
    //     InOurRender == true   -> this call IS our render: our camera is the correct answer.
    //     InOurRender == false  -> a job thread (or the render thread outside our Draw). Our
    //                              camera is in the global only by accident, and the PLAYER'S
    //                              is the correct answer.
    // The global's temporary state stops mattering to anyone but us.
    //
    // THE APPROXIMATION IS DELIBERATE AND SAFE HERE. We return a CENTRE distance while the
    // engine returns a bounding-BOX distance, so we can be wrong by up to an entity's own
    // radius. Against being wrong by 3906 km that is noise, and every threshold this feeds
    // (RootStreamingDistance 200 m, impostor swap, shadow tracking) is far larger than the
    // entities it sorts.
    private static volatile bool _swapInstalled;
    private static volatile object _playerCam;      // boxed Vector3D; one reference write, cannot tear

    // Called from InstallCamera/RestoreCamera with the PLAYER'S view, captured before we
    // overwrite the global. Published as a whole boxed value for the same tear-freedom reason
    // the Viewer class exists.
    internal static void SwapOpened(Vector3D playerCamera)
    {
        _playerCam = playerCamera;
        _swapInstalled = true;
    }

    internal static void SwapClosed() => _swapInstalled = false;

    internal static float Nearest(double x, double y, double z, float engineDistance)
    {
        // FIRST, before the bubble logic: this is a correctness fix for the PLAYER'S world,
        // not a feed enhancement, so it must run even when the nearest-viewer bubble is off.
        // SPLIT THE ZERO. "0 corrections" is ambiguous between three completely different
        // worlds, and guessing between them is how this project loses evenings:
        //   no calls at all inside our window      -> the jobs never overlap our draw, so
        //                                             CalculateDistanceToCamera is NOT the
        //                                             vector and the theory is wrong here
        //   calls inside it, all on our thread     -> those are OUR draw's own queries and
        //                                             are correct; still no race
        //   calls inside it, on other threads      -> the race IS real and corrections must
        //                                             fire; a zero then means MY logic is wrong
        if (_swapInstalled)
        {
            _inWindowCalls++;
            if (WholeSceneRender.InOurRender) _inWindowOurThread++;
        }

        if (_swapInstalled && !WholeSceneRender.InOurRender && FeedConfig.FixLodCycling)
        {
            if (_playerCam is Vector3D p)
            {
                double px = x - p.X, py = y - p.Y, pz = z - p.Z;
                var corrected = (float)Math.Sqrt(px * px + py * py + pz * pz);
                if (!float.IsNaN(corrected))
                {
                    _swapCorrections++;
                    // The bubble must not then ALSO apply: this call belongs to the player's
                    // world, and granting it our bubble would be the very confusion we are
                    // fixing. Return the player's answer outright.
                    return corrected;
                }
            }
        }

        var v = _viewer;
        if (v == null) return engineDistance;

        var dx = x - v.X; if (dx > v.R || dx < -v.R) return engineDistance;
        var dy = y - v.Y; if (dy > v.R || dy < -v.R) return engineDistance;
        var dz = z - v.Z; if (dz > v.R || dz < -v.R) return engineDistance;

        var d2 = dx * dx + dy * dy + dz * dz;
        if (d2 > v.R2) return engineDistance;

        // The engine already had a closer answer — its number is a bounding-BOX distance while
        // ours is a centre distance, so for anything large it wins on merit and we must not
        // raise it.
        var e2 = (double)engineDistance * engineDistance;
        if (d2 >= e2) return engineDistance;

        if (Environment.TickCount64 > v.ExpiresAt) return engineDistance;

        var d = Math.Sqrt(d2);

        // THE CURVE (task #42). Identity inside Full; a smoothstep gain rising to FarBias
        // between Full and the boundary. Monotone in d (gain and d both non-decreasing), so
        // the curve cannot invert ordering and can never report an entity CLOSER than it is —
        // the min() safety argument is untouched, because inflating our answer only ever
        // hands the win back to the engine. The bubble test above guarantees d <= R, so t is
        // in [0,1] without a clamp. BiasM1 == 0 is the curve switched off and returns the
        // exact pre-curve answer.
        if (v.BiasM1 != 0.0 && d > v.F)
        {
            var t = (d - v.F) * v.InvSpan;
            var s = t * t * (3.0 - 2.0 * t);
            d *= 1.0 + v.BiasM1 * s;
        }

        return (float)d;
    }
}
