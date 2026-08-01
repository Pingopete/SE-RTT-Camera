using System.Reflection;
using System.Text;
using Keen.VRage.Library.Mathematics;

namespace RttProbe;

// The end-to-end feed: an orbiting camera 100 m from a tagged LCD panel, looking
// at it, rendered as a real second 3D view and displayed on that panel.
//
// Threading: panels are found from the LCD tick hook (which hands us the render
// component holding _lcdBlock) and published here as a plain value. The render
// pass reads that value on the render thread. Nothing is shared but a struct.
internal static class CameraFeed
{
    public const string Tag = "[RTC]";

    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;


    // Published by the sim/tick side, consumed by the render side.
    //
    // A CLASS, not a struct, and deliberately so. As a ~60-byte struct this was torn
    // by the render thread mid-write: the tick hook rewrites it several times a
    // second while the render thread reads it, and a reader that caught Valid=false
    // (or a half-updated Position) fell back to the player's main view for exactly
    // one frame. That was the flash from inside the ship, ~2x/second. A reference
    // assignment is atomic, so a reader now sees the old target or the new one and
    // never a mixture.
    public sealed class Target
    {
        public Vector3D Position;      // the panel itself
        public Vector3D Centre;        // what the camera orbits and looks at
        public double Extent;          // half-diagonal of the grid, 0 if unknown
        public string Name;

        // LOCAL UP at the subject — the grid's own up, which on a planet is the surface
        // normal. The orbit plane is built perpendicular to this. Zero means "unknown", and
        // the orbit falls back to world Y (the pre-2026-08-01 behaviour).
        public Vector3D Up;
    }

    // The up the orbit is currently built around. Published with the Target rather than read
    // from it inside OrbitCameraWorld, because that method is also called from the diagnostic
    // paths with a bare position and no Target at all.
    internal static Vector3D OrbitUp;

    // PLANET RADIAL UP, published by the RENDER thread and consumed by the LCD tick.
    //
    // Crossing the threads deliberately: the planet-env group this is derived from is resolved
    // lazily inside the render path and is null everywhere else, so discovery asking for it
    // directly always got nothing. A stale value here is harmless — a planet's radial direction
    // at a fixed base does not change — so a plain field beats any synchronisation.
    internal static Vector3D PlanetUpCache;

    // THE SUBJECT'S WORLD POSITION, going the other way: published by the LCD tick, read by
    // the render thread's planet scan.
    //
    // The scan needs a world position to decide WHICH of the four planets in the setup we are
    // standing on, and to sanity-check that a candidate centre is world-space rather than
    // camera-relative. It first read Feeds.Cur.Target.Centre directly and got 0,0,0 while the
    // tick was logging the panel at 124917,613,-262239 — so that cross-thread read is not
    // seeing what the tick wrote. Publishing the value the orbit itself uses removes the
    // question: if this is ever zero, the orbit is looking at the origin too, and the source
    // dump now says so out loud instead of silently scanning against a wrong position.
    internal static Vector3D SubjectCentreCache;

    // Did OrbitUp come from the planet (exact) or from the subject's rotation (a guess)?
    // Reported in the orbit-plane proof so the two are never confused again.
    internal static bool OrbitUpIsPlanet;

    // Rate limit for the orbit-plane proof in OrbitCameraWorld.
    private static long _orbitPlaneDiagMs;
    // PER-FEED (phase C1a): WHAT this feed is pointed at. Once feeds are independent
    // this is the single most feed-defining value in the mod — it is the camera's
    // subject. The backing field stays volatile for the tick/render publish above.
    private static Target _target
    { get => Feeds.Cur.Target; set => Feeds.Cur.Target = value; }
    public static Target Current => _target;

    // Latched: once a panel has been found, the render pass must never fall back to
    // the main view, because doing so puts the player's viewpoint on the panel.
    public static bool EverFound { get; private set; }

    private static int _findLogs, _errLogs;

    // Budget for the re-route line. Process-level: it is a statement about the ROUTER, which
    // is shared, and a retag is a rare deliberate act — eight of them is plenty of evidence
    // and stops a pathological flip-flop filling the log.
    private static int _rerouteLogs;
    private static readonly HashSet<string> _seenNames = new();

    // "Announced this panel as a mirror" — a log latch about a NAME, process-level like
    // the other said-once latches, so gate cycles do not re-announce every mirror.
    private static readonly HashSet<string> _mirrorLogs = new();

    // The tagged panel's surface contexts, by reference. The render-side hook is
    // handed a surface context with no name on it, so identity recorded here is
    // how it recognises the panel to draw on.
    //
    // Bounded on purpose. RebuildSurfaceContent REPLACES the contexts, so every
    // forced repaint adds a fresh object here and the old one becomes garbage that
    // this set then pins — a managed leak that grows for as long as repaints are
    // being driven, and each pinned context holds a runtime LCD material. The set
    // only needs the live ones, so drop the accumulated dead weight periodically.
    private static readonly HashSet<object> _targetSurfaces = new(ReferenceEqualityComparer.Instance);
    private const int MaxTrackedSurfaces = 32;
    private static int _surfaceTrims;

    public static bool IsTargetSurface(object ctx) => ctx != null && _targetSurfaces.Contains(ctx);

    private static void TrackSurface(object ctx)
    {
        if (ctx == null) return;

        // Record which feed owns this surface (phase C3). This runs under the owning feed's
        // ambient — discovery is scoped by FeedRouter.ForComponent — so ownership is simply
        // written down here rather than re-derived later, which is what lets the panel-render
        // hook route a context that carries no name of its own.
        FeedRouter.ClaimSurface(ctx, Feeds.Cur);

        if (_targetSurfaces.Count >= MaxTrackedSurfaces)
        {
            _targetSurfaces.Clear();
            if (_surfaceTrims++ < 3)
                RttLog.Line($"  repaint: surface-context set hit {MaxTrackedSurfaces} — cleared (contexts are re-created on every rebuild).");
        }
        _targetSurfaces.Add(ctx);
    }

    // Is any surface of this panel actually powered?
    //
    // LcdPanelSurfaceRenderComponent._surfaces is LcdPanelSurfaceContext[], and each
    // carries a public CurrentMaterialState field (PowerOff=0, DefaultScreen=1,
    // CustomRender=2). Fails OPEN: if the shape ever changes and we cannot read it, treat
    // the panel as powered, because a mod that silently refuses to run is harder to
    // diagnose than one that runs when it should not.
    private static string _powerLog
    { get => Feeds.Cur.PowerLog; set => Feeds.Cur.PowerLog = value; }

    // `wantIndex` = the tagged surface, or -1 for "any surface will do" (block-name tagging).
    //
    // ASKING ABOUT THE RIGHT SCREEN MATTERS on a multi-surface block: a command seat whose
    // navigation screens are lit would report POWERED however dead the screen we were asked
    // to feed, so the gate would hold a feed open against a panel showing nothing. Falls back
    // to the old any-surface test only when nothing identifies a single screen.
    private static bool IsPanelPowered(object renderComponent, int wantIndex)
    {
        try
        {
            if (renderComponent.GetType().GetField("_surfaces", Any)?.GetValue(renderComponent)
                is not System.Collections.IEnumerable surfaces) return true;

            bool sawAny = false, anyOn = false;
            int i = -1;
            foreach (var s in surfaces)
            {
                i++;
                if (s == null) continue;
                if (wantIndex >= 0 && i != wantIndex) continue;
                var f = s.GetType().GetField("CurrentMaterialState", Any);
                if (f == null) return true;                 // unknown shape: fail open
                sawAny = true;
                if (Convert.ToInt32(f.GetValue(s)) != 0) { anyOn = true; break; }
            }
            if (!sawAny) return true;

            string state = anyOn ? "POWERED" : "PowerOff";
            if (state != _powerLog)
            {
                _powerLog = state;
                RttLog.Line($"Tagged panel is {state} (LcdPanelSurfaceContext.CurrentMaterialState). " +
                            (anyOn ? "The feed may run." : "The feed will go dormant."));
            }
            return anyOn;
        }
        catch { return true; }
    }

    public static void Reset() => Reset(true);

    // `last` = no other feed is still live (phase F2). The split matters because half of
    // what this method clears is PROCESS state, not feed state, and one feed's teardown
    // reaching into it is a live neighbour's bug:
    //
    //   EverFound is the latch that stops the render pass falling back to the main view —
    //   clearing it while another feed renders puts the PLAYER'S VIEWPOINT on that feed's
    //   panel, the single worst-looking failure this route has;
    //   _targetSurfaces is how the render-side hook recognises a panel it may draw on, so
    //   emptying it makes a live feed's panels unrecognisable until they are rediscovered;
    //   _seenNames/_powerLog/_boundsDiag/_closedTryGet are "we already said this about the
    //   engine" latches, whose whole value is being said once.
    public static void Reset(bool last)
    {
        // ---- always: THIS feed's own state ----
        _target = null;
        _boundsGrid = null; _boundsAt = 0;
        LastRenderComponent = null;
        _powerLog = null;

        // The primary election and the claim set re-form from live ticks (phase E2
        // fan-out): a destroyed primary hands the feed to the next ticking claimant, and
        // a destroyed mirror's stale claim stops driving repaints. ExpireClaims does the
        // same job continuously now, so this is the coarse version for a full cycle.
        lock (Feeds.Cur.ClaimedPanels)
        {
            Feeds.Cur.PrimaryPanelName = null;
            Feeds.Cur.ClaimedPanels.Clear();
        }

        if (!last) return;

        // ---- last feed out only: the shared discovery state ----
        EverFound = false;
        _boundsDiag = _orbitLogged = false; _closedTryGet = null;
        _findLogs = _errLogs = 0;
        _seenNames.Clear();
        _targetSurfaces.Clear();
        _surfaceTrims = 0;
    }

    // ---- claim expiry and re-election (phase F3) ---------------------------------
    //
    // A claim is renewed by its panel's tick. When a panel is destroyed, ground down,
    // unpowered or switched off, its ticks stop and its claim goes stale — but with two
    // panels on one feed the FEED does not go dormant, so no gate cycle ever comes to clear
    // it. Two consequences, both silent:
    //
    //   a dead MIRROR keeps PanelBinding.WantsRepaint true forever (live binds < claimants),
    //   so we drive forced repaints on a panel that no longer exists for the rest of the
    //   session;
    //
    //   a dead PRIMARY keeps the feed's identity — orbit target, captured render target,
    //   render component — pinned to it. The camera freezes at its last published position
    //   and the surviving panel can never take over, because PrimaryPanelName is only ever
    //   set when it is null.
    //
    // Same idle window the gate uses, so "this panel is gone" means one thing across the mod.
    // Called once per engine frame per slot from FeedGate.PumpOne; at most one expiry per
    // call, which keeps it allocation-free (the dictionary enumerator is a struct) and is
    // plenty — claims are counted in single digits.
    internal static void ExpireClaims()
    {
        var feed = Feeds.Cur;
        if (feed.ClaimedPanels.Count == 0) return;    // unlocked fast path: an int read

        long now = Clock.Ms, idle = FeedConfig.PanelIdleMs;
        string dead = null;
        int left;
        bool wasPrimary;

        // Same lock as the claim in OnLcdTick — see there for why. Everything that decides
        // and mutates happens inside; the log is written outside, because formatting a
        // sentence is not something to do while holding a lock the LCD thread wants.
        lock (feed.ClaimedPanels)
        {
            foreach (var kv in feed.ClaimedPanels)
                if (now - kv.Value > idle) { dead = kv.Key; break; }
            if (dead == null) return;

            feed.ClaimedPanels.Remove(dead);
            left = feed.ClaimedPanels.Count;
            wasPrimary = string.Equals(feed.PrimaryPanelName, dead, StringComparison.OrdinalIgnoreCase);
            if (wasPrimary) feed.PrimaryPanelName = null;   // next claimant to tick elects itself
        }

        if (wasPrimary)
        {
            lock (_mirrorLogs) _mirrorLogs.Remove(dead);    // so a rebuilt panel is announced again

            // Drop the captured render target with it. It belongs to the panel that just
            // died, and a re-elected primary must capture its OWN — otherwise the feed
            // spends the rest of its life delivering into a target nothing displays.
            // Nulling it is also what re-arms the forced repaints that get us there.
            _panelRt = null;
        }

        RttLog.Line($"Panel claim EXPIRED: \"{dead}\" has not ticked for {idle} ms — destroyed, " +
                    $"deconstructed, unpowered or switched off. " + (wasPrimary
                        ? $"It was this feed's PRIMARY, so the election is reopened: the next of the " +
                          $"{left} remaining claimant(s) to tick takes over the camera. " +
                          (left == 0 ? "None remain — the gate will go dormant on its own idle window." : "")
                        : $"It was a mirror; {left} claimant(s) remain and the feed is otherwise " +
                          "unaffected."));
    }

    // ---------------------------------------------------------------- discovery
    // Called from the LCD tick hook with an LcdPanelSurfaceRenderComponent. Its
    // _lcdBlock is the panel; from there the grid gives a world transform.
    public static void OnLcdTick(object renderComponent)
    {
        if (renderComponent == null) return;
        try
        {
            var lcd = renderComponent.GetType().GetField("_lcdBlock", Any)?.GetValue(renderComponent);
            if (lcd == null) return;

            var entity = Prop(lcd, "Entity");
            DumpNameSources(renderComponent, entity, lcd);

            // SURFACE TEXT ONLY (2026-08-01, user). No block-name fallback: the tag lives in
            // the screen's own text field and nowhere else, so a block with no tagged surface
            // is simply not ours. FeedRouter owns both the scan and the key, because it is
            // what tells this method which feed it is running as — two copies of the rule is
            // how the route and the claim disagreed earlier today.
            string blockName = Prop(entity, "DebugName") as string;
            int surfaceIndex = FeedRouter.FindTaggedSurface(renderComponent, out string surfaceText, out int surfaceFeed);
            if (surfaceIndex < 0) return;                       // no tagged screen on this block

            string name = FeedRouter.SurfaceKey(surfaceFeed, surfaceIndex, entity);
            if (string.IsNullOrEmpty(name)) return;

            // Log every distinct panel once — makes "why isn't it finding my panel"
            // answerable without guessing at where the name lives. Says WHICH SURFACE and
            // WHAT WAS TYPED on it, because with multi-surface blocks "the panel" is no
            // longer a thing that has one answer.
            if (_seenNames.Add(name) && _seenNames.Count <= 20)
                RttLog.Line($"LCD panel seen: \"{name}\"   <-- TAGGED " +
                            (surfaceFeed < 0 ? "(unnumbered — takes the next free feed)" : $"feed {surfaceFeed}") +
                            $", from SURFACE {surfaceIndex}'s text (\"{surfaceText}\") on block \"{blockName}\"");

            // ONE tag test for the whole mod (FeedRouter.IsFeedPanel), so [RTC2] cannot be
            // recognised here and quietly ignored by the panel-render hook — which is exactly
            // the kind of split that makes a panel "found" in the log and black on screen.
            if (!FeedRouter.IsFeedPanel(name)) return;

            // DOES THE TAG STILL AGREE WITH THE ROUTE WE ARE SCOPED TO?
            //
            // BlitProbe.OnTick entered a feed scope before calling us, using FeedRouter's
            // component cache — and that cache was built the first time this component was
            // seen. Fine while the tag lived in a block name. Now that it lives in an editable
            // TEXT FIELD, retagging is the normal way to configure the mod, and the cache is
            // the thing standing between the user's edit and the mod noticing it.
            //
            // Observed 2026-08-01: a panel retagged [RTC] -> [RTC2] produced the claim
            // "[RTC2] #0 @block" recorded ON FEED 0 — the fresh name and the stale route
            // disagreeing inside a single log line — leaving feed 1 with no panel to aim at.
            //
            // Correct the caches and let the NEXT tick arrive properly scoped, rather than
            // re-entering a scope in the middle of a half-done claim. One dropped tick, ~16 ms,
            // against a mis-scoped one that would write this panel's identity into the wrong
            // feed's state.
            // ResolveByName, not FeedForIndex: an UNNUMBERED tag has no index to resolve, and
            // its feed is whatever the router auto-assigned and cached. The router is the one
            // place that knows, for both forms.
            {
                var want = FeedRouter.ResolveByName(name);
                if (!ReferenceEquals(want, Feeds.Cur))
                {
                    int wasId = Feeds.Cur.Id;
                    FeedRouter.Recache(renderComponent, name, want);
                    if (_rerouteLogs++ < 8)
                        RttLog.Line($"Feed routing CHANGED: \"{name}\" now belongs to FEED {want.Id} " +
                                    $"(was feed {wasId}). The tag on the screen was edited, so the route " +
                                    "follows it. This tick is dropped; the next one arrives correctly " +
                                    "scoped and the panel re-claims its surface for its new feed.");
                    return;
                }
            }

            // THE LIVENESS SIGNAL for the whole mod.
            //
            // The first version stamped unconditionally here, on the assumption that
            // switching a panel off stops its render component ticking. IT DOES NOT — the
            // component keeps ticking to draw the powered-off screen, so the mod never
            // went dormant and a whole A/B test was run against a fully active mod. The
            // absence of a tick is not the absence of a panel.
            //
            // The engine states it explicitly instead: every surface carries
            // CurrentMaterialState, an LcdPanelRenderState of PowerOff=0, DefaultScreen=1
            // or CustomRender=2. A panel is alive to us only while at least one of its
            // surfaces is out of PowerOff.
            if (!IsPanelPowered(renderComponent, surfaceIndex)) return;
            FeedGate.NotePanelAlive();

            // FIRST CLAIMANT WINS (phase E2 fan-out). With two panels routed to one feed,
            // letting every tick publish made the orbit target, the captured panel RT and
            // LastRenderComponent thrash between the panels — last claimant wins, twice a
            // frame, and on different grids that is a camera oscillating between two ships.
            // The feed's IDENTITY follows the panel that claimed it first; later claimants
            // are display-only mirrors (their surfaces still register below, which is what
            // routes their material bind). Cleared in Reset, so a gate cycle re-elects
            // from whatever is actually ticking — a destroyed primary hands over within
            // one cycle.
            // LOCKED, because the claim set is now touched from BOTH threads (phase F3).
            // This tick runs on the LCD thread; ExpireClaims runs from the render thread's
            // per-frame pump, every frame. A Dictionary being written on one thread while
            // another rehashes it is the classic silent corruption — a lost entry if you are
            // lucky, a spin inside a bucket chain if you are not. The lock is uncontended in
            // the normal case and covers a handful of instructions.
            //
            // The ELECTION is inside it too: `??=` is a read-then-write, and two panels
            // claiming the same feed in the same instant could otherwise both believe they
            // won.
            var feed = Feeds.Cur;
            bool primary;
            lock (feed.ClaimedPanels)
            {
                feed.ClaimedPanels[name] = Clock.Ms;   // stamped, so the claim can expire
                feed.PrimaryPanelName ??= name;
                primary = string.Equals(feed.PrimaryPanelName, name, StringComparison.OrdinalIgnoreCase);
            }

            // Remember this feed's surface so the render-side hook can recognise it by
            // identity — the surface map is what routes a panel's content pass to this feed
            // so its material can bind.
            //
            // ONLY THE TAGGED SURFACE, when one carries the tag. Registering every surface of
            // the block was right while a block meant a screen, and is wrong the moment it
            // does not: on a command seat it would hand the mod six screens when the user
            // asked for one, and on a block whose other surface is a stats or status display
            // it claims a screen that is doing something else. Falls back to all surfaces
            // only for the old block-name tagging, where nothing identifies a single screen.
            var surfaces = renderComponent.GetType().GetField("_surfaces", Any)?.GetValue(renderComponent);
            int added = 0;
            if (surfaces is System.Collections.IEnumerable list)
            {
                int si = -1;
                foreach (var s in list)
                {
                    si++;
                    if (s == null) continue;
                    if (surfaceIndex >= 0 && si != surfaceIndex) continue;   // not the tagged one

                    // NO "already seen, skip". TrackSurface does two things — it adds to the
                    // global seen-set AND it records WHICH FEED owns this surface — and the
                    // second one has to be re-asserted every tick, because ownership can move.
                    //
                    // Skipping on _targetSurfaces.Contains meant a panel retagged from [RTC]
                    // to [RTC2] never re-claimed its surface: the seen-set still held it from
                    // its old life, so ClaimSurface was never called and _bySurface went on
                    // naming feed 0 as the owner. The panel-render hook then scoped that
                    // panel's binding to the OLD feed while discovery claimed it for the new
                    // one, and the two feeds bound and unbound the same panel against each
                    // other. Reported 2026-08-01 as a corrupted, static image on the feed.
                    //
                    // Both operations are idempotent — a HashSet add and a dictionary write —
                    // so re-asserting costs nothing and is the only version that is correct
                    // when the tag is a thing the user edits.
                    TrackSurface(s);
                    added++;
                }
            }

            if (!primary)
            {
                bool announce; lock (_mirrorLogs) announce = _mirrorLogs.Add(name);
                if (announce)
                    RttLog.Line($"Feed {feed.Id}: panel \"{name}\" MIRRORS this feed ({added} surfaces registered) — " +
                                $"it shows \"{feed.PrimaryPanelName}\"'s camera. Display only; the orbit target is unchanged.");
                // The mirror still needs repaints while its bind is pending — the bind
                // runs inside the content-render hook, which an idle panel never enters.
                if (PanelBinding.WantsRepaint) ForceRepaint(renderComponent);
                return;
            }

            var pos = WorldPositionOf(entity);
            if (pos == null) return;

            // Orbit the SHIP, not the panel. The panel's position is a point on the
            // hull, so a 100 m circle centred there spends most of its arc inside the
            // grid — which is the clipping seen on the feed, not a projection bug.
            var (centre, extent) = GridBounds(entity, pos.Value);

            // PHASE J: ORBIT SOMEWHERE ELSE.
            //
            // Everything above found the PANEL and its own grid, which is still exactly what
            // we want for the panel binding, the claim key and the render-target capture.
            // Only the orbit CENTRE moves. Keeping the two separate is the point: the feed is
            // still delivered to this panel, on this grid, next to the player — it is merely
            // looking somewhere else.
            //
            // This is the configuration goal 10 has never been able to test. Up to now the
            // camera has always been ~100 m from the player, so "nothing disappeared from the
            // feed" was never evidence about streaming; there was simply never any distance.
            var anchor = WorldGrids.ResolveAnchor(entity);
            if (anchor.HasValue)
            {
                // BOTH Position AND Centre, deliberately. The camera pass gates on
                // `OrbitGrid && Extent > 0` and orbits Target.Position when the gate is
                // false — and extent lookups fail quietly (the world survey printed 0 m for
                // every grid). The first deploy of this feature set only Centre, the gate
                // chose Position, and the camera silently never left the base: the ORBIT
                // ANCHOR log said "re-centred" while the flash detector showed no 273 km
                // eye jump. Overriding both makes the anchor independent of which side of
                // that gate the pass takes.
                pos = anchor.Value.Position;
                centre = anchor.Value.Position;
                // Take the anchor's own size when it has one. A 100 m orbit around a station
                // whose hull is 200 m across spends its whole arc inside the hull — the same
                // clipping the "orbit the SHIP, not the panel" comment above was written for.
                if (anchor.Value.Extent > 0) extent = anchor.Value.Extent;
            }

            // Built fully, then published in one reference write.
            // PLANET RADIAL FIRST, block rotation only as a fallback. The planet's centre
            // gives the surface normal by definition; a grid's own up is the surface normal
            // only if it was built gravity-aligned, which is an assumption and was wrong here.
            // In space there is no planet and the block rotation is the sensible answer.
            // The render thread publishes this (see WholeSceneRender's planet-env rebuild); the
            // planet group it needs is not reachable from this thread. Radial from the planet
            // centre is the surface normal by definition and beats every other candidate.
            var planetUp = PlanetUpCache.LengthSquared() > 0.5 ? PlanetUpCache : (Vector3D?)null;
            var chosenUp = planetUp ?? _lastUp;

            _target = new Target
            {
                Position = pos.Value,
                Centre = centre,
                Extent = extent,
                Name = name,
                Up = chosenUp,
            };
            OrbitUp = chosenUp;
            OrbitUpIsPlanet = planetUp.HasValue;
            SubjectCentreCache = centre;      // for the render thread's planet scan
            EverFound = true;

            // The inventory runs from HERE because this is the one place holding a live
            // entity, which is the only handle we have on the Scene. Request is consumed
            // once (TakeWorldGridSurveyRequest), so a `worldGridSurvey = 1` left in the file
            // does not re-dump on every poll.
            if (FeedConfig.TakeWorldGridSurveyRequest()) WorldGrids.DumpGrids(entity);

            if (_findLogs++ < 3)
                RttLog.Line($"[RTC] panel located: \"{name}\" at {pos.Value.X:F1},{pos.Value.Y:F1},{pos.Value.Z:F1} ({added} surfaces registered)");

            LastRenderComponent = renderComponent;
            CapturePanelRenderTarget(renderComponent, surfaceIndex);

            // A panel only borrows a render target when it has content to paint. After
            // an out-of-range eviction nothing marks it dirty again, so without this it
            // never re-acquires one and the feed stays dark forever. Driving repaints
            // only while the target is missing keeps the cost near zero.
            //
            // Also repaint while a material rebind is pending: the bind runs inside the
            // content-render hook, and an idle panel never enters it.
            if (_panelRt == null || PanelBinding.WantsRepaint) ForceRepaint(renderComponent);
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("camera feed discovery", e); }
    }

    // ---- THE TAG LIVES IN THE SURFACE'S TEXT FIELD (design decision, 2026-08-01) --------
    //
    // Stated by the user, and it settles a mismatch that has been in the code since the
    // beginning: discovery keyed on the BLOCK (entity DebugName, falling back to surface 0's
    // display name) while the render-side hook keyed on the SURFACE (ctx.State.Text). Those
    // agree on a one-screen LCD and disagree on everything else — and "everything else" is
    // where this mod is going: a command seat carries many surfaces, and the feed has to be
    // able to name ONE of them.
    //
    // WHY THE TEXT FIELD, specifically. SE2 has no LCD app-selection screen yet. When Keen
    // ships one, that becomes the natural place to choose "this screen shows a camera feed"
    // and this whole mechanism should move there. Until then the text field is the only
    // per-surface place a user can type something the mod can read, which makes it the
    // selector by elimination rather than by preference. Recorded so the eventual move is a
    // deliberate migration and not a rediscovery.
    //
    // Returns the index of the first surface whose TEXT carries a feed tag, or -1 if none —
    // in which case the caller falls back to the block name, so every world tagged the old
    // way keeps working.
    private static int FindTaggedSurface(object renderComponent, out string tagText, out int feedIndex)
    {
        tagText = null;
        feedIndex = 0;
        try
        {
            if (renderComponent.GetType().GetField("_surfaces", Any)?.GetValue(renderComponent)
                is not System.Collections.IEnumerable list) return -1;

            int i = -1;
            foreach (var s in list)
            {
                i++;
                if (s == null) continue;
                var text = Prop(Prop(s, "State"), "Text") as string;
                if (string.IsNullOrEmpty(text)) continue;
                if (!FeedRouter.TryParseTag(text, out int idx)) continue;
                tagText = text;
                feedIndex = idx;
                return i;
            }
        }
        catch { }
        return -1;
    }

    // A claim key that is unique PER SURFACE and still carries its tag.
    //
    // The tag comes first on purpose. FeedRouter.TryParseTag takes the FIRST "[RTCn]" it
    // finds, and a block called "LCD Panel [RTC]" whose surface 3 reads "[RTC2]" would
    // otherwise resolve to feed 0 — the block's tag winning over the surface's, which is
    // precisely the confusion this change exists to end.
    //
    // Normalised to [RTCn] rather than echoing the raw text, so "[RTC]" and "[RTC1]" are one
    // key and a user who writes "[RTC2] forward camera" does not get a claim key that changes
    // when they edit the prose after it.
    // feedIndex < 0 = the tag was UNNUMBERED ("[RTT]"), which is a meaningful state and must
    // survive into the key: the router reads the key back to decide between "you asked for
    // feed N" and "give me the next free feed", and baking a number in here would silently
    // turn every unnumbered panel into a request for feed 0.
    private static string SurfaceClaimKey(int feedIndex, int surfaceIndex, string blockName) =>
        feedIndex < 0
            ? $"[RTT] #{surfaceIndex} @{blockName ?? "?"}"
            : $"[RTT{feedIndex + 1}] #{surfaceIndex} @{blockName ?? "?"}";

    // TWO SOURCES OF TRUTH, NOT THREE (2026-08-01, user): the surface's TEXT (preferred, and
    // handled by the caller) and the BLOCK NAME. The surface DISPLAY NAME is explicitly
    // ignored.
    //
    // It used to be the second fallback here, because GS2 reads it. That made three places a
    // tag could live and only one of them visible in the terminal list the user actually tags
    // in — so a panel could be "found" via a display name nobody typed, and the log would name
    // a panel the user could not locate. Two sources are already one more than ideal; the
    // third bought nothing.
    private static string NameOf(object entity, object lcd) => Prop(entity, "DebugName") as string;

    // ---- WHERE DOES THE NAME THE USER TYPED ACTUALLY LIVE? ------------------------
    //
    // Asked because guessing was wrong once already. Removing GetSurfaceEffectiveDisplayName
    // on the instruction "the display name should be ignored" left DebugName as the only
    // block-level source — and DebugName returns the COMPOSITION name for a block the user
    // has not renamed ("LCDFlat150_ServerComposition"), not the name shown in the terminal.
    // Earlier in the same session the log read "LCD Panel [RTS]", and that string was coming
    // from the call I deleted. So the block-name tag went invisible.
    //
    // One line per LCD block, first time it is seen, listing every candidate side by side
    // with every surface's text. Costs a handful of reflection calls once per block per
    // session and replaces a guess with a fact.
    private static readonly HashSet<string> _nameDump = new();

    private static void DumpNameSources(object renderComponent, object entity, object lcd)
    {
        try
        {
            var dbg = Prop(entity, "DebugName") as string;

            // DEDUPE ON SOMETHING UNIQUE. The first version keyed on DebugName alone, and
            // DebugName is the COMPOSITION name — every LCD of the same model shares it. So
            // two different panels on one grid collapsed to one line and the dump hid exactly
            // the panel being investigated. An instrument that silently drops half its
            // subjects is worse than none.
            string display = null;
            try
            {
                var dmi = lcd.GetType().GetMethod("GetSurfaceEffectiveDisplayName", Any);
                display = dmi?.Invoke(lcd, new object[] { 0 }) as string;
            }
            catch { }

            string key = dbg + "|" + display + "|" + Prop(Prop(renderComponent, "State"), "Text");
            if (!_nameDump.Add(key)) return;
            if (_nameDump.Count > 16) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("PANEL NAME SOURCES: DebugName=\"").Append(dbg).Append('"');

            foreach (var candidate in new[] { "DisplayName", "CustomName", "Name" })
            {
                var v = Prop(entity, candidate) as string ?? Prop(lcd, candidate) as string;
                if (v != null) sb.Append("  ").Append(candidate).Append("=\"").Append(v).Append('"');
            }

            try
            {
                var mi = lcd.GetType().GetMethod("GetSurfaceEffectiveDisplayName", Any);
                if (mi != null)
                    sb.Append("  SurfaceDisplayName(0)=\"")
                      .Append(mi.Invoke(lcd, new object[] { 0 }) as string).Append('"');
            }
            catch { }

            if (renderComponent.GetType().GetField("_surfaces", Any)?.GetValue(renderComponent)
                is System.Collections.IEnumerable surfaces)
            {
                int i = -1;
                foreach (var s in surfaces)
                {
                    i++;
                    if (s == null) continue;
                    sb.Append("  |surface ").Append(i).Append(" text=\"")
                      .Append(Prop(Prop(s, "State"), "Text") as string).Append('"');
                }
            }

            RttLog.Line(sb.ToString());
        }
        catch { }
    }

    // block -> grid -> GetWorldTransform(blockPosition) -> Position
    // Each step reports itself once: this failed silently on the first run, and a
    // null at the end says nothing about which link broke.
    private static bool _posDiagLogged;

    // The up vector read from the last world transform we walked, and the flag saying whether
    // it is trustworthy. Set by WorldPositionOf, consumed when the Target is published.
    private static Vector3D _lastUp;

    // Pull an up vector out of an engine WorldTransform, whatever shape it turns out to be.
    //
    // Tried in order of directness. Logged once with WHICH candidate answered, because the
    // failure mode of a wrong guess here is a subtly tilted orbit rather than an exception,
    // and this project has paid for silently-wrong reflection more than once today.
    // The GRID's up — the base's own orientation, which on a planet is gravity-aligned and so
    // is the surface normal. Distinct from any single block's rotation.
    //
    // Dumps the grid's orientation-bearing members once when it cannot find one, because
    // three different sources have now been tried for this vector and each silently produced
    // a plausible-but-wrong answer. A wrong up here is never an exception, only a tilted ring.
    private static bool _gridUpDumped;

    private static Vector3D GridUp(object grid, bool diag)
    {
        if (grid == null) return default;
        try
        {
            // A world matrix, by whatever name, on the grid or on a position component.
            foreach (var host in new[] { grid, Prop(grid, "PositionComp"), Prop(grid, "Entity") })
            {
                if (host == null) continue;
                foreach (var member in new[] { "WorldMatrix", "WorldTransform", "PositionAndOrientation", "Transform" })
                {
                    var m = Prop(host, member);
                    if (m == null) continue;

                    // A matrix exposes Up directly; a transform exposes a rotation.
                    if (Prop(m, "Up") is Vector3D mu && mu.LengthSquared() > 0.5)
                    {
                        if (diag) RttLog.Line($"  orbit up: GRID {member}.Up = {mu.X:F3},{mu.Y:F3},{mu.Z:F3} " +
                                              "— the base's own orientation, gravity-aligned on a planet.");
                        return Normalize(mu);
                    }
                    var viaRot = UpFromTransform(m, false);
                    if (viaRot.LengthSquared() > 0.5)
                    {
                        if (diag) RttLog.Line($"  orbit up: GRID {member} rotation -> {viaRot.X:F3},{viaRot.Y:F3},{viaRot.Z:F3}");
                        return viaRot;
                    }
                }
            }

            if (!_gridUpDumped)
            {
                _gridUpDumped = true;
                var sb = new System.Text.StringBuilder();
                sb.Append("  orbit up: NO grid orientation found. Grid type ").Append(grid.GetType().FullName)
                  .Append(" — members that could carry one:");
                foreach (var p in grid.GetType().GetProperties(Any))
                    if (p.PropertyType.Name.Contains("Matrix") || p.PropertyType.Name.Contains("Transform")
                        || p.PropertyType.Name.Contains("Quaternion") || p.Name.Contains("World")
                        || p.Name.Contains("Orient") || p.Name.Contains("Position"))
                        sb.Append("\n    prop  ").Append(p.PropertyType.Name).Append(' ').Append(p.Name);
                foreach (var f in grid.GetType().GetFields(Any))
                    if (f.FieldType.Name.Contains("Matrix") || f.FieldType.Name.Contains("Transform")
                        || f.FieldType.Name.Contains("Quaternion") || f.Name.Contains("World")
                        || f.Name.Contains("Orient"))
                        sb.Append("\n    field ").Append(f.FieldType.Name).Append(' ').Append(f.Name);
                RttLog.Line(sb.ToString());
            }
        }
        catch { }
        return default;
    }

    private static Vector3D UpFromTransform(object wt, bool diag)
    {
        try
        {
            if (Prop(wt, "Up") is Vector3D up && up.LengthSquared() > 0.5)
            {
                if (diag) RttLog.Line($"  orbit up: from WorldTransform.Up = {up.X:F3},{up.Y:F3},{up.Z:F3}");
                return Normalize(up);
            }

            // Rotation quaternion: up = q * (0,1,0). Written out rather than reached for a
            // helper, because the engine's quaternion type is not one we reference.
            var rot = Prop(wt, "Rotation") ?? Prop(wt, "Orientation");
            if (rot != null)
            {
                double x = ToD(Prop(rot, "X")), y = ToD(Prop(rot, "Y")),
                       z = ToD(Prop(rot, "Z")), w = ToD(Prop(rot, "W"));
                if (x * x + y * y + z * z + w * w > 0.5)
                {
                    // All three axes, and the raw quaternion, because "the derived up is near
                    // world Y" has two very different causes and they need different fixes:
                    // either the subject really is roughly world-Y-up, or the transform we are
                    // reading carries an IDENTITY rotation and we are extracting world Y out
                    // of nothing. A quaternion near (0,0,0,1) is the second case.
                    var vx = new Vector3D(1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y + w * z), 2.0 * (x * z - w * y));
                    var vy = new Vector3D(2.0 * (x * y - w * z), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z + w * x));
                    var vz = new Vector3D(2.0 * (x * z + w * y), 2.0 * (y * z - w * x), 1.0 - 2.0 * (x * x + y * y));

                    if (diag)
                    {
                        bool identity = Math.Abs(w) > 0.999 && x * x + y * y + z * z < 1e-4;
                        RttLog.Line($"  orbit up: quaternion x={x:F4} y={y:F4} z={z:F4} w={w:F4}" +
                                    (identity ? "  <-- IDENTITY: this transform carries NO rotation, so the " +
                                                "'up' below is just world Y and the orbit plane is not " +
                                                "following the surface. Need a different source."
                                              : "") +
                                    $"\n    axisX={vx.X:F3},{vx.Y:F3},{vx.Z:F3}" +
                                    $"  axisY(up)={vy.X:F3},{vy.Y:F3},{vy.Z:F3}" +
                                    $"  axisZ={vz.X:F3},{vz.Y:F3},{vz.Z:F3}");
                    }
                    return Normalize(vy);
                }
            }

            if (diag) RttLog.Line("  orbit up: NO up on the world transform — the orbit stays in the " +
                                  "world XZ plane, which tips into the terrain on a planet. Members: " +
                                  string.Join(", ", wt.GetType().GetProperties(Any).Take(12).Select(pp => pp.Name)));
        }
        catch { }
        return default;   // zero = "unknown", caller falls back to world Y
    }

    private static double ToD(object o)
    {
        try { return o == null ? 0.0 : Convert.ToDouble(o); } catch { return 0.0; }
    }

    private static Vector3D? WorldPositionOf(object entity)
    {
        bool diag = !_posDiagLogged;
        _posDiagLogged = true;
        try
        {
            var blockType = Type.GetType(
                "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
            if (diag) RttLog.Line($"  pos: entity={(entity == null ? "null" : entity.GetType().Name)} blockType={(blockType == null ? "NOT FOUND" : "ok")}");
            if (blockType == null || entity == null) return null;

            // Entity.TryGet<T> is declared as TryGet<T>(StringId tag = default), so
            // reflection sees one parameter even though C# calls it with none. Match
            // on 0 or 1 parameters and supply the default when there is one.
            var tryGet = entity.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "TryGet" && m.IsGenericMethodDefinition
                                  && m.GetParameters().Length <= 1
                                  && m.ReturnType.IsGenericParameter);
            if (tryGet == null)
                tryGet = entity.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "TryGet" && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length <= 1);
            if (diag)
            {
                var all = entity.GetType().GetMethods(Any).Where(m => m.Name is "TryGet" or "Get").ToList();
                RttLog.Line($"  pos: TryGet<T>={(tryGet == null ? "NOT FOUND" : $"ok ({tryGet.GetParameters().Length} params)")}" +
                            $"  [candidates: {string.Join(" | ", all.Select(m => $"{m.Name}{(m.IsGenericMethodDefinition ? "<T>" : "")}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})"))}]");
            }
            if (tryGet == null) return null;

            var closed = tryGet.MakeGenericMethod(blockType);
            var args = closed.GetParameters().Length == 0
                ? null
                : new object[] { DefaultOf(closed.GetParameters()[0].ParameterType) };
            var block = closed.Invoke(entity, args);
            if (diag) RttLog.Line($"  pos: block={(block == null ? "NULL" : block.GetType().Name)}");
            if (block == null) return null;

            var grid = Prop(block, "Grid");
            var aabb = Prop(block, "AABB");
            var min = aabb == null ? null : Prop(aabb, "Min");
            if (diag) RttLog.Line($"  pos: grid={(grid == null ? "NULL" : "ok")} aabb={(aabb == null ? "NULL" : "ok")} min={min}");
            if (grid == null || min == null) return null;

            var gwt = grid.GetType().GetMethod("GetWorldTransform", Any);
            if (diag) RttLog.Line($"  pos: GetWorldTransform={(gwt == null ? "NOT FOUND" : "ok")}");
            var wt = gwt?.Invoke(grid, new[] { min });
            if (diag) RttLog.Line($"  pos: worldTransform={(wt == null ? "NULL" : wt.GetType().Name)}");
            if (wt == null) return null;

            // THE LOCAL UP, from the same transform (2026-08-01). The orbit used to be built
            // in the world XZ plane around world +Y, which is arbitrary-but-harmless in space
            // and wrong on a planet: local up is radial from the planet centre, so a world-Y
            // orbit tips into the ground and spends half its arc underneath the terrain.
            // Reported exactly that way.
            //
            // The grid's own up is the right proxy and needs no planet lookup: a base built on
            // a planet is gravity-aligned, so its up IS the surface normal, and for a ship in
            // space "orbit in the hull's horizontal plane" is a better answer than world Y
            // anyway. Cached on the Target so the render side does not re-walk this.
            // THE GRID'S OWN ORIENTATION, not this block's.
            //
            // GetWorldTransform is being handed the BLOCK's AABB min, so what comes back is
            // the transform AT THAT BLOCK — including the block's own rotation. For a
            // wall-mounted LCD that is the screen's facing, which has nothing to do with
            // which way the ground is, and it is why the orbit ring kept coming out tilted
            // while measuring perfectly flat against the up it was given. The user spotted
            // this before I did.
            //
            // A base built on a planet is gravity-aligned, so the GRID's up is the surface
            // normal. Dumped once if it cannot be found, rather than silently falling back
            // to the block again.
            _lastUp = GridUp(grid, diag);
            if (_lastUp.LengthSquared() < 0.5) _lastUp = UpFromTransform(wt, diag);

            var p = Prop(wt, "Position");
            if (diag) RttLog.Line($"  pos: Position={(p == null ? "NULL" : p.GetType().Name + " " + p)}");
            if (p is Vector3D v) return v;
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("panel world position", e); }
        return null;
    }

    // ------------------------------------------------------------- grid bounds
    // The grid's world centre and half-extent, so the orbit can clear the hull.
    //
    // Names are discovered rather than assumed: the first run dumps every member
    // that looks like a bounding volume, so a miss is one log line away from a fix
    // instead of a guessing session.
    private static bool _boundsDiag;

    // PER-FEED (phase C1a): the memoised bounds of THIS feed's grid. Keyed on the grid
    // object and a timestamp, so two feeds orbiting two different grids must not share
    // one slot — that would hand feed B feed A's orbit radius for a whole cache window.
    private static object _boundsGrid
    { get => Feeds.Cur.BoundsGrid; set => Feeds.Cur.BoundsGrid = value; }
    private static (Vector3D Centre, double Extent) _boundsCache
    { get => Feeds.Cur.BoundsCache; set => Feeds.Cur.BoundsCache = value; }
    private static long _boundsAt
    { get => Feeds.Cur.BoundsAt; set => Feeds.Cur.BoundsAt = value; }

    private static (Vector3D Centre, double Extent) GridBounds(object entity, Vector3D fallback)
    {
        try
        {
            var block = BlockOf(entity);
            var grid = Prop(block, "Grid");
            if (grid == null) return (fallback, 0);

            // Grids move; re-read periodically rather than every tick.
            if (ReferenceEquals(grid, _boundsGrid) && Clock.Ms - _boundsAt < 1000) return _boundsCache;
            _boundsGrid = grid; _boundsAt = Clock.Ms;

            // The narrow name filter found nothing on CubeGridComponent, so dump the
            // whole surface once — to a file, not the log, because it is long and the
            // log is where crash forensics live.
            if (!_boundsDiag)
            {
                _boundsDiag = true;
                var sb = new StringBuilder($"=== {grid.GetType().FullName} ===\n\n-- properties --\n");
                foreach (var p in grid.GetType().GetProperties(Any).OrderBy(p => p.Name))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object v = null; try { v = p.GetValue(grid); } catch { }
                    sb.AppendLine($"  {p.PropertyType.Name,-30} {p.Name,-34} = {Short(v)}");
                }
                sb.AppendLine("\n-- fields --");
                foreach (var f in grid.GetType().GetFields(Any).OrderBy(f => f.Name))
                {
                    object v = null; try { v = f.GetValue(grid); } catch { }
                    sb.AppendLine($"  {f.FieldType.Name,-30} {CleanName(f.Name),-34} = {Short(v)}");
                }
                sb.AppendLine("\n-- zero-arg methods --");
                foreach (var m in grid.GetType().GetMethods(Any).OrderBy(m => m.Name))
                {
                    if (m.GetParameters().Length != 0 || m.ReturnType == typeof(void) || m.IsGenericMethod) continue;
                    sb.AppendLine($"  {m.ReturnType.Name,-30} {m.Name}()");
                }
                var path = Path.Combine(RttLog.OutDir, "grid-survey.txt");
                try { File.WriteAllText(path, sb.ToString()); RttLog.Line($"  orbit: grid={grid.GetType().Name}; full member survey -> {path}"); }
                catch (Exception e) { RttLog.Error("grid survey", e); }
            }

            // A world-space box or sphere is ideal; anything with Min/Max or
            // Center/Radius will do.
            foreach (var name in new[] { "WorldAABB", "WorldBoundingBox", "WorldVolume", "WorldBoundingSphere", "AABB", "BoundingBox" })
            {
                var b = Prop(grid, name);
                if (b == null) continue;

                var min = AsVec(Prop(b, "Min"));
                var max = AsVec(Prop(b, "Max"));
                if (min != null && max != null)
                {
                    var c = new Vector3D((min.Value.X + max.Value.X) * 0.5,
                                         (min.Value.Y + max.Value.Y) * 0.5,
                                         (min.Value.Z + max.Value.Z) * 0.5);
                    double half = Length(new Vector3D(max.Value.X - min.Value.X,
                                                      max.Value.Y - min.Value.Y,
                                                      max.Value.Z - min.Value.Z)) * 0.5;

                    // Local (cell) coordinates are small numbers near the origin while
                    // world coordinates out here are ~250 km. If the box is clearly
                    // local, transform its centre through the grid.
                    if (Length(c) < 10000.0)
                    {
                        var w = WorldOfCell(grid, Prop(b, "Min"), Prop(b, "Max"));
                        if (w != null) { c = w.Value; half *= 2.5; }   // cells -> metres
                        else return (fallback, 0);
                    }
                    _boundsCache = (c, half);
                    LogOrbit(name, c, half);
                    return _boundsCache;
                }

                var ctr = AsVec(Prop(b, "Center")) ?? AsVec(Prop(b, "Centre"));
                if (ctr != null && Prop(b, "Radius") is double r)
                {
                    _boundsCache = (ctr.Value, r);
                    LogOrbit(name, ctr.Value, r);
                    return _boundsCache;
                }
            }
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("grid bounds", e); }
        return (fallback, 0);
    }

    private static bool _orbitLogged;

    private static void LogOrbit(string via, Vector3D c, double extent)
    {
        if (_orbitLogged) return;
        _orbitLogged = true;
        RttLog.Line($"  orbit: centred on the grid via {via} — centre {c.X:F1},{c.Y:F1},{c.Z:F1} extent {extent:F1}m");
    }

    // Midpoint of the grid's cell-space box, in world coordinates.
    private static Vector3D? WorldOfCell(object grid, object minCell, object maxCell)
    {
        try
        {
            var gwt = grid.GetType().GetMethod("GetWorldTransform", Any);
            if (gwt == null) return null;
            var a = AsVec(Prop(gwt.Invoke(grid, new[] { minCell }), "Position"));
            var b = AsVec(Prop(gwt.Invoke(grid, new[] { maxCell }), "Position"));
            if (a == null || b == null) return null;
            return new Vector3D((a.Value.X + b.Value.X) * 0.5,
                                (a.Value.Y + b.Value.Y) * 0.5,
                                (a.Value.Z + b.Value.Z) * 0.5);
        }
        catch { return null; }
    }

    // Survey helpers: keep one line per member, however large the value.
    private static string Short(object v)
    {
        if (v == null) return "null";
        var s = v.ToString() ?? "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 90 ? s[..90] + "..." : s;
    }

    private static string CleanName(string n)
    {
        int a = n.IndexOf('<'), b = n.IndexOf('>');
        return (a == 0 && b > 1) ? n[1..b] : n;
    }

    private static Vector3D? AsVec(object o) => o is Vector3D v ? v : null;
    private static double Length(Vector3D v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    // The block component, via the same TryGet<T> dance WorldPositionOf uses.
    private static MethodInfo _closedTryGet;

    private static object BlockOf(object entity)
    {
        try
        {
            if (_closedTryGet == null)
            {
                var blockType = Type.GetType(
                    "Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent, Game2.Simulation");
                var tryGet = entity.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "TryGet" && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length <= 1);
                if (blockType == null || tryGet == null) return null;
                _closedTryGet = tryGet.MakeGenericMethod(blockType);
            }
            var args = _closedTryGet.GetParameters().Length == 0
                ? null
                : new object[] { DefaultOf(_closedTryGet.GetParameters()[0].ParameterType) };
            return _closedTryGet.Invoke(entity, args);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------- camera
    // An orbit around the ship, always looking at its centre. Returns the camera's
    // world matrix (view->world); the caller inverts it for the view matrix.
    //
    // Radius is a clearance, not a literal distance: orbiting at exactly the
    // configured 100 m around a grid whose half-diagonal is 80 m puts the camera
    // inside the hull for much of the arc. Take whichever is larger.
    public static MatrixD OrbitCameraWorld(Vector3D target, double timeSeconds)
        => OrbitCameraWorld(target, 0, timeSeconds);

    public static MatrixD OrbitCameraWorld(Vector3D target, double extent, double timeSeconds)
    {
        double period = FeedConfig.OrbitPeriod, height = FeedConfig.OrbitHeight;
        double radius = Math.Max(FeedConfig.OrbitRadius, extent * FeedConfig.OrbitClearance);

        double a = 2.0 * Math.PI * (timeSeconds % period) / period;

        // THE ORBIT PLANE IS PERPENDICULAR TO LOCAL UP, not to world Y (2026-08-01).
        //
        // The old form added radius on world X/Z and height on world Y, so the circle always
        // lay in the world XZ plane. In space that is arbitrary and nobody notices. On a
        // planet local up is radial from the planet centre and is almost never world Y, so
        // the circle tips into the ground and the camera spends half of every lap under the
        // terrain — which is exactly what was reported.
        //
        // Local up comes from the subject's own world transform (see UpFromTransform): a
        // planet-side base is gravity-aligned, so its up IS the surface normal. Zero means we
        // could not read one, and world Y is then the same behaviour as before.
        var upAxis = OrbitUp.LengthSquared() > 0.5 ? Normalize(OrbitUp) : new Vector3D(0, 1, 0);

        // Any two axes spanning the plane. Seeded from whichever world axis is least parallel
        // to up, so the cross product never degenerates — picking a fixed seed would collapse
        // exactly when up happens to align with it.
        var seed = Math.Abs(upAxis.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
        var axis1 = Normalize(Cross(upAxis, seed));
        var axis2 = Cross(upAxis, axis1);

        var eye = target
                + upAxis * (height + extent * 0.35)      // rise with the subject, along LOCAL up
                + axis1 * (radius * Math.Cos(a))
                + axis2 * (radius * Math.Sin(a));

        // ORBIT PLANE PROOF, because "it still looks perpendicular" and "the plane is now
        // perpendicular to the normal" are the same sentence read two ways, and neither of us
        // can settle it by looking at a screenshot.
        //
        // Decompose the eye's offset from the subject into its component ALONG local up and
        // its component ACROSS it. A HORIZONTAL orbit holds "along" constant at the configured
        // height while "across" stays at the radius. A VERTICAL one swings "along" between
        // +radius and -radius as the angle sweeps — and the negative half is the part that was
        // underground. One number per sample, and the answer is unambiguous.
        long nowMs = Clock.Ms;
        if (nowMs - _orbitPlaneDiagMs > 2000)
        {
            _orbitPlaneDiagMs = nowMs;
            var d = eye - target;
            double alongUp = d.X * upAxis.X + d.Y * upAxis.Y + d.Z * upAxis.Z;
            double acrossUp = Math.Sqrt(Math.Max(0.0, d.LengthSquared() - alongUp * alongUp));
            RttLog.Line($"Orbit plane: angle={a * 180.0 / Math.PI:F0}deg  along-up={alongUp:F1}m  " +
                        $"across-up={acrossUp:F1}m  up={upAxis.X:F2},{upAxis.Y:F2},{upAxis.Z:F2}  " +
                        $"(source={(OrbitUp.LengthSquared() <= 0.5 ? "WORLD Y fallback"
                                    : OrbitUpIsPlanet ? "PLANET RADIAL" : "subject transform")}). " +
                        "along-up CONSTANT across angles = orbit parallel to the surface; " +
                        "along-up swinging +/-radius = orbit still vertical.");
        }

        // Row 2 of the camera's world matrix points AWAY from the subject in this
        // engine's convention, not along the view direction. Building it as
        // (target - eye) — the intuitive "look at" vector — aimed the camera outward
        // from the orbit centre, so the feed showed everything except the ship.
        var fwd = Normalize(eye - target);

        // The camera's reference up is the LOCAL up too, so the horizon sits level in the
        // feed instead of rolling as the orbit goes round a planet.
        var worldUp = upAxis;
        var right = Normalize(Cross(worldUp, fwd));
        if (double.IsNaN(right.X)) right = new Vector3D(1, 0, 0);   // looking straight up/down
        var up = Cross(fwd, right);

        // NO 180-DEGREE ROLL HERE ANY MORE (2026-08-01).
        //
        // A roll was applied at this point because "the feed was upside down". It was a
        // stale compensation for the fwd bug fixed immediately above: while fwd was built
        // as (target - eye) the whole basis came out inverted, the roll cancelled it, and
        // when fwd was corrected nobody removed the cancellation. Two compensating fixes,
        // the second one silently making the first wrong.
        //
        // Worked through at angle 0 with up = world Y: eye sits at target + (0,130,-100),
        // giving fwd (0,0.79,-0.61), right (-1,0,0) and up (0,0.61,0.79). That up already
        // has positive Y, so the horizon is level and the sky is at the top of the frame.
        // Negating both axes from there is what put the sky at the BOTTOM, which is how
        // the planet capture came out: ground filling the frame with a bright horizon
        // along the bottom edge.

        // WHICH WAY IS UP IN THE FEED, AS A NUMBER.
        //
        // Two screenshots were read two different ways and both readings were wrong: at a 52
        // degree look-down the horizon is off the top of the frame entirely, so the pale band
        // in the picture is hazed distant terrain, not sky — and at eye level on a hillside
        // the bright edge is a hill silhouette, which can sit at any angle. Neither image can
        // settle the orientation, so stop asking them.
        //
        // The camera's up row IS the screen's up direction. Project local up onto the basis:
        //
        //     up.upRow  =  +cos(look-down)   upright        (0.61 at the 130/100 orbit)
        //                  -cos(look-down)   upside down
        //     up.rightRow != 0               rolled by atan2 of the two — a tilted horizon
        //
        // This measures the CAMERA only. If it reads upright and the panel still looks wrong,
        // the flip is downstream in the blit, and that is a different fix.
        if (nowMs - _orbitPlaneDiagMs == 0)      // same 2 s tick as the plane proof above
        {
            double upOnUp = upAxis.X * up.X + upAxis.Y * up.Y + upAxis.Z * up.Z;
            double upOnRight = upAxis.X * right.X + upAxis.Y * right.Y + upAxis.Z * right.Z;
            RttLog.Line($"Orbit orientation: up.upRow={upOnUp:F2} up.rightRow={upOnRight:F2} " +
                        $"=> roll={Math.Atan2(upOnRight, upOnUp) * 180.0 / Math.PI:F1}deg, " +
                        (upOnUp > 0 ? "UPRIGHT" : "UPSIDE DOWN") + " before projection. " +
                        "Expect roll ~0 and upRow=+cos(look-down); the look-down is " +
                        $"{Math.Atan2(height + extent * 0.35, radius) * 180.0 / Math.PI:F0}deg.");
        }

        // Camera world matrix: rows are the basis vectors, translation is the eye.
        var m = default(MatrixD);
        SetRow(ref m, 0, right, 0);
        SetRow(ref m, 1, up, 0);
        SetRow(ref m, 2, fwd, 0);
        SetRow(ref m, 3, eye, 1);
        return m;
    }

    private static void SetRow(ref MatrixD m, int row, Vector3D v, double w)
    {
        var t = typeof(MatrixD);
        string[] names = row switch
        {
            0 => new[] { "M11", "M12", "M13", "M14" },
            1 => new[] { "M21", "M22", "M23", "M24" },
            2 => new[] { "M31", "M32", "M33", "M34" },
            _ => new[] { "M41", "M42", "M43", "M44" },
        };
        object box = m;
        t.GetField(names[0])?.SetValue(box, v.X);
        t.GetField(names[1])?.SetValue(box, v.Y);
        t.GetField(names[2])?.SetValue(box, v.Z);
        t.GetField(names[3])?.SetValue(box, w);
        m = (MatrixD)box;
    }

    private static Vector3D Normalize(Vector3D v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len < 1e-9 ? new Vector3D(0, 0, 1) : new Vector3D(v.X / len, v.Y / len, v.Z / len);
    }

    private static Vector3D Cross(Vector3D a, Vector3D b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    // A panel showing "Online" is not in a mode that runs the custom content
    // renderer at all, so our draw hook is never reached. Switching it to a
    // text/image content type is what puts it on that path — the same move Grid
    // Schematics makes before it can draw.
    private static bool _takeoverDone, _takeoverDiag;

    // SetSurfaceContent / SetSurfaceText are replicated sync properties: writing
    // them from the render thread throws "Sync property was disabled by
    // ISignalTableBuilder". Grid Schematics can set them only because it calls from
    // a simulation-side tick. Rather than stand up a sim tick purely for this, the
    // panel is configured once from the game's own terminal UI, and we match on the
    // text the player typed. One attempt is made anyway in case a future build
    // allows it, then it stops rather than throwing every tick.
    private static bool _takeoverGaveUp;

    private static void TakeOverPanel(object lcd)
    {
        if (_takeoverDone || _takeoverGaveUp) return;
        try
        {
            var setContent = lcd.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "SetSurfaceContent" && m.GetParameters().Length == 2);
            if (setContent == null)
            {
                if (!_takeoverDiag) { _takeoverDiag = true; RttLog.Line("  takeover: SetSurfaceContent NOT FOUND"); }
                return;
            }

            var contentType = setContent.GetParameters()[1].ParameterType;
            object mode = null;
            if (contentType.IsEnum)
                foreach (var n in new[] { "TextAndImage", "Image", "Text", "Script" })
                    if (Enum.GetNames(contentType).Contains(n)) { mode = Enum.Parse(contentType, n); break; }

            if (!_takeoverDiag)
            {
                _takeoverDiag = true;
                RttLog.Line($"  takeover: SetSurfaceContent({contentType.Name}) modes=[{(contentType.IsEnum ? string.Join(",", Enum.GetNames(contentType)) : "not an enum")}] chose={mode ?? (object)"NONE"}");
            }
            if (mode == null) return;

            setContent.Invoke(lcd, new object[] { 0, mode });

            // Stamp the tag into the surface text. Surface contexts are re-created,
            // so the render side cannot match them by reference — it reads this text
            // instead, which is how Grid Schematics bridges the same gap.
            var setText = lcd.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "SetSurfaceText" && m.GetParameters().Length == 2);
            setText?.Invoke(lcd, new object[] { 0, Tag });

            _takeoverDone = true;
            RttLog.Line($"  takeover: [RTC] panel switched to custom content mode, text stamped \"{Tag}\".");
        }
        catch (Exception e)
        {
            _takeoverGaveUp = true;
            RttLog.Error("panel takeover (giving up — configure the panel in-game instead)", e);
            RttLog.Line($"  ACTION NEEDED: in the panel's terminal, set content to Text/Image and put {Tag} in its TEXT.");
        }
    }

    // LCD content is repainted only when something marks it dirty. A panel showing
    // static text never repaints, so the draw hook we rely on is never invoked.
    // Driving a live feed therefore means asking for a repaint every tick — the
    // same conclusion Grid Schematics reached for its 60 fps panels.
    private static bool _repaintDiag;
    private static System.Reflection.MethodInfo _rebuildMi;

    // The panel already owns an OffscreenRenderTarget and displays it every frame.
    // Writing the camera frame into *that* target means the panel shows it with no
    // material rebinding, no DrawImage (which rejects render-target handles and
    // kills the game), and no forced repaints. Deliberately NOT repainting is what
    // keeps our image on screen: the panel's own content would otherwise overwrite
    // it, and LCD content only repaints when marked dirty.
    // PER-FEED (phase C1a): boxed OffscreenRenderTarget belonging to THIS feed's panel.
    private static object _panelRt
    { get => Feeds.Cur.PanelRt; set => Feeds.Cur.PanelRt = value; }
    public static object PanelRenderTarget => _panelRt;

    // Called by the camera pass when it finds the target has been evicted. The tick
    // hook cannot do this: it stops firing when the panel leaves range.
    public static void ForgetPanelRenderTarget() => _panelRt = null;

    // The render component for the tagged panel, needed to refresh its material
    // replacements after a rebind.
    //
    // PER-FEED (phase F2). This was a plain static, so with two feeds whichever panel
    // ticked last owned it and PanelBinding.TryBind called UpdateMaterialReplacements on
    // the OTHER feed's block — refreshing a panel that had not been rebound while leaving
    // the one that had waiting for its own panel to happen to repaint. It is written only
    // by the primary election, which is the definition of a per-feed fact.
    public static object LastRenderComponent
    { get => Feeds.Cur.LastRenderComponent; private set => Feeds.Cur.LastRenderComponent = value; }

    // The Id of the render target this feed is copying frames INTO, for the [RTS] mirror
    // probe (BlitProbe.MirrorDiag). Text rather than a number so "no target" is expressible
    // without a sentinel, and readable straight out of the log next to the panel's own id.
    internal static string PanelRtIdText
    {
        get
        {
            var rt = _panelRt;
            if (rt == null) return "<none>";
            try { return Prop(rt, "Id")?.ToString() ?? "<no id>"; }
            catch { return "<err>"; }
        }
    }
    // PER-FEED: "did THIS feed capture its panel's render target" is the first thing that
    // has to be true for a panel to show anything, so it must be answerable per feed.
    private static bool _panelRtDiag
    { get => Feeds.Cur.PanelRtDiag; set => Feeds.Cur.PanelRtDiag = value; }

    // How many surfaces this block exposes — for the capture diagnostic below. A count of 1
    // rules out the multi-surface hazard entirely; anything higher makes "the first surface
    // with a target" a guess rather than a rule.
    private static int _surfaceCount(System.Collections.IEnumerable list)
    {
        int n = 0;
        try { foreach (var _ in list) n++; } catch { }
        return n;
    }

    // `wantIndex` = the surface whose TEXT carries the feed tag, or -1 when the tag came from
    // the block name and no single screen is identified.
    //
    // THIS IS WHERE THE FEED ACTUALLY LANDS. The handover copies our camera into the render
    // target captured here, so picking the wrong surface writes the feed onto the wrong
    // screen — and, because the panel keeps drawing its own content into that same target,
    // silently destroys whatever that screen was for. "The first surface that has a render
    // target" was a safe rule while a block meant a screen and a guess as soon as it did not.
    private static void CapturePanelRenderTarget(object renderComponent, int wantIndex)
    {
        try
        {
            var surfaces = renderComponent.GetType().GetField("_surfaces", Any)?.GetValue(renderComponent);
            if (surfaces is not System.Collections.IEnumerable list) return;

            // WHICH SURFACE, AND WHAT IS WRITTEN ON IT. "The first surface that has a render
            // target" is a fine rule while a block carries one screen and a fatal one if it
            // carries several: an LCD block exposes ALL its surfaces here, so a console whose
            // first surface is a stats or status screen would have the feed written straight
            // into it — the panel then shows the camera and whatever it was drawing itself is
            // overwritten. Reported 2026-08-01 (the [RTS] debug panel showing feed 0's
            // picture) and not diagnosable from the old log line, which named neither the
            // surface nor its text.
            int index = -1;
            foreach (var ctx in list)
            {
                index++;
                if (ctx == null) continue;
                if (wantIndex >= 0 && index != wantIndex) continue;   // the tagged screen only
                var rtField = ctx.GetType().GetField("RenderTarget", Any);
                var rt = rtField?.GetValue(ctx);
                if (rt == null) continue;

                // Nullable<OffscreenRenderTarget>: unwrap before use.
                var hasValue = rt.GetType().GetProperty("HasValue")?.GetValue(rt);
                if (hasValue is bool b && !b) continue;
                var value = rt.GetType().GetProperty("Value")?.GetValue(rt) ?? rt;

                // Reflected rather than cast: CameraFeed does not reference the LCD types
                // (BlitProbe does), and a diagnostic is not worth a new using on a file this
                // size. Prop walks properties then fields, so State.Text resolves either way.
                string surfaceText = null;
                try { surfaceText = Prop(Prop(ctx, "State"), "Text") as string; } catch { }

                _panelRt = value;
                if (!_panelRtDiag)
                {
                    _panelRtDiag = true;
                    RttLog.Line($"  panel RT captured: {value.GetType().Name} Id={Prop(value, "Id")} " +
                                $"valid={Prop(value, "IsValid")} from SURFACE {index} of {_surfaceCount(list)} " +
                                $"on this block, whose text is \"{surfaceText}\". If that text is not this " +
                                "feed's tag, the feed is being written into the wrong screen of a " +
                                "multi-surface block.");
                }
                return;
            }

            // No render target right now. The LCD system evicts them by distance, so
            // this happens whenever the player walks away — and continuing to use the
            // last one we saw means writing into a target that has been returned to
            // the pool. Clear it; the feed stops until the panel has one again.
            if (_panelRt != null)
            {
                _panelRt = null;
                RttLog.Line("  panel RT released (panel out of range?) — feed paused until it returns.");
            }
            if (!_panelRtDiag)
            {
                _panelRtDiag = true;
                RttLog.Line("  panel RT: none yet — the panel has no render target (needs custom content mode).");
            }
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("capture panel rt", e); }
    }

    private static System.Reflection.FieldInfo _ctxField;
    private static bool _ctxFieldIsKvp;

    private static void ForceRepaint(object renderComponent)
    {
        try
        {
            _rebuildMi ??= renderComponent.GetType().GetMethod("RebuildSurfaceContent", Any);
            if (_rebuildMi == null)
            {
                if (!_repaintDiag) { _repaintDiag = true; RttLog.Line("  repaint: RebuildSurfaceContent NOT FOUND"); }
                return;
            }

            // Find the component's collection of surface contexts. It may hold them
            // directly or as key/value pairs, so probe the first element.
            if (_ctxField == null)
            {
                foreach (var f in renderComponent.GetType().GetFields(Any))
                {
                    object v = null;
                    try { v = f.GetValue(renderComponent); } catch { }
                    if (v is not System.Collections.IEnumerable en || v is string) continue;
                    foreach (var item in en)
                    {
                        if (item != null && item.GetType().Name == "LcdPanelSurfaceContext")
                        { _ctxField = f; _ctxFieldIsKvp = false; }
                        else if (item?.GetType().GetProperty("Value")?.GetValue(item) is object inner
                                 && inner.GetType().Name == "LcdPanelSurfaceContext")
                        { _ctxField = f; _ctxFieldIsKvp = true; }
                        break;  // only the first element decides
                    }
                    if (_ctxField != null) break;
                }
                if (_ctxField == null) return;   // no contexts yet; retry next tick
                RttLog.Line($"  repaint: surface collection = {_ctxField.FieldType.Name} {_ctxField.Name} (kvp={_ctxFieldIsKvp})");
            }

            if (_ctxField.GetValue(renderComponent) is not System.Collections.IEnumerable list) return;

            // Rebuild every context this component owns: they get re-created, so
            // holding a reference to one is not enough to keep a panel painting.
            int n = 0;
            foreach (var item in list)
            {
                var c = _ctxFieldIsKvp ? item?.GetType().GetProperty("Value")?.GetValue(item) : item;
                if (c == null) continue;
                TrackSurface(c);
                _rebuildMi.Invoke(renderComponent, new[] { c });
                n++;
            }

            if (!_repaintDiag) { _repaintDiag = true; RttLog.Line($"  repaint: rebuilding {n} surface context(s) per tick."); }
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("force repaint", e); }
    }

    private static object DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var p = o.GetType().GetProperty(name, Any);
            if (p != null) return p.GetValue(o);
            return o.GetType().GetField(name, Any)?.GetValue(o);
        }
        catch { return null; }
    }

    // Diagnostics for the log, so a silent feed is diagnosable.
    public static string Describe()
    {
        var t = _target;
        if (t == null) return $"no {Tag} panel found yet ({_seenNames.Count} panels seen)";
        return $"target \"{t.Name}\" at {t.Position.X:F1},{t.Position.Y:F1},{t.Position.Z:F1}" +
               (t.Extent > 0 ? $", orbiting the grid centre (extent {t.Extent:F0}m)" : ", orbiting the panel");
    }
}
