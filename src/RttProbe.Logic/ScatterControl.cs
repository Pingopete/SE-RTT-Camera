using System;
using System.Linq;
using System.Reflection;

namespace RttProbe;

// THE SCATTER SPAWN RADIUS — the global half of the scatter control surface.
//
// WHAT THIS OWNS. Exactly one engine value: Keen.VRage.Render.Data.FloraSettings
// .RenderingDistanceMultiplier, reached through CoreSystems.Settings._flora. It is the
// multiplier behind the reported "strict circular radius around the camera where objects
// spawn", and its only consumers are the two ComputeCullingDistance methods:
//
//     float InstanceBatch.ComputeCullingDistance(float d)
//         => CoreSystems.Settings.Flora.RenderingDistanceMultiplier * d;
//
// — the whole body, verified in IL. PlanetFloraEntityRenderComponent has the same one-liner.
//
// WHY IT IS NOT SCOPED PER PASS LIKE EVERYTHING ELSE IN ScopeScatter. The call chain is
// InstanceBatch.Initialize <- InstanceSparseOctree.AllocateInstanceBatch <-
// AddInstanceInInstanceBatches: the radius is read ONCE, when a flora instance batch is
// allocated, and stored on the batch. Allocation happens while sectors stream in — during
// the flora sector update, not during our nested Draw. A scope that installs a value for
// the duration of our render would never be observed by this consumer at all. So the value
// is set globally and left set, and that is a property of the engine's design rather than a
// shortcut on our side.
//
// TWO THINGS THAT WILL OTHERWISE READ AS "NO EFFECT":
//   1. It is not retroactive. Flora batches already allocated keep the radius they were
//      born with. Raising the multiplier changes the world only as sectors cycle, over a
//      minute or two — so a verdict taken five seconds after the edit grades nothing.
//   2. It multiplies a PER-DEFINITION base distance, so every flora layer scales together.
//      There is no separate near/far control at this level.
//
// SAFETY. This is global, unguarded, and costs VRAM — three facts that belong together. The
// user's standing choice for world-residency knobs is "warn loudly, never act", so there is
// no cap and no automatic backoff here; the loud log at the apply site IS the mechanism. A
// CTD on 2026-08-02 came from this exact class of knob left high after a diagnostic sweep.
internal static class ScatterControl
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // The engine's own value, captured the first time we ever change it. Kept for the whole
    // process lifetime so "back to -1" restores what shipped rather than whatever the
    // previous edit happened to leave behind.
    private static float? _engineOriginal;

    // What we last wrote, so the common case (config unchanged) costs one double compare.
    private static double _applied = -1;

    private static bool _warnedMissing;

    // Idempotent and cheap. Called from the render path rather than a timer because the
    // knob only matters while a feed is looking at a world, and because a global mutation
    // driven off a background tick would be harder to attribute in the log.
    internal static void Apply()
    {
        var want = FeedConfig.WorldFloraRadiusMult;
        if (Math.Abs(want - _applied) < 1e-9) return;

        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (settings == null) return;

            var field = settings.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == "FloraSettings");
            if (field == null)
            {
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    RttLog.Global("worldFloraRadiusMult: SettingsManager has no FloraSettings field — " +
                                  "the scatter spawn radius cannot be reached on this build and this knob " +
                                  "will have NO EFFECT. Nothing else is affected.");
                }
                _applied = want;                 // do not retry every frame
                return;
            }

            // Struct field: GetValue hands back a COPY, so it has to be written back. Same
            // shape as ScopeSetValues, and getting it wrong here would look exactly like
            // "the knob does nothing" — which is the failure this project keeps meeting.
            var box = field.GetValue(settings);
            var mult = box.GetType().GetField("RenderingDistanceMultiplier", Any);
            if (mult == null || mult.FieldType != typeof(float))
            {
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    RttLog.Global("worldFloraRadiusMult: FloraSettings has no float " +
                                  "RenderingDistanceMultiplier — NO EFFECT on this build.");
                }
                _applied = want;
                return;
            }

            _engineOriginal ??= (float)mult.GetValue(box);

            float now = want > 0 ? (float)want : _engineOriginal.Value;
            mult.SetValue(box, now);
            field.SetValue(settings, box);

            RttLog.Global(want > 0
                ? $"SCATTER RADIUS: Flora.RenderingDistanceMultiplier {_engineOriginal.Value:0.###} -> {now:0.###} " +
                  "(GLOBAL — the player's world too, not just the feed). This is the multiplier in " +
                  "InstanceBatch.ComputeCullingDistance, baked into each flora batch AT ALLOCATION, so it " +
                  "applies only to batches allocated from now on: give it a minute or two of sector cycling " +
                  "before judging it, and do not read the first few seconds as a null result. More resident " +
                  "flora is more VRAM and this knob has NO automatic guard — watch the PERF line's VRAM."
                : $"SCATTER RADIUS: Flora.RenderingDistanceMultiplier restored to the engine's " +
                  $"{_engineOriginal.Value:0.###}. Batches allocated while it was raised keep their larger " +
                  "radius until they cycle out.");

            _applied = want;
        }
        catch (Exception e)
        {
            RttLog.Error("scatter radius apply", e);
            _applied = want;                     // never throw once per frame
        }
    }

    // Report what is actually in the engine, not what we think we wrote. The distinction
    // has mattered repeatedly here: a knob that silently failed to bind looks identical to
    // a knob that bound and did nothing, and only a read-back separates them.
    internal static string Describe()
    {
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var settings = core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var field = settings?.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == "FloraSettings");
            if (field == null) return "flora settings unreachable";

            var box = field.GetValue(settings);
            object Get(string n) => box.GetType().GetField(n, Any)?.GetValue(box);

            // THE SWAPCHAIN TERM, and why it is printed next to the flora numbers rather
            // than filed under diagnostics. LODSetup.Compose computes
            //
            //     GlobalFloraDistanceMult = MathF.Min(1, 1080 / SwapChain.Resolution.Y)
            //                               * Flora.LODDistanceMultiplier
            //
            // and SwapChain is the PLAYER'S swapchain — the only one the engine has. Our
            // feed renders at its own size and never gets a vote, so above 1080p the engine
            // scales the FEED'S flora LOD distance down by a ratio derived from a resolution
            // the feed does not have. That is the single-viewer disease in the LOD system.
            //
            // The printed effective/want pair is the whole point: `want` is the multiplier
            // that would give the feed the treatment its OWN height deserves, so it is the
            // value to put in wholeSceneFloraLodMult, read off rather than guessed at.
            string swap = "swapchain unreadable";
            try
            {
                var sc = core.GetField("SwapChain", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var res = sc?.GetType().GetProperty("Resolution", Any)?.GetValue(sc);
                var y = res?.GetType().GetField("Y", Any)?.GetValue(res);
                if (y is int h && h > 0)
                {
                    float engineTerm = Math.Min(1f, 1080f / h);
                    float feedTerm = Math.Min(1f, 1080f / Math.Max(1, FeedConfig.WholeSceneHeight));
                    float lodMult = (float)(Get("LODDistanceMultiplier") ?? 1f);
                    swap = $"playerSwapchainY={h} engineFloraTerm={engineTerm:0.###} " +
                           $"(effective floraLodDist x{engineTerm * lodMult:0.###}) " +
                           $"feedWouldWant x{feedTerm * lodMult:0.###} " +
                           $"=> wholeSceneFloraLodMult {(engineTerm > 0 ? feedTerm / engineTerm * lodMult : 0):0.###} for parity";
                }
            }
            catch { }

            return $"radiusMult={Get("RenderingDistanceMultiplier")} " +
                   $"lodMult={Get("LODDistanceMultiplier")} " +
                   $"fadePct={Get("FadeDistancePercentage")} " +
                   $"frozen={Get("FreezeEntities")} | {swap}";
        }
        catch { return "flora settings read failed"; }
    }
}
