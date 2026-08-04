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
    // Shared readers, so the radius and LOD knobs cannot drift apart in how they reach the
    // settings. Both return null rather than throwing; callers treat null as "not on this
    // build" and say so once.
    private static object SettingsObj()
    {
        var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
        return core?.GetField("Settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }

    private static FieldInfo FloraField(object settings) =>
        settings?.GetType().GetFields(Any).FirstOrDefault(f => f.FieldType.Name == "FloraSettings");

    // The multiplier actually written into FloraSettings last time, which is what the
    // already-allocated batches were baked with. NOT _applied: that is the CONFIG value and
    // can be <=0 meaning "use the engine's", so it cannot be used as a rescale denominator.
    private static float? _lastEffective;

    // THE DISTANT-TIER BOUNDARY. Raytracing.FloraMaxDistance gates FloraSubSectorMesh
    // visibility at value*1.2; stock 250 puts the boundary at 300 m, inside the feed's
    // visible band, so merged patches rebuild/evict as the orbit drags them across it.
    // Written like every other flora knob: struct field, read-modify-WRITE BACK.
    private static float? _rtFloraOriginal;
    private static double _rtFloraApplied = double.NaN;

    private static void ApplyFloraMaxDistance()
    {
        var want = FeedConfig.FeedFloraMaxDistance;

        // RE-ASSERT AND VERIFY, never write-once. The first version wrote 1200 and latched;
        // four minutes later the monitor read the field back as 250 — the engine restores
        // this setting (it is a graphics setting, re-applied from its own source). A
        // write-once applier with a latch reports success while the value silently reverts,
        // which is the same shape as every other silent-zero in this project. So: read the
        // live value every pass, and re-write whenever it has drifted from what we asked for.
        // Cost is one boxed field read per render.
        if (Math.Abs(want - _rtFloraApplied) < 1e-9 && !FloraMaxDrifted(want)) return;
        try
        {
            var settings = SettingsObj();
            var field = settings?.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == "RaytracingSettings");
            if (field == null)
            {
                if (!_rtFloraWarned)
                {
                    _rtFloraWarned = true;
                    RttLog.Global("feedFloraMaxDistance: SettingsManager has no RaytracingSettings field — " +
                                  "the distant-flora boundary cannot be reached, this knob has NO EFFECT.");
                }
                _rtFloraApplied = want;
                return;
            }

            var box = field.GetValue(settings);
            var fm = box.GetType().GetField("FloraMaxDistance", Any);
            if (fm == null || fm.FieldType != typeof(float))
            {
                if (!_rtFloraWarned)
                {
                    _rtFloraWarned = true;
                    RttLog.Global("feedFloraMaxDistance: RaytracingSettings has no float FloraMaxDistance — " +
                                  "NO EFFECT on this build.");
                }
                _rtFloraApplied = want;
                return;
            }

            _rtFloraOriginal ??= (float)fm.GetValue(box);
            float now = want > 0 ? (float)want : _rtFloraOriginal.Value;
            fm.SetValue(box, now);
            field.SetValue(settings, box);          // struct: the write-back is the whole point

            // Log only the FIRST application; the re-assert path would otherwise print
            // every render. The rewrite counter carries the ongoing story.
            if (_rtFloraRewrites > 0) { _rtFloraApplied = want; return; }

            RttLog.Global($"DISTANT FLORA BOUNDARY: Raytracing.FloraMaxDistance " +
                          $"{_rtFloraOriginal.Value:0.#} -> {now:0.#} (visibility threshold " +
                          $"{now * 1.2f:0} m). Merged sub-sector meshes flip _isVisible at that " +
                          "distance and each flip REBUILDS or EVICTS a whole patch — the measured " +
                          "flicker. Boundary now " +
                          (now * 1.2f > FeedConfig.WholeSceneFloraMaxMetres
                              ? "BEYOND the feed's flora draw distance, so crossings happen where nothing is drawn."
                              : "STILL INSIDE the feed's flora draw distance — raise it further."));
            _rtFloraApplied = want;
        }
        catch (Exception e) { RttLog.Error("flora max distance apply", e); _rtFloraApplied = want; }
    }

    private static bool _rtFloraWarned;
    private static long _rtFloraRewrites;
    private static bool _rtFloraDriftLogged;

    // True when the live value is not what we asked for. Cheap enough to run per render.
    private static bool FloraMaxDrifted(double want)
    {
        if (want <= 0) return false;
        try
        {
            var settings = SettingsObj();
            var field = settings?.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == "RaytracingSettings");
            if (field == null) return false;
            var box = field.GetValue(settings);
            if (box.GetType().GetField("FloraMaxDistance", Any)?.GetValue(box) is not float live) return false;
            bool drifted = Math.Abs(live - want) > 0.5;
            if (drifted)
            {
                _rtFloraRewrites++;
                if (!_rtFloraDriftLogged)
                {
                    _rtFloraDriftLogged = true;
                    RttLog.Global($"DISTANT FLORA BOUNDARY: the engine RESTORED FloraMaxDistance to {live:0.#} " +
                                  $"after our write of {want:0.#} — this setting is re-applied from its own " +
                                  "source, so a write-once applier silently reverts. Now re-asserted every " +
                                  "render; the rewrite count in later lines is how often it drifts back.");
                }
            }
            return drifted;
        }
        catch { return false; }
    }

    internal static void Apply()
    {
        ApplyFloraMaxDistance();

        // FIRST, and unconditionally: the radius check below early-returns whenever the
        // radius is unchanged, which would otherwise strand the LOD knob permanently.
        ApplyLodMult();

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
            float before = _lastEffective ?? _engineOriginal.Value;
            mult.SetValue(box, now);
            field.SetValue(settings, box);

            // RETROFIT THE BATCHES THAT ARE ALREADY ALLOCATED.
            //
            // Without this the knob is only half-applied: _cullingDistance is baked at
            // allocation, so a live change leaves old batches on the old radius and new ones on
            // the new, and the boundary instances flip as sectors churn. That patchwork IS the
            // distant-flora flashing, and it is why every observation taken right after a live
            // change was measuring the change rather than the value.
            int rescaled = WorldGrids.RescaleFloraCullingDistances(before, now);
            _lastEffective = now;

            RttLog.Global(want > 0
                ? $"SCATTER RADIUS: Flora.RenderingDistanceMultiplier {before:0.###} -> {now:0.###} " +
                  "(GLOBAL — the player's world too, not just the feed). " +
                  (rescaled > 0
                      ? $"RETROFITTED {rescaled} already-allocated flora batch(es) by x{now / before:0.###} so the " +
                        "whole feed agrees immediately instead of running a patchwork of old and new radii."
                      : "No already-allocated batches were reachable to retrofit — the change applies only to " +
                        "batches allocated from here on, so give it a minute of sector cycling before judging.") +
                  " NOTE: only sectors WE own are reachable, so the PLAYER'S flora stays patchworked until it " +
                  "cycles — a fresh load is still the honest test. More resident flora is more VRAM and this " +
                  "knob has NO automatic guard: watch the PERF line's VRAM."
                : $"SCATTER RADIUS: Flora.RenderingDistanceMultiplier restored to the engine's " +
                  $"{_engineOriginal.Value:0.###}" +
                  (rescaled > 0 ? $", and {rescaled} allocated batch(es) rescaled to match." : "."));

            _applied = want;
        }
        catch (Exception e)
        {
            RttLog.Error("scatter radius apply", e);
            _applied = want;                     // never throw once per frame
        }
    }

    // ---- GLOBAL FLORA LOD DISTANCE MULTIPLIER -----------------------------------------
    //
    // WHY THIS CANNOT BE PER-PASS, measured in game 2026-08-02.
    //
    // The octree decides which instances a sector SUPPLIES outside our render, on the
    // engine's flora job schedule, reading FloraSettings.LODDistanceMultiplier as it stands
    // THEN. Our draw then filters that set inside our render. Scoping the value per pass
    // therefore guarantees the two halves disagree, and instances in the gap are
    // supplied-by-one-and-rejected-by-the-other — which flickers as the orbit shifts
    // distances slightly.
    //
    // THE A/B THAT PROVED IT (user observation, both directions):
    //     scoped 0.85 (octree 1.2 / draw 0.85)  -> finer detail, MORE popping
    //     unscoped     (octree 1.2 / draw 1.2)  -> coarser detail, LESS popping
    // Agreement reduced the popping; the detail loss was just the higher multiplier. So the
    // answer is to AGREE AT THE VALUE WE WANT rather than to abandon the value.
    //
    // GLOBAL means the PLAYER'S flora LOD changes too, exactly like worldFloraRadiusMult.
    // That is the price of there being one setting and two viewers.
    //
    // SIGN, because it is counterintuitive and has been got wrong twice: this scales the
    // MEASURED DISTANCE that LOD selection consumes.
    //     LOWER  -> plants read as CLOSER -> finer meshes -> more detail, more cost
    //     HIGHER -> plants read as FARTHER -> coarser meshes -> less detail, less cost
    // Below 1 does NOT extend range; it makes plants lie about their distance and draw
    // close-up LODs far away. That is what tanked fps at 0.5 and made everything look stuck
    // at one LOD.
    private static double _lodApplied = double.NaN;
    private static float? _lodEngineOriginal;
    private static bool _warnedLodMissing;

    private static void ApplyLodMult()
    {
        var want = FeedConfig.WorldFloraLodMult;
        if (want.Equals(_lodApplied)) return;

        try
        {
            var settings = SettingsObj();
            var field = FloraField(settings);
            if (settings == null || field == null) { _lodApplied = want; return; }

            var box = field.GetValue(settings);
            var mult = box.GetType().GetField("LODDistanceMultiplier", Any);
            if (mult == null || mult.FieldType != typeof(float))
            {
                if (!_warnedLodMissing)
                {
                    _warnedLodMissing = true;
                    RttLog.Global("worldFloraLodMult: FloraSettings has no float " +
                                  "LODDistanceMultiplier — NO EFFECT on this build.");
                }
                _lodApplied = want;
                return;
            }

            _lodEngineOriginal ??= (float)mult.GetValue(box);

            float now = want > 0 ? (float)want : _lodEngineOriginal.Value;
            mult.SetValue(box, now);
            field.SetValue(settings, box);

            RttLog.Global(want > 0
                ? $"FLORA LOD (GLOBAL): Flora.LODDistanceMultiplier {_lodEngineOriginal.Value:0.###} -> {now:0.###}. " +
                  "Both halves now agree — the octree selects instances and our draw filters them under the " +
                  "SAME value, which is what a per-pass scope could never do. LOWER = finer meshes = more " +
                  "detail and more cost. GLOBAL: the player's flora changes too."
                : $"FLORA LOD (GLOBAL): restored to the engine's {_lodEngineOriginal.Value:0.###}.");

            _lodApplied = want;
        }
        catch (Exception e)
        {
            RttLog.Error("flora lod apply", e);
            _lodApplied = want;
        }
    }

    // Report what is actually in the engine, not what we think we wrote. The distinction
    // has mattered repeatedly here: a knob that silently failed to bind looks identical to
    // a knob that bound and did nothing, and only a read-back separates them.
    // The PLAYER'S swapchain height — the only one the engine has, and therefore the
    // resolution every resolution-derived quality term is computed against, including our
    // feed's. Shared by the flora-parity maths below and by the texture streaming probe,
    // which needs it for the same reason: texel density is texels PER SCREEN PIXEL, and the
    // screen in question is never the feed's.
    //
    // Returns null rather than a guessed default — a fabricated 1080 would silently make
    // every derived figure look correct.
    internal static int? PlayerSwapchainHeight()
    {
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var sc = core?.GetField("SwapChain", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var res = sc?.GetType().GetProperty("Resolution", Any)?.GetValue(sc);
            if (res?.GetType().GetField("Y", Any)?.GetValue(res) is int h && h > 0) return h;
        }
        catch { }
        return null;
    }

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
            // (PlayerSwapchainHeight below is the shared reader.)
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
                if (PlayerSwapchainHeight() is int h && h > 0)
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
