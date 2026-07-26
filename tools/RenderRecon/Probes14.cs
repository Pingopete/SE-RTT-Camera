using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Hypothesis: the handover copies from a texture written in a different command
// list, so its resource state is wrong. Can we transition it explicitly?
internal static class Probes14
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 36. Resource state transitions");
        r.AppendLine();
        foreach (var name in new[] { "CopyCommandList", "DirectCommandList", "ComputeCommandList", "ResourceStateMonitor" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine("```");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter && !m.IsConstructor)
                         .Where(m => m.Name.Contains("Transition") || m.Name.Contains("Barrier")
                                  || m.Name.Contains("State") || m.Name.Contains("Copy")
                                  || m.Name.Contains("Flush")))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }

        // How does DrawOne itself sequence its copy? The reference for doing it right.
        var dw = w.AllMethodsWithBody().FirstOrDefault(x =>
            x.Type.Name == "OffscreenUIRenderer" && x.Method.Name == "DrawOne").Method;
        if (dw != null)
        {
            r.AppendLine("### `OffscreenUIRenderer.DrawOne` — full call order");
            r.AppendLine("```");
            foreach (var ins in dw.Body.Instructions)
                if (ins.Operand is MethodReference mr)
                {
                    var d = mr.DeclaringType?.Name ?? "";
                    if (d.Contains("Profiler") || d.Contains("Scope")) continue;
                    r.AppendLine($"  {d}.{mr.Name}");
                }
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
