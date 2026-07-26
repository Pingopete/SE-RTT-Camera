using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// ExplicitStateTransition takes an AutoResourceState, not a texture. How do we get
// one from a texture?
internal static class Probes15
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 37. AutoResourceState — how to get one from a texture");
        r.AppendLine();

        var art = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "AutoResourceState");
        if (art != null)
        {
            r.AppendLine($"### `{art.FullName}`");
            r.AppendLine("```");
            foreach (var f in art.Fields.Take(20)) r.AppendLine($"  field {f.FieldType.Name} {f.Name}");
            foreach (var c in art.Methods.Where(m => m.IsConstructor))
                r.AppendLine($"  ctor({string.Join(", ", c.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            foreach (var m in art.Methods.Where(m => m.IsSpecialName && m.Name.StartsWith("op_")))
                r.AppendLine($"  {m.Name} : {m.ReturnType.Name} <- ({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }

        // Any member anywhere that yields one.
        r.AppendLine("### Members returning AutoResourceState");
        r.AppendLine("```");
        int n = 0;
        foreach (var (asm, t) in w.AllTypes())
        {
            if (!t.Name.Contains("Texture") && !t.Name.Contains("Buffer") && !t.Name.Contains("Resource")) continue;
            foreach (var p in t.Properties)
                if (p.PropertyType.Name == "AutoResourceState") { r.AppendLine($"prop  {t.Name}.{p.Name}"); n++; }
            foreach (var f in t.Fields)
                if (f.FieldType.Name == "AutoResourceState") { r.AppendLine($"field {t.Name}.{f.Name} {(f.IsPublic ? "pub" : "int")}"); n++; }
            foreach (var m in t.Methods)
                if (m.ReturnType.Name == "AutoResourceState") { r.AppendLine($"meth  {t.Name}.{m.Name}()"); n++; }
            if (n > 60) break;
        }
        r.AppendLine("```");
        r.AppendLine();

        // And what RWRenderTargetTexture actually offers.
        var tex = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "RWRenderTargetTexture");
        if (tex != null)
        {
            r.AppendLine("### `RWRenderTargetTexture` members");
            r.AppendLine("```");
            foreach (var b in new[] { tex, tex.BaseType?.Resolve() })
            {
                if (b == null) continue;
                r.AppendLine($"  -- {b.Name} --");
                foreach (var p in b.Properties) r.AppendLine($"    prop {p.PropertyType.Name} {p.Name}");
                foreach (var f in b.Fields) r.AppendLine($"    field {f.FieldType.Name} {f.Name}");
            }
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
