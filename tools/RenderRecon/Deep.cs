using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RttCamera.Recon;

// Second-pass probes. The first pass established that offscreen targets are fed
// only by the 2D UI batcher. These answer the follow-up: is the *scene* pipeline
// re-enterable with a different view — i.e. is a true dual render merely
// invasive, or actually absent?
internal static class Deep
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        RenderViewPlumbing(w, r);
        FrameStructure(w, r);
        MainTargetSurface(w, r);
        CompositeView(w, r);
        SceneStages(w, r);
    }

    // Where does the one RenderView come from, and who writes it? If it is a
    // settable field consulted by the scene stages, a second pass is at least
    // conceivable; if it is baked per frame, it is not.
    private static void RenderViewPlumbing(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 7. RenderView plumbing");
        r.AppendLine();

        foreach (var name in new[] { "RenderView", "RenderViewSlim" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) continue;
            r.AppendLine($"### `{t.FullName}` (fields)");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields.Take(60))
                r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}{f.FieldType.Name} {f.Name}");
            r.AppendLine("```");
            r.AppendLine();
        }

        // Every read and write of a RenderView-typed member.
        r.AppendLine("### Members typed RenderView / RenderViewSlim");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t) in w.AllTypes())
        {
            foreach (var f in t.Fields)
                if (f.FieldType.Name is "RenderView" or "RenderViewSlim")
                    r.AppendLine($"field  {t.FullName}.{f.Name} {(f.IsPublic ? "pub" : "int")}{(f.IsStatic ? " static" : "")}");
            foreach (var p in t.Properties)
                if (p.PropertyType.Name is "RenderView" or "RenderViewSlim")
                    r.AppendLine($"prop   {t.FullName}.{p.Name} setter={(p.SetMethod != null ? (p.SetMethod.IsPublic ? "pub" : "int") : "none")}");
        }
        r.AppendLine("```");
        r.AppendLine();
    }

    // The shape of a rendered frame: what RenderFrame() actually calls, in order.
    // A per-view loop would show up here.
    private static void FrameStructure(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 8. Frame structure");
        r.AppendLine();

        foreach (var (typeName, methodName) in new[]
        {
            ("Render12EngineComponent", "RenderFrame"),
            ("ContractsProcessor", "ProcessRenderFrame"),
            ("CameraSettings", "EnableCompositeRenderView"),
        })
        {
            var m = w.AllMethodsWithBody()
                .Where(x => x.Type.Name == typeName && x.Method.Name == methodName)
                .Select(x => x.Method).FirstOrDefault();
            if (m == null) { r.AppendLine($"### `{typeName}.{methodName}` — not found"); r.AppendLine(); continue; }

            r.AppendLine($"### `{m.DeclaringType.FullName}.{m.Name}` — call sequence");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr)
                    r.AppendLine($"  call {mr.DeclaringType?.Name}.{mr.Name}");
                else if (ins.OpCode.Name.StartsWith("br") || ins.OpCode.Name.StartsWith("blt") || ins.OpCode.Name.StartsWith("ble"))
                    r.AppendLine($"  {ins.OpCode.Name}");
            }
            r.AppendLine("```");
            r.AppendLine();
        }
    }

    private static void MainTargetSurface(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 9. MainRenderTarget");
        r.AppendLine();
        var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "MainRenderTarget");
        if (t == null) { r.AppendLine("_not found_"); r.AppendLine(); return; }
        r.AppendLine("```");
        foreach (var f in t.Fields) r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.Name} {f.Name}");
        foreach (var p in t.Properties) r.AppendLine($"  prop {p.PropertyType.Name} {p.Name}");
        foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter))
            r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
        r.AppendLine("```");
        r.AppendLine();
    }

    // "Composite render view" is the only name in the codebase that hints at
    // more than one view being composited. Find out what it actually gates.
    private static void CompositeView(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 10. Composite render view");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr && mr.Name.Contains("CompositeRenderView"))
                    r.AppendLine($"{t.FullName}.{m.Name}  ->  {mr.DeclaringType?.Name}.{mr.Name}");
                else if (ins.Operand is FieldReference fr && fr.Name.Contains("Composite"))
                    r.AppendLine($"{t.FullName}.{m.Name}  field  {fr.Name}");
            }
        }
        r.AppendLine("```");
        r.AppendLine();

        var ct = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "CameraSettings");
        if (ct != null)
        {
            r.AppendLine("### `CameraSettings` members");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in ct.Fields.Take(60)) r.AppendLine($"  field {f.FieldType.Name} {f.Name}");
            foreach (var m in ct.Methods.Where(m => !m.IsConstructor).Take(40))
                r.AppendLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }

    // The scene stages themselves — what consumes the view and draws geometry.
    // Their entry signatures say whether a stage can be pointed at a target.
    private static void SceneStages(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 11. Scene draw stages");
        r.AppendLine();
        r.AppendLine("Types in the Render12 stage namespaces, with any method that takes a");
        r.AppendLine("view or a target — the signature that a second pass would need.");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t) in w.AllTypes())
        {
            if (asm.Name.Name != "VRage.Render12") continue;
            var ns = t.Namespace ?? "";
            if (!ns.Contains("Stage") && !ns.Contains("Core.Systems")) continue;
            if (t.Name.StartsWith("<")) continue;

            var interesting = t.Methods.Where(m =>
                m.Parameters.Any(p => p.ParameterType.Name is "RenderView" or "RenderViewSlim"
                                   || p.ParameterType.Name.Contains("RenderTarget"))).ToList();
            if (interesting.Count == 0) continue;
            r.AppendLine($"{t.FullName}");
            foreach (var m in interesting.Take(12))
                r.AppendLine($"    {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
        }
        r.AppendLine("```");
        r.AppendLine();
    }
}
