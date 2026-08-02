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
    internal static void Publish(Vector3D centre, double radius)
    {
        if (radius <= 0.0) { Clear(); return; }
        _viewer = new Viewer
        {
            X = centre.X, Y = centre.Y, Z = centre.Z,
            R = radius, R2 = radius * radius,
            ExpiresAt = Environment.TickCount64 + LeaseMs,
        };
    }

    internal static void Clear() => _viewer = null;

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
    internal static float Nearest(double x, double y, double z, float engineDistance)
    {
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

        return (float)Math.Sqrt(d2);
    }
}
