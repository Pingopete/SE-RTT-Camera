using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// The scene renders HDR; the panel target is sRGB LDR. CopyJob converts format but
// not range, so bright values clamp to white. Its `postProcess` parameter is the
// candidate for tonemapping — what does it accept?
internal static class Probes10
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 32. Tonemapping the feed");
        r.AppendLine();

        var m = w.AllMethodsWithBody().FirstOrDefault(x =>
            x.Type.Name == "CopyJob" && x.Method.Name == "DoWork").Method;
        if (m != null)
        {
            r.AppendLine("### `CopyJob.DoWork` parameters");
            r.AppendLine("```");
            foreach (var p in m.Parameters)
                r.AppendLine($"  {p.ParameterType.FullName} {p.Name}");
            r.AppendLine("```");
            r.AppendLine();
        }

        foreach (var name in new[] { "PostProcess", "Channel", "EyeAdaptationJob", "TonemappingJob", "ToneMappingJob" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`{(t.IsEnum ? " (enum)" : "")}");
            r.AppendLine("```");
            if (t.IsEnum)
                foreach (var f in t.Fields.Where(f => f.IsStatic))
                    r.AppendLine($"  {f.Name} = {f.Constant}");
            else
            {
                foreach (var f in t.Fields.Take(20)) r.AppendLine($"  field {f.FieldType.Name} {f.Name}");
                foreach (var mm in t.Methods.Where(x => !x.IsGetter && !x.IsSetter).Take(20))
                    r.AppendLine($"  {mm.ReturnType.Name} {mm.Name}({string.Join(", ", mm.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            }
            r.AppendLine("```");
            r.AppendLine();
        }

        // Anything named like a tonemap in the post-process stage.
        r.AppendLine("### Tonemap-ish types");
        r.AppendLine("```");
        foreach (var (asm, t) in w.AllTypes())
            if (t.Name.Contains("onemap", StringComparison.OrdinalIgnoreCase)
             || t.Name.Contains("Exposure", StringComparison.OrdinalIgnoreCase))
                r.AppendLine($"{asm.Name.Name}: {t.FullName}");
        r.AppendLine("```");
        r.AppendLine();
    }
}
