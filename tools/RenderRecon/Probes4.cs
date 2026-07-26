using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Fifth pass, prompted by a sharper question: with plugin-level access (Harmony
// on internals, reflection onto private state), can the engine be *made* to
// produce a second view?
//
// That reframes everything. The earlier passes asked "is there an API for this",
// which is the wrong question when you can patch anything managed. The right
// question is "what is the global state that defines a view, and can the draw
// sequence be re-entered against a swapped copy of it?"
internal static class Probes4
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        CoreSystemsGlobals(w, r);
        SceneDrawSequence(w, r);
        StageEntryPoints(w, r);
    }

    // Everything a second pass would have to swap and restore. The size of this
    // list is the honest measure of how invasive the trick is.
    private static void CoreSystemsGlobals(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 21. CoreSystems — the swappable globals");
        r.AppendLine();
        var t = w.AllTypes().Select(x => x.Type)
            .FirstOrDefault(x => x.FullName == "Keen.VRage.Render12.Core.CoreSystems");
        if (t == null) { r.AppendLine("_not found_"); r.AppendLine(); return; }

        r.AppendLine("```");
        foreach (var f in t.Fields.Where(f => f.IsStatic))
            r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}static field {f.FieldType.Name} {f.Name}");
        foreach (var p in t.Properties)
            r.AppendLine($"  prop {p.PropertyType.Name} {p.Name} setter={(p.SetMethod != null ? (p.SetMethod.IsPublic ? "pub" : "int") : "none")}");
        foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter).Take(30))
            r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
        r.AppendLine("```");
        r.AppendLine();
    }

    // The actual per-frame scene draw sequence. If it is a plain ordered call
    // chain, re-entering it is conceivable; if it is a job graph with barriers
    // and cross-frame fences, it is not.
    private static void SceneDrawSequence(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 22. What orchestrates the scene draw");
        r.AppendLine();
        r.AppendLine("Methods that touch three or more distinct render stages — the");
        r.AppendLine("orchestrators, whatever they are called.");
        r.AppendLine();
        r.AppendLine("```");
        string[] stageMarks = { "GeometryStage", "LightingStage", "PostProcessStage", "PrepareStage", "UIStage", "ShadowStage" };
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            if (asm.Name.Name != "VRage.Render12") continue;
            var touched = new HashSet<string>();
            foreach (var ins in m.Body.Instructions)
            {
                var ns = (ins.Operand as MethodReference)?.DeclaringType?.Namespace
                      ?? (ins.Operand as FieldReference)?.DeclaringType?.Namespace ?? "";
                foreach (var s in stageMarks) if (ns.Contains(s)) touched.Add(s);
            }
            if (touched.Count < 3) continue;
            r.AppendLine($"{t.FullName}.{m.Name}   [{string.Join(", ", touched.OrderBy(x => x))}]");
        }
        r.AppendLine("```");
        r.AppendLine();
    }

    // Per-stage entry points. A re-entrant second pass would have to call these
    // in order — so whether they are instance methods on stateful objects, and
    // what they take, decides feasibility.
    private static void StageEntryPoints(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 23. Stage entry points and their state");
        r.AppendLine();
        r.AppendLine("```");
        string[] want = { "DoWork", "Draw", "Execute", "Render", "BeginFrame", "EndFrame", "Compose", "PrepareDraw" };
        int n = 0;
        foreach (var (asm, t) in w.AllTypes())
        {
            if (asm.Name.Name != "VRage.Render12") continue;
            var ns = t.Namespace ?? "";
            if (!ns.Contains("GeometryStage") && !ns.Contains("PrepareStage") && !ns.Contains("LightingStage")) continue;
            if (t.Name.StartsWith("<") || t.IsNested) continue;

            var entries = t.Methods.Where(m => want.Contains(m.Name)).ToList();
            if (entries.Count == 0) continue;
            if (n++ > 70) { r.AppendLine("  ... (truncated)"); break; }
            r.AppendLine($"{t.Name}   (fields: {t.Fields.Count})");
            foreach (var m in entries.Take(6))
                r.AppendLine($"    {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
        }
        r.AppendLine("```");
        r.AppendLine();
    }
}
