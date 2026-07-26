using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Fidelity roadmap: we run a fragment of ExecuteScenePreparationAndRender only.
// ExecuteLighting and ExecuteForwardAndPostProcess are where deferred lighting,
// bloom, eye adaptation and tonemapping live. What do they need?
internal static class Probes12
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 34. The missing stages");
        r.AppendLine();

        foreach (var name in new[]
        {
            "ExecuteScenePreparationAndRender", "ExecuteLighting",
            "ExecuteForwardAndPostProcess", "ExecutePostPasses", "ExecuteForwardPasses",
        })
        {
            var hit = w.AllMethodsWithBody().FirstOrDefault(x =>
                x.Type.Name == "SceneDrawSystem" && x.Method.Name == name);
            var m = hit.Method;
            if (m == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }

            r.AppendLine($"### `SceneDrawSystem.{name}`");
            r.AppendLine();
            r.AppendLine("Signature:");
            r.AppendLine("```");
            r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}(");
            foreach (var p in m.Parameters)
                r.AppendLine($"      {p.ParameterType.FullName?.Split('.').Last()} {p.Name}");
            r.AppendLine("  )");
            r.AppendLine("```");
            r.AppendLine();
            r.AppendLine("Calls, in order:");
            r.AppendLine("```");
            var seen = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                var d = mr.DeclaringType?.Name ?? "";
                if (d.Contains("Profiler") || d.Contains("Scope") || d.StartsWith("Nullable")) continue;
                var s = $"{d}.{mr.Name}";
                if (seen.Contains(s)) continue;
                seen.Add(s);
                r.AppendLine("  " + s);
            }
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
