using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// OffscreenUIRenderer.DoWork drains a render-request queue and calls DrawOne for
// each target. If our target can be pushed into that queue, DrawOne fires for it
// and the already-written handover copies our frame in at a legal moment.
// So: what populates it?
internal static class Probes9
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 31. What populates the offscreen render-request queue");
        r.AppendLine();

        var m = w.AllMethodsWithBody().FirstOrDefault(x =>
            x.Type.Name == "OffscreenTargetManager" && x.Method.Name == "TryDequeueNextRenderRequest").Method;
        if (m != null)
        {
            r.AppendLine("### `TryDequeueNextRenderRequest` — which field it drains");
            r.AppendLine("```");
            foreach (var ins in m.Body.Instructions)
                if (ins.Operand is FieldReference fr) r.AppendLine($"  {ins.OpCode.Name,-10} {fr.DeclaringType?.Name}.{fr.Name} : {fr.FieldType.Name}");
            r.AppendLine("```");
            r.AppendLine();
        }

        // Everything that writes any OffscreenTargetManager field — the enqueue side.
        r.AppendLine("### Writers of OffscreenTargetManager state");
        r.AppendLine("```");
        foreach (var (asm, t, mm) in w.AllMethodsWithBody())
            foreach (var ins in mm.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                if (mr.DeclaringType?.Name != "OffscreenTargetManager") continue;
                r.AppendLine($"{asm.Name.Name}: {t.FullName}.{mm.Name}  ->  {mr.Name}");
                break;
            }
        r.AppendLine("```");
        r.AppendLine();

        // And the full surface of the manager, so the enqueue entry point is visible.
        var mt = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "OffscreenTargetManager");
        if (mt != null)
        {
            r.AppendLine("### `OffscreenTargetManager` members");
            r.AppendLine("```");
            foreach (var f in mt.Fields) r.AppendLine($"  field {f.FieldType.Name} {f.Name}");
            foreach (var mm in mt.Methods.Where(x => !x.IsGetter && !x.IsSetter))
                r.AppendLine($"  {(mm.IsPublic ? "pub " : "int ")}{mm.ReturnType.Name} {mm.Name}({string.Join(", ", mm.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
