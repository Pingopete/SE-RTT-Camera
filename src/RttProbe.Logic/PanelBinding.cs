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


        if (File.Exists(LivePath))
        {
            _disarmed = true;
            RttLog.Line("!!! PREVIOUS SESSION DIED DURING BINDING — disabled. Delete " + LivePath + " to retry.");
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
            if (IsAttempted(ctx)) return;

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
    // Render thread only (FeedGate.Shutdown runs there, same thread the bind ran on).
    public static void Unbind()
    {
        // EVERY bound panel (phase E2 fan-out), each restored independently — one panel
        // having been destroyed must not stop the others being put back. Consume-and-clear:
        // the list is this feed's record of what it changed, and after this it has changed
        // nothing.
        var list = _boundPanels;
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
            RttLog.Line($"Panel material: {restored} panel(s) rebound to the STOCK screen material " +
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
            var aspect = Prop(def, "AspectRatio");
            var orientation = Prop(Prop(ctx, "State"), "Orientation");

            // colorMetalOverride is Nullable<ResourceHandle<TextureAsset>>; our target
            // carries ResourceHandle<T> for a different T. The engine supplies the
            // conversions: ResourceHandle<T> -> ResourceHandle -> ResourceHandle<TextureAsset>.
            var texHandle = Prop(rt, "TextureHandle");
            var ps = mi.GetParameters();
            var wantType = Nullable.GetUnderlyingType(ps[4].ParameterType) ?? ps[4].ParameterType;
            var converted = ConvertHandle(texHandle, wantType);

            RttLog.Line($"Phase 2: binding — material={(baseMaterial == null ? "NULL" : "ok")} " +
                        $"aspect={aspect} orientation={orientation} " +
                        $"handle={(texHandle == null ? "NULL" : texHandle.GetType().Name)} -> " +
                        $"{(converted == null ? "CONVERSION FAILED" : wantType.Name)}");

            if (baseMaterial == null || converted == null)
            {
                RttLog.Line("Phase 2: aborting bind — missing material or handle conversion.");
                try { File.Delete(LivePath); } catch { }
                return;
            }

            mi.Invoke(ctx, new[] { renderer, baseMaterial, aspect, orientation, converted });
            RttLog.Line("=== PHASE 2: panel material rebound to our own render target. ===");

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
