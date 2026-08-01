using Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Render.Contracts;

namespace RttProbe;

// The plumbing spike.
//
// Four stages, each logged before it is attempted so a hard crash names its own
// cause. The last one is the actual unknown: whether IDrawBatch.DrawImage accepts
// a render target's *generated* texture handle, or rejects it inside the render
// thread's command replay — where nothing can catch the throw and the game dies.
//
// Arming is driven from the panel itself so nothing risky happens by merely
// loading the mod:
//   [RTT]   stages 1-3 — create our own target and draw into it. Safe.
//   [RTT!]  also stage 4 — blit that target onto the panel. The risky one.
internal static class BlitProbe
{
    private const string TagSafe = "[RTT]";
    private const string TagArmed = "[RTT!]";
    private const int RtSize = 512;

    private static readonly string MarkerPath = Path.Combine(RttLog.OutDir, "blit-armed.marker");

    private static RenderContracts _contracts;
    private static UISystem _ui;
    private static bool _resolveTried;

    // The stats panel needs UISystem.GetFont, and this is the only place the UISystem is
    // resolved. Exposed rather than re-resolved so there is exactly one owner of it.
    internal static UISystem Ui => _ui;

    // PER-FEED (phase C1a). The target and the batch that paints it are the most
    // obviously per-feed things in the mod — two feeds means two targets — and the
    // stats panel (A1) already rehearsed exactly this shape on a second surface.
    private static OffscreenRenderTarget? _rt
    { get => Feeds.Cur.Rt; set => Feeds.Cur.Rt = value; }
    private static bool _rtTried
    { get => Feeds.Cur.RtTried; set => Feeds.Cur.RtTried = value; }

    // The camera feed renders into this same target, so the render side needs a
    // handle on it. Boxed, because CameraRender works in reflection terms.
    public static object FeedTarget => _rt.HasValue ? (object)_rt.Value : null;

    // Once the camera pass is copying real frames in, the 2D test pattern would
    // just overwrite them. The backing field is volatile: written on the tick side,
    // read on the render side, and a property over it keeps those semantics.
    public static bool FeedOwnsTarget
    { get => Feeds.Cur.FeedOwnsTarget; set => Feeds.Cur.FeedOwnsTarget = value; }

    private static PersistentDrawBatch _persistentBatch
    { get => Feeds.Cur.PersistentBatch; set => Feeds.Cur.PersistentBatch = value; }
    private static bool _batchRetired
    { get => Feeds.Cur.BatchRetired; set => Feeds.Cur.BatchRetired = value; }
    private static long _lastPaint;
    private static int _paintCount;

    private static bool _blitRecorded;
    private static int _framesSinceBlit;
    private static bool _blitConfirmed;
    private static bool _disarmed;

    private static int _tickLogs, _renderLogs, _errLogs, _panelHookLogs;
    private static long _tickCount;

    public static void Reset()
    {
        // A marker left behind means the previous session recorded a blit and
        // never came back — i.e. the replay rejected the handle. Refuse to repeat
        // it until a human clears the file.
        if (File.Exists(MarkerPath))
        {
            _disarmed = true;
            RttLog.Line("!!! PREVIOUS SESSION DIED WITH A BLIT ARMED !!!");
            RttLog.Line($"!!! DrawImage rejected the render-target handle in the replay. Stage 4 is DISABLED.");
            RttLog.Line($"!!! Delete {MarkerPath} to try again.");
        }
        _rt = null;
        _rtTried = false;
        _resolveTried = false;
        _contracts = null;
        _ui = null;
        _blitRecorded = false;
        _blitConfirmed = false;
        _framesSinceBlit = 0;
        _paintCount = 0;
        _persistentBatch = null;
        _batchRetired = false;
        FeedOwnsTarget = false;
        _tickLogs = _renderLogs = _errLogs = 0;
    }

    // ------------------------------------------------------------------ tick
    // Per frame, outside panel content recording. Stages 1-3 live here.
    public static void OnTick(object component)
    {
        _tickCount++;

        // PANEL-DRIVEN (phase C1b). The engine hands us ONE LCD component per call, on its
        // own schedule — so the feed is whoever owns that panel, a lookup, NOT a rotation.
        // Rotating here would hand panel A's tick to feed B, which is the single easiest way
        // to make two feeds silently corrupt each other.
        using (Feeds.Enter(Feeds.ForPanel(component)))
            OnTickScoped(component);
    }

    private static void OnTickScoped(object component)
    {
        try
        {
            // Polled here as well as in the whole-scene hook: this is the tick that keeps
            // running when the render-side route is off, so it is what lets a dormant mod
            // notice the panel coming back. Panel DISCOVERY below must stay ungated —
            // that is the signal the gate reads.
            FeedGate.Poll();

            if (_tickLogs < 1) { _tickLogs++; RttLog.Line("Tick hook alive."); }

            // Locate the [RTC] panel this tick belongs to, if any.
            CameraFeed.OnLcdTick(component);

            // And any [RTS] stats panel. Separate call because OnLcdTick returns early for
            // anything not carrying the feed tag, so a stats panel would never be seen.
            StatsPanel.OnLcdTick(component);

            // The DisposePendingProbes drain used to run here, on the premise that this
            // tick is the game thread and therefore outside any frame we record. The second
            // half of that premise is FALSE — the render thread renders concurrently with
            // this tick — and it cost a third device removal to establish. Both the drain
            // and its queue are gone; see the probe-manager comment in WholeSceneRender.Reset.

            // On-demand resource report (drop output/resource-report.marker). Read-only
            // reflection, one File.Exists every 2 s until asked. Deliberately NOT a config
            // knob: a config change can cost a gate cycle, and gate-cycle churn is what
            // took the device three times on 2026-07-30.
            FeedResourceReport.MaybeRun();

            ResolveContracts(component);
            if (_contracts == null || _ui == null) return;

            EnsureRenderTarget();
            if (_rt == null) return;

            // Confirmation of stage 4: the crash we are hunting happens after our
            // postfix returns, during replay. Surviving frames is the evidence.
            if (_blitRecorded && !_blitConfirmed && ++_framesSinceBlit >= 120)
            {
                _blitConfirmed = true;
                try { File.Delete(MarkerPath); } catch { }
                RttLog.Line("=== STAGE 4 CONFIRMED: blit survived 120 frames. DrawImage accepts a render-target handle. ===");
            }

            // The test pattern is only useful until real frames arrive.
            var now = Environment.TickCount64;
            // The test pattern and the camera copy are mutually exclusive writers to
            // the same target. Painting while the handover is armed means DrawOne draws
            // our batch and then our postfix copies over it in the same servicing —
            // two writers, one pass, which killed the game on the first copy.
            // Armed AND actually copying. While the copy is disabled for diagnostics
            // the handover writes nothing, so suppressing the test pattern too would
            // leave a blank panel and no way to tell a broken target from a quiet one.
            bool feedArmed = FeedConfig.CopyEnabled &&
                             File.Exists(Path.Combine(RttLog.OutDir, "handover-armed.marker"));
            if (!FeedOwnsTarget && !feedArmed)
            {
                if (now - _lastPaint >= 500) { _lastPaint = now; PaintTestPattern(); }
            }
            else if (!_batchRetired && FeedConfig.RetireTestPattern && _paintCount > 0)
            {
                // Suppressing the *repaint* is not enough: a persistent batch keeps
                // being drawn on every servicing until it is replaced, so DrawOne would
                // still paint the last test pattern immediately before our copy lands on
                // top of it. Two writers per servicing, which is the rule we already
                // learned the hard way. Replace it with an empty one — same API, and the
                // only one proven to retire the previous batch.
                _batchRetired = true;
                try
                {
                    _persistentBatch = _ui.CreatePersistentBatchFor(_rt, 0, _persistentBatch, true);
                    _persistentBatch?.Submit();
                    RttLog.Line("Stage 3: test pattern retired — the camera feed is the only writer now.");
                }
                catch (Exception e) { RttLog.Error("retire test pattern", e); }
            }

            // Readback lives here rather than in the camera pass: this hook can make
            // contracts calls (it already creates the offscreen target), whereas the
            // camera pass runs on the render thread where the enqueued command is
            // never pumped.
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("tick", e); }
    }

    // ---------------------------------------------------------------- stage 1
    // RenderContracts is not exposed as a singleton we can reach, but the LCD
    // render component holds one (its rebuild path calls GetUISystem), so it can
    // be fished out of that component's fields.
    private static void ResolveContracts(object component)
    {
        if (_resolveTried || component == null) return;
        _resolveTried = true;
        RttLog.Line("Stage 1: resolving RenderContracts / UISystem...");
        try
        {
            const System.Reflection.BindingFlags All =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

            foreach (var f in component.GetType().GetFields(All))
            {
                object v = null;
                try { v = f.GetValue(f.IsStatic ? null : component); } catch { }
                if (v is RenderContracts rc)
                {
                    _contracts = rc;
                    _ui = rc.GetUISystem();
                    break;
                }
                if (v is UISystem us) _ui = us;
            }
            RttLog.Line($"Stage 1: contracts={(_contracts != null ? "OK" : "NOT FOUND")} uiSystem={(_ui != null ? "OK" : "NOT FOUND")}");
            if (_contracts == null)
                RttLog.Line("Stage 1 FAILED — cannot create a render target without RenderContracts. Fields seen: " +
                    string.Join(", ", component.GetType().GetFields(All).Select(f => f.FieldType.Name).Distinct().Take(25)));
        }
        catch (Exception e) { RttLog.Error("stage1 resolve", e); }
    }

    // ---------------------------------------------------------------- stage 2
    private static void EnsureRenderTarget()
    {
        if (_rtTried) return;
        _rtTried = true;

        // NAMED PER FEED (phase C3). This was the literal "RttProbe" for every caller, which
        // was fine when there was exactly one, and is a collision the moment there are two:
        // both feeds would ask the engine's contracts for a target under one name, and the
        // feed that asked second would either be handed the first feed's target — two panels
        // showing one camera — or fight it for ownership. Neither failure announces itself.
        string name = Feeds.Count <= 1 ? "RttProbe" : $"RttProbe{Feeds.Cur.Id}";

        RttLog.Line($"Stage 2: CreateOffscreenTarget(\"{name}\", {RtSize}x{RtSize})...");
        try
        {
            var rt = _contracts.CreateOffscreenTarget(name, new Vector2I(RtSize, RtSize));
            RttLog.Line($"Stage 2: returned Id={rt.Id} IsValid={rt.IsValid}");
            if (!rt.IsValid) { RttLog.Line("Stage 2 FAILED — target is not valid."); return; }
            _rt = rt;
            RttLog.Line("Stage 2 OK.");
        }
        catch (Exception e) { RttLog.Error("stage2 create", e); }
    }

    // ---------------------------------------------------------------- stage 3
    // Draw a distinctive, animated pattern into our own target. Animation is the
    // point: a frozen image on the panel later would mean the blit is sampling a
    // stale copy rather than the live target.
    private static void PaintTestPattern()
    {
        try
        {
            // Persistent, not immediate. An immediate batch is drawn once and
            // discarded — but our batch is recorded from the tick hook while the target
            // is only serviced later, in DrawOne, which clears it first and then finds
            // nothing to draw. A persistent batch survives until replaced, which is what
            // "content that lives on a render target" actually needs.
            var batch = _persistentBatch = _ui.CreatePersistentBatchFor(_rt, 0, _persistentBatch, true);
            if (batch == null)
            {
                if (_errLogs++ < 5) RttLog.Line("Stage 3: CreateImmediateBatchFor returned null.");
                return;
            }

            const float S = RtSize;
            Fill(batch, 0, 0, S, S, 20, 24, 40);                       // dark blue ground
            Fill(batch, 16, 16, S - 16, S - 16, 220, 60, 60);           // red border block
            Fill(batch, 48, 48, S - 48, S - 48, 30, 34, 55);            // inner panel

            // Corner markers make orientation and any flip readable at a glance.
            Fill(batch, 48, 48, 112, 112, 255, 255, 255);               // top-left white
            Fill(batch, S - 112, 48, S - 48, 112, 80, 255, 80);         // top-right green
            Fill(batch, 48, S - 112, 112, S - 48, 80, 160, 255);        // bottom-left blue
            Fill(batch, S - 112, S - 112, S - 48, S - 48, 255, 220, 60);// bottom-right yellow

            // Sweeping bar: proves the target is being repainted, not cached.
            float t = (_paintCount % 20) / 20f;
            float bx = 64 + t * (S - 192);
            Fill(batch, bx, S * 0.5f - 24, bx + 128, S * 0.5f + 24, 255, 255, 255);

            batch.Submit();
            _paintCount++;
            if (_paintCount == 1) RttLog.Line("Stage 3 OK: first paint submitted into our own render target.");
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("stage3 paint", e); }
    }

    // Draw the camera feed across the whole tagged surface.
    private static int _feedDrawLogs;

    private static void DrawFeed(IDrawBatch batch, LcdPanelSurfaceContext ctx)
    {
        try
        {
            var res = ctx.Definition.Resolution;
            var dest = new BoundingBox2(new Vector2(0f, 0f), new Vector2(res.X, res.Y));
            ResourceHandle handle = _rt.Value.TextureHandle;

            if (_feedDrawLogs++ == 0)
                RttLog.Line($"=== FEED DRAW: painting camera feed onto [RTC] panel ({res.X}x{res.Y}). ===");

            batch.DrawImage(handle, dest, new ColorSRGB((byte)255, (byte)255, (byte)255, (byte)255), false, null, null);
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("feed draw", e); }
    }

    private static void Fill(IDrawBatch batch, float x0, float y0, float x1, float y1, byte r, byte g, byte b)
    {
        var splines = new[]
        {
            new QuadraticBezier2(new Vector2(x0, y0), new Vector2(x1, y0)),
            new QuadraticBezier2(new Vector2(x1, y0), new Vector2(x1, y1)),
            new QuadraticBezier2(new Vector2(x1, y1), new Vector2(x0, y1)),
            new QuadraticBezier2(new Vector2(x0, y1), new Vector2(x0, y0)),
        };
        batch.DrawFill(splines, new ColorSRGB(r, g, b, (byte)255), null, false);
    }

    // ------------------------------------------------------------ panel render
    // Stage 4. The batch handed to us here targets the panel's own offscreen
    // render target, so this is a genuine RT-to-RT blit.
    public static void OnPanelRender(object rendererObj, object batchObj, object ctxObj)
    {
        // PANEL-DRIVEN, keyed on the SURFACE CONTEXT (phase C3). A surface context carries no
        // _lcdBlock and therefore no name, so it cannot be routed the way OnTick's component
        // is — it is registered to its feed during discovery instead, which already runs
        // under that feed's scope. The scope opens before the gate check because
        // FeedGate.Active is per-feed state now: "is this panel's feed live" cannot be
        // answered without first knowing which feed.
        using (Feeds.Enter(Feeds.ForSurface(ctxObj)))
            OnPanelRenderScoped(rendererObj, batchObj, ctxObj);
    }

    private static void OnPanelRenderScoped(object rendererObj, object batchObj, object ctxObj)
    {
        if (batchObj is not IDrawBatch batch || ctxObj is not LcdPanelSurfaceContext ctx) return;
        try
        {
            // The camera feed owns its panel outright: fill the surface with the
            // live render rather than compositing it over the panel's own content.
            string text = null;
            try { text = ctx.State.Text; } catch { }

            // Match the feed panel by its stamped tag, not by reference: surface
            // contexts are re-created, so an identity check silently stops matching.
            if (_panelHookLogs < 1)
            {
                _panelHookLogs++;
                RttLog.Line($"Panel render hook firing (text=\"{text}\", rt={_rt != null}).");
            }

            // THE STATS PANEL FIRST, AND NOT BEHIND THIS SURFACE'S FEED GATE.
            //
            // It used to sit below `if (!FeedGate.Active) return`, and the scope wrapping this
            // method is Feeds.ForSurface(ctx) — which resolves an [RTS] surface to PRIMARY,
            // because nothing ever registers a stats surface to a feed. So the debug panel was
            // gated on FEED 0 specifically. Grind down feed 0's panel and the stats panel goes
            // blank, which is the exact moment it is most worth reading; observed 2026-08-01,
            // reported as "only shows a blank screen with the [RTS] text".
            //
            // Seventh instance of the same family — something keyed to Primary that has no
            // business being keyed to a feed at all. The stats panel is a statement about the
            // MOD: it draws into the panel's own batch, touches no feed machinery, and its most
            // valuable reading is "feed fps 0:off 1:47.4", which by definition happens when a
            // feed is down.
            //
            // The one thing that still silences it is the PAUSE MARKER, and that is deliberate:
            // paused means the game renders exactly as it would without this mod, and a panel
            // we are still drawing on would make that comparison a lie.
            if (text != null && text.Contains(StatsPanel.Tag, StringComparison.OrdinalIgnoreCase))
            {
                if (!FeedGate.Paused) StatsPanel.Draw(batch, ctx);
                return;
            }

            // Dormant means the panel draws its own content, exactly as it would without
            // this mod installed. BELOW the stats branch: this gate is about whether THIS
            // SURFACE'S FEED is live, which is a question only feed panels are asking.
            if (!FeedGate.Active) return;

            // DrawImage with a render-target-backed handle is fatal: UISystemComponent
            // .GetTexture asserts IsGuid(), and an OffscreenRenderTarget's handle is a
            // generated RenderId handle. Confirmed by killing the game the instant a
            // tagged panel repainted. The feed reaches the panel by writing into the
            // panel's own render target instead — see CameraFeed.CapturePanelRenderTarget.
            // FeedRouter.IsFeedPanel, not a raw Contains, so [RTC2] is recognised here too.
            // A tag the discovery side accepts and this side rejects would bind the panel's
            // material and then let the panel paint straight over the feed.
            if (FeedRouter.IsFeedPanel(text))
            {
                PanelBinding.OnPanelRender(rendererObj, ctx);
            }
            if (FeedRouter.IsFeedPanel(text))
            {
                // Draw nothing. The feed does not go through this batch at all — it is
                // written straight into the panel's own render target from the UI stage
                // (FeedHandover). Drawing here would only paint over it.
                if (_feedDrawLogs++ == 0)
                    RttLog.Line("[RTC] panel content suppressed — the feed owns its render target.");
                return;
            }

            if (string.IsNullOrEmpty(text)) return;

            bool armed = text.Contains(TagArmed, StringComparison.OrdinalIgnoreCase);
            bool safe = armed || text.Contains(TagSafe, StringComparison.OrdinalIgnoreCase);
            if (!safe) return;

            if (_renderLogs < 1)
            {
                _renderLogs++;
                var res0 = ctx.Definition.Resolution;
                RttLog.Line($"Panel hook alive on a tagged surface ({res0.X}x{res0.Y}), armed={armed}.");
            }

            if (!armed || _rt == null) return;
            if (_disarmed)
            {
                if (_renderLogs < 3) { _renderLogs++; RttLog.Line("Stage 4 skipped — disarmed by a previous crash marker."); }
                return;
            }

            var res = ctx.Definition.Resolution;
            float side = Math.Min(res.X, res.Y) * 0.6f;
            float ox = (res.X - side) * 0.5f, oy = (res.Y - side) * 0.5f;
            var dest = new BoundingBox2(new Vector2(ox, oy), new Vector2(ox + side, oy + side));

            // ResourceHandle<T> -> ResourceHandle via the engine's own implicit
            // conversion. This handle is *generated* (backed by a RenderId), not
            // the file-backed guid handle the UI recorder normally sees — which is
            // precisely what is under test.
            ResourceHandle handle = _rt.Value.TextureHandle;

            if (!_blitRecorded)
            {
                _blitRecorded = true;
                try { File.WriteAllText(MarkerPath, $"blit armed {DateTime.Now:O}\n"); } catch { }
                RttLog.Line($"Stage 4: recording DrawImage with RT handle {handle} into panel batch.");
                RttLog.Line("Stage 4: if the game dies now, the replay rejected it (marker file left behind).");
            }

            batch.DrawImage(handle, dest, new ColorSRGB((byte)255, (byte)255, (byte)255, (byte)255), false, null, null);
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("stage4 blit", e); }
    }
}
