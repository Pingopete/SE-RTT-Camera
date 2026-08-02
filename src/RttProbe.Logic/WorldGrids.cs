using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Keen.VRage.Library.Mathematics;

namespace RttProbe;

// THE WORLD WALK — phase J's missing instrument.
//
// Every render this project has ever done sat ~100 m from the player, because the orbit
// centres on the tagged panel's own grid by construction. That is precisely why nothing has
// ever disappeared from a feed, and why docs/open-question-remote-streaming.md has been
// unable to close: "we saw only planets and stars at 50 km" was consistent with BOTH
// "streaming dropped it" and "there was nothing there to begin with".
//
// This file supplies the two pieces that let the question be asked properly:
//
//   DumpGrids()     — an inventory: every grid in the scene, with position and distance,
//                     so a target can be CHOSEN rather than guessed at.
//   ResolveAnchor() — turn a name from that inventory into an orbit centre.
//
// Read-only with respect to the engine. It enumerates and reflects; it never writes engine
// state, so it cannot participate in the shared-state bug class that has cost this project
// eight instances so far.
internal static class WorldGrids
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string ReportPath = Path.Combine(RttLog.OutDir, "world-grids.txt");

    internal readonly struct GridInfo
    {
        public readonly string Name;
        public readonly Vector3D Position;
        public readonly double Extent;
        public readonly object Entity;

        public GridInfo(string name, Vector3D position, double extent, object entity)
        { Name = name; Position = position; Extent = extent; Entity = entity; }
    }

    // RESOLUTION IS CACHED, AND THE CACHE IS KEYED BY THE QUERY.
    //
    // Keying on the anchor string means an edited config invalidates the cache
    // automatically rather than relying on someone remembering to clear it. A resolved HIT
    // is then final: the position is refreshed each call from the CACHED ENTITY (a handful
    // of reflection reads), never by re-walking the scene. The first version re-walked all
    // 34k entities every 5 s on the tick thread as its idea of freshness — the user asked,
    // reasonably, why a discovery pass was running continuously for a camera that had
    // already found its target. Only a dead entity (position read fails) re-arms the walk,
    // and misses retry on a slow cadence in case the target simply has not spawned yet.
    private static string _cachedFor;
    private static GridInfo? _cached;
    private static long _lastWalkTicks;
    private static readonly HashSet<string> _missLogged = new(StringComparer.OrdinalIgnoreCase);

    private const int MissRetryMs = 30000;

    // Returns the anchor for the configured value, or null to mean "stay on our own grid".
    // Two forms: "x,y,z" is a fixed world position (no walk, no entity — the way to aim at
    // a planet surface point where no grid exists), anything else is a case-insensitive
    // substring of a grid display name. A miss is deliberately NOT an error: the target may
    // simply not have spawned yet, and staying on our own grid is the safe behaviour.
    internal static GridInfo? ResolveAnchor(object anyEntityInScene)
    {
        var want = FeedConfig.OrbitAnchor;
        if (string.IsNullOrWhiteSpace(want)) { _cachedFor = null; _cached = null; return null; }

        // Coordinate anchor. Parsed every call (it is three TryParses) so a live edit of
        // the numbers moves the camera on the next tick with no cache to invalidate.
        if (TryParseCoords(want, out var fixedPos))
        {
            if (_cachedFor != want)
            {
                _cachedFor = want; _cached = null;
                RttLog.Line($"ORBIT ANCHOR: fixed coordinates {fixedPos.X:F0},{fixedPos.Y:F0},{fixedPos.Z:F0}. " +
                            "The orbit centres on this world position — no grid needed. Edit the numbers " +
                            "live to nudge the camera; orbitHeight/orbitRadius tune the shot as usual.");
            }
            return new GridInfo("(coordinates)", fixedPos, 0, null);
        }

        // Name anchor, already resolved: refresh the position from the cached entity and
        // return. No walk. If the entity died, fall through and let the walk re-arm.
        if (_cachedFor == want && _cached.HasValue && _cached.Value.Entity != null)
        {
            try
            {
                var fresh = Describe(_cached.Value.Entity);
                if (fresh.HasValue) { _cached = fresh; return fresh; }
            }
            catch { }
            RttLog.Line($"ORBIT ANCHOR: \"{_cached.Value.Name}\" no longer yields a position — the grid " +
                        "is gone or was rebuilt. Re-walking the scene for the name.");
            _cached = null;
        }

        // Unresolved (first attempt, or a prior miss). Walk at most every MissRetryMs.
        var now = Environment.TickCount64;
        if (_cachedFor == want && !_cached.HasValue && now - _lastWalkTicks < MissRetryMs)
            return null;

        try
        {
            _lastWalkTicks = now;
            var hit = Enumerate(anyEntityInScene)
                .Where(g => g.Name != null &&
                            g.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                // Largest match wins. Names are not unique and a station usually shares its
                // name with debris split off it; the big one is what a human meant.
                .OrderByDescending(g => g.Extent)
                .Select(g => (GridInfo?)g)
                .FirstOrDefault();

            if (hit == null)
            {
                // Log a miss ONCE per distinct query, not once per attempt — this runs on
                // the tick and would otherwise fill the log.
                if (_missLogged.Add(want))
                    RttLog.Line($"ORBIT ANCHOR: no grid matched \"{want}\". The feed stays on its own " +
                                "grid; retrying every 30 s in case it spawns. Run worldGridSurvey = 1 to " +
                                "list what actually exists — the name must be a substring of a grid's " +
                                "DISPLAY NAME, matched case-insensitively. \"x,y,z\" aims at coordinates instead.");
                _cachedFor = want; _cached = null;
                return null;
            }

            RttLog.Line($"ORBIT ANCHOR: \"{want}\" -> \"{hit.Value.Name}\" at " +
                        $"{hit.Value.Position.X:F0},{hit.Value.Position.Y:F0},{hit.Value.Position.Z:F0} " +
                        $"(extent {hit.Value.Extent:F0} m). Resolved ONCE — the position now tracks the " +
                        "cached entity and the scene is not walked again unless the grid dies.");

            _cachedFor = want; _cached = hit;
            return hit;
        }
        catch (Exception e) { RttLog.Error("orbit anchor resolve", e); return null; }
    }

    private static bool TryParseCoords(string s, out Vector3D pos)
    {
        pos = default;
        var parts = s.Split(',');
        if (parts.Length != 3) return false;
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;
        pos = new Vector3D(x, y, z);
        return true;
    }

    // The one-shot inventory. Triggered from the config via
    // FeedConfig.TakeWorldGridSurveyRequest().
    //
    // TWO SECTIONS: planets, then grids annotated with their nearest planet. The
    // annotation exists because "find a grid on planet X" was the first real question
    // asked of this file, and a bare grid list cannot answer it — 44 of the 45 grids in
    // this save read as "in space" until you know where the OTHER planets are.
    internal static void DumpGrids(object anyEntityInScene)
    {
        try
        {
            var player = CameraFeed.SubjectCentreCache;
            var sb = new StringBuilder();
            sb.AppendLine("=== WORLD SURVEY ===");
            sb.AppendLine("Written by WorldGrids.DumpGrids (worldGridSurvey = 1).");
            sb.AppendLine("Distances are from the feed's current subject centre (the panel's own grid),");
            sb.AppendLine("which is the best proxy available here for 'where the player is'.");
            sb.AppendLine("Use the grid NAME as orbitAnchor (substring, case-insensitive), or \"x,y,z\"");
            sb.AppendLine("to aim at raw coordinates (e.g. a planet surface point with no grid on it).");
            sb.AppendLine();

            var planets = EnumeratePlanets(anyEntityInScene)
                .OrderBy(p => (p.Position - player).Length())
                .ToList();

            sb.AppendLine($"--- {planets.Count} planet-like bodies ---");
            sb.AppendLine($"{"distance",12}  {"radius",9}  {"position",-34}  name");
            sb.AppendLine(new string('-', 100));
            foreach (var p in planets)
            {
                var d = (p.Position - player).Length();
                var pos = $"{p.Position.X:F0}, {p.Position.Y:F0}, {p.Position.Z:F0}";
                var r = p.Extent > 0 ? $"{p.Extent:F0}m" : "?";
                sb.AppendLine($"{d,11:F0}m  {r,9}  {pos,-34}  {p.Name}");
            }

            var all = Enumerate(anyEntityInScene)
                .OrderBy(g => (g.Position - player).Length())
                .ToList();

            sb.AppendLine();
            sb.AppendLine($"--- {all.Count} grid(s) ---");
            sb.AppendLine($"{"distance",12}  {"extent",8}  {"position",-34}  name  [nearest planet]");
            sb.AppendLine(new string('-', 100));
            foreach (var g in all)
            {
                var d = (g.Position - player).Length();
                var pos = $"{g.Position.X:F0}, {g.Position.Y:F0}, {g.Position.Z:F0}";
                // Nearest planet by SURFACE distance where a radius is known — centre
                // distance ranks a big close planet below a small far one.
                string near = "";
                if (planets.Count > 0)
                {
                    var np = planets.OrderBy(p =>
                        (g.Position - p.Position).Length() - Math.Max(0, p.Extent)).First();
                    var surf = (g.Position - np.Position).Length() - Math.Max(0, np.Extent);
                    near = $"  [{np.Name}: {surf / 1000.0:F1} km above nominal surface]";
                }
                sb.AppendLine($"{d,11:F0}m  {g.Extent,7:F0}m  {pos,-34}  {g.Name}{near}");
            }

            AppendManagedAreas(sb, anyEntityInScene, player);
            AppendObservers(sb, anyEntityInScene, player);
            AppendSpaceProbe(sb, anyEntityInScene);

            Directory.CreateDirectory(RttLog.OutDir);
            File.WriteAllText(ReportPath, sb.ToString());
            RttLog.Line($"WORLD SURVEY: {planets.Count} planet-like bodies and {all.Count} grid(s) " +
                        $"written to {ReportPath}.");
        }
        catch (Exception e) { RttLog.Error("world grid survey", e); }
    }

    // MANAGED WORLD AREAS — the content that is in the SAVE but not in the WORLD.
    //
    // The grid walk above can only see instantiated entities, and the user's observation
    // that forced this section was sharp: the asteroid station 273 km out in SPACE was
    // loaded, while the planet surface showed nothing outside the player's bubble. Surface
    // POIs go through the managed-area system — serialized bundles behind a spatial
    // trigger, spawned when something with the right tag walks in. So "where are the
    // grids outside my bubble" is answered HERE, not by the entity walk:
    // ManagedWorldAreaSessionComponent._areas holds every area with its Name, its
    // BoundingBoxD and its LoadingState, whether or not its content currently exists.
    //
    // This is also the tier-2 recon for goal 10: each area carries the IEntityTrigger the
    // player trips, and the session component exposes the ISpatialTriggerSystem those
    // triggers register with — the exact surface a camera entity must reach to make the
    // world materialize around it.
    private static void AppendManagedAreas(StringBuilder sb, object anyEntityInScene, Vector3D player)
    {
        try
        {
            // The panel's entity is a BLOCK entity: it carries CubeBlockComponent, and the
            // grid component lives on the GRID entity behind block.Grid. Asking the panel
            // entity for CubeGridComponent directly returns null — which is how the first
            // run of this section reported "no Session reachable" while the world was fine.
            //
            // And the block is NOT in the panel entity's own Components array either (second
            // false "NULL"): TryGet<T> evidently searches beyond the array — it is the
            // mechanism CameraFeed.WorldPositionOf uses successfully on this same entity
            // every tick — so for THIS hop the generic dance is the proven road, not the
            // array walk. The array walk stays right for scene-wide enumeration, where
            // TryGet was the one that silently failed. Neither mechanism is universally
            // correct; each has one context where it is the only one that works.
            var tBlock = Type.GetType(
                "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
            var grid = ComponentOf(anyEntityInScene, "CubeGridComponent")
                       ?? Prop(TryGetViaGeneric(anyEntityInScene, tBlock), "Grid");
            var session = Prop(grid, "Session");
            if (session == null)
            {
                sb.AppendLine("\n--- managed world areas: no Session reachable from the panel's grid ---");
                sb.AppendLine($"    grid component: {(grid == null ? "NULL" : grid.GetType().Name)}");
                return;
            }

            // Find the session component by NAME across whatever collection the Session
            // exposes — the generic Get<T> dance is exactly what silently failed for grid
            // components, so it is not trusted here either.
            // Session.SessionComponents is an ENTITY, not a collection — session components
            // are ordinary components riding a dedicated entity (IL: `Entity
            // <SessionComponents>k__BackingField`). Two earlier attempts enumerated it as a
            // sequence and found nothing; the right move is the same Components-array walk
            // used for every other component in this file.
            object mwa = null;
            var scEntity = Prop(session, "SessionComponents");
            if (scEntity != null && _fComponents?.GetValue(scEntity) is IEnumerable scComps)
                foreach (var c in scComps)
                {
                    if (c == null) continue;
                    var n2 = c.GetType().Name;
                    // Exact core name preferred; any ManagedWorldArea-marked component
                    // accepted as fallback (a game-side subclass would still carry it).
                    if (n2 == "ManagedWorldAreaSessionComponent") { mwa = c; break; }
                    if (mwa == null && n2.Contains("ManagedWorldArea", StringComparison.Ordinal)
                                    && Prop(c, "_areas") != null)
                        mwa = c;
                }
            if (mwa == null)
            {
                // One diagnostic dump instead of a guess: name the session's members so the
                // next attempt is a lookup. The session type is stable; this fires once.
                sb.AppendLine("\n--- managed world areas: ManagedWorldAreaSessionComponent NOT FOUND on the session ---");
                // Name the components actually present on the session entity, so the next
                // attempt is a lookup instead of another guess-reload — reloads are priced
                // by the VRAM ratchet now, not free.
                try
                {
                    var names = new List<string>();
                    if (scEntity != null && _fComponents?.GetValue(scEntity) is IEnumerable cs2)
                        foreach (var item in cs2)
                        { if (names.Count >= 400) break; names.Add(item?.GetType().Name ?? "null"); }
                    // 60 was the original cap and the list hit it EXACTLY, making "not in
                    // the list" and "past the cap" indistinguishable — a truncation that
                    // reads as an answer, the same trap as head-limited greps. 400 is
                    // comfortably above any plausible session-component count.
                    sb.AppendLine("    session-entity components: " + string.Join(", ", names.Distinct()));
                }
                catch (Exception e2) { sb.AppendLine("    session entity unreadable: " + e2.Message); }
                return;
            }

            var areas = Prop(mwa, "_areas") as IEnumerable;
            var loadAll = Prop(mwa, "_loadAll");
            sb.AppendLine($"\n--- managed world areas (content in the SAVE, spawned by trigger; _loadAll={loadAll}) ---");
            if (areas == null) { sb.AppendLine("    _areas not readable"); return; }

            sb.AppendLine($"{"distance",12}  {"state",-14}  {"centre",-34}  name");
            sb.AppendLine(new string('-', 100));
            int n = 0;
            foreach (var a in areas)
            {
                if (a == null) continue;
                n++;
                var name = Prop(a, "Name")?.ToString() ?? "(unnamed)";
                var state = Prop(a, "_state")?.ToString() ?? "?";
                var bounds = Prop(a, "Bounds");
                var centre = bounds == null ? (Vector3D?)null
                    : (Prop(bounds, "Center") is Vector3D c ? c
                       : Prop(bounds, "Min") is Vector3D mn && Prop(bounds, "Max") is Vector3D mx
                         ? (mn + mx) * 0.5 : (Vector3D?)null);
                var pos = centre.HasValue
                    ? $"{centre.Value.X:F0}, {centre.Value.Y:F0}, {centre.Value.Z:F0}" : "?";
                var dist = centre.HasValue ? $"{(centre.Value - player).Length():F0}m" : "?";
                sb.AppendLine($"{dist,12}  {state,-14}  {pos,-34}  {name}");
            }
            sb.AppendLine($"{n} area(s).");
        }
        catch (Exception e)
        {
            sb.AppendLine($"\n--- managed world areas: FAILED ({e.GetType().Name}: {e.Message}) ---");
        }
    }

    // MATERIALIZE ONE MANAGED AREA — the goal-10 tier-2 pathfinder.
    //
    // Calls the engine's own ManagedWorldArea.TryLoad() on the named area: the exact method
    // its spatial trigger invokes when a player walks in, so the content spawns by the
    // supported path with all its own lifecycle. This is deliberately NOT a new trigger
    // registration yet — it answers the prior question: does the load path work at all when
    // something other than a player's proximity asks for it? If yes, the continuous version
    // (a trigger volume riding the feed camera) has a proven foundation; if it faults, we
    // learned that BEFORE building machinery on top of it.
    //
    // The mutation runs where the request is consumed — the panel-locate tick, the same
    // context the engine's own trigger callbacks use for their load kicks (TryLoad hands the
    // real work to an async task internally either way).
    internal static void LoadAreaByName(object anyEntityInScene, string wantName)
    {
        try
        {
            // SERVER-CAPTURED AREAS ONLY. The area list reachable from the panel's grid is
            // the CLIENT mirror, and TryLoad on a client-scene area throws
            // KeyNotFoundException from Scene.FinishBefore<SpawnSyncPoint> — it crashed the
            // game twice on 2026-08-01 (loading is a server concern; only the server scene
            // registers the spawn sync point). The bootstrap captures the server-side
            // (area, session) pairs at world load via its OnRegistered patch; this loader
            // refuses to fall back to the client path under any circumstance.
            //
            // Bridge fields are read by REFLECTION, not direct reference: a new logic
            // running against an old bootstrap must degrade to this log line, not die with
            // MissingFieldException the first time the method JITs.
            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            var regsField = bridge?.GetField("ManagedAreaRegistrations");
            var lockField = bridge?.GetField("ManagedAreaLock");
            if (regsField == null || lockField == null)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": this bootstrap predates the server-side area " +
                            "capture. RESTART THE GAME to load the new bootstrap — the client-side " +
                            "fallback is disabled because it is a confirmed crash.");
                return;
            }

            // EVERY matching candidate, from EVERY captured session — then choose by PROOF.
            //
            // The "use the last batch's session" heuristic picked an area whose scene threw
            // the same KeyNotFoundException (CTD #3): OnRegistered fires for the client
            // mirror's areas too, and which batch lands last is an accident of load order.
            // Scene job tables (_jobSystemsIndex / _jobGroupToIndex) are sealed at scene
            // construction, so whether a given area is LOADABLE is a readable fact: its
            // scene's table either contains ManagedWorldArea+SpawnSyncPoint or it does not.
            // Read the dictionaries and only ever call TryLoad where the key exists — the
            // crash becomes a log line saying which scenes were probed and what they held.
            var candidates = new System.Collections.Generic.List<object>();
            int captured;
            var regs = (System.Collections.Generic.List<object[]>)regsField.GetValue(null);
            lock (lockField.GetValue(null))
            {
                captured = regs.Count;
                foreach (var pair in regs)
                {
                    var n = Prop(pair[0], "Name")?.ToString();
                    if (n != null && n.IndexOf(wantName, StringComparison.OrdinalIgnoreCase) >= 0)
                        candidates.Add(pair[0]);
                }
            }

            if (captured == 0)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": the bootstrap has captured NO area registrations. " +
                            "Registrations fire during world load — if the game started with the new " +
                            "bootstrap and a world is running, the patch failed; check the boot log for " +
                            "'Patched ManagedWorldArea.OnRegistered'.");
                return;
            }
            if (candidates.Count == 0)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": no captured area matched ({captured} registration(s) " +
                            "held). The survey's managed-areas section lists the valid names.");
                return;
            }

            var tSync = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldArea+SpawnSyncPoint, VRage.Core.Game");
            object area = null;
            var probeReport = new StringBuilder();
            foreach (var cand in candidates)
            {
                var scene = Prop(Prop(Prop(cand, "Session"), "Entity"), "Scene");
                bool loadable = false;
                if (scene != null && tSync != null)
                {
                    var sys = Prop(scene, "_jobSystemsIndex") as System.Collections.IDictionary;
                    var grp = Prop(scene, "_jobGroupToIndex") as System.Collections.IDictionary;
                    loadable = (sys?.Contains(tSync) ?? false) || (grp?.Contains(tSync) ?? false);
                    probeReport.Append($"\n    scene #{scene.GetHashCode():x8}: " +
                        $"jobSystems={(sys?.Contains(tSync) == true ? "HAS" : "lacks")} " +
                        $"jobGroups={(grp?.Contains(tSync) == true ? "HAS" : "lacks")} SpawnSyncPoint " +
                        $"(state={Prop(cand, "_state")})");
                }
                else probeReport.Append($"\n    scene unreadable for one candidate " +
                                        $"(scene={(scene == null ? "null" : "ok")}, syncType={(tSync == null ? "NOT FOUND" : "ok")})");
                if (loadable && area == null) area = cand;
            }

            if (area == null)
            {
                RttLog.Line($"LOAD AREA \"{wantName}\": {candidates.Count} candidate(s) across the captured " +
                            "sessions, and NO scene's job table contains ManagedWorldArea+SpawnSyncPoint — " +
                            "TryLoad would throw in every one of them (this exact throw has cost three CTDs " +
                            "today, so it is refused). Scenes probed:" + probeReport +
                            "\n    CONCLUSION if all scenes lack it: this session type was built without the " +
                            "area-load job group, and materialization needs a different route (the area's own " +
                            "spatial trigger, or the offloading component's LoadAsync).");
                return;
            }
            // THE CALL IS REFUSED. Read this before re-enabling it.
            //
            // With the right scene chosen, TryLoad DID work — no crash, state went
            // Unloaded -> Loading — and then the SIM THREAD DEADLOCKED while the render
            // thread kept running perfectly (cadence counters climbing, config poll
            // answering, game "frozen but responding" for the user).
            //
            // The IL says why. TryLoad is:
            //     var t = TryStartLoading(false); SkipWait(t);
            //     if (!t.IsCompleted) Session.Entity.Scene.FinishBefore<SpawnSyncPoint>(ref t);
            // and the load task itself parks on ContinueOnDCS<SpawnSyncPoint>(task, scene).
            // FinishBefore INSERTS A BLOCKING TASK into the scene scheduler's dependency
            // graph at that job group. Done from inside the sim frame that is a normal
            // ordering constraint; done from our panel tick it is a dependency the pump
            // never satisfies, and the sim stops.
            //
            // So the seat matters even when the scene is right. Two legitimate routes
            // remain, and both are the ORIGINAL tier-2 plan rather than this shortcut:
            //   1. a spatial trigger volume riding the feed camera, so the engine's OWN
            //      trigger callback fires the load from its own correct context;
            //   2. a Harmony hook on a method that genuinely runs inside the sim update,
            //      issuing the deferred request from there.
            // Four incidents (3 CTDs + this freeze) came from calling engine world
            // mutation off-thread. The probe above stays because it is READ-ONLY and it
            // is what makes route 1 verifiable; the call does not.
            RttLog.Line($"LOAD AREA \"{wantName}\": scene probe:{probeReport}" +
                        "\n    -> CALL REFUSED. TryLoad from this thread deadlocks the sim: it plants a " +
                        "blocking task in the scene scheduler (FinishBefore<SpawnSyncPoint>) that only the " +
                        "sim pump can clear. Verified 2026-08-01 — render thread stayed alive while the " +
                        "world froze. Materialization needs a spatial trigger on the camera, or a hook " +
                        "inside the sim update. See the comment at this line.");
            return;
#pragma warning disable CS0162 // unreachable — kept so the working call sequence is not lost


            var name = Prop(area, "Name")?.ToString();
            var state = Prop(area, "_state")?.ToString();
            if (string.Equals(state, "Loaded", StringComparison.OrdinalIgnoreCase))
            {
                RttLog.Line($"LOAD AREA: \"{name}\" is already Loaded — nothing to do.");
                return;
            }

            var tryLoad = area.GetType().GetMethod("TryLoad", Any, null, Type.EmptyTypes, null);
            if (tryLoad == null)
            {
                RttLog.Line($"LOAD AREA: \"{name}\" has no TryLoad() — engine shape changed; " +
                            "candidates: " + string.Join(", ", area.GetType()
                                .GetMethods(Any).Where(m => m.Name.Contains("Load"))
                                .Select(m => m.Name).Distinct()));
                return;
            }

            RttLog.Line($"LOAD AREA: \"{name}\" state={state} -> calling the engine's TryLoad(). " +
                        "This is the same path its own spatial trigger fires; content spawns " +
                        "asynchronously. Watch the feed if the orbit anchor is parked on this area.");
            tryLoad.Invoke(area, null);
            RttLog.Line($"LOAD AREA: TryLoad() returned for \"{name}\" — state now " +
                        $"{Prop(area, "_state")}. The spawn completes async; the area's own " +
                        "trigger/lifecycle owns it from here, including unloading it later if " +
                        "its rules say so.");
#pragma warning restore CS0162
        }
        catch (Exception e) { RttLog.Error($"load area \"{wantName}\"", e); }
    }

    // PER-BODY CLIPMAP CAMERA — the terrain fix.
    //
    // Called from the bootstrap's prefix on VoxelRenderUpdateSessionComponent.UpdateClipmap,
    // once per voxel body per frame. Returns a replacement boxed WorldTransform, or null to
    // leave the engine's choice alone.
    //
    // THE RULE, and it is designed so the player can NEVER lose:
    //     take over a body only when the feed camera is closer to it than the player is,
    //     AND the player is further away than MinPlayerDistance.
    // Both conditions, always. A body the player is anywhere near keeps the player's camera,
    // full stop. What we take over is terrain the player is provably not looking at.
    //
    // WHY THIS IS NOT THE TUG-OF-WAR. Swapping RenderSettings.CameraTransform would be: one
    // global, two writers, both directions rebuilt every round — the same shape as the LOD
    // popping bug. This is the opposite. Every clipmap still receives exactly ONE camera per
    // frame; we only choose WHICH viewer that is, per body, and the choice is stable because
    // it is a distance comparison rather than a race. Two bodies, two viewers, no contention.
    //
    // KNOWN LIMITATION, stated up front: when the player and the camera are near the SAME
    // body (a camera 20 km away on the player's own planet), both distances are ~the body
    // radius, the rule declines, and that terrain stays coarse. One clipmap cannot have two
    // centres. Same-planet remote detail needs the per-feed clipmap build; this fixes the
    // cross-body case, which is the headline "camera on another world" product.
    private static int _clipmapOverrides, _clipmapCalls;
    private static long _clipmapLogTicks;
    private static string _lastOverrideDesc = "";

    internal static object ChooseClipmapCamera(object renderComponent, object boxedTransform)
    {
        if (!FeedConfig.PerBodyClipmapCamera || boxedTransform == null) return null;
        _clipmapCalls++;
        try
        {
            // Where the feed camera is. SubjectCentreCache is the orbit CENTRE, published by
            // the tick; the eye orbits within tens of metres of it, which is far below any
            // distance this decision turns on.
            var feedPos = CameraFeed.SubjectCentreCache;
            if (feedPos.LengthSquared() <= 1.0) return null;      // no feed target yet

            var fPos = boxedTransform.GetType().GetField("Position", Any);
            if (fPos?.GetValue(boxedTransform) is not Vector3D playerPos) return null;

            // The body's origin, via its clipmap's local-to-world. For a planet this is the
            // CENTRE, so both distances are measured to the same reference and the
            // comparison stays meaningful (a surface dweller sits ~radius from it).
            var clip = Prop(renderComponent, "Clipmap");
            var l2w = clip == null ? null : Prop(clip, "LocalToWorld");
            if (l2w == null) return null;
            var fBodyPos = l2w.GetType().GetField("Position", Any);
            if (fBodyPos?.GetValue(l2w) is not Vector3D bodyPos) return null;

            var dPlayer = (playerPos - bodyPos).Length();
            var dFeed   = (feedPos   - bodyPos).Length();

            // A CLEAR MARGIN, not merely "nearer". The first version used dFeed < dPlayer and
            // immediately took over asteroids where both viewers were 150 km away and the
            // difference was metres — a near-tie is noise, not intent, and overriding on it
            // churns a clipmap for no benefit. Requiring the camera to be at least twice as
            // close makes the decision stable and obviously-correct: the cross-body case
            // clears it by orders of magnitude (61 km vs 4,000 km), a tie cannot.
            if (dFeed * 2.0 >= dPlayer) return null;
            if (dPlayer < FeedConfig.ClipmapMinPlayerDistance) return null;   // player too close to risk it

            // AND the camera must actually be NEAR this body. "Closer than the player" is not
            // enough on its own: with the player 3,900 km away, a rock 561 km from the camera
            // still qualified, and the first armed run was re-centring the clipmaps of bodies
            // NOBODY is looking at — 187,000 overrides across 25% of all body-updates, which
            // is meshing work spent on nothing. A remote camera only needs detail on what is
            // in front of it.
            if (dFeed > FeedConfig.ClipmapMaxFeedDistance) return null;

            // Replace ONLY the position, on a copy, so the transform's orientation and any
            // other field the engine set survive untouched.
            var replacement = boxedTransform.GetType()
                .GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(boxedTransform, null) ?? boxedTransform;
            fPos.SetValue(replacement, feedPos);
            _clipmapOverrides++;

            // TIME CADENCE ONLY. The first version also logged whenever the BODY changed,
            // which with many bodies in range meant a line per body per frame — it hit 4,200
            // lines/sec and put 180 MB into rtt.log before the spike detector caught it. A
            // per-frame path may only ever log on a clock, never on "something differed".
            var now = Environment.TickCount64;
            if (now - _clipmapLogTicks > 15000)
            {
                _clipmapLogTicks = now;
                _lastOverrideDesc = $"body@{bodyPos.X:F0},{bodyPos.Y:F0},{bodyPos.Z:F0}";
                RttLog.Line($"CLIPMAP CAMERA: {_lastOverrideDesc} is LODing around the FEED — player is " +
                            $"{dPlayer / 1000.0:F0} km from it, the camera {dFeed / 1000.0:F0} km. Bodies the " +
                            $"player is nearer to, or that the camera is not within " +
                            $"{FeedConfig.ClipmapMaxFeedDistance / 1000.0:F0} km of, are untouched. " +
                            $"({_clipmapOverrides} override(s) of {_clipmapCalls} body-updates; this line is " +
                            "rate-limited to one per 15 s.)");
            }
            return replacement;
        }
        catch { return null; }
    }

    // PRELOAD THE WORLD AROUND THE FEED CAMERA (goal 10, tier 1).
    //
    // SpaceProbeSessionComponent.PreloadAsync(BoundingBoxD, Precision) fans the volume out
    // across every registered ISpaceProbePreloadable that overlaps it. The two registered
    // providers in this save are exactly the two systems a remote feed is missing —
    // VoxelStorageComponentBase (terrain data) and PlanetEnvironmentComponent (flora
    // sectors) — so ONE call asks for both.
    //
    // WHY THIS IS SAFE TO CALL FROM HERE, established BEFORE the first call rather than
    // after (the discipline four incidents bought today):
    //   * PreloadInternalAsync continues with TaskExtensions.ContinueDirectly — NOT
    //     ContinueOnDCS<SyncPoint>, and it never calls Scene.FinishBefore. That pair is
    //     precisely what made ManagedWorldArea.TryLoad deadlock the sim from this thread:
    //     FinishBefore plants a blocking task in the scene scheduler that only the sim pump
    //     can clear.
    //   * A reachability sweep from PreloadAreaAsync across Core.Game/Voxels/DCS/Simulation
    //     for FinishBefore|ContinueOnDCS returned one hit, and it was a FALSE POSITIVE: the
    //     path hops through IDisposable.Dispose(), which ilscan's virtual expansion links to
    //     every Dispose in the loaded set, landing in an unrelated encounter spawner.
    //     Reading VoxelStorageComponentBase's actual body settles it — box maths,
    //     PerformPinRequest on streamable voxel resources, CollectPreloaded callbacks, and
    //     no scheduler touch at all. The tool over-approximates BY DESIGN; a hit means
    //     "go read this", never "this happens".
    //
    // It also names an explicit VOLUME rather than competing for a shared "where is the
    // viewer" slot, so unlike the observer route it structurally cannot become the
    // single-slot tug-of-war that causes the LOD popping.
    //
    // Fire-and-forget: the returned Task is deliberately not awaited or stored. Preloading
    // is a HINT — if it never completes, the feed simply looks as it does today.
    private static long _lastPreloadTicks;
    private static int _preloadCount;
    private static bool _preloadShapeLogged;

    internal static void PreloadAroundCamera(object anyEntityInScene, Vector3D centre, double radius)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastPreloadTicks < Math.Max(1000, FeedConfig.PreloadIntervalMs)) return;
            _lastPreloadTicks = now;

            var probe = FindSessionComponent(anyEntityInScene, "SpaceProbeSessionComponent");
            if (probe == null)
            {
                if (!_preloadShapeLogged)
                { _preloadShapeLogged = true; RttLog.Line("PRELOAD: SpaceProbeSessionComponent not reachable — preload disabled."); }
                return;
            }

            var tPrec = Type.GetType(
                "Keen.VRage.Core.Game.GameSystems.SpaceProbe.Precision, VRage.Core.Game");
            var mi = probe.GetType().GetMethods(Any).FirstOrDefault(m =>
                m.Name == "PreloadAsync" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType.Name == "BoundingBoxD");
            if (tPrec == null || mi == null)
            {
                if (!_preloadShapeLogged)
                {
                    _preloadShapeLogged = true;
                    RttLog.Line($"PRELOAD: shape missing (Precision={(tPrec == null ? "NOT FOUND" : "ok")}, " +
                                $"PreloadAsync(BoundingBoxD,Precision)={(mi == null ? "NOT FOUND" : "ok")}). " +
                                "Overloads present: " + string.Join(" | ", probe.GetType().GetMethods(Any)
                                    .Where(m => m.Name == "PreloadAsync")
                                    .Select(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)))));
                }
                return;
            }

            // BoundingBoxD(Vector3D min, Vector3D max) — a cube of side 2*radius on the
            // camera's subject centre, which is the anchor when one is set.
            var tBox = mi.GetParameters()[0].ParameterType;
            var half = new Vector3D(radius, radius, radius);
            var box = Activator.CreateInstance(tBox, centre - half, centre + half);

            var precName = FeedConfig.PreloadPrecision;
            object prec;
            try { prec = Enum.Parse(tPrec, precName, true); }
            catch { prec = Enum.Parse(tPrec, "Medium", true); }

            mi.Invoke(probe, new[] { box, prec });
            _preloadCount++;

            // First call and then every 20th: enough to prove liveness without flooding.
            if (_preloadCount == 1 || _preloadCount % 20 == 0)
                RttLog.Line($"PRELOAD #{_preloadCount}: asked the space probe for a {radius * 2:F0} m cube at " +
                            $"{centre.X:F0},{centre.Y:F0},{centre.Z:F0} at {prec} precision. This fans out to every " +
                            "registered preloadable overlapping the volume — terrain data AND flora sectors. " +
                            "Fire-and-forget: if it does nothing the feed just looks as it did.");
        }
        catch (Exception e)
        {
            // Log once and DISARM. A repeating exception from a world-mutating call is
            // exactly the shape that turns into a crash loop while nobody is watching.
            if (!_preloadShapeLogged)
            {
                _preloadShapeLogged = true;
                RttLog.Error("preload around camera (now disarmed for this session)", e);
            }
            _lastPreloadTicks = long.MaxValue / 2;
        }
    }

    // THE OBSERVER REGISTRY — read-only recon for the camera-as-observer route.
    //
    // IObservers is the engine's own MULTI-viewer abstraction and the most promising route
    // to world materialization around a feed camera:
    //     Observer Add(ImmutableArray<StringId> tags, ObserverData data)
    //     ReadOnlySpan<Observer> Get(StringId tag)          <- plural BY DESIGN
    // plus helpers that ask for the CLOSEST observer to a point, which only makes sense if
    // several are expected. ObserverComponent is the supported way to register: an entity
    // carrying it calls Add on OnAddedToScene with its definition's Tags + InfluenceRadius,
    // and re-registers itself on move. ObserverComponentDefinition is just those two fields.
    //
    // WHAT THIS DUMP IS FOR: an observer is only useful if we register it under the tag the
    // consumer looks for, and tags are StringIds resolved at runtime — not readable from the
    // assemblies. So enumerate the LIVE registry: whatever the player is registered as tells
    // us exactly what to imitate.
    //
    // STRICTLY READ-ONLY. No Add, no Update. Note IObservers.CreateJobContext(Session)
    // exists, which strongly implies the registry is meant to be touched from inside DCS
    // jobs — so when the time comes to register, the safe route is attaching an
    // ObserverComponent to an entity and letting the ENGINE call Add from its own context,
    // not calling Add ourselves. Today cost four incidents to learn that distinction.
    private static void AppendObservers(StringBuilder sb, object anyEntityInScene, Vector3D player)
    {
        try
        {
            sb.AppendLine("\n--- observer registry (IObservers) ---");
            var voxObs = FindSessionComponent(anyEntityInScene, "VoxelObserverSessionComponent");
            var observers = Prop(voxObs, "_observers");
            if (observers == null)
            {
                sb.AppendLine("    IObservers not reachable" +
                              $" (VoxelObserverSessionComponent={(voxObs == null ? "not found" : "found")})");
                return;
            }
            sb.AppendLine($"    registry: {observers.GetType().FullName}");

            // The concrete registry's own fields hold the tag -> observers mapping. Dump
            // whatever collections it carries rather than guessing a member name; one
            // structural dump beats three reload-priced guesses.
            foreach (var f in observers.GetType().GetFields(Any))
            {
                object v = null; try { v = f.GetValue(observers); } catch { }
                if (v == null) { sb.AppendLine($"    {f.FieldType.Name,-34} {f.Name} = null"); continue; }
                var count = (v as System.Collections.ICollection)?.Count;
                sb.AppendLine($"    {f.FieldType.Name,-34} {f.Name}" +
                              (count.HasValue ? $"  count={count}" : $" = {v}"));

                // ENUMERATE, THEN REFLECT Key/Value. The engine's ListDictionary<K,V> is not
                // an IDictionary — its enumerator yields KeyValuePair<K,V>, and casting each
                // item to DictionaryEntry throws, which is exactly what killed the first run
                // of this dump. Reading Key/Value by reflection handles both shapes.
                if (v is System.Collections.IEnumerable pairs and not string
                    && v.GetType().Name.Contains("Dictionary", StringComparison.Ordinal))
                {
                    foreach (var item in pairs)
                    {
                        var k = Prop(item, "Key"); var val = Prop(item, "Value");
                        if (k == null && val == null) { sb.AppendLine("        " + item); continue; }
                        var entries = (val as System.Collections.ICollection)?.Count;
                        sb.AppendLine($"        tag [{k}] -> {(entries.HasValue ? entries + " observer(s)" : val?.GetType().Name)}");
                        if (val is System.Collections.IEnumerable list and not string)
                            foreach (var o in list) sb.AppendLine("            " + DescribeObserver(o, player));
                        else if (val != null)
                            sb.AppendLine("            " + DescribeObserver(val, player));
                    }
                }
                else if (v is System.Collections.IEnumerable seq and not string)
                {
                    int n = 0;
                    foreach (var o in seq)
                    {
                        if (n++ >= 20) { sb.AppendLine("        ... (truncated at 20)"); break; }
                        sb.AppendLine("        " + DescribeObserver(o, player));
                    }
                }
            }

            // The voxel side specifically: which tag it watches, and whether it is currently
            // following an observer or being force-aligned to the render camera.
            var dbg = Prop(voxObs, "_voxelDebugConfig");
            sb.AppendLine($"    voxel ObserverTag = {Prop(dbg, "ObserverTag") ?? "?"}" +
                          "   (VoxelObserverSessionComponent.UpdateObserver copies the FIRST observer " +
                          "with this tag into a single _observerEntity, and AlignToRenderCamera can " +
                          "instead force it to RenderSettings.CameraTransform — so terrain follows ONE " +
                          "point either way. Terrain likely needs its own clipmap, not just an observer.)");
        }
        catch (Exception e) { sb.AppendLine($"    observer dump FAILED ({e.GetType().Name}: {e.Message})"); }
    }

    // THE PRELOAD API — the other live route, and the only one that addresses BOTH gaps.
    //
    //     SpaceProbeSessionComponent.PreloadAsync(BoundingBoxD | OrientedBoundingBoxD | LineD,
    //                                             Precision) -> Task<...>
    //
    // It keeps a spatial tree of registered ISpaceProbePreloadable providers and fans the
    // request out to whichever overlap the volume. The two implementers are exactly the two
    // systems the feed is missing: VoxelStorageComponentBase (terrain data) and
    // PlanetEnvironmentComponent (the flora sectors). So one call over a box around the
    // camera asks for terrain AND clutter, which no other route does in a single step.
    //
    // Advantages over the observer route: it names the VOLUME explicitly rather than relying
    // on a single global "where the viewer is", so it cannot be a tug-of-war with the
    // player; and there are three Precision-keyed caches, so repeat requests over a
    // stationary orbit should be cheap.
    //
    // THREADING, given today: PreloadAsync returns a Task and the game's own admin tools and
    // debug screen call it — encouraging, but "returns a Task" is NOT proof it can be
    // *initiated* from any thread. TryLoad also returned a Task and still deadlocked the sim
    // by planting a scheduler dependency. Confirm the seat before the first call; this dump
    // is read-only reconnaissance, not a call site.
    private static void AppendSpaceProbe(StringBuilder sb, object anyEntityInScene)
    {
        try
        {
            sb.AppendLine("\n--- space probe / preload API ---");
            var probe = FindSessionComponent(anyEntityInScene, "SpaceProbeSessionComponent");
            if (probe == null) { sb.AppendLine("    SpaceProbeSessionComponent not found"); return; }

            var providers = Prop(probe, "_preloadableProviders") as System.Collections.IDictionary;
            sb.AppendLine($"    registered preloadable providers: {(providers == null ? "?" : providers.Count.ToString())}");
            if (providers != null)
            {
                // The dictionary is keyed by PreloadableToken; the PROVIDER is what we want
                // named, so look through both key and value for the ISpaceProbePreloadable.
                var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry kv in providers)
                {
                    string n = null;
                    foreach (var host in new[] { kv.Value, kv.Key })
                    {
                        if (host == null) continue;
                        if (host.GetType().Name.Contains("Preloadable", StringComparison.Ordinal))
                            foreach (var f2 in host.GetType().GetFields(Any))
                            {
                                object inner = null; try { inner = f2.GetValue(host); } catch { }
                                var tn = inner?.GetType().Name;
                                if (tn != null && (tn.Contains("Voxel", StringComparison.Ordinal)
                                                || tn.Contains("Planet", StringComparison.Ordinal)
                                                || tn.Contains("Environment", StringComparison.Ordinal)))
                                { n = tn; break; }
                            }
                        n ??= host.GetType().Name;
                        if (!n.Contains("Token", StringComparison.Ordinal)) break;
                    }
                    n ??= "null";
                    kinds[n] = kinds.TryGetValue(n, out var c) ? c + 1 : 1;
                }
                foreach (var k in kinds.OrderByDescending(k => k.Value))
                    sb.AppendLine($"        {k.Value,5}  {k.Key}");
                sb.AppendLine("    (VoxelStorageComponentBase = terrain data, PlanetEnvironmentComponent " +
                              "= flora sectors. Both present means one PreloadAsync over a box at the " +
                              "camera asks for terrain AND clutter in a single call.)");
            }
            foreach (var m in probe.GetType().GetMethods(Any).Where(m => m.Name == "PreloadAsync"))
                sb.AppendLine($"    PreloadAsync({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
        }
        catch (Exception e) { sb.AppendLine($"    space probe dump FAILED ({e.GetType().Name}: {e.Message})"); }
    }

    private static string DescribeObserver(object o, Vector3D player)
    {
        if (o == null) return "<null>";
        var sbo = new StringBuilder(o.GetType().Name);
        foreach (var name in new[] { "Tags", "Tag", "InfluenceRadius", "Radius", "Data", "Transform", "WorldTransform" })
        {
            var v = Prop(o, name);
            if (v == null) continue;
            if (v is Vector3D p)
                sbo.Append($"  {name}={p.X:F0},{p.Y:F0},{p.Z:F0} ({(p - player).Length():F0} m from subject)");
            else if (v is System.Collections.IEnumerable en and not string)
            {
                var items = new List<string>();
                foreach (var i in en) { if (items.Count >= 8) break; items.Add(i?.ToString() ?? "null"); }
                sbo.Append($"  {name}=[{string.Join(", ", items)}]");
            }
            else sbo.Append($"  {name}={v}");
        }
        // Position often hides one level down, on the observer's data/transform struct.
        foreach (var host in new[] { Prop(o, "Data"), Prop(o, "Transform"), Prop(o, "WorldTransform") })
        {
            if (host == null) continue;
            var p2 = Prop(host, "Position") ?? Prop(host, "Translation");
            if (p2 is Vector3D pv)
            { sbo.Append($"  pos={pv.X:F0},{pv.Y:F0},{pv.Z:F0} ({(pv - player).Length():F0} m)"); break; }
        }
        return sbo.ToString();
    }

    // Any session component by type name — generalised from the managed-area hop, which
    // needed exactly this and had it inlined.
    private static object FindSessionComponent(object anyEntityInScene, string typeName)
    {
        var tBlock3 = Type.GetType(
            "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
        var grid = ComponentOf(anyEntityInScene, "CubeGridComponent")
                   ?? Prop(TryGetViaGeneric(anyEntityInScene, tBlock3), "Grid");
        var scEntity = Prop(Prop(grid, "Session"), "SessionComponents");
        if (scEntity == null || _fComponents?.GetValue(scEntity) is not IEnumerable comps) return null;
        foreach (var c in comps)
            if (c != null && c.GetType().Name == typeName) return c;
        return null;
    }

    // The session-component hop, shared by the survey section and the loader.
    private static object FindManagedWorldAreaComponent(object anyEntityInScene)
    {
        var tBlock2 = Type.GetType(
            "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
        var grid = ComponentOf(anyEntityInScene, "CubeGridComponent")
                   ?? Prop(TryGetViaGeneric(anyEntityInScene, tBlock2), "Grid");
        var session = Prop(grid, "Session");
        var scEntity = Prop(session, "SessionComponents");
        if (scEntity == null || _fComponents?.GetValue(scEntity) is not IEnumerable scComps) return null;
        object mwa = null;
        foreach (var c in scComps)
        {
            if (c == null) continue;
            var n = c.GetType().Name;
            if (n == "ManagedWorldAreaSessionComponent") return c;
            if (mwa == null && n.Contains("ManagedWorldArea", StringComparison.Ordinal)
                            && Prop(c, "_areas") != null)
                mwa = c;
        }
        return mwa;
    }

    // Planet-like bodies: entities carrying any component whose type name mentions Planet.
    // Name comes from DisplayName if any component offers one, else the entity's DebugName —
    // which for planets is usually the readable body name. Extent is the best "Radius"-ish
    // member found on any of its components (0 = unknown; the dump prints "?" rather than
    // pretending).
    private static IEnumerable<GridInfo> EnumeratePlanets(object anyEntityInScene)
    {
        var scene = SceneOf(anyEntityInScene);
        if (scene == null) yield break;
        _miEnumerate ??= scene.GetType().GetMethod("EnumerateEntities", Any, null, Type.EmptyTypes, null);
        _tCtx ??= Type.GetType("Keen.VRage.DCS.Accessors.DEntityContext, VRage.DCS");
        _tEntity ??= Type.GetType("Keen.VRage.DCS.Components.Entity, VRage.DCS");
        _miFromData ??= _tEntity?.GetMethod("TryGetFromDataEntity", Any);
        if (_miEnumerate == null || _tCtx == null || _miFromData == null) yield break;
        if (_miEnumerate.Invoke(scene, null) is not IEnumerable handles) yield break;

        foreach (var handle in handles)
        {
            GridInfo? info = null;
            try
            {
                var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                var entity = _miFromData.Invoke(null, new[] { ctx });
                if (entity == null) continue;
                if (_fComponents == null)
                    _fComponents = entity.GetType().GetField("Components", Any);
                if (_fComponents?.GetValue(entity) is not IEnumerable comps) continue;

                object planetComp = null;
                string displayName = null;
                double radius = 0;
                var hasVoxel = false;
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var tn = c.GetType().Name;
                    if (tn.Contains("Planet", StringComparison.Ordinal)) planetComp ??= c;
                    if (tn.Contains("Voxel", StringComparison.Ordinal)) hasVoxel = true;
                    displayName ??= Prop(c, "DisplayName")?.ToString();
                    if (radius <= 0)
                        foreach (var rn in new[] { "Radius", "MaxRadius", "AverageRadius", "MinRadius" })
                        {
                            var rv = Prop(c, rn);
                            if (rv != null)
                            {
                                try { radius = Convert.ToDouble(rv, CultureInfo.InvariantCulture); } catch { }
                                if (radius > 0) break;
                            }
                        }
                }
                // "Planet component or voxel body" would also catch asteroids; requiring a
                // Planet-named component keeps this to actual planetary machinery.
                if (planetComp == null || !hasVoxel) continue;

                var pos = PositionOf(planetComp, entity);
                if (pos == null) continue;

                var name = displayName;
                if (string.IsNullOrWhiteSpace(name)) name = Prop(entity, "DebugName")?.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = "(unnamed body)";
                info = new GridInfo(name, pos.Value, radius, entity);
            }
            catch { }
            if (info.HasValue) yield return info.Value;
        }
    }

    // ---- the walk itself ---------------------------------------------------------------

    private static MethodInfo _miEnumerate;
    private static bool _shapeLogged;

    private static IEnumerable<GridInfo> Enumerate(object anyEntityInScene)
    {
        var scene = SceneOf(anyEntityInScene);
        if (scene == null) yield break;

        _miEnumerate ??= scene.GetType().GetMethod("EnumerateEntities", Any, null, Type.EmptyTypes, null);
        if (_miEnumerate == null)
        {
            if (!_shapeLogged)
            {
                _shapeLogged = true;
                RttLog.Line("WORLD WALK: Scene.EnumerateEntities() not found. Candidates: " +
                    string.Join(" | ", scene.GetType().GetMethods(Any)
                        .Where(m => m.Name.StartsWith("Enumerate") || m.Name.Contains("Entit"))
                        .Select(m => m.Name + "(" + m.GetParameters().Length + ")").Distinct()));
            }
            yield break;
        }

        if (_miEnumerate.Invoke(scene, null) is not IEnumerable handles) yield break;

        // ENUMERATEENTITIES YIELDS DEntity, NOT Entity.
        //
        // DEntity is a raw ECS handle (an id pair — the component dumps show "158:1"), and it
        // carries no TryGet<T>. The first version of this walk passed those handles straight
        // to TryGetComponent and got a silent 0 grids, which reads identically to "the world
        // is empty". The bridge is Entity.TryGetFromDataEntity(DEntityContext), and
        // DEntityContext has a public (Scene, DEntity) constructor.
        _tCtx ??= Type.GetType("Keen.VRage.DCS.Accessors.DEntityContext, VRage.DCS");
        _tEntity ??= Type.GetType("Keen.VRage.DCS.Components.Entity, VRage.DCS");
        _miFromData ??= _tEntity?.GetMethod("TryGetFromDataEntity", Any);
        if (_tCtx == null || _miFromData == null)
        {
            if (!_shapeLogged)
            {
                _shapeLogged = true;
                RttLog.Line($"WORLD WALK: bridge missing — DEntityContext={(_tCtx == null ? "NOT FOUND" : "ok")} " +
                            $"Entity.TryGetFromDataEntity={(_miFromData == null ? "NOT FOUND" : "ok")}. " +
                            "Without it, DEntity handles cannot be turned into Entities and the walk finds nothing.");
            }
            yield break;
        }

        // COUNT EVERY STAGE. A zero at the end is ambiguous between "no entities", "none
        // convertible" and "none are grids"; three counters make it self-diagnosing and save
        // a hot reload, which the VRAM ratchet prices at roughly a tenth of a session.
        int seen = 0, resolved = 0, grids = 0;
        _lastHadComponent = 0; _lastHadPosition = 0;

        foreach (var handle in handles)
        {
            seen++;
            GridInfo? info = null;
            // Per-entity try/catch: one malformed entity must not abort the whole inventory,
            // which is exactly what a single unguarded throw in a walk like this does.
            try
            {
                var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                var entity = _miFromData.Invoke(null, new[] { ctx });
                if (entity != null) { resolved++; info = Describe(entity); }
            }
            catch { }
            if (info.HasValue) { grids++; yield return info.Value; }
        }

        RttLog.Line($"WORLD WALK: {seen} DEntity handle(s), {resolved} resolved to Entity, " +
                    $"{_lastHadComponent} carried a CubeGridComponent, {_lastHadPosition} of those " +
                    $"yielded a position, {grids} listed. The stage whose count drops to zero is the " +
                    "one that is broken.");

        // ZERO GRIDS TWICE IS AN INSTRUMENT PROBLEM, NOT A WORLD PROBLEM — there is
        // observably a base and a control seat in this save. Stop theorising about which
        // component name is right and ask the entities themselves: a census of every
        // distinct component type name across the whole scene, written once per walk that
        // finds nothing. The next name to try is then a lookup, not a guess.
        if (grids == 0 && resolved > 0)
        {
            try
            {
                var census = new Dictionary<string, int>(StringComparer.Ordinal);
                int sampled = 0;
                foreach (var handle in handles)
                {
                    if (sampled >= 3000) break;   // ample for a census; keeps the tick sane
                    object entity = null;
                    try
                    {
                        var ctx = Activator.CreateInstance(_tCtx, scene, handle);
                        entity = _miFromData.Invoke(null, new[] { ctx });
                    }
                    catch { }
                    if (entity == null) continue;
                    sampled++;
                    if (_fComponents?.GetValue(entity) is not IEnumerable comps) continue;
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        var n = c.GetType().FullName;
                        census[n] = census.TryGetValue(n, out var k) ? k + 1 : 1;
                    }
                }
                var path = Path.Combine(RttLog.OutDir, "component-census.txt");
                File.WriteAllText(path,
                    $"Component census over {sampled} entities (walk found 0 grids):\n\n" +
                    string.Join("\n", census.OrderByDescending(kv => kv.Value)
                                            .Select(kv => $"{kv.Value,7}  {kv.Key}")));
                RttLog.Line($"WORLD WALK: census of {census.Count} distinct component types over " +
                            $"{sampled} entities -> {path}. Grep it for Grid.");
            }
            catch (Exception e) { RttLog.Error("component census", e); }
        }
    }

    private static Type _tCtx, _tEntity;
    private static MethodInfo _miFromData;

    // Split diagnostics: "had the component" and "had a position" are DIFFERENT failures,
    // and the first version of this walk conflated them — the census then showed 18
    // CubeGridComponents while the walk reported 0 grids, which is only possible when the
    // position lookup is what is failing. Never fold two failure modes into one counter.
    private static int _lastHadComponent, _lastHadPosition;

    private static GridInfo? Describe(object entity)
    {
        var grid = ComponentOf(entity, "CubeGridComponent");
        if (grid == null) return null;
        _lastHadComponent++;

        var name = Prop(grid, "DisplayName")?.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = "(unnamed)";

        var pos = PositionOf(grid, entity);
        if (pos == null) return null;
        _lastHadPosition++;

        return new GridInfo(name, pos.Value, ExtentOf(grid), entity);
    }

    // Position, tried in the order most likely to be present. A grid always has a world
    // transform somewhere; which member exposes it has moved between builds, so this asks
    // rather than assuming.
    private static Vector3D? PositionOf(object grid, object entity)
    {
        // _positionComponent FIRST: the grid-survey dump shows CubeGridComponent stores its
        // WorldTransformComponent there, and the generic names below all missed it — which
        // is exactly how the first walk found 18 grids and 0 positions. The entity may also
        // carry the WorldTransformComponent directly (census: 127 of them), so both roads
        // lead to the same component type.
        foreach (var host in new[]
                 { Prop(grid, "_positionComponent"), ComponentOf(entity, "WorldTransformComponent"),
                   grid, Prop(grid, "Entity"), entity })
        {
            if (host == null) continue;
            foreach (var member in new[] { "WorldPosition", "Position", "Translation" })
            {
                var v = Prop(host, member);
                if (v is Vector3D d) return d;
            }
            // WorldTransform is typically a struct of Position + Orientation; take it apart
            // rather than expecting the host to flatten it for us.
            var wt = Prop(host, "WorldTransform") ?? Prop(host, "PositionComp") ?? Prop(host, "Transform");
            if (wt != null)
            {
                var p = Prop(wt, "Position") ?? Prop(wt, "Translation");
                if (p is Vector3D d2) return d2;
            }
        }
        return null;
    }

    private static double ExtentOf(object grid)
    {
        foreach (var member in new[] { "WorldAABB", "AABB", "WorldVolume" })
        {
            var b = Prop(grid, member);
            if (b == null) continue;
            var min = Prop(b, "Min"); var max = Prop(b, "Max");
            if (min is Vector3D a && max is Vector3D c) return (c - a).Length() * 0.5;
            var r = Prop(b, "Radius");
            if (r != null) return Convert.ToDouble(r, CultureInfo.InvariantCulture);
        }
        return 0.0;
    }

    private static object SceneOf(object entity)
    {
        if (entity == null) return null;
        var direct = Prop(entity, "Scene");
        if (direct != null) return direct;
        var grid = ComponentOf(entity, "CubeGridComponent");
        return grid == null ? null : Prop(grid, "Scene");
    }

    // BY NAME OVER THE Components ARRAY, NOT Entity.TryGet<T>.
    //
    // The first version closed TryGet<T> over CubeGridComponent and got 0 grids from 34,141
    // resolved entities — a silent all-null that reads identically to "the world has no
    // grids". TryGet's tag-keyed lookup semantics were never established for grid components
    // (the feed's own code never uses it for grids either; it reads block.Grid). Entity
    // carries a plain `ImmutableArray Components` field, and walking that asks no questions
    // about tags at all. Name-suffix matching also survives the component type moving
    // namespaces between game builds.
    private static FieldInfo _fComponents;

    private static object ComponentOf(object entity, string typeNameSuffix)
    {
        if (entity == null) return null;
        _fComponents ??= entity.GetType().GetField("Components", Any);
        if (_fComponents?.GetValue(entity) is not IEnumerable comps) return null;
        foreach (var c in comps)
            if (c != null && c.GetType().Name.EndsWith(typeNameSuffix, StringComparison.Ordinal))
                return c;
        return null;
    }

    // Entity.TryGet<T> — declared TryGet<T>(StringId tag = default), so reflection sees one
    // parameter even where C# calls it with none. Same dance as CameraFeed.WorldPositionOf.
    private static object TryGetViaGeneric(object entity, Type componentType)
    {
        if (entity == null || componentType == null) return null;
        try
        {
            var tryGet = entity.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "TryGet" && m.IsGenericMethodDefinition
                                  && m.GetParameters().Length <= 1);
            if (tryGet == null) return null;
            var closed = tryGet.MakeGenericMethod(componentType);
            var ps = closed.GetParameters();
            var args = ps.Length == 0
                ? null
                : new[] { ps[0].ParameterType.IsValueType
                            ? Activator.CreateInstance(ps[0].ParameterType) : null };
            return closed.Invoke(entity, args);
        }
        catch { return null; }
    }

    // WALKS THE HIERARCHY FOR FIELDS, deliberately. GetField on a derived type never
    // returns a BASE class's private fields — which is how `_areas` (private on
    // ManagedWorldAreaSessionComponent) read as null through its Client subclass and made
    // the correct component fail its own sanity check. Properties don't need the walk
    // (non-public inherited getters are rare here); private fields do, always.
    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var p = o.GetType().GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Any);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }
        catch { return null; }
    }
}
