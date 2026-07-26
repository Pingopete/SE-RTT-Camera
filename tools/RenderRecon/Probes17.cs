using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// A standalone CullingContext crashes: DrawContextManager runs per-frame lifecycle
// (OnBeginDraw/OnEndDraw) on the contexts it owns, and ours gets none of it.
// If that lifecycle iterates the EnvProbeCulling ARRAY, then growing the array and
// taking the spare slot means the engine manages our context for us.
internal static class Probes17
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 39. DrawContextManager per-frame lifecycle");
        r.AppendLine();
        foreach (var name in new[] { "BorrowShadowCulling", "ReturnShadowCulling" })
        {
            var m = w.AllMethodsWithBody().FirstOrDefault(x =>
                x.Type.Name == "DrawContextManager" && x.Method.Name == name).Method;
            if (m == null) { r.AppendLine($"### `{name}` — not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `DrawContextManager.{name}`");
            r.AppendLine("```");
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is MethodReference mr)
                    r.AppendLine($"  call {mr.DeclaringType?.Name}.{mr.Name}");
                else if (ins.Operand is FieldReference fr)
                    r.AppendLine($"  fld  {fr.DeclaringType?.Name}.{fr.Name} : {fr.FieldType.Name}");
                else if (ins.OpCode.Name.StartsWith("br") || ins.OpCode.Name.Contains("ldlen"))
                    r.AppendLine($"  {ins.OpCode.Name}");
            }
            r.AppendLine("```");
            r.AppendLine();
        }

        r.AppendLine("### All DrawContextManager methods");
        r.AppendLine("```");
        var dcm = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "DrawContextManager");
        if (dcm != null)
            foreach (var m in dcm.Methods.Where(m => !m.IsGetter && !m.IsSetter))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
        r.AppendLine("```");
        r.AppendLine();

        // What lifecycle does CullingContext itself expose?
        var ct = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == "CullingContext");
        if (ct != null)
        {
            r.AppendLine("### `CullingContext` members");
            r.AppendLine("```");
            foreach (var m in ct.Methods.Where(m => !m.IsGetter && !m.IsSetter))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
