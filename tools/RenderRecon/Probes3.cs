using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Fourth pass, aimed squarely at the only question that matters for a *true*
// second 3D render: can the scene stages be run again with a different view and
// a different output?
//
// That needs two things to be true. Either one being false settles it:
//   (a) the view the stages read must be swappable
//   (b) the target the stages write must be redirectable
internal static class Probes3
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        StageOrchestration(w, r);
        ViewWriters(w, r);
        SceneDrawSystemSurface(w, r);
        TargetBinding(w, r);
        ScreenBuffersSurface(w, r);
        CommandBufferChannel(w, r);
    }

    // How is a frame's work sequenced? A fixed call chain and a job graph have
    // very different re-entrancy stories.
    private static void StageOrchestration(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 16. Stage orchestration");
        r.AppendLine();

        foreach (var (typeName, methodName) in new[]
        {
            ("ContractsProcessor", "ProcessRenderFrame"),
            ("Render12EngineComponent", "ProcessMessages"),
            ("Render12EngineComponent", "IRender_Present"),
        })
        {
            var m = w.AllMethodsWithBody()
                .Where(x => x.Type.Name == typeName && x.Method.Name == methodName)
                .Select(x => x.Method).FirstOrDefault();
            if (m == null) { r.AppendLine($"### `{typeName}.{methodName}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{m.DeclaringType.FullName}.{m.Name}`");
            r.AppendLine();
            r.AppendLine("```");
            var seen = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                var s = $"{mr.DeclaringType?.Name}.{mr.Name}";
                if (mr.DeclaringType?.Namespace?.StartsWith("System") == true) continue;
                if (s.Contains("Profiler") || s.Contains("Log.") || s.Contains("ProfilingScope")) continue;
                if (seen.Contains(s)) continue;
                seen.Add(s);
                r.AppendLine("  " + s);
            }
            r.AppendLine("```");
            r.AppendLine();
        }

        // The set of render "stages" and their entry points.
        r.AppendLine("### Stage types");
        r.AppendLine();
        r.AppendLine("```");
        var stages = w.AllTypes()
            .Where(x => x.Asm.Name.Name == "VRage.Render12"
                     && (x.Type.Namespace ?? "").Contains("Stage")
                     && !x.Type.Name.StartsWith("<") && !x.Type.IsNested)
            .Select(x => x.Type.Namespace).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        foreach (var ns in stages) r.AppendLine("  " + ns);
        r.AppendLine("```");
        r.AppendLine();
    }

    // (a) Is the view swappable? Find every write to the SettingsManager view
    // fields — if only one method sets them, that is the seam.
    private static void ViewWriters(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 17. Who writes the render view");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode.Name is not ("stfld" or "stsfld")) continue;
                if (ins.Operand is not FieldReference fr) continue;
                if (fr.Name is "_renderView" or "_previousRenderView" or "_freezedRenderView")
                    r.AppendLine($"{t.FullName}.{m.Name}  writes  {fr.Name}");
            }
        }
        r.AppendLine("```");
        r.AppendLine();

        // And who reads it — the breadth of consumers says how much would have to
        // be fooled for a second pass to be coherent.
        r.AppendLine("### Readers of `SettingsManager.RenderView`");
        r.AppendLine();
        r.AppendLine("```");
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr && mr.Name == "get_RenderView")
                { r.AppendLine($"{t.FullName}.{m.Name}"); n++; break; }
            }
            if (n > 80) { r.AppendLine("  ... (truncated)"); break; }
        }
        r.AppendLine($"({n} readers)");
        r.AppendLine("```");
        r.AppendLine();
    }

    private static void SceneDrawSystemSurface(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 18. SceneDrawSystem");
        r.AppendLine();
        var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "SceneDrawSystem");
        if (t == null) { r.AppendLine("_not found_"); r.AppendLine(); return; }
        r.AppendLine("```");
        foreach (var f in t.Fields.Take(40)) r.AppendLine($"  field {f.FieldType.Name} {f.Name}");
        foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter))
            r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
        r.AppendLine("```");
        r.AppendLine();
    }

    // (b) Is the target redirectable? Find where the scene's colour output is
    // chosen. If stages resolve it from a frame-global rather than a parameter,
    // there is no seam to redirect.
    private static void TargetBinding(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 19. Where the scene's output target comes from");
        r.AppendLine();
        r.AppendLine("Methods that bind render targets (`OMSetRenderTargets` /");
        r.AppendLine("`SetRenderTargets` / `ClearRenderTargetView`) and where the target");
        r.AppendLine("value originates — parameter, field, or frame-global.");
        r.AppendLine();
        r.AppendLine("```");
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            if (asm.Name.Name != "VRage.Render12") continue;
            bool binds = m.Body.Instructions.Any(i => i.Operand is MethodReference mr
                && (mr.Name.Contains("SetRenderTarget") || mr.Name.Contains("SetupRenderTargets")
                 || mr.Name == "OMSetRenderTargets"));
            if (!binds) continue;
            if (n++ > 90) { r.AppendLine("  ... (truncated)"); break; }

            bool targetFromParam = m.Parameters.Any(p =>
                p.ParameterType.Name.Contains("RenderTarget") || p.ParameterType.Name.Contains("Texture"));

            // Where does the bound target actually come from? A call into a
            // frame-global resource holder means there is no seam to redirect.
            var sources = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is FieldReference fr &&
                    (fr.FieldType.Name.Contains("Texture") || fr.FieldType.Name.Contains("RenderTarget")))
                { var s = $"fld:{fr.DeclaringType?.Name}.{fr.Name}"; if (!sources.Contains(s)) sources.Add(s); }
                else if (ins.Operand is MethodReference mr2 && mr2.Name.StartsWith("get_") &&
                    (mr2.ReturnType.Name.Contains("Texture") || mr2.ReturnType.Name.Contains("RenderTarget")))
                { var s = $"get:{mr2.DeclaringType?.Name}.{mr2.Name[4..]}"; if (!sources.Contains(s)) sources.Add(s); }
            }
            r.AppendLine($"{t.Name}.{m.Name}  fromParam={targetFromParam}");
            foreach (var s in sources.Take(6)) r.AppendLine($"      {s}");
        }
        r.AppendLine("```");
        r.AppendLine();
    }

    // The frame-global buffer holder the stages read their targets from. Its
    // shape decides whether "render the scene somewhere else" has a seam at all.
    public static void ScreenBuffersSurface(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 19b. ScreenBuffers");
        r.AppendLine();
        foreach (var name in new[] { "ScreenBuffers", "GBuffer" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields.Take(40)) r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.Name} {f.Name}");
            foreach (var p in t.Properties.Take(40)) r.AppendLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter).Take(25))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }

        // How many distinct owners hold one? One instance = one scene view.
        r.AppendLine("### Fields typed ScreenBuffers");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t) in w.AllTypes())
        {
            foreach (var f in t.Fields)
                if (f.FieldType.Name == "ScreenBuffers")
                    r.AppendLine($"{t.FullName}.{f.Name}{(f.IsStatic ? " static" : "")}");
            foreach (var p in t.Properties)
                if (p.PropertyType.Name == "ScreenBuffers")
                    r.AppendLine($"{t.FullName}.{p.Name} (prop)");
        }
        r.AppendLine("```");
        r.AppendLine();
    }

    // The one remaining public channel into the renderer. If RenderCommandBuffer
    // is an open command stream, what can actually be put on it?
    private static void CommandBufferChannel(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 20. RenderCommandBuffer — what can be sent to the renderer");
        r.AppendLine();
        foreach (var name in new[] { "RenderCommandBuffer", "RenderDrawCommandBuffer" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields.Take(30)) r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.Name} {f.Name}");
            foreach (var p in t.Properties) r.AppendLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter && !m.IsConstructor).Take(50))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
