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

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        try
        {
            var p = t.GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            return t.GetField(name, Any)?.GetValue(o);
        }
        catch { return null; }
    }
}
