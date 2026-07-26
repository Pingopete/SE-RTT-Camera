using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Fidelity: our feed runs cull -> cluster -> IndirectEnvironmentPass only. What
// does the main frame do that we skip? DrawInternal is the reference sequence.
internal static class Probes11
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 33. The main frame's job sequence (fidelity reference)");
        r.AppendLine();

        foreach (var mm in w.AllMethodsWithBody()
                     .Where(x => x.Type.Name is "Render12EngineComponent" or "SceneDrawSystem"
                              && (x.Method.Name.Contains("DrawInternal") || x.Method.Name == "Draw")))
        {
            r.AppendLine($"### `{mm.Type.Name}.{mm.Method.Name}`");
            r.AppendLine("```");
            var seen = new List<string>();
            foreach (var ins in mm.Method.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                var d = mr.DeclaringType?.Name ?? "";
                // Only job-ish calls: the things that actually draw.
                if (!d.EndsWith("Job") && !d.Contains("SceneDrawSystem") && !d.Contains("Manager")) continue;
                var s = $"{d}.{mr.Name}";
                if (seen.Contains(s)) continue;
                seen.Add(s);
                r.AppendLine("  " + s);
            }
            r.AppendLine("```");
            r.AppendLine();
        }

        // What the environment pass itself does, to see what it already covers.
        r.AppendLine("### `IndirectEnvironmentPassJob.DoWork` — what it draws");
        r.AppendLine("```");
        var ep = w.AllMethodsWithBody().FirstOrDefault(x =>
            x.Type.Name == "IndirectEnvironmentPassJob" && x.Method.Name == "DoWork").Method;
        if (ep != null)
        {
            var seen = new List<string>();
            foreach (var ins in ep.Body.Instructions)
                if (ins.Operand is MethodReference mr)
                {
                    var s = $"{mr.DeclaringType?.Name}.{mr.Name}";
                    if (s.Contains("Profiler") || s.Contains("Scope")) continue;
                    if (seen.Contains(s)) continue;
                    seen.Add(s);
                    r.AppendLine("  " + s);
                }
        }
        r.AppendLine("```");
        r.AppendLine();
    }
}
