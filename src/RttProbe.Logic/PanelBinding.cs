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

    private static bool _surveyed, _bound, _disarmed;

    // The bind happens inside the content-render hook, which an idle panel never
    // enters. While this is true the tick hook drives repaints to get us in there.
    public static bool WantsRepaint => !_bound && !_disarmed && File.Exists(ArmPath);
    private static int _errLogs;

    public static void Reset()
    {
        _surveyed = _bound = false;
        _errLogs = 0;
        _emissivityApplied = double.NaN;
        _emissivityBlocked = false;


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

            if (_bound) return;

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

        _bound = true;   // one attempt per load; a retry loop on the render thread is a bad idea
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
