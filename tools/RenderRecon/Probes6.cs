using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// Route B: get the camera frame out of the GPU and back in as a file-backed
// texture, which DrawImage *does* accept (Grid Schematics relies on exactly that
// and works). Slow, but every step is already proven in this engine.
//
// The unknowns are the readback callback's shape and the file registration path.
internal static class Probes6
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 28. Route B — GPU readback to a file-backed texture");
        r.AppendLine();
        r.AppendLine("`DrawImage` rejects generated (render-target) handles but accepts");
        r.AppendLine("file-backed guid handles. So the frame has to leave the GPU, land on");
        r.AppendLine("disk, and be registered as a resource.");
        r.AppendLine();

        foreach (var name in new[] { "RenderOutputManager", "OffscreenRenderTarget", "ContentCache" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == name);
            if (t == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields.Take(25))
                r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.FullName?.Split('.').Last()} {f.Name}");
            foreach (var e in t.Events)
                r.AppendLine($"  event {e.EventType.FullName?.Split('.').Last()} {e.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter).Take(30))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }

        // The generic argument list of OnScreenshotToMemoryTaken is the callback
        // signature — that is what a subscriber has to match.
        r.AppendLine("### Screenshot-to-memory callback shape");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t) in w.AllTypes())
        {
            foreach (var f in t.Fields)
            {
                if (!f.Name.Contains("Screenshot", StringComparison.OrdinalIgnoreCase)) continue;
                r.AppendLine($"{t.Name}.{f.Name} : {f.FieldType.FullName}");
            }
        }
        r.AppendLine("```");
        r.AppendLine();

        // Who consumes the readback today — the working example to copy.
        r.AppendLine("### Existing consumers of screenshot-to-memory");
        r.AppendLine();
        r.AppendLine("```");
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr &&
                    (mr.Name.Contains("ScreenshotToMemory") || mr.Name == "TakeScreenshotToMemory"))
                {
                    r.AppendLine($"{asm.Name.Name}: {t.FullName}.{m.Name} -> {mr.Name}");
                    n++;
                    break;
                }
            }
            if (n > 40) break;
        }
        if (n == 0) r.AppendLine("(none — nothing in the shipped game uses it)");
        r.AppendLine("```");
        r.AppendLine();
    }
}

internal static class Probes7
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 29. Where does a RenderOutputManager instance live?");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t) in w.AllTypes())
        {
            foreach (var f in t.Fields)
                if (f.FieldType.Name.Contains("RenderOutputManager"))
                    r.AppendLine($"field  {t.FullName}.{f.Name}{(f.IsStatic ? " STATIC" : "")} {(f.IsPublic ? "pub" : "int")}");
            foreach (var p in t.Properties)
                if (p.PropertyType.Name.Contains("RenderOutputManager"))
                    r.AppendLine($"prop   {t.FullName}.{p.Name}");
            foreach (var m in t.Methods)
                if (m.ReturnType.Name.Contains("RenderOutputManager"))
                    r.AppendLine($"method {t.FullName}.{m.Name}() -> {m.ReturnType.Name}");
        }
        r.AppendLine("```");
        r.AppendLine();

        r.AppendLine("### Who raises OnScreenshotToMemoryTaken (the delivery path)");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
            foreach (var ins in m.Body.Instructions)
                if (ins.Operand is FieldReference fr && fr.Name.Contains("OnScreenshotToMemoryTaken"))
                { r.AppendLine($"{asm.Name.Name}: {t.FullName}.{m.Name}  [{ins.OpCode.Name}]"); break; }
        r.AppendLine("```");
        r.AppendLine();
    }
}
