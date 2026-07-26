using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Third pass. Everything else ruled a true dual render out; environment probes
// are the one system left that genuinely rasterises the world from an arbitrary
// position into a texture. If that is steerable and its result is samplable,
// it is the only real RTT path in the shipped engine.
internal static class Probes2
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        EnvironmentProbes(w, r);
        HologramStage(w, r);
        LcdMaterialPath(w, r);
        HandleTypes(w, r);
    }

    // Exact shapes needed to call DrawImage with a render target's texture.
    // OffscreenRenderTarget.TextureHandle is ResourceHandle<T>; DrawImage wants a
    // bare ResourceHandle — so the conversion between them has to be pinned down
    // before writing the blit, not guessed at.
    private static void HandleTypes(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 15. Handle types for the blit");
        r.AppendLine();
        foreach (var name in new[] { "ResourceHandle", "ResourceHandle`1", "GeneratedResourceHandle", "OffscreenRenderTarget", "RenderId", "BoundingBox2" })
        {
            var t = w.AllTypes().Select(x => x.Type)
                .FirstOrDefault(x => x.Name == name && !x.Name.StartsWith("<"));
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields)
                r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.Name} {f.Name}");
            foreach (var c in t.Methods.Where(m => m.IsConstructor))
                r.AppendLine($"  {(c.IsPublic ? "pub " : "int ")}ctor({string.Join(", ", c.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            foreach (var p in t.Properties)
                r.AppendLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.Methods.Where(m => m.IsSpecialName && m.Name.StartsWith("op_")))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.Name} : {m.ReturnType.Name} <- ({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
            foreach (var m in t.Methods.Where(m => !m.IsConstructor && !m.IsSpecialName).Take(20))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }

    private static void EnvironmentProbes(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 12. Environment probes — the only arbitrary-viewpoint scene render");
        r.AppendLine();

        foreach (var name in new[] { "EnvironmentProbeManager", "EnvironmentProbeComponent", "EnvironmentProbeEntity" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields.Take(40))
                r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.Name} {f.Name}");
            foreach (var p in t.Properties.Take(30))
                r.AppendLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter).Take(40))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }

        // Is a probe placeable from game code, or purely a render-internal thing?
        r.AppendLine("### Probe references from outside VRage.Render12");
        r.AppendLine();
        r.AppendLine("```");
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            if (asm.Name.Name == "VRage.Render12") continue;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr && mr.DeclaringType != null
                    && mr.DeclaringType.Name.Contains("EnvironmentProbe"))
                {
                    r.AppendLine($"{asm.Name.Name}: {t.FullName}.{m.Name} -> {mr.DeclaringType.Name}.{mr.Name}");
                    if (++n > 40) break;
                }
            }
            if (n > 40) break;
        }
        if (n == 0) r.AppendLine("(none — probes are render-internal only)");
        r.AppendLine("```");
        r.AppendLine();
    }

    // A "hologram" pass exists (HologramComposite shader). If the engine already
    // draws world geometry into a separate buffer for holograms, that machinery
    // may be closer to a PIP feed than anything in the UI stage.
    private static void HologramStage(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 13. Hologram pass");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t) in w.AllTypes())
        {
            if (!t.FullName.Contains("Hologram")) continue;
            r.AppendLine($"{asm.Name.Name}: {t.FullName}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter).Take(14))
                r.AppendLine($"    {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
        }
        r.AppendLine("```");
        r.AppendLine();
    }

    // How an LCD surface's texture reaches its material. Whatever texture we can
    // produce has to enter the world through this path.
    private static void LcdMaterialPath(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 14. How an LCD surface texture reaches its material");
        r.AppendLine();

        foreach (var (typeName, methodName) in new[]
        {
            ("LcdPanelSurfaceContext", "SetNewScreenMaterialHandle"),
            ("LcdPanelSurfaceRenderComponent", "TransitionToCustomRender"),
            ("LcdPanelSurfaceRenderComponent", "UpdateMaterialReplacements"),
        })
        {
            var m = w.AllMethodsWithBody()
                .Where(x => x.Type.Name == typeName && x.Method.Name == methodName)
                .Select(x => x.Method).FirstOrDefault();
            if (m == null) { r.AppendLine($"### `{typeName}.{methodName}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{typeName}.{methodName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr)
                    r.AppendLine($"  call  {mr.DeclaringType?.Name}.{mr.Name}");
                else if (ins.Operand is FieldReference fr)
                    r.AppendLine($"  fld   {fr.DeclaringType?.Name}.{fr.Name}");
            }
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
