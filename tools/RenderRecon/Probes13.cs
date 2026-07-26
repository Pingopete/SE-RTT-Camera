using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// The single biggest visual defect is HDR clamping. ApplyToneMapping /
// ComputeExposure are the engine's own answer — are they callable with our own
// textures, or do they read the global ScreenBuffers?
internal static class Probes13
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 35. Tonemapping and exposure entry points");
        r.AppendLine();
        foreach (var name in new[] { "ApplyToneMapping", "ComputeExposure", "ApplyBloom", "PatchHoles", "DrawSkybox" })
        {
            var hit = w.AllMethodsWithBody().FirstOrDefault(x =>
                x.Type.Name == "SceneDrawSystem" && x.Method.Name == name);
            var m = hit.Method;
            if (m == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }

            r.AppendLine($"### `SceneDrawSystem.{name}`");
            r.AppendLine("```");
            foreach (var p in m.Parameters)
                r.AppendLine($"  param {p.ParameterType.FullName?.Split('.').Last()} {p.Name}");
            r.AppendLine("  --- reads/calls ---");
            var seen = new List<string>();
            bool touchesGlobal = false;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is FieldReference fr && fr.DeclaringType?.Name == "CoreSystems")
                {
                    if (fr.Name is "ScreenBuffers") touchesGlobal = true;
                    var g = $"CoreSystems.{fr.Name}";
                    if (!seen.Contains(g)) { seen.Add(g); r.AppendLine("  " + g); }
                }
                if (ins.Operand is MethodReference mr)
                {
                    var d = mr.DeclaringType?.Name ?? "";
                    if (d.Contains("Profiler") || d.Contains("Scope")) continue;
                    if (d == "ScreenBuffers") touchesGlobal = true;
                    var s = $"{d}.{mr.Name}";
                    if (!seen.Contains(s)) { seen.Add(s); r.AppendLine("  " + s); }
                }
            }
            r.AppendLine($"  === reads global ScreenBuffers: {(touchesGlobal ? "YES — needs a buffer swap" : "no — callable with our own textures")}");
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
