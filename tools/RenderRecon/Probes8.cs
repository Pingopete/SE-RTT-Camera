using System.Text;
using Mono.Cecil;

namespace RttCamera.Recon;

// The readback enqueues fine but never delivers. Who is supposed to drain the
// queue, and under what condition?
internal static class Probes8
{
    public static void Run(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 30. Who drains the screenshot-to-memory queue");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
            foreach (var ins in m.Body.Instructions)
                if (ins.Operand is MethodReference mr &&
                    (mr.Name is "TryDequeueWork" or "TryDequeueNextRenderRequest" or "EnqueueTakingScreenshotToMemory"
                     || mr.Name.Contains("ScreenshotToMemoryTaken")))
                { r.AppendLine($"{asm.Name.Name}: {t.FullName}.{m.Name}  ->  {mr.Name}"); break; }
        r.AppendLine("```");
        r.AppendLine();

        foreach (var name in new[] { "TryDequeueWork", "TryDequeueNextRenderRequest" })
        {
            var m = w.AllMethodsWithBody().FirstOrDefault(x =>
                x.Type.Name == "OffscreenTargetManager" && x.Method.Name == name).Method;
            if (m == null) continue;
            r.AppendLine($"### `OffscreenTargetManager.{name}` — full IL");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var ins in m.Body.Instructions)
                r.AppendLine($"  {ins.OpCode.Name,-14} {(ins.Operand?.ToString() is string s && s.Length > 100 ? s[..100] : ins.Operand)}");
            r.AppendLine("```");
            r.AppendLine();
        }
    }
}
