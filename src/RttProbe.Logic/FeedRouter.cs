using System.Reflection;

namespace RttProbe;

// WHICH FEED DOES THIS PANEL BELONG TO? (phase C3)
//
// C1b established that six of the ten entry points are PANEL- or TARGET-driven: the engine
// hands us a specific LCD component or offscreen target on its own schedule, and the feed is
// whoever owns it. At Count == 1 that was `=> All[0]` and correct by luck. This is the real
// answer.
//
// THE ASSIGNMENT MUST BE STABLE FROM THE FIRST TICK, not settle after a frame or two. The
// scope is entered in BlitProbe.OnTick BEFORE CameraFeed.OnLcdTick has parsed anything, so a
// router that answered "Primary, I'll know next time" would run panel B's discovery against
// feed A's state for one tick — publishing B's position into A's Target, pointing A's camera
// at the wrong grid, and then quietly correcting. A transient like that is invisible in the
// log and looks exactly like a camera glitch.
//
// So the FIRST time a component is seen, the name is parsed immediately and the claim is
// made there and then. After that it is a reference-keyed dictionary hit.
//
// CLAIMS ARE BY NAME, not by component reference, because the reference is not stable:
// RebuildSurfaceContent replaces surface contexts, panels stream out and back, and the render
// component can be recreated. The name is what the user typed and is the only durable
// identity we have. The component map is a cache in front of it.
internal static class FeedRouter
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // Component -> feed. Reference identity: the same component object ticks repeatedly, and
    // this is the hot path, so it must not re-parse a name every tick.
    private static readonly Dictionary<object, FeedInstance> _byComponent =
        new(ReferenceEqualityComparer.Instance);

    // Panel name -> feed. THE durable claim. Ordinal-ignore-case to match how the tag itself
    // is compared everywhere else.
    private static readonly Dictionary<string, FeedInstance> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    // Surface context -> feed. The panel-render hook is handed a SURFACE CONTEXT, which has
    // no _lcdBlock and therefore no name — it cannot be routed by the two maps above. It is
    // registered instead at the moment discovery walks the panel's surfaces, which already
    // runs under the owning feed's scope, so the answer is simply recorded rather than
    // derived.
    private static readonly Dictionary<object, FeedInstance> _bySurface =
        new(ReferenceEqualityComparer.Instance);

    private static int _claimLogs;

    internal static void Reset()
    {
        _byComponent.Clear();
        _byName.Clear();
        _bySurface.Clear();
        _claimLogs = 0;
    }

    // Called from CameraFeed.TrackSurface, under the owning feed's ambient.
    internal static void ClaimSurface(object ctx, FeedInstance feed)
    {
        if (ctx == null || feed == null) return;
        // Same bound and same reason as the component map: RebuildSurfaceContent REPLACES
        // these objects, so the map accretes dead keys for as long as repaints are driven.
        if (_bySurface.Count >= MaxTrackedComponents) _bySurface.Clear();
        _bySurface[ctx] = feed;
    }

    // Unknown surfaces resolve to Primary. That covers the window before discovery has
    // walked this panel, and every untagged LCD in the world — both cases are rejected a
    // moment later by the tag test in the panel-render hook, so Primary costs nothing.
    internal static FeedInstance ForSurface(object ctx)
    {
        if (ctx != null && _bySurface.TryGetValue(ctx, out var f)) return f;
        return Feeds.Primary;
    }

    // Bounded like CameraFeed._targetSurfaces, and for the same reason: the component map is
    // keyed on objects the engine recreates, so it accumulates dead entries for as long as
    // panels are streaming. The NAME map is the durable one and is never trimmed — it is one
    // small string per tagged panel and it is what keeps a claim stable across a component
    // being rebuilt.
    private const int MaxTrackedComponents = 64;

    internal static FeedInstance ForComponent(object renderComponent)
    {
        if (renderComponent == null) return Feeds.Primary;

        if (_byComponent.TryGetValue(renderComponent, out var known)) return known;

        string name = PanelNameOf(renderComponent);

        // Not one of ours. Hand back Primary: OnLcdTick early-returns on the missing tag, so
        // nothing per-feed is touched, and caching the answer stops us re-parsing the name of
        // every untagged LCD in the world on every one of its ticks.
        if (!IsFeedPanel(name))
        {
            Remember(renderComponent, Feeds.Primary);
            return Feeds.Primary;
        }

        var feed = ClaimByName(name);
        Remember(renderComponent, feed);
        return feed;
    }

    private static void Remember(object component, FeedInstance feed)
    {
        if (_byComponent.Count >= MaxTrackedComponents) _byComponent.Clear();
        _byComponent[component] = feed;
    }

    // THE TAG CAN CHANGE UNDER US NOW (2026-08-01).
    //
    // Every map in this file is a cache in front of a name that was assumed immutable, and
    // that assumption was fair while the tag lived in a block name: renaming a block is a
    // rare, deliberate act. The moment the tag moved into the surface's TEXT FIELD, editing
    // it became the NORMAL way to configure the mod — and a permanent component -> feed cache
    // turned into a bug you hit on your first retag.
    //
    // Observed the same evening the text-field rule landed: a panel retagged from [RTC] to
    // [RTC2] kept routing to feed 0, so the claim was recorded as "[RTC2] #0 @block" ON FEED
    // 0 — the fresh name and the stale route disagreeing inside one log line. Feed 1 was left
    // with no panel to aim at, and a feed with no target renders whatever its camera last
    // had, which is how the player's own viewpoint ended up on a panel.
    //
    // Called by discovery, which re-derives the tag from the live text on every tick and so
    // is the one place that always knows the truth.
    internal static void Recache(object component, string name, FeedInstance feed)
    {
        if (component != null) Remember(component, feed);
        if (!string.IsNullOrEmpty(name)) _byName[name] = feed;
    }

    // Which feed does a tag index mean right now? Clamped the same way ClaimByName clamps —
    // asking for a feed beyond the active count shares Primary rather than indexing past it.
    internal static FeedInstance FeedForIndex(int index) =>
        index >= 0 && index < Feeds.Count ? Feeds.At(index) : Feeds.Primary;

    // Assignment is EXPLICIT, from the tag itself:
    //
    //     [RTC]  or [RTC1]  -> feed 0
    //     [RTC2]            -> feed 1
    //     [RTC3]            -> feed 2      ... i.e. [RTCn] -> feed n-1
    //
    // The first design assigned feeds in DISCOVERY ORDER — whichever tagged panel ticked
    // first became feed 0. That is fragile in exactly the way that wastes an afternoon: tick
    // order is the engine's business, so the same two panels could swap feeds between
    // sessions, and every symptom of a routing bug ("the wrong camera is on the wrong
    // screen") is also a symptom of the assignment having flipped. Naming the feed in the
    // tag makes it the user's decision, stable across sessions, and readable off the block.
    //
    // Backwards compatible: a plain [RTC] is feed 0, which is what every existing world has.
    private static FeedInstance ClaimByName(string name)
    {
        if (_byName.TryGetValue(name, out var already)) return already;

        TryParseTag(name, out int index);

        int n = Feeds.Count;
        FeedInstance feed;

        // EACH LOG IS SCOPED TO THE FEED IT IS ABOUT. RttLog.Line stamps [feed N] from the
        // ambient, and this method is the one DECIDING the ambient — so logging unscoped
        // here reads the enclosing scope (or none at all) and tags the line with a feed that
        // is not the one the sentence names.
        //
        // Caught by the unscoped-access detector during the first clean two-feed run. Harmless
        // in itself: these lines name their feed in the text, so nothing was ever misread. It
        // is fixed anyway because the detector's whole value is that its SILENCE is evidence,
        // and a known-benign entry sitting in the log trains the eye to skip past the next one.
        if (index < n)
        {
            feed = Feeds.At(index);
            if (_claimLogs++ < 8)
                using (Feeds.Enter(feed))
                    RttLog.Line($"Feed routing: panel \"{name}\" -> FEED {feed.Id}. Its own camera, " +
                                $"ScreenBuffers, LDR ring and gate. (feedCount={n}.)");
        }
        else
        {
            // Asked for a feed that is not active. Say so precisely, because the alternative
            // symptom is "my second panel just mirrors the first" with nothing in the log.
            feed = Feeds.Primary;
            if (_claimLogs++ < 8)
                using (Feeds.Enter(feed))
                    RttLog.Line($"Feed routing: panel \"{name}\" asks for feed {index}, but only " +
                                $"{n} feed(s) are active (feedCount={n}). It will SHARE feed " +
                                $"{feed.Id} and show that camera's picture. Raise feedCount to " +
                                $"{index + 1} to give it its own.");
        }

        _byName[name] = feed;
        return feed;
    }

    // Does this name carry a feed tag, and which feed does it ask for?
    //
    // Matches "[RTC" + optional digits + "]" so [RTC] and [RTC2] are both recognised, while
    // an untagged panel and a near-miss like [RTCX] are not. Returns the ZERO-BASED feed
    // index; the tag is one-based because "RTC2" reading as "the second camera" is the whole
    // point of letting the user write it.
    internal static bool TryParseTag(string name, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(name)) return false;

        const string open = "[RTC";
        int at = name.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;

        int i = at + open.Length;
        int digits = 0, value = 0;
        while (i < name.Length && name[i] >= '0' && name[i] <= '9')
        {
            value = value * 10 + (name[i] - '0');
            digits++; i++;
            if (digits > 3) return false;          // not a feed tag, just a name with digits
        }
        if (i >= name.Length || name[i] != ']') return false;

        // [RTC] and [RTC1] both mean the first feed.
        index = digits == 0 ? 0 : Math.Max(0, value - 1);
        return true;
    }

    // Is this name one of ours at all? The single tag test the whole mod shares, so [RTC2]
    // cannot be recognised by one call site and ignored by another.
    internal static bool IsFeedPanel(string name) => TryParseTag(name, out _);

    // The panel's user-facing name, by the same route CameraFeed uses for discovery. Kept
    // here rather than called through CameraFeed so the router has no ordering dependency on
    // discovery having run — the router is what tells discovery which feed it is running as.
    internal static string PanelNameOf(object renderComponent)
    {
        try
        {
            var lcd = renderComponent.GetType().GetField("_lcdBlock", Any)?.GetValue(renderComponent);
            if (lcd == null) return null;

            var entity = lcd.GetType().GetProperty("Entity", Any)?.GetValue(lcd);
            var dbg = entity?.GetType().GetProperty("DebugName", Any)?.GetValue(entity) as string;
            if (IsFeedPanel(dbg)) return dbg;

            var mi = lcd.GetType().GetMethod("GetSurfaceEffectiveDisplayName", Any);
            var n = mi?.Invoke(lcd, new object[] { 0 }) as string;
            return string.IsNullOrEmpty(n) ? dbg : n;
        }
        catch { return null; }
    }

    // ---- the target-driven side ---------------------------------------------------
    //
    // FeedHandover is handed the offscreen target being drawn, and the feed is whoever parked
    // a frame for it.
    //
    // Matched on the HANDLE TEXT, which is exactly the test the handover already uses to
    // decide "is this target ours" (`handle.Contains(_panelHandleText)`), just asked per feed
    // instead of once. Deliberately NOT a reference comparison against FeedComponent: the
    // engine recreates these components, and the existing handle test is the one that has
    // been proven across every session this route has run.
    //
    // No match means the engine is drawing somebody else's target — the stats panel, an
    // ordinary LCD — and the handover's own check rejects it a moment later. Primary is a
    // safe answer precisely because nothing is parked for it either.
    internal static FeedInstance ForTargetComponent(object component)
    {
        if (component == null) return Feeds.Primary;

        string handle;
        try { handle = component.GetType().GetProperty("Handle", Any)?.GetValue(component)?.ToString(); }
        catch { return Feeds.Primary; }
        if (string.IsNullOrEmpty(handle)) return Feeds.Primary;

        int n = Feeds.Count;
        for (int i = 0; i < n; i++)
        {
            var f = Feeds.At(i);
            var text = f.PanelHandleText;
            if (!string.IsNullOrEmpty(text) && handle.Contains(text)) return f;
        }
        return Feeds.Primary;
    }
}
