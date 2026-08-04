using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RttCamera.Recon;

// Offline reconnaissance for the RTT/PIP camera feasibility question.
//
// The whole POC hinges on one thing: can the engine be made to render the 3D
// scene from a second camera into an offscreen target whose texture we can then
// blit onto an LCD panel? Everything here is designed to answer that from the
// shipped assemblies, before a line of mod code is written.
internal static class Program
{
    public static string GameDir = @"E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2";
    private static string _outDir;

    private static int Main(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--game" or "-g") GameDir = args[i + 1];

        if (!Directory.Exists(GameDir))
        {
            Console.Error.WriteLine($"Game dir not found: {GameDir}");
            return 1;
        }

        _outDir = Path.Combine(FindRepoRoot(), "docs");
        Directory.CreateDirectory(_outDir);

        var report = new StringBuilder();
        report.AppendLine("# SE2 render-to-texture reconnaissance");
        report.AppendLine();
        report.AppendLine($"Generated from `{GameDir}`.");
        report.AppendLine();
        report.AppendLine("Question this answers: can a second camera render the 3D scene into an");
        report.AppendLine("offscreen target that we can blit onto an LCD panel?");
        report.AppendLine();

        var cecil = new CecilWorld(GameDir);

        Probes.OffscreenTargetUsers(cecil, report);
        Probes.OffscreenTargetSignatures(cecil, report);
        Probes.CameraSurface(cecil, report);
        Probes.RenderSystemApi(cecil, report);
        Probes.DrawImageGate(cecil, report);
        Probes.ViewAndPassTypes(cecil, report);
        Deep.Run(cecil, report);
        Probes2.Run(cecil, report);
        Probes3.Run(cecil, report);
        Probes4.Run(cecil, report);
        Probes5.Run(cecil, report);
        Probes6.Run(cecil, report);
        Probes7.Run(cecil, report);
        Probes8.Run(cecil, report);
        Probes9.Run(cecil, report);
        Probes10.Run(cecil, report);
        Probes11.Run(cecil, report);
        Probes12.Run(cecil, report);
        Probes13.Run(cecil, report);
        Probes14.Run(cecil, report);
        Probes15.Run(cecil, report);
        Probes16.Run(cecil, report);
        Probes17.Run(cecil, report);

        var path = Path.Combine(_outDir, "rtt-recon.md");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"Wrote {path} ({report.Length:N0} chars)");
        return 0;
    }

    // Walk up from the binary to the directory holding Directory.Build.props, so
    // output lands in the repo regardless of how deep the bin folder is.
    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props")))
            d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }
}

// Loads every engine assembly once, with a resolver that can follow references.
internal sealed class CecilWorld
{
    public readonly List<AssemblyDefinition> Assemblies = new();
    public readonly Dictionary<string, AssemblyDefinition> ByName = new(StringComparer.OrdinalIgnoreCase);

    // The assemblies that could plausibly hold render/camera/LCD logic. Loading
    // all ~200 (Avalonia, Roslyn, EOS, ...) costs minutes and finds nothing.
    private static readonly string[] Interesting =
    {
        "VRage.Render", "VRage.Render12", "VRage.Core", "VRage.Core.Game", "VRage.Library",
        "VRage.DCS", "VRage.Game", "Game2.Client", "Game2.Game", "Game2.Simulation",
    };

    public CecilWorld(string gameDir)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(gameDir);
        var pars = new ReaderParameters { AssemblyResolver = resolver };

        foreach (var name in Interesting)
        {
            var path = Path.Combine(gameDir, name + ".dll");
            if (!File.Exists(path)) { Console.Error.WriteLine($"  (missing {name}.dll)"); continue; }
            try
            {
                var asm = AssemblyDefinition.ReadAssembly(path, pars);
                Assemblies.Add(asm);
                ByName[name] = asm;
            }
            catch (Exception e) { Console.Error.WriteLine($"  (failed {name}: {e.Message})"); }
        }
        Console.WriteLine($"Loaded {Assemblies.Count} assemblies.");
    }

    public IEnumerable<(AssemblyDefinition Asm, TypeDefinition Type)> AllTypes()
    {
        foreach (var asm in Assemblies)
        {
            TypeDefinition[] types;
            try { types = asm.MainModule.GetTypes().ToArray(); }
            catch { continue; }
            foreach (var t in types) yield return (asm, t);
        }
    }

    public IEnumerable<(AssemblyDefinition Asm, TypeDefinition Type, MethodDefinition Method)> AllMethodsWithBody()
    {
        foreach (var (asm, t) in AllTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                yield return (asm, t, m);
            }
        }
    }

    public static string Short(MethodReference m) => $"{m.DeclaringType?.Name}.{m.Name}";
}

internal static class Probes
{
    private const string RtType = "OffscreenRenderTarget";

    // ---------------------------------------------------------------- probe 1
    // Who creates offscreen targets, and what do they do with them? If some
    // engine system renders the *scene* into one, that call site is the recipe.
    public static void OffscreenTargetUsers(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 1. Who creates / consumes offscreen render targets");
        r.AppendLine();
        r.AppendLine("Every method that calls `CreateOffscreenTarget`, `Borrow`, or reads");
        r.AppendLine("`OffscreenRenderTarget.TextureHandle`, with the other engine calls it makes");
        r.AppendLine("(so the surrounding recipe is visible).");
        r.AppendLine();

        var hits = new List<(string Where, string Kind, List<string> Calls)>();

        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            bool creates = false, borrows = false, readsHandle = false, takesRt = false;

            foreach (var p in m.Parameters)
                if (p.ParameterType.FullName.Contains(RtType)) takesRt = true;

            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                if (mr.Name == "CreateOffscreenTarget") creates = true;
                else if (mr.Name == "Borrow" && mr.ReturnType.FullName.Contains(RtType)) borrows = true;
                else if (mr.Name == "get_TextureHandle" && mr.DeclaringType.Name.Contains(RtType)) readsHandle = true;
            }
            if (!creates && !borrows && !readsHandle && !takesRt) continue;

            var kinds = new List<string>();
            if (creates) kinds.Add("creates");
            if (borrows) kinds.Add("borrows");
            if (readsHandle) kinds.Add("reads-handle");
            if (takesRt) kinds.Add("takes-rt-param");

            var calls = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                var s = CecilWorld.Short(mr);
                // Keep engine-side calls; drop the noise of list/string plumbing.
                if (mr.DeclaringType?.Namespace?.StartsWith("System") == true) continue;
                if (!calls.Contains(s)) calls.Add(s);
            }
            hits.Add(($"{asm.Name.Name}: {t.FullName}.{m.Name}", string.Join("+", kinds), calls));
        }

        r.AppendLine($"**{hits.Count} call sites.**");
        r.AppendLine();
        foreach (var (where, kind, calls) in hits.OrderBy(h => h.Where, StringComparer.Ordinal))
        {
            r.AppendLine($"### `{where}` â€” {kind}");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var c in calls.Take(45)) r.AppendLine("  " + c);
            if (calls.Count > 45) r.AppendLine($"  ... +{calls.Count - 45} more");
            r.AppendLine("```");
            r.AppendLine();
        }
    }

    // ---------------------------------------------------------------- probe 2
    // Any method anywhere that mentions OffscreenRenderTarget in its signature,
    // plus every field/property of that type. Shows where RTs are plumbed to.
    public static void OffscreenTargetSignatures(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 2. Offscreen target in signatures, fields and properties");
        r.AppendLine();
        r.AppendLine("```");
        int n = 0;
        foreach (var (asm, t) in w.AllTypes())
        {
            foreach (var f in t.Fields)
                if (f.FieldType.FullName.Contains(RtType))
                { r.AppendLine($"field  {t.FullName}.{f.Name} : {f.FieldType.Name}"); n++; }
            foreach (var p in t.Properties)
                if (p.PropertyType.FullName.Contains(RtType))
                { r.AppendLine($"prop   {t.FullName}.{p.Name} : {p.PropertyType.Name}"); n++; }
            foreach (var m in t.Methods)
            {
                bool sig = m.ReturnType.FullName.Contains(RtType)
                        || m.Parameters.Any(p => p.ParameterType.FullName.Contains(RtType));
                if (!sig) continue;
                var ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
                r.AppendLine($"method {t.FullName}.{m.Name}({ps}) -> {m.ReturnType.Name}");
                n++;
            }
        }
        r.AppendLine("```");
        r.AppendLine();
        r.AppendLine($"{n} entries.");
        r.AppendLine();
    }

    // ---------------------------------------------------------------- probe 3
    // The camera surface. A second scene view needs (a) a camera the renderer
    // will honour and (b) somewhere for its output to land.
    public static void CameraSurface(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 3. Camera types and the render camera path");
        r.AppendLine();

        var cameraTypes = w.AllTypes()
            .Where(x => x.Type.Name.Contains("Camera") && !x.Type.Name.StartsWith("<"))
            .OrderBy(x => x.Type.FullName, StringComparer.Ordinal)
            .ToList();

        r.AppendLine($"**{cameraTypes.Count} camera-named types.**");
        r.AppendLine();
        r.AppendLine("```");
        foreach (var (asm, t) in cameraTypes)
            r.AppendLine($"{asm.Name.Name,-18} {(t.IsPublic ? "pub " : "int ")}{t.FullName}");
        r.AppendLine("```");
        r.AppendLine();

        // Full member dump for the ones that matter most.
        foreach (var want in new[] { "CameraComponent", "RenderCameraComponent", "CameraSystemComponent", "MainRenderTarget", "RenderSettings" })
        {
            var t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == want);
            if (t == null) { r.AppendLine($"### `{want}` â€” not found"); r.AppendLine(); continue; }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var f in t.Fields)
                r.AppendLine($"  {(f.IsPublic ? "pub " : "int ")}field {f.FieldType.Name} {f.Name}");
            foreach (var p in t.Properties)
                r.AppendLine($"  prop  {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }

    // ---------------------------------------------------------------- probe 4
    // The full RenderSystem / RenderContracts surface â€” looking for any
    // "render this view" entry point that is not the main camera.
    public static void RenderSystemApi(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 4. RenderSystem / render contract surface");
        r.AppendLine();

        foreach (var want in new[] { "RenderSystem", "RenderContracts", "MaterialSystem", "UISystem" })
        {
            var t = w.AllTypes().Select(x => x.Type)
                .FirstOrDefault(x => x.Name == want && x.Namespace != null && x.Namespace.Contains("Render.Contracts"));
            if (t == null)
            {
                t = w.AllTypes().Select(x => x.Type).FirstOrDefault(x => x.Name == want);
                if (t == null) { r.AppendLine($"### `{want}` â€” not found"); r.AppendLine(); continue; }
            }
            r.AppendLine($"### `{t.FullName}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var p in t.Properties) r.AppendLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter && !m.IsConstructor))
                r.AppendLine($"  {(m.IsPublic ? "pub " : "int ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})");
            r.AppendLine("```");
            r.AppendLine();
        }
    }

    // ---------------------------------------------------------------- probe 5
    // The "Route A" gate inherited from Grid Schematics: does the UI recorder
    // accept a render target's generated texture handle in DrawImage, or does it
    // go down a content-cache path that throws on the render thread?
    public static void DrawImageGate(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 5. Does DrawImage accept a render-target texture handle?");
        r.AppendLine();
        r.AppendLine("IL of the UI recorder's texture-handle classification. A generated");
        r.AppendLine("(render-target) handle must not fall into the file-backed content-cache");
        r.AppendLine("path â€” that throws inside the render thread's replay, which crashes.");
        r.AppendLine();

        var names = new[] { "TryExtractGraphicsType", "DrawImage", "DrawImageExt", "ResolveTexture", "GetTexture" };
        int dumped = 0;
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            if (!names.Contains(m.Name)) continue;
            if (!t.FullName.Contains("UISystem") && !t.FullName.Contains("Recorder") && !t.FullName.Contains("DrawBatch")) continue;
            if (dumped++ > 12) break;
            r.AppendLine($"### `{asm.Name.Name}: {t.FullName}.{m.Name}`");
            r.AppendLine();
            r.AppendLine("```");
            foreach (var ins in m.Body.Instructions.Take(160))
                r.AppendLine($"  {ins.OpCode.Name,-14} {Trim(ins.Operand?.ToString())}");
            if (m.Body.Instructions.Count > 160) r.AppendLine($"  ... +{m.Body.Instructions.Count - 160} instructions");
            r.AppendLine("```");
            r.AppendLine();
        }
        if (dumped == 0) { r.AppendLine("_No matching methods found._"); r.AppendLine(); }
    }

    // ---------------------------------------------------------------- probe 6
    // Anything that smells like a per-view render pass: a second scene draw has
    // to go through one of these if it exists at all.
    public static void ViewAndPassTypes(CecilWorld w, StringBuilder r)
    {
        r.AppendLine("## 6. View / viewport / pass / frame-graph types");
        r.AppendLine();
        r.AppendLine("Candidates for a second scene view. Names only â€” the shortlist gets a");
        r.AppendLine("full dump on the next pass once we know which are real.");
        r.AppendLine();

        string[] needles = { "Viewport", "RenderPass", "FrameGraph", "RenderView", "SceneView", "DrawScene", "RenderScene", "Mirror", "Reflection", "Portal", "Preview" };
        r.AppendLine("```");
        foreach (var needle in needles)
        {
            var found = w.AllTypes()
                .Where(x => x.Type.Name.Contains(needle) && !x.Type.Name.StartsWith("<"))
                .OrderBy(x => x.Type.FullName, StringComparer.Ordinal)
                .Take(30).ToList();
            r.AppendLine($"-- {needle}: {found.Count}");
            foreach (var (asm, t) in found)
                r.AppendLine($"     {asm.Name.Name,-18} {t.FullName}");
        }
        r.AppendLine("```");
        r.AppendLine();

        // Methods whose name says they render a scene/view â€” the real prize.
        r.AppendLine("### Methods named like a scene/view render entry point");
        r.AppendLine();
        r.AppendLine("```");
        string[] mNeedles = { "RenderScene", "DrawScene", "RenderView", "RenderToTexture", "RenderTo", "RenderFrame", "RenderCamera" };
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethodsWithBody())
        {
            if (!mNeedles.Any(x => m.Name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
            if (n++ > 120) break;
            var ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
            r.AppendLine($"{asm.Name.Name,-16} {t.FullName}.{m.Name}({ps})");
        }
        r.AppendLine("```");
        r.AppendLine();
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > 150 ? s[..150] + "â€¦" : s;
    }
}
