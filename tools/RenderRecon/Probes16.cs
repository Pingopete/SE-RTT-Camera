using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Architecture question: can we bind OUR OWN offscreen target to the panel's
// material? If so the whole handover disappears — no copy into the panel, no
// RequestRender, no DrawOne hook, and no LCD eviction problem, because we would
// own the target rather than borrowing the panel's.
internal static class Probes16
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 38. Binding our own target to the panel material");
        r.AppendLine();

        foreach (var (tn, mn) in new[]
        {
            ("LcdPanelSurfaceContext", "SetNewScreenMaterialHandle"),
            ("LcdContentRendererSessionComponent", "CreateRuntimeLcdMaterial"),
            ("LcdPanelSurfaceRenderComponent", "UpdateMaterialReplacements"),
        })
        {
            var hit = w.AllMethodsWithBody().FirstOrDefault(x => x.Type.Name == tn && x.Method.Name == mn);
            var m = hit.Method;
            if (m == null) { r.AppendLine($"### `{tn}.{mn}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{tn}.{mn}`");
            r.AppendLine("```");
            foreach (var p in m.Parameters)
                r.AppendLine($"  param {p.ParameterType.FullName?.Split('.').Last()} {p.Name}");
            r.AppendLine("  --- calls ---");
            var seen = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr)
                {
                    var s = $"{mr.DeclaringType?.Name}.{mr.Name}";
                    if (seen.Contains(s)) continue; seen.Add(s); r.AppendLine("  " + s);
                }
                else if (ins.Operand is FieldReference fr)
                {
                    var s = $"fld {fr.DeclaringType?.Name}.{fr.Name}";
                    if (seen.Contains(s)) continue; seen.Add(s); r.AppendLine("  " + s);
                }
            }
            r.AppendLine("```");
            r.AppendLine();
        }

        // Where does TransitionToCustomRender get its PBRMaterialDefinition?
        var t2 = w.AllMethodsWithBody().FirstOrDefault(x =>
            x.Type.Name == "LcdPanelSurfaceRenderComponent" && x.Method.Name == "TransitionToCustomRender").Method;
        if (t2 != null)
        {
            r.AppendLine("### Where TransitionToCustomRender sources its material");
            r.AppendLine("```");
            foreach (var ins in t2.Body.Instructions)
                if (ins.Operand is FieldReference fr && (fr.FieldType.Name.Contains("Material") || fr.Name.Contains("Material")))
                    r.AppendLine($"  fld {fr.DeclaringType?.Name}.{fr.Name} : {fr.FieldType.Name}");
                else if (ins.Operand is MethodReference mr && mr.Name.Contains("Material"))
                    r.AppendLine($"  call {mr.DeclaringType?.Name}.{mr.Name}");
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
