using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RttCamera.Recon;

// Sixth pass. The engine already renders scene geometry from viewpoints that are
// not the player's, into targets that are not the screen — for environment
// probes and shadow cascades. Those paths are the working template for a camera
// feed, and more usefully they encode the answer to the hard question:
// *what state has to be saved and restored around a foreign-view pass?*
//
// Keen already worked that out. This pass reads it back off them.
internal static class Probes5
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        ForeignViewPasses(w, r);
        CameraParameterSeam(w, r);
        GlobalMutations(w, r);
        DrawContexts(w, r);
    }

    // Full instruction streams for the passes worth imitating.
    private static void ForeignViewPasses(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 24. The foreign-view passes, in full");
        r.AppendLine();

        (string Type, string Method)[] targets =
        {
            ("SceneDrawSystem", "ExecuteEnvironmentProbeUpdate"),
            ("SceneDrawSystem", "RenderShadowCascades"),
            ("SceneDrawSystem", "DrawUnlit"),
            ("EnvironmentProbeManager", "PrepareProbes"),
        };

        foreach (var (typeName, methodName) in targets)
        {
            var hits = w.AllMethodsWithBody()
                .Where(x => x.Type.Name == typeName && x.Method.Name == methodName).ToList();
            if (hits.Count == 0) { r.AppendLine($"### `{typeName}.{methodName}` — not found"); r.AppendLine(); continue; }

            foreach (var (asm, t, m) in hits)
            {
                r.AppendLine($"### `{t.FullName}.{m.Name}`");
                r.AppendLine();
                r.AppendLine($"Locals: {m.Body.Variables.Count}, instructions: {m.Body.Instructions.Count}");
                r.AppendLine();
                r.AppendLine("```");
                foreach (var ins in m.Body.Instructions)
                {
                    var op = ins.Operand;
                    string txt = op switch
                    {
                        MethodReference mr => $"{mr.DeclaringType?.Name}.{mr.Name}",
                        FieldReference fr => $"{fr.DeclaringType?.Name}.{fr.Name} : {fr.FieldType.Name}",
                        TypeReference tr => tr.Name,
                        null => "",
                        _ => op.ToString(),
                    };
                    // Keep the shape readable: only the opcodes that carry meaning
                    // for control flow and state access.
                    var name = ins.OpCode.Name;
                    if (name is "nop" or "pop" or "dup") continue;
                    if (txt.Length == 0 && !name.StartsWith("br") && !name.StartsWith("ld") && !name.StartsWith("st")) continue;
                    r.AppendLine($"  {name,-14} {Trim(txt)}");
                }
                r.AppendLine("```");
                r.AppendLine();
            }
        }
    }

    // The single writer of the view. Its body says exactly what changing the
    // camera entails — and what else it touches while doing it.
    private static void CameraParameterSeam(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 25. SettingsManager.SetCameraParameters");
        r.AppendLine();
        var hits = w.AllMethodsWithBody()
            .Where(x => x.Type.Name == "SettingsManager" && x.Method.Name == "SetCameraParameters").ToList();
        foreach (var (asm, t, m) in hits)
        {
            var ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
            r.AppendLine($"### `{t.Name}.{m.Name}({ps})`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr) r.AppendLine($"  call  {mr.DeclaringType?.Name}.{mr.Name}");
                else if (ins.Operand is FieldReference fr && ins.OpCode.Name.StartsWith("st"))
                    r.AppendLine($"  STORE {fr.DeclaringType?.Name}.{fr.Name}");
            }
            r.AppendLine("```");
            r.AppendLine();
        }

        // Who calls it, and how often — is it once per frame, or already used to
        // retarget the renderer mid-frame?
        r.AppendLine("### Callers");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
            foreach (var ins in m.Body.Instructions)
                if (ins.Operand is MethodReference mr && mr.Name == "SetCameraParameters")
                { r.AppendLine($"{t.FullName}.{m.Name}"); break; }
        r.AppendLine("```");
        r.AppendLine();
    }

    // Which CoreSystems globals are actually written after startup? Those are the
    // ones a second pass has to save and restore; the rest are set up once.
    private static void GlobalMutations(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 26. Which CoreSystems globals get written, and by whom");
        r.AppendLine();
        r.AppendLine("A global only written by `CoreSystems.Initialize` is startup state and can");
        r.AppendLine("be ignored. Anything written elsewhere is live state a second pass must");
        r.AppendLine("account for.");
        r.AppendLine();
        r.AppendLine("```");
        var byField = new Dictionary<string, List<string>>();
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode.Name != "stsfld") continue;
                if (ins.Operand is not FieldReference fr) continue;
                if (fr.DeclaringType?.Name != "CoreSystems") continue;
                if (!byField.TryGetValue(fr.Name, out var list)) byField[fr.Name] = list = new List<string>();
                var where = $"{t.Name}.{m.Name}";
                if (!list.Contains(where)) list.Add(where);
            }
        }
        foreach (var kv in byField.OrderBy(k => k.Key, StringComparer.Ordinal))
            r.AppendLine($"{kv.Key,-34} <- {string.Join(", ", kv.Value)}");
        r.AppendLine("```");
        r.AppendLine();
    }

    // Draw contexts are how the renderer tracks per-pass GPU state. A second pass
    // almost certainly needs its own.
    private static void DrawContexts(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 27. Draw contexts");
        r.AppendLine();
        foreach (var name in new[] { "DrawContextManager", "DrawContext" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields.Take(30)) r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.Name} {f.Name}");
            foreach (var p in t.Properties.Take(20)) r.AppendLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter).Take(30))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }

    private static string Trim(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > 110 ? s[..110] + "…" : s);
}
