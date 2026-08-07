using System.Reflection;
using System.Text;

namespace RttProbe;

// Phase 2: own the render target the panel displays.
//
// Everything so far has written into a target the LCD system owns — it decides when
// that resource is read, rebuilt, recycled and evicted, on its own schedule. That is
// the root cause of the handover's instability: measured dying within tens of
// seconds even at 2 fps, in every configuration, with erratic lifetimes.
//
// The panel's material samples whatever texture is handed to it:
//
//   LcdPanelSurfaceContext.SetNewScreenMaterialHandle(
//       LcdContentRendererSessionComponent renderer,
//       PBRMaterialDefinition baseMaterial,
//       float aspectRatio,
//       LcdScreenOrientation orientation,
//       ResourceHandle<TextureAsset>? colorMetalOverride)   <- the texture
//
// Point that at OUR OffscreenRenderTarget and the panel samples us directly. That
// removes the copy into the panel, the DrawOne hook, RequestRender, the state
// transition, the double buffer, the consumption handshake and the eviction problem
// in one move — because nothing else touches a target we own.
//
// Discovery first: the base PBRMaterialDefinition has to come from somewhere, and
// guessing at it on the render thread is how the last several hours went.
internal static class PanelBinding
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string ArmPath = Path.Combine(RttLog.OutDir, "bind-armed.marker");
    private static readonly string LivePath = Path.Combine(RttLog.OutDir, "bind-live.marker");

    // PER-FEED, and per PANEL within the feed (phase E2 fan-out): the list of panels this
    // feed has bound — or attempted to bind — to our material. The survey and the disarm
    // marker stay process-global — one describes the engine's LCD types, the other is a
    // file on disk that switches the whole route off.
    private static List<(WeakReference Renderer, WeakReference Ctx)> _boundPanels => Feeds.Cur.BoundPanels;

    // THE BOUND LIST IS TOUCHED FROM TWO THREADS, so every access to it is locked.
    //
    // It always was — TryBind appends from the render thread while WantsRepaint is read
    // from the LCD tick — and reading a List<T> that another thread is growing was already
    // a latent race. Pruning (below) makes the read path a WRITER as well, which turns a
    // latent race into a routine one, so this stops being something to note and starts
    // being something to fix. The list is per feed and holds a handful of entries; the lock
    // is uncontended in the normal case and covers a few instructions.
    private static bool IsAttempted(object ctx)
    {
        var list = _boundPanels;
        lock (list)
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i].Ctx.Target, ctx)) return true;
        return false;
    }

    // ---- THE BIND WATCH: is our material STILL on the panel? ---------------------------
    //
    // THE BUG THIS EXISTS TO FIX, reported twice in game: "the game randomly loses the
    // panel's texture causing the feed to die", the second time WHILE APPROACHING A PLANET.
    // The render loop keeps running and every counter stays healthy — only the picture is
    // gone — because the feed never died. Our MATERIAL REPLACEMENT was dropped, and
    // `IsAttempted` is a bind-ONCE latch: once a ctx is in the list, OnPanelRender returns
    // before TryBind forever. Losing the binding was therefore permanent by construction,
    // and the only recovery was a full park cycle.
    //
    // "While approaching a planet" is the useful half of the report: that is when LOD and
    // renderer rebuilds happen, which is exactly when an engine-side material replacement
    // list gets rebuilt without ours in it.
    //
    // WHAT MAKES THIS SAFE TO ACT ON. Re-binding is NOT free — it goes through
    // SetNewScreenMaterialHandle, which is the call implicated in the [RTS] mirror leak
    // (task #31) and, through it, in past device removals. So a re-bind must happen ONLY
    // when we are genuinely unbound, never speculatively and never on a timer.
    //
    // The precise test already existed and was only being used for logging: TryBind reads
    // back `ctx._screenMaterialHandle` — the runtime material the engine created FOR US. If
    // that field still equals what our bind produced, we are bound. If it changed, something
    // else re-materialised the panel and we are not.
    //
    // Compared with Equals, not ReferenceEquals: the handle may be a struct, and a boxed
    // struct is never reference-equal to another box of the same value.
    //
    // A BLIND READER MUST NOT MANUFACTURE "LOST". If the field cannot be resolved, this
    // returns Unknown and the caller does exactly what it did before — bind once, never
    // again. Treating "cannot tell" as "unbound" would produce a re-bind on every content
    // pass, on the render thread, through the one call known to leak. That is the failure
    // this project has hit in other guises: the wrong answer in the unsafe direction.
    private enum Bind { Fresh, Bound, Lost, Unknown }

    // Weak keys: the entry dies with the panel context, so this never keeps a dead panel
    // alive and never needs pruning of its own.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object[]>
        _ourHandle = new();

    private static FieldInfo _screenMatField;
    private static bool _screenMatFieldResolved, _watchBlindLogged;

    private static object ReadScreenMaterialHandle(object ctx)
    {
        if (!_screenMatFieldResolved)
        {
            _screenMatFieldResolved = true;
            _screenMatField = ctx.GetType().GetField("_screenMaterialHandle",
                                  BindingFlags.Instance | BindingFlags.NonPublic);
        }
        return _screenMatField?.GetValue(ctx);
    }

    private static Bind BindStatus(object ctx)
    {
        if (!IsAttempted(ctx)) return Bind.Fresh;
        if (!_ourHandle.TryGetValue(ctx, out var box) || box == null) return Bind.Unknown;
        if (_screenMatField == null && _screenMatFieldResolved) return Bind.Unknown;

        object now;
        try { now = ReadScreenMaterialHandle(ctx); }
        catch { return Bind.Unknown; }
        if (_screenMatField == null) return Bind.Unknown;

        return Equals(now, box[0]) ? Bind.Bound : Bind.Lost;
    }

    // Drop the stale (renderer, ctx) entry so the re-bind REPLACES it rather than adding a
    // second one. TryBind appends per attempt and UnbindNow restores per entry, so a
    // duplicate would put the same panel back twice — the double-release this file already
    // warns about at the end of UnbindNow.
    private static void ForgetPanel(object ctx)
    {
        var list = _boundPanels;
        lock (list)
            for (int i = list.Count - 1; i >= 0; i--)
                if (ReferenceEquals(list[i].Ctx.Target, ctx))
                {
                    list[i] = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                }
        _ourHandle.Remove(ctx);
    }

    // Re-bind budget. If the detection is ever wrong, or the engine starts fighting us for
    // the material every frame, this turns an unbounded render-thread retry loop into a
    // bounded one that says so. Deliberately generous per event and strict per minute.
    private static long _lastRebindMs;
    private static int _rebinds, _rebindsThisMinute;
    private static long _rebindMinuteMs;

    private static bool RebindAllowed()
    {
        var now = Environment.TickCount64;
        if (now - _lastRebindMs < 2000) return false;          // never twice in one repaint burst
        if (now - _rebindMinuteMs > 60000) { _rebindMinuteMs = now; _rebindsThisMinute = 0; }
        if (_rebindsThisMinute >= 6)
        {
            if (_rebindsThisMinute == 6)
            {
                _rebindsThisMinute++;
                RttLog.Line("PANEL REBIND: 6 in one minute — STOPPING for this minute. That is no longer " +
                            "a lost binding, it is a fight: something is re-materialising the panel as fast " +
                            "as we bind it. Re-binding through SetNewScreenMaterialHandle is the call " +
                            "implicated in the [RTS] mirror leak, so backing off is the safe direction.");
            }
            return false;
        }
        _lastRebindMs = now; _rebindsThisMinute++; _rebinds++;
        return true;
    }

    // The shape of the panel this feed is actually driving. 0 = not yet known, in which case
    // the camera renders square exactly as before — an unknown must never be guessed at as
    // 1.0 and silently framed wrong.
    internal static double PrimaryPanelAspect;

    // One line per distinct shape, not per bind — panels rebind on every content pass and an
    // unguarded log here would be thousands of lines a minute.
    private static readonly HashSet<string> _coverLogged = new();
    private static string AspectTag(float a) => a.ToString("F3");

    // Per-panel private material definitions — see the clone note in TryBind. Weak keys, so a
    // destroyed panel takes its clone with it and this never pins dead render objects alive.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object>
        _privateMaterial = new();
    private static System.Reflection.MethodInfo _cloneMi;
    private static bool _cloneMissLogged;
    private static System.Reflection.MethodInfo _cloneCtxMi;
    private static bool _cloneCtxTried, _cloneNoopLogged;

    private static bool _surveyed, _disarmed;

    // The bind happens inside the content-render hook, which an idle panel never
    // enters. While this is true the tick hook drives repaints to get us in there.
    //
    // FAN-OUT: true while any CLAIMING panel is still unbound — live bound pairs counted
    // against the claim set, so a second panel joining an already-bound feed re-arms the
    // repaint drive until its own bind lands.
    public static bool WantsRepaint
    {
        get
        {
            if (_disarmed || !File.Exists(ArmPath)) return false;
            int alive = PruneDead();
            int claimed = Feeds.Cur.ClaimedPanels.Count;
            return alive < (claimed > 0 ? claimed : 1);
        }
    }

    // Drop entries whose panel is gone, and return how many are left alive.
    //
    // The list is APPEND-ONLY otherwise, and it is appended to per bind ATTEMPT — which
    // means once per surface context, and RebuildSurfaceContent replaces those contexts on
    // every forced repaint. Over a long session with repaints being driven that is a slowly
    // growing list of dead WeakReference pairs, walked on every panel tick. Pruning here
    // costs nothing (this walk already happened) and bounds it by the number of LIVE panels.
    //
    // Order is not load-bearing — Unbind restores each entry independently — so the cheap
    // swap-with-last removal is safe.
    private static int PruneDead()
    {
        var list = _boundPanels;
        lock (list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Ctx.IsAlive && list[i].Renderer.IsAlive) continue;
                list[i] = list[list.Count - 1];
                list.RemoveAt(list.Count - 1);
            }
            return list.Count;
        }
    }
    private static int _errLogs;

    public static void Reset() => Reset(true);

    // `last` = no other feed is still live (phase F2). Everything this method clears is
    // PROCESS state — the survey latch and the emissivity bookkeeping describe the shared
    // LCD material, not a feed — so a single feed's teardown must not clear it while a
    // neighbour is still relying on it. The per-feed part of a binding teardown is the
    // BoundPanels list, and that is consumed by Unbind, not here.
    public static void Reset(bool last)
    {
        // NOT the bound list. Reset runs in FeedGate.Shutdown BEFORE RestoreEngineState
        // calls Unbind, and Unbind is what consumes the list — clearing it here would
        // leave every panel wearing our runtime material with nothing left knowing how
        // to put the stock one back. (The old code had the same shape: it cleared the
        // _bound flag but left the weak pair for Unbind.)
        _errLogs = 0;
        if (last)
        {
            _surveyed = false;
            _emissivityApplied = double.NaN;
            _emissivityBlocked = false;
        }


        // Against the PROCESS, not this assembly — see CameraRender.WrittenByThisProcess.
        if (File.Exists(LivePath) && !CameraRender.WrittenByThisProcess(LivePath))
        {
            _disarmed = true;
            RttLog.Line("!!! PREVIOUS SESSION DIED DURING BINDING — disabled. Delete " + LivePath + " to retry.");
        }
        else if (File.Exists(LivePath))
        {
            RttLog.Line("Panel binding: mid-bind marker present but written by THIS process — a " +
                        "hot reload landing mid-bind, not a death. Continuing, and clearing it.");
            try { File.Delete(LivePath); } catch { }
        }
    }

    // Called from the LCD render postfix for the [RTC] panel, with the renderer.
    public static void OnPanelRender(object renderer, object ctx)
    {
        if (renderer == null || ctx == null) return;
        try
        {
            if (!_surveyed) { _surveyed = true; Survey(renderer, ctx); }

            if (!FeedGate.Active) return;   // dormant: do not touch the panel's material
            if (_disarmed) return;
            if (!File.Exists(ArmPath)) return;

            // Deliberately AFTER the disarm and arm-marker checks.
            //
            // This originally ran before them, so that the emissive gain could be swept
            // against the stock test pattern. That was wrong: it mutates a game material,
            // and the arm markers exist precisely so that a disarmed plugin changes
            // nothing. "Disarmed" has to mean disarmed, or the safety model is worthless
            // the one time it matters.
            ApplyEmissivity(ctx);
            ApplyFsrMask(ctx);

            // Per-PANEL, not per-feed (phase E2 fan-out): a feed that already bound one
            // panel still binds a second claimant when its ctx arrives here.
            //
            // BIND-ONCE BECOMES BIND-AND-STAY-BOUND. This used to be `if (IsAttempted(ctx))
            // return;`, which made a lost binding permanent — see the Bind enum above.
            switch (BindStatus(ctx))
            {
                case Bind.Bound:
                    return;

                case Bind.Unknown:
                    // Exactly the old behaviour, and said out loud ONCE so a silently
                    // disabled watch is never mistaken for a panel that never drops.
                    if (!_watchBlindLogged)
                    {
                        _watchBlindLogged = true;
                        RttLog.Line("PANEL REBIND WATCH IS BLIND: could not read ctx._screenMaterialHandle, " +
                                    "so 'still bound?' is unanswerable and this falls back to bind-once. " +
                                    "A lost panel texture will NOT self-heal — park-cycle the feed " +
                                    "(feedsDisabled = 1, then blank) to recover. This is a reader problem, " +
                                    "NOT evidence that the binding is holding.");
                    }
                    return;

                case Bind.Lost:
                    if (!FeedConfig.PanelRebindOnLoss) return;
                    if (!RebindAllowed()) return;
                    RttLog.Line($"PANEL REBIND #{_rebinds}: our screen material is no longer on this panel — " +
                                "something re-materialised it (LOD or renderer rebuild is the prime suspect; " +
                                "this was first reported while approaching a planet). Re-binding to our render " +
                                "target. The feed never died; only the material replacement was dropped.");
                    ForgetPanel(ctx);
                    break;
            }

            TryBind(renderer, ctx);
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("panel binding", e); }
    }

    // The display side of the tonal-range problem.
    //
    // LCDPixel.hlsl computes
    //
    //     output.Emissivity = saturate(ext.Values.y - 1/255.) * materialInstance.EmissivityMultiplier
    //
    // and that emissivity is added into the MAIN view's HDR light buffer
    // (specular += basecolor * Emissivity * Post_.BloomEmissiveness) before the engine's
    // own bloom and Hable tonemap. So the panel genuinely can exceed white and glow —
    // the emissive path is real, and it is the only axis on which our feed can produce
    // light rather than reflectance.
    //
    // What it is NOT is per-pixel. SetNewScreenMaterialHandle overrides only
    // ColorMetalTexture; the emissive MASK comes from the base material's
    // ExtensionsTexture green channel, which we do not write. So this is a uniform gain
    // over an image already clamped to 1.0 by GBuffer0.rgb. Per-pixel range needs our own
    // ExtensionsTexture bound as well — a separate step, and only worth building if this
    // one proves the path is live.
    //
    // Two outcomes, both decisive: the panel gets dramatically brighter and starts
    // blooming (the path works, and the gain is the knob), or nothing changes at all
    // (the stock extensions green is ~0 for LCDScreen_On, the shipped x10 was never doing
    // anything, and binding our own extensions texture is mandatory rather than optional).
    private static double _emissivityApplied = double.NaN;
    private static bool _emissivityBlocked;
    private static object _stockEmissivity, _emissiveMaterial;
    private static System.Reflection.PropertyInfo _emissiveProp;

    // Put the shared LCD material back exactly as the game shipped it. Called when the
    // feed gate goes dormant, so a mod-free comparison really is mod-free.
    public static void RestoreEngineState()
    {
        // The FSR mask goes back first, and on its own terms: it is a SEPARATE property on
        // the same shared material, so an early-out on the emissivity trio would have left
        // every LCD in the world permanently marked FSR-reactive after the gate went
        // dormant — which would silently invalidate exactly the mod-free comparison this
        // method exists to make honest.
        if (_stockFsrMask != null && _fsrMaskMaterial != null && _fsrMaskProp != null)
        {
            try
            {
                _fsrMaskProp.SetValue(_fsrMaskMaterial, _stockFsrMask);
                RttLog.Line($"Panel material: FSRMaskAmount restored to the stock {_stockFsrMask}.");
            }
            catch (Exception e) { RttLog.Error("restore panel FSR mask", e); }
            finally
            {
                _stockFsrMask = _fsrMaskMaterial = null;
                _fsrMaskProp = null; _fsrFramesField = null;
                _fsrMaskBlocked = _fsrMaskLogged = false;
            }
        }

        if (_stockEmissivity == null || _emissiveMaterial == null || _emissiveProp == null) return;
        try
        {
            _emissiveProp.SetValue(_emissiveMaterial, _stockEmissivity);
            RttLog.Line($"Panel material: EmissivityMultiplier restored to the stock {_stockEmissivity}.");
        }
        catch (Exception e) { RttLog.Error("restore panel emissivity", e); }
        finally
        {
            _stockEmissivity = _emissiveMaterial = null;
            _emissiveProp = null;
            _emissivityApplied = double.NaN;   // so it re-applies cleanly on restart
        }
    }

    // THE FSR REACTIVE MASK — the fix for the accumulating star-smear on the panel.
    //
    // Symptom, reported after months of living with it: at close range the feed looks
    // sharp, but the further the player stands back the more the stars smear ALONG their
    // apparent path; moving the player briefly cleans it up, and standing still lets it
    // build again. Everything on the panel gets it — stars just show it most.
    //
    // It is the player's FSR accumulating temporal history over a surface it believes is
    // static. The engine already has the answer and we were bypassing it:
    //
    //   RebuildSurfaceContent (content changed):  ScreenMaterial.FSRMaskAmount = 1f
    //                                             ctx.FsrMaskFramesRemaining  = 5
    //   TickFsrMask (once per frame, per surface): if (remaining > 0) { remaining--;
    //                                                if (remaining == 0) FSRMaskAmount = 0f; }
    //
    // FSRMaskAmount writes the panel into FSR's REACTIVE mask, which tells FSR "these
    // pixels change independently of their motion vectors — do not trust history here".
    // Our feed replaces the panel's content EVERY frame but never goes through
    // RebuildSurfaceContent, so nothing ever arms it: the amount sits at 0 forever and FSR
    // blends our changing image with reprojected old frames. Distance-dependent because
    // FSR leans harder on history the fewer screen pixels the panel covers, and player
    // motion perturbs the history, which is exactly the reported behaviour.
    //
    // Re-armed on EVERY panel tick rather than once. Our postfix runs after TickFsrMask has
    // already decremented and possibly zeroed, so a one-shot write would be undone; setting
    // it every tick means we always win that race.
    //
    // SHARED-MATERIAL CAVEAT, stated because it is a real cost and not hypothetical:
    // LCDMaterialDefinition is shared by every panel in the world (see ApplyEmissivity), so
    // FSRMaskAmount = 1 marks ALL LCD panels reactive, not only ours. For the others that
    // means FSR stops accumulating history on their surfaces — slightly noisier text, no
    // correctness problem. The stock value is captured and restored on shutdown exactly as
    // emissivity is, so vanilla behaviour returns when the mod goes dormant.
    private static object _stockFsrMask;
    private static object _fsrMaskMaterial;
    private static PropertyInfo _fsrMaskProp;
    private static FieldInfo _fsrFramesField;
    private static bool _fsrMaskBlocked, _fsrMaskLogged;

    private static void ApplyFsrMask(object ctx)
    {
        if (_fsrMaskBlocked || !FeedConfig.PanelFsrMask) return;
        try
        {
            var material = Prop(ctx, "ScreenMaterial");
            if (_fsrMaskProp == null)
            {
                _fsrMaskProp = material?.GetType().GetProperty("FSRMaskAmount", Any);
                if (_fsrMaskProp == null || !_fsrMaskProp.CanWrite)
                {
                    _fsrMaskBlocked = true;
                    RttLog.Line("FSR mask: FSRMaskAmount not settable on " +
                                (material?.GetType().Name ?? "<no ScreenMaterial>") +
                                " — the panel cannot be marked FSR-reactive, so expect temporal " +
                                "smearing on the feed at distance when the player's AA is FSR.");
                    return;
                }
                _stockFsrMask = _fsrMaskProp.GetValue(material);
                _fsrMaskMaterial = material;
            }

            _fsrFramesField ??= ctx.GetType().GetField("FsrMaskFramesRemaining", Any);

            // Write the MATERIAL property only when it is not already 1, and re-arm the
            // plain int field every tick.
            //
            // The first version set both every tick. That is ~30 writes/sec to a property on
            // a shared material DEFINITION, where the engine writes it about twice per
            // content change — and this project has already lost a build to exactly that
            // shape (the planet-env rebuild's attempt 2 died of descriptor churn from
            // re-registering tables 20x/sec). It was not the cause of the 2026-07-29 CTD —
            // that was the flares pass — but it is unnecessary risk for no benefit.
            //
            // Re-arming the counter is what actually keeps the mask alive: TickFsrMask only
            // zeroes FSRMaskAmount when the countdown REACHES zero, so a counter that never
            // reaches zero means the amount never needs rewriting. The field is a plain int
            // on a per-surface context object — no setter logic, no material involvement.
            var current = _fsrMaskProp.GetValue(material);
            if (!(current is float f && f == 1f)) _fsrMaskProp.SetValue(material, 1f);
            _fsrFramesField?.SetValue(ctx, 5);   // the engine's own re-arm value

            if (!_fsrMaskLogged)
            {
                _fsrMaskLogged = true;
                RttLog.Line($"FSR mask: {material.GetType().Name}.FSRMaskAmount held at 1 and " +
                            $"FsrMaskFramesRemaining re-armed to 5 every panel tick (stock was " +
                            $"{_stockFsrMask}). The panel is now in FSR's reactive mask, so the " +
                            "player's upscaler stops accumulating history over our per-frame " +
                            "content — this is the accumulating star-smear fix." +
                            (_fsrFramesField == null
                                ? " WARNING: FsrMaskFramesRemaining not found, so TickFsrMask will " +
                                  "zero the amount and the fix will only half work."
                                : ""));
            }
        }
        catch (Exception e) { _fsrMaskBlocked = true; RttLog.Error("apply FSR mask", e); }
    }

    private static void ApplyEmissivity(object ctx)
    {
        double want = FeedConfig.Emissivity;
        if (_emissivityBlocked || want <= 0.0 || want == _emissivityApplied) return;
        try
        {
            var material = Prop(ctx, "ScreenMaterial");
            var p = material?.GetType().GetProperty("EmissivityMultiplier", Any);
            if (p == null || !p.CanWrite)
            {
                _emissivityBlocked = true;
                RttLog.Line($"Emissivity: EmissivityMultiplier not settable on " +
                            $"{material?.GetType().Name ?? "<no ScreenMaterial>"} — display gain unavailable.");
                return;
            }

            var was = p.GetValue(material);

            // Remember the STOCK value the first time, and remember WHERE it lives.
            // LCDMaterialDefinition is shared by every panel in the world, so leaving our
            // multiplier on it after the mod goes dormant would light the whole ship
            // differently from vanilla and quietly invalidate any A/B comparison.
            if (_stockEmissivity == null) { _stockEmissivity = was; _emissiveMaterial = material; _emissiveProp = p; }

            p.SetValue(material, (float)want);
            _emissivityApplied = want;
            RttLog.Line($"Emissivity: {material.GetType().Name}.EmissivityMultiplier {was} -> {want}. " +
                        "No visible change means the base material's extensions green is ~0 and the " +
                        "multiplier has nothing to scale.");
        }
        catch (Exception e) { _emissivityBlocked = true; RttLog.Error("apply emissivity", e); }
    }

    // What is reachable, and where does a PBRMaterialDefinition come from?
    private static void Survey(object renderer, object ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Panel binding survey {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        sb.AppendLine();
        sb.AppendLine($"renderer : {renderer.GetType().FullName}");
        sb.AppendLine($"context  : {ctx.GetType().FullName}");
        sb.AppendLine();

        var mi = ctx.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "SetNewScreenMaterialHandle");
        sb.AppendLine("-- SetNewScreenMaterialHandle --");
        if (mi == null) sb.AppendLine("  NOT FOUND");
        else foreach (var p in mi.GetParameters())
            sb.AppendLine($"  {p.ParameterType.FullName?.Split('.').Last()} {p.Name}");
        sb.AppendLine();

        // The base material is the unknown. Look for a PBRMaterialDefinition anywhere
        // reachable from the context, its definition, or the renderer.
        sb.AppendLine("-- hunting a PBRMaterialDefinition --");
        foreach (var (label, root) in new[]
        {
            ("ctx", ctx),
            ("ctx.Definition", Prop(ctx, "Definition")),
            ("ctx.ScreenMaterial", Prop(ctx, "ScreenMaterial")),
            ("renderer", renderer),
        })
        {
            if (root == null) { sb.AppendLine($"  {label}: null"); continue; }
            sb.AppendLine($"  {label} ({root.GetType().Name}):");
            foreach (var f in root.GetType().GetFields(Any).Take(30))
            {
                object v = null;
                try { v = f.GetValue(root); } catch { }
                var tn = f.FieldType.Name;
                if (tn.Contains("Material") || tn.Contains("PBR"))
                    sb.AppendLine($"      field {tn,-34} {Clean(f.Name),-30} {(v == null ? "null" : "set")}");
            }
            foreach (var p in root.GetType().GetProperties(Any).Take(30))
            {
                var tn = p.PropertyType.Name;
                if (!tn.Contains("Material") && !tn.Contains("PBR")) continue;
                object v = null;
                try { v = p.GetValue(root); } catch { }
                sb.AppendLine($"      prop  {tn,-34} {p.Name,-30} {(v == null ? "null" : "set")}");
            }
        }
        sb.AppendLine();

        // Aspect ratio and orientation come off the surface definition / state.
        var def = Prop(ctx, "Definition");
        sb.AppendLine("-- surface definition --");
        if (def != null)
            foreach (var f in def.GetType().GetFields(Any).Take(25))
            {
                object v = null; try { v = f.GetValue(def); } catch { }
                sb.AppendLine($"  {f.FieldType.Name,-30} {Clean(f.Name),-28} = {v}");
            }
        sb.AppendLine();

        var path = Path.Combine(RttLog.OutDir, "panel-binding-survey.txt");
        try { File.WriteAllText(path, sb.ToString()); RttLog.Line($"Panel binding survey written to {path}"); }
        catch (Exception e) { RttLog.Error("survey write", e); }
    }

    // Put the panel back on its STOCK screen material through the engine's own path.
    //
    // The game's deferred-assert log showed "Can't remove material <guid>. It is not
    // present in material system." at gate shutdown, timestamp-matched to our teardown.
    // SetNewScreenMaterialHandle is ReleaseScreenMaterialHandle() + CreateRuntimeLcdMaterial
    // + store — so after our bind, the panel's _screenMaterialHandle holds OUR runtime
    // material, and across gate cycles both the LCD system's own lifecycle and the next
    // rebind can try to release the same instance: the second release asserts.
    //
    // Calling the same method once more with the stock DefaultScreenMaterial and NO
    // colorMetalOverride releases our handle exactly once, via the designed path, and
    // leaves the panel owning a fresh stock material whose lifecycle the LCD system
    // manages normally. Nothing left dangling for anyone to double-release.
    //
    // ...AND IT MUST NOT RUN ON THE RENDER THREAD. That sentence used to read "Render thread
    // only (FeedGate.Shutdown runs there, same thread the bind ran on)" and it was exactly
    // backwards.
    //
    // SetNewScreenMaterialHandle calls ReleaseScreenMaterialHandle -> RuntimeMaterialHandle
    // .Dispose() -> RenderCommandBuffer.Default, and the engine guards that property:
    //     RenderThreadManager.cs:51
    //     '_renderThread == null || _renderThread == _mainThread || _renderThread != Thread.CurrentThread'
    // From the IL, Default is not thread-local — it returns the GLOBAL command buffer
    // (RenderEngineComponent.Instance.CommandBuffer, via a null _currentThreadOverride).
    // So calling this from the render thread appends a material-release command into the
    // very buffer the render thread is consuming, mid-frame. The assert fires once per
    // session in EVERY session, at bind and at unbind alike, and on 2026-08-02 at 19:51:03
    // the unbind one was followed 108 ms later by the player's next frame dying in
    // FlaresContext.GetFlareConstants — the exit-to-menu CTD.
    //
    // So: if we are on the render thread, HAND THE LIST OFF and let the sim-pump seat do the
    // engine calls. WorldGrids.OnSimPump runs at the top of every Scene.Tick, on the scene's
    // own thread, which is not the render thread — the seat already exists and this is
    // simply the right place for the work.
    //
    // WHAT IF THE PUMP NEVER DRAINS IT? At exit-to-menu the pump can stop first, and then
    // the panels keep our runtime material. That is the acceptable failure: the world is
    // being torn down, the LCD system releases its own handles, and the worst case is the
    // "Can't remove material" DEFERRED ASSERT this method was written to avoid. An assert in
    // a log beats a NullReferenceException on the render thread. Never trade a crash for a
    // tidier teardown.
    public static void Unbind()
    {
        // The render thread's identity, learned from the one place we are certainly on it.
        // Not read from RenderThreadManager._renderThread: that field is per-INSTANCE and
        // finding the live manager is one more reflection hunt that can silently return
        // null — in which case we would decide "not the render thread" and do the unsafe
        // thing. Observing our own thread is not falsifiable in that way.
        int rt = WholeSceneRender.RenderThreadId;
        if (rt != 0 && Environment.CurrentManagedThreadId == rt)
        {
            var src = _boundPanels;
            int handed;
            lock (src)
            {
                lock (_deferredUnbind)
                {
                    _deferredUnbind.AddRange(src);
                    handed = src.Count;
                }
                src.Clear();
            }

            if (handed > 0)
                RttLog.Line($"Panel material: {handed} panel(s) DEFERRED to the sim-pump seat for unbind — " +
                            "releasing an LCD runtime material writes into the global render command buffer, " +
                            "and doing that from the render thread is what preceded the exit-to-menu CTD. " +
                            "The restore happens on the next Scene.Tick.");
            return;
        }

        UnbindNow(_boundPanels, "inline (not on the render thread)");
    }

    // Handed off by Unbind when it is called on the render thread. Process-global rather
    // than per-feed: by the time it drains, the feed registry may have moved on, and a
    // panel wearing our material is a panel wearing our material regardless of who bound it.
    private static readonly List<(WeakReference Renderer, WeakReference Ctx)> _deferredUnbind = new();

    // Called from WorldGrids.OnSimPump, before its own early-outs — a deferred unbind must
    // not depend on the marker knobs being on.
    internal static void DrainDeferredUnbind()
    {
        lock (_deferredUnbind)
        {
            if (_deferredUnbind.Count == 0) return;
            UnbindNow(_deferredUnbind, "on the sim-pump seat (deferred off the render thread)");
        }
    }

    private static void UnbindNow(List<(WeakReference Renderer, WeakReference Ctx)> list, string where)
    {
        // EVERY bound panel (phase E2 fan-out), each restored independently — one panel
        // having been destroyed must not stop the others being put back. Consume-and-clear:
        // the list is this feed's record of what it changed, and after this it has changed
        // nothing.
        int restored = 0, gone = 0;

        // Locked for the same reason every other access is (see IsAttempted). Held across
        // the engine calls deliberately: this is the render thread putting the panels back
        // on their stock material, and a bind attempt arriving from a repaint half way
        // through would be appending to a list that is about to be cleared.
        lock (list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var renderer = list[i].Renderer.Target;
                var ctx = list[i].Ctx.Target;
                if (renderer == null || ctx == null) { gone++; continue; }   // panel destroyed

                try
                {
                    var mi = ctx.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "SetNewScreenMaterialHandle");
                    var def = Prop(ctx, "Definition");
                    var baseMaterial = Prop(def, "DefaultScreenMaterial");
                    var aspect = Prop(def, "AspectRatio");
                    var orientation = Prop(Prop(ctx, "State"), "Orientation");
                    if (mi == null || baseMaterial == null) continue;

                    mi.Invoke(ctx, new[] { renderer, baseMaterial, aspect, orientation, null });
                    restored++;
                }
                catch (Exception e) { RttLog.Error("panel unbind", e); }
            }
            list.Clear();
        }

        if (restored > 0 || gone > 0)
            RttLog.Line($"Panel material: {restored} panel(s) rebound to the STOCK screen material {where} " +
                        (gone > 0 ? $"({gone} already destroyed) " : "") +
                        "— our runtime material released through the engine's own path, so nothing " +
                        "dangles for the \"Can't remove material\" double-release.");
    }

    // Rebuild the panel's screen material so it samples OUR render target.
    //
    //   ctx.SetNewScreenMaterialHandle(renderer,
    //       ctx.Definition.DefaultScreenMaterial,   // base PBR material (LCDScreen_On)
    //       ctx.Definition.AspectRatio,
    //       ctx.State.Orientation,
    //       ourTarget.TextureHandle)                // <- colorMetalOverride
    private static void TryBind(object renderer, object ctx)
    {
        var rt = BlitProbe.FeedTarget;
        if (rt == null) { RttLog.Line("Phase 2: no render target of ours yet — waiting."); return; }

        // Registered at ATTEMPT, so a failed bind is not retried every content pass (a
        // retry loop on the render thread is a bad idea) and Unbind still sweeps it.
        // Weak, because the panel can be destroyed (world unload, block grinding) while
        // we hold these, and a strong reference would keep dead render objects alive.
        var bound = _boundPanels;
        lock (bound) bound.Add((new WeakReference(renderer), new WeakReference(ctx)));
        try
        {
            File.WriteAllText(LivePath, $"binding attempted {DateTime.Now:O}\n");

            var mi = ctx.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "SetNewScreenMaterialHandle");
            if (mi == null) { RttLog.Line("Phase 2: SetNewScreenMaterialHandle not found."); return; }

            var def = Prop(ctx, "Definition");
            var baseMaterial = Prop(def, "DefaultScreenMaterial");
            var defAspect = Prop(def, "AspectRatio");
            var orientation = Prop(Prop(ctx, "State"), "Orientation");

            // ---- SQUARE RENDER ONTO A NON-SQUARE PANEL, WITHOUT A SECOND RENDER ----------
            //
            // THE TWO REQUIREMENTS, and the second is the binding one (user, 2026-08-04):
            //   1. no aspect DISTORTION — uniform scale until the panel's LONG axis is filled
            //      by the 1024x1024 render, short axis overrunning off the display ("cover")
            //   2. NO DUPLICATED RENDERING when several panels show the same feed
            //
            // (2) rules out the obvious implementation. Re-rendering or resampling per panel
            // would give pixel-perfect fit and destroy the property that makes the fan-out
            // cheap: BlitProbe.FeedTarget is ONE target and TryBind hands that same object to
            // every panel, so panels are extra SAMPLERS, not extra renders. Verified live —
            // request = drawOne(ours) = copies = 25.2/s with the feed bound, independent of
            // panel count. Any fix must therefore be a per-MATERIAL property, and the counter
            // above is the regression test: if drawOne(ours) ever tracks panel count, it broke.
            //
            // aspectRatio is the only per-panel lever the bind gives us, and it is genuinely
            // shader-visible — CreateRuntimeLcdMaterial does
            // LCDMaterialDefinition.set_ScreenAspectRatio(aspectRatio) (read from IL), and the
            // value is part of SharedRuntimeMaterialKey so panels with different aspects get
            // different material instances off the SAME texture. That is exactly the shape we
            // need: per-panel framing, shared pixels.
            //
            // WHAT IS NOT ESTABLISHED, and why this is a knob rather than a formula: that
            // parameter's per-pixel meaning is documented nowhere and its only proven use is
            // laying out TEXT. For an override texture it may crop (what we want), letterbox,
            // or be ignored. So we pass a CHOSEN value and print both numbers; each panel on
            // the multi-panel block is then one labelled data point, and the mapping gets
            // measured instead of assumed.
            // PUBLISH THE PANEL'S REAL SHAPE. CameraRender uses it to pre-squeeze the
            // projection so the panel's own stretch lands undistorted and full-bleed — see the
            // anamorphic-fit note there. Taken from the panel we are ACTUALLY binding, so a
            // rebuilt or retagged panel re-fits on its next bind with no restart.
            if (defAspect is float pa && pa > 0.05f && pa < 20.0f)
            {
                if (System.Math.Abs(PrimaryPanelAspect - pa) > 0.001)
                    RttLog.Line($"PANEL FIT: primary panel aspect {pa:F3} published for feed " +
                                $"{Feeds.Cur.Id} (was {PrimaryPanelAspect:F3}). The projection re-shapes " +
                                "on the next camera pass; the render target does not change.");
                PrimaryPanelAspect = pa;
            }

            object aspect = defAspect;
            var wantAspect = FeedConfig.PanelBindAspect;
            string aspectWhy = "panel's own Definition.AspectRatio (unchanged)";
            if (wantAspect >= 0.0 && defAspect is float dA)
            {
                float use = wantAspect > 0.0 ? (float)wantAspect : 1.0f;
                aspect = use;
                aspectWhy = wantAspect > 0.0
                    ? $"forced {use:F3} by panelBindAspect"
                    : $"1.000 — declaring the CONTENT square (panel's own is {dA:F3})";
            }

            // ---- A PRIVATE MATERIAL PER PANEL, and why it is the keystone ------------------
            //
            // The engine borrows runtime LCD materials from a SHARED store keyed on
            //     SharedRuntimeMaterialKey { MaterialDefinition, AspectRatio, Orientation }
            // (read from IL). Every panel matching all three gets the SAME runtime material
            // instance — and we bind our render target into it as a colorMetal override.
            //
            // TWO CONSEQUENCES, one observed today and one long open:
            //  * Changing the aspect changes the key, so we silently BORROW A DIFFERENT
            //    PANEL'S MATERIAL. In game 2026-08-04 that showed as a flipped, glitched
            //    display the moment panelBindAspect moved off native.
            //  * Any other panel whose key matches ours displays OUR FEED. That is the
            //    [RTS] mirror (task #31), open for weeks with two theories already killed —
            //    the mechanism was sitting in the key definition the whole time.
            //
            // Passing a CLONE of the base definition makes MaterialDefinition unique to this
            // panel, so the key can never collide: a private runtime material, holding our
            // override, that nobody else can borrow and whose aspect we may set freely. That
            // last part is what unblocks per-panel framing of ONE shared texture — different
            // panels sampling the same square differently, with no extra render.
            //
            // ONE CLONE PER PANEL, CACHED. Cloning per bind would mint a new key every time
            // and defeat the store completely; the table is weak-keyed so a destroyed panel
            // takes its clone with it.
            if (FeedConfig.PanelPrivateMaterial && baseMaterial != null)
            {
                try
                {
                    if (!_privateMaterial.TryGetValue(ctx, out var mine) || mine == null)
                    {
                        // THE PARAMETERLESS DeepClone RETURNS THE SAME INSTANCE for these
                        // definitions — measured 2026-08-04: "clone of LCDMaterialDefinition
                        // #00b928be -> #00b928be", and neither PBRMaterialDefinition nor
                        // LCDMaterialDefinition overrides GetHashCode, so an identical hash is
                        // identical IDENTITY, not a value coincidence. Definitions are almost
                        // certainly interned, so cloning one hands the registered object back.
                        //
                        // That matters because the shared-material key holds the definition BY
                        // REFERENCE: same object, same key, same borrowed material — the clone
                        // would have achieved exactly nothing while logging success.
                        //
                        // So try the CloningContext overload with a FRESH context first, which
                        // is the one that can produce a genuinely new graph, and fall back to
                        // the parameterless form only as a second choice.
                        if (_cloneCtxMi == null && !_cloneCtxTried)
                        {
                            _cloneCtxTried = true;
                            _cloneCtxMi = baseMaterial.GetType().GetMethods(Any)
                                .FirstOrDefault(m => m.Name == "DeepClone" && m.GetParameters().Length == 1);
                        }
                        if (_cloneCtxMi != null)
                        {
                            try
                            {
                                var ctxType = _cloneCtxMi.GetParameters()[0].ParameterType;
                                var bare = ctxType.IsByRef ? ctxType.GetElementType() : ctxType;
                                var args = new[] { Activator.CreateInstance(bare) };
                                mine = _cloneCtxMi.Invoke(baseMaterial, args);
                            }
                            catch { mine = null; }
                        }
                        if (mine == null || ReferenceEquals(mine, baseMaterial))
                        {
                            _cloneMi ??= baseMaterial.GetType().GetMethods(Any)
                                .FirstOrDefault(m => m.Name == "DeepClone" && m.GetParameters().Length == 0);
                            mine = _cloneMi?.Invoke(baseMaterial, null);
                        }

                        // THE CHECK THAT MUST GATE THE VERDICT. A clone that is the same object
                        // is not a clone, and saying "PRIVATE clone" over it is exactly the kind
                        // of self-contradicting success line this project keeps being misled by.
                        if (ReferenceEquals(mine, baseMaterial))
                        {
                            mine = null;
                            if (!_cloneNoopLogged)
                            {
                                _cloneNoopLogged = true;
                                RttLog.Line("PANEL MATERIAL: DeepClone returned THE SAME INSTANCE " +
                                            $"(#{baseMaterial.GetHashCode():x8}), so the shared-material key is " +
                                            "UNCHANGED and this panel is still on the shared material. Definitions " +
                                            "appear to be interned. NOT private, NOT a fix — the [RTS] mirror and " +
                                            "the aspect collision both remain. A unique key needs a genuinely new " +
                                            "definition object (object-builder construction), not a clone.");
                            }
                        }

                        if (mine != null)
                        {
                            _privateMaterial.Remove(ctx);
                            _privateMaterial.Add(ctx, mine);
                            RttLog.Line($"PANEL MATERIAL: bound with a PRIVATE clone (VERIFIED distinct instance) of " +
                                        $"{baseMaterial.GetType().Name}#{baseMaterial.GetHashCode():x8} " +
                                        $"-> #{mine.GetHashCode():x8}. The shared-material key is now unique to " +
                                        "this panel, so no other panel can borrow the material carrying our feed " +
                                        "(the [RTS] mirror, task #31) and this panel's aspect is ours to set.");
                        }
                        else if (!_cloneMissLogged)
                        {
                            _cloneMissLogged = true;
                            RttLog.Line("PANEL MATERIAL: no parameterless DeepClone on " +
                                        $"{baseMaterial.GetType().Name} — falling back to the SHARED material. " +
                                        "Mirroring and aspect collisions remain possible. Shape miss, not a safe result.");
                        }
                    }
                    if (mine != null) baseMaterial = mine;
                }
                catch (Exception e) { RttLog.Error("panel material clone", e); }
            }

            // ---- GOAL 11: HAND THIS PANEL A TEXTURE ITS OWN SHAPE, NOT THE RAW SQUARE ------
            //
            // The panel's display aspect comes from IMMUTABLE definition data only — its pixel
            // Resolution, corrected for the user's Orientation. Deliberately NOT from
            // State.PreserveAspectRatio, which is the stock toggle we are required to be
            // independent of, and not from the aspect we pass at bind time, which is part of
            // SharedRuntimeMaterialKey and is left native so we never borrow another panel's
            // runtime material.
            //
            // CoverTargetFor returns a target whose aspect already EQUALS this panel's, filled
            // by one cover-cropped quad from the shared square. Because the shapes match, the
            // panel's own fit mode becomes a no-op either way: contain has nothing to
            // letterbox, stretch has nothing to stretch. That is the whole point — we do not
            // fight the stock setting, we make it irrelevant.
            //
            // NULL IS A SUPPORTED ANSWER and means "bind the square directly": the feature is
            // off, the panel is already square, or the derived target could not be made.
            // Falling back to today's behaviour beats blanking a panel.
            object rtForPanel = rt;
            try
            {
                if (FeedConfig.PanelCoverFit)
                {
                    var res = Prop(def, "Resolution");
                    int ori = orientation is int oi ? oi : System.Convert.ToInt32(orientation);
                    float declared = defAspect is float df ? df : 1.0f;

                    // Vector2I read reflectively, same as every other panel property here, so
                    // this file keeps its "no engine math types" shape.
                    int rx = 0, ry = 0;
                    if (res != null)
                    {
                        var rt2 = res.GetType();
                        var fx = rt2.GetField("X", Any); var fy = rt2.GetField("Y", Any);
                        if (fx != null && fy != null)
                        {
                            rx = System.Convert.ToInt32(fx.GetValue(res));
                            ry = System.Convert.ToInt32(fy.GetValue(res));
                        }
                    }

                    float eff = BlitProbe.EffectiveAspect(rx, ry, declared, ori);
                    var fitted = BlitProbe.CoverTargetFor(eff);
                    if (fitted != null)
                    {
                        rtForPanel = fitted;
                        if (_coverLogged.Add(AspectTag(eff)))
                            RttLog.Line($"COVER FIT: panel resolution {rx}x{ry} orientation={ori} " +
                                        $"-> effective aspect {eff:F3} (declared {declared:F3}). Binding the " +
                                        "SHAPE-MATCHED target instead of the raw square, so PreserveAspectRatio " +
                                        "cannot change what is displayed.");
                    }
                }
            }
            catch (Exception e) { RttLog.Error("cover fit select", e); }

            // colorMetalOverride is Nullable<ResourceHandle<TextureAsset>>; our target
            // carries ResourceHandle<T> for a different T. The engine supplies the
            // conversions: ResourceHandle<T> -> ResourceHandle -> ResourceHandle<TextureAsset>.
            var texHandle = Prop(rtForPanel, "TextureHandle");
            var ps = mi.GetParameters();
            var wantType = Nullable.GetUnderlyingType(ps[4].ParameterType) ?? ps[4].ParameterType;
            var converted = ConvertHandle(texHandle, wantType);

            // Both numbers, always: the panel's real shape AND what we told the engine. With
            // several panels of different shapes on one block, these lines are the dataset
            // that maps ScreenAspectRatio's actual behaviour for an override texture.
            RttLog.Line($"Phase 2: binding — material={(baseMaterial == null ? "NULL" : "ok")} " +
                        $"panelAspect={defAspect} passedAspect={aspect} ({aspectWhy}) " +
                        $"orientation={orientation} " +
                        $"handle={(texHandle == null ? "NULL" : texHandle.GetType().Name)} -> " +
                        $"{(converted == null ? "CONVERSION FAILED" : wantType.Name)}. " +
                        "The render is 1024x1024 SQUARE and SHARED — every panel samples the one " +
                        "target, so nothing here may add a render or a resample.");

            if (baseMaterial == null || converted == null)
            {
                RttLog.Line("Phase 2: aborting bind — missing material or handle conversion.");
                try { File.Delete(LivePath); } catch { }
                return;
            }

            mi.Invoke(ctx, new[] { renderer, baseMaterial, aspect, orientation, converted });
            RttLog.Line("=== PHASE 2: panel material rebound to our own render target. ===");

            // MIRROR FORENSICS (2026-08-01): say which runtime material the engine just
            // created for US, in the same identity format the [RTS diag] uses. If the stats
            // panel's handle flips to THIS value on its next 500 ms rebuild, the LCD material
            // system is serving our runtime material to other panels — the shared-definition
            // cache collision — and the fix is binding with a per-panel material definition
            // rather than the shared LCDScreen_On.
            try
            {
                var ours = ReadScreenMaterialHandle(ctx);
                RttLog.Line($"Phase 2: OUR runtime material handle = " +
                            (ours == null ? "<null>" : $"{ours.GetType().Name}#{ours.GetHashCode():x8}") +
                            " (compare against [RTS diag] lines).");

                // REMEMBER IT — this is what makes "are we still bound?" answerable on every
                // later content pass, and so what turns bind-once into bind-and-stay-bound.
                // Recorded even when null: a null here means the engine gave us no handle,
                // and a later NON-null is still a change worth reacting to.
                _ourHandle.Remove(ctx);
                _ourHandle.Add(ctx, new[] { ours });
            }
            catch { }

            // The block's renderer has to pick the new material up.
            var rc = CameraFeed.LastRenderComponent;
            var upd = rc?.GetType().GetMethod("UpdateMaterialReplacements", Any);
            if (upd != null && upd.GetParameters().Length == 0)
            {
                upd.Invoke(rc, null);
                RttLog.Line("Phase 2: UpdateMaterialReplacements applied.");
            }
            else RttLog.Line($"Phase 2: UpdateMaterialReplacements {(upd == null ? "not found" : "has parameters")} — material may not refresh until the panel does.");

            try { File.Delete(LivePath); } catch { }
        }
        catch (Exception e)
        {
            RttLog.Error("phase 2 bind", e);
            try { File.Delete(LivePath); } catch { }
        }
    }

    // ResourceHandle<T> -> ResourceHandle -> ResourceHandle<TextureAsset>
    private static object ConvertHandle(object handle, Type want)
    {
        if (handle == null || want == null) return null;
        if (want.IsInstanceOfType(handle)) return handle;
        try
        {
            object bare = handle;
            foreach (var m in handle.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "op_Implicit" && m.ReturnType.Name == "ResourceHandle")
                { bare = m.Invoke(null, new[] { handle }); break; }

            foreach (var m in want.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if ((m.Name == "op_Explicit" || m.Name == "op_Implicit") && m.ReturnType == want)
                {
                    var p = m.GetParameters();
                    if (p.Length != 1) continue;
                    var pt = p[0].ParameterType.IsByRef ? p[0].ParameterType.GetElementType() : p[0].ParameterType;
                    if (pt != null && pt.IsInstanceOfType(bare)) return m.Invoke(null, new[] { bare });
                }
        }
        catch (Exception e) { RttLog.Error("handle conversion", e); }
        return null;
    }

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

    private static string Clean(string n)
    {
        int a = n.IndexOf('<'), b = n.IndexOf('>');
        return (a == 0 && b > 1) ? n[1..b] : n;
    }
}
