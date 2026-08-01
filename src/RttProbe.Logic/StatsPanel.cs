using Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.Contracts;

namespace RttProbe;

// THE STATS PANEL (roadmap goal 9, plan phase A1).
//
// Perf numbers on an in-world LCD so modes can be A/B'd by looking up, instead of tailing
// rtt.log on a second monitor. Tag a panel [RTS].
//
// WHY THIS IS SIMPLER THAN THE FEED PATH, and deliberately so. The feed owns a panel
// outright: its own offscreen target, a material rebind, a handover copy from the UI
// stage, a mip chain. None of that is needed here. OnPanelRender hands us the PANEL'S OWN
// batch — the same batch the [RTT] test pattern proved DrawImage against — so a stats
// panel is just DrawFill + DrawString into a batch the engine already manages. No target,
// no binding, no handover, no new GPU resources (Rule 11 untouched).
//
// It is also the per-feed instancing pathfinder (plan phase C): this is the first surface
// besides the feed panel, so it forces "which panel am I talking about" to become a
// parameter rather than a static. On this surface a mistake shows wrong numbers, not a
// broken feed.
//
// REPAINT CADENCE — the one unknown, and it is instrumented rather than assumed. Panel
// content is rebuilt when the engine decides it is dirty; our text is recorded during
// that rebuild. So we set ContentDirty on a timer and LOG whether repaints actually
// follow. If they do not, the fallback is to invoke the render component's own
// RebuildSurfaceContent, which we can reach from the renderer object OnPanelRender
// already gives us — but that is a bigger hammer and is not used until the cheap route is
// proven insufficient.
internal static class StatsPanel
{
    internal const string Tag = "[RTS]";

    private static long _lastPoke, _lastDraw;
    private static int _draws, _errLogs;
    private static bool _firstDrawLogged, _fontLogged, _cadenceLogged;
    private static int _pokes;

    public static void Reset()
    {
        _lastPoke = _lastDraw = 0;
        _draws = _errLogs = _pokes = 0;
        _firstDrawLogged = _fontLogged = _cadenceLogged = false;
        // Reflection handles belong to the previous component instance after a reload,
        // and every "logged once" latch has to fall with them — a surviving latch would
        // swallow the line that proves the fix took, which is the failure mode this
        // project has lost the most time to.
        _rebuildMi = null; _ctxField = null; _ctxFieldIsKvp = false; _scanDiag = false;
    }

    private const System.Reflection.BindingFlags Any =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

    private static System.Reflection.MethodInfo _rebuildMi;
    private static System.Reflection.FieldInfo _ctxField;
    private static bool _ctxFieldIsKvp, _scanDiag;

    // Called from the LCD tick for EVERY render component, tagged or not — stats panels
    // are not [RTC] panels, so CameraFeed.OnLcdTick returns before ever seeing them.
    //
    // Marks content dirty and drives the rebuild directly, which is the same pair
    // CameraFeed.ForceRepaint uses to keep an idle feed panel painting. Marking dirty
    // alone was the cheaper option, but nothing in the engine was found that polls the
    // flag on a timer — so this drives the rebuild rather than hoping something else
    // will, and the draw side logs the achieved cadence either way.
    public static void OnLcdTick(object renderComponent)
    {
        if (!FeedConfig.StatsPanel || renderComponent == null) return;
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastPoke < Math.Max(100, FeedConfig.StatsPanelMs)) return;

            _rebuildMi ??= renderComponent.GetType().GetMethod("RebuildSurfaceContent", Any);
            if (_rebuildMi == null) return;

            // Same collection probe as CameraFeed.ForceRepaint: the contexts may be held
            // directly or as key/value pairs, so the first element decides.
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
                        break;
                    }
                    if (_ctxField != null) break;
                }
                if (_ctxField == null) return;   // no contexts yet; retry next tick
            }

            if (_ctxField.GetValue(renderComponent) is not System.Collections.IEnumerable list) return;

            bool poked = false;
            foreach (var item in list)
            {
                var c = (_ctxFieldIsKvp ? item?.GetType().GetProperty("Value")?.GetValue(item) : item)
                        as LcdPanelSurfaceContext;
                if (c == null) continue;

                string text = null;
                try { text = c.State.Text; } catch { }
                if (text == null || !text.Contains(Tag, StringComparison.OrdinalIgnoreCase)) continue;

                c.ContentDirty = true;
                _rebuildMi.Invoke(renderComponent, new object[] { c });
                poked = true;
            }

            if (poked)
            {
                _lastPoke = now;
                _pokes++;
                if (!_scanDiag) { _scanDiag = true; RttLog.Line($"Stats panel: found a [RTS] surface; repainting every {FeedConfig.StatsPanelMs} ms."); }
            }
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("stats panel poke", e); }
    }

    // Called from BlitProbe.OnPanelRender when the surface carries our tag. The batch
    // belongs to the panel; we are one more content recorder, exactly like the game's own.
    public static void Draw(IDrawBatch batch, LcdPanelSurfaceContext ctx)
    {
        if (!FeedConfig.StatsPanel || batch == null || ctx == null) return;
        try
        {
            var res = ctx.Definition.Resolution;

            // Font straight from the panel's own configured handle through the public
            // UISystem accessor. No reflection, and it means the panel renders in whatever
            // font its owner picked in the terminal.
            Font font = null;
            try
            {
                var ui = BlitProbe.Ui;
                if (ui != null) font = ui.GetFont(ctx.State.Font);
            }
            catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("stats font", e); }

            if (!_fontLogged)
            {
                _fontLogged = true;
                RttLog.Line(font != null
                    ? $"Stats panel: font resolved from the panel's own handle via UISystem.GetFont " +
                      $"(EmSizePx={font.EmSizePx}); surface {res.X}x{res.Y}."
                    : "Stats panel: NO FONT — UISystem.GetFont returned null for this panel's handle. " +
                      "Falling back to bar-only output; numbers will not be readable. Check " +
                      "ctx.State.Font and BlitProbe.Ui.");
            }

            // Background first, so the panel's own content never shows through half-drawn.
            Fill(batch, 0, 0, res.X, res.Y, 8, 10, 16);

            var s = Perf.Latest;
            double budget = FeedConfig.RttBudgetMs;
            bool over = s != null && budget > 0 && s.SubmitMean > budget * 1.2;

            // THE BUDGET TRIPWIRE (design: budget lock v2). Ships with the panel rather
            // than with the scheduler, because it needs nothing the scheduler provides —
            // and landing it now means every later phase is developed under the budget's
            // supervision instead of being audited against it afterwards.
            if (font != null)
            {
                float x = res.X * 0.06f, y = res.Y * 0.08f;
                float line = res.Y * 0.115f;
                float scale = res.Y / 512f;   // authored against 512; scales to any panel

                Text(batch, font, x, y, scale * 0.9f, 200, 220, 255, "RTT FEED");
                y += line;

                if (s == null)
                {
                    Text(batch, font, x, y, scale * 0.7f, 180, 180, 180, "waiting for first sample...");
                }
                else
                {
                    Text(batch, font, x, y, scale * 0.75f, 235, 235, 235,
                         $"{s.Fps:F1} fps   p50 {s.OursP50:F1}");
                    y += line;
                    Text(batch, font, x, y, scale * 0.75f, 235, 235, 235,
                         $"p95 {s.OursP95:F1}   >50ms {s.OursOver50 + s.IdleOver50}");
                    y += line;

                    // Submit vs budget — the number the whole phase-2 design turns on.
                    Text(batch, font, x, y, scale * 0.75f,
                         over ? (byte)255 : (byte)140, over ? (byte)90 : (byte)230, over ? (byte)90 : (byte)140,
                         budget > 0
                             ? $"submit {s.SubmitMean:F1} / {budget:F1}ms {(over ? "OVER" : "ok")}"
                             : $"submit {s.SubmitMean:F1}ms");
                    y += line;

                    Text(batch, font, x, y, scale * 0.7f, 190, 190, 200,
                         $"VRAM {s.VramGb:F2}G  {(s.VramDeltaMb >= 0 ? "+" : "")}{s.VramDeltaMb:F0}M");
                    y += line;

                    Text(batch, font, x, y, scale * 0.7f, 150, 170, 200,
                         $"{FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight}" +
                         (FeedConfig.WholeSceneOwnProbes ? " prb" : "") +
                         (FeedConfig.WholeSceneOwnFlares ? " flr" : ""));
                    y += line;

                    // WHICH FEEDS ARE IN THE ROTATION (phase F1). Every other number on this
                    // panel is an aggregate, so with two feeds it reads identically whether
                    // both are live or one has quietly gone away — and "quietly gone away" is
                    // precisely the state this phase exists to make impossible to miss. Here
                    // it is legible from across the room while you grind a panel down.
                    Text(batch, font, x, y, scale * 0.7f, 150, 200, 160,
                         "feeds " + Feeds.RotationShort());
                }
            }
            else
            {
                // No font: a bar whose length is submit-vs-budget still communicates the
                // one thing that matters, and proves the panel is live.
                float w = res.X * 0.88f;
                double frac = s != null && budget > 0 ? Math.Clamp(s.SubmitMean / budget, 0, 1.5) : 0;
                Fill(batch, res.X * 0.06f, res.Y * 0.45f, res.X * 0.06f + w, res.Y * 0.55f, 40, 44, 60);
                Fill(batch, res.X * 0.06f, res.Y * 0.45f, res.X * 0.06f + w * (float)Math.Min(frac, 1.0),
                     res.Y * 0.55f, over ? (byte)255 : (byte)120, over ? (byte)90 : (byte)220, (byte)120);
            }

            _draws++;
            long now = Environment.TickCount64;

            if (!_firstDrawLogged)
            {
                _firstDrawLogged = true;
                RttLog.Line($"=== STATS PANEL LIVE: drawing into a [RTS] surface ({res.X}x{res.Y}). " +
                            "This is the first non-feed surface the mod owns — the per-feed instancing " +
                            "pathfinder for plan phase C. ===");
            }

            // Proof the ContentDirty poke actually drives repaints. If this never appears,
            // the panel is frozen on one sample and the fallback (RebuildSurfaceContent)
            // is needed — say so rather than leaving a stale panel looking healthy.
            if (!_cadenceLogged && _draws >= 10)
            {
                _cadenceLogged = true;
                double per = _lastDraw > 0 ? (now - _lastDraw) : 0;
                RttLog.Line($"Stats panel: {_draws} repaints from {_pokes} tick pokes " +
                            $"(~{per:F0} ms since the previous) — the ContentDirty + " +
                            "RebuildSurfaceContent pair is driving re-recording as intended. Repaints " +
                            "running AHEAD of pokes is normal: the engine also rebuilds this surface " +
                            "for its own reasons, and every rebuild re-records our text.");
            }
            _lastDraw = now;
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("stats panel draw", e); }
    }

    private static void Text(IDrawBatch batch, Font font, float x, float y, float scale,
                             byte r, byte g, byte b, string s)
    {
        batch.DrawString(font, new Vector2(x, y), new ColorSRGB(r, g, b, (byte)255), s, scale,
                         false, null, 0f);
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
}
