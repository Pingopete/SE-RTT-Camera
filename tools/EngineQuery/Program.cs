using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RttCamera.EngineQuery;

// A generic, ad-hoc query CLI over the shipped SE2 assemblies.
//
// RenderRecon (the sibling tool) answers a fixed list of questions and has to be
// edited and rebuilt to ask a new one. That was fine while the questions were
// known in advance; it stops being fine once several lines of investigation are
// open at once. This asks arbitrary questions from the command line instead.
//
// Read-only, offline, no game required.
internal static class Program
{
    private static string _gameDir = @"E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2";
    private static int _max = 400;
    private static int _ilMax = 400;
    private static bool _quiet;

    private static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        var positional = new List<string>();
        string asmFilter = "render";
        string outFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" or "-g": _gameDir = args[++i]; break;
                case "--asm" or "-a": asmFilter = args[++i]; break;
                case "--max" or "-m": _max = int.Parse(args[++i]); break;
                case "--il-max": _ilMax = int.Parse(args[++i]); break;
                case "--out" or "-o": outFile = args[++i]; break;
                case "--quiet" or "-q": _quiet = true; break;
                default: positional.Add(args[i]); break;
            }
        }

        if (!Directory.Exists(_gameDir)) { Console.Error.WriteLine($"Game dir not found: {_gameDir}"); return 1; }
        if (positional.Count == 0) { Usage(); return 1; }

        var world = new World(_gameDir, asmFilter, _quiet);
        Cmd.SetCap(_max);
        var r = new StringBuilder();

        string cmd = positional[0].ToLowerInvariant();
        string arg = positional.Count > 1 ? positional[1] : null;
        string arg2 = positional.Count > 2 ? positional[2] : null;

        try
        {
            switch (cmd)
            {
                case "asm": Cmd.Assemblies(world, r); break;
                case "types": Cmd.Types(world, r, Need(arg, "regex")); break;
                case "dump": Cmd.Dump(world, r, Need(arg, "type regex")); break;
                case "il": Cmd.Il(world, r, Need(arg, "Type::Method"), _ilMax); break;
                case "callers": Cmd.Callers(world, r, Need(arg, "method regex"), arg2); break;
                case "callees": Cmd.Callees(world, r, Need(arg, "Type::Method")); break;
                case "sig": Cmd.Sig(world, r, Need(arg, "regex")); break;
                case "members": Cmd.Members(world, r, Need(arg, "type-name regex")); break;
                case "strings": Cmd.Strings(world, r, Need(arg, "regex")); break;
                case "hierarchy" or "tree": Cmd.Hierarchy(world, r, Need(arg, "type regex")); break;
                case "enum": Cmd.Enum(world, r, Need(arg, "type regex")); break;
                case "newobj": Cmd.NewObj(world, r, Need(arg, "type regex")); break;
                case "writes": Cmd.Writes(world, r, Need(arg, "field regex")); break;
                default: Console.Error.WriteLine($"Unknown command '{cmd}'."); Usage(); return 1;
            }
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }

        var text = r.ToString();
        if (outFile != null) { File.WriteAllText(outFile, text); Console.WriteLine($"Wrote {outFile} ({text.Length:N0} chars)"); }
        else Console.Out.Write(text);
        return 0;
    }

    private static string Need(string v, string what) =>
        v ?? throw new ArgumentException($"missing argument: {what}");

    private static void Usage() => Console.Error.WriteLine("""
        eq <command> [arg] [arg2] [options]

        commands
          asm                        list the loaded assemblies and type counts
          types     <regex>          type full names matching regex
          dump      <typeRegex>      every field / property / method of matching types
          il        <Type::Method>   IL bodies (both halves are regexes)
          callers   <methodRegex>    methods whose body calls a matching method
                    [declTypeRegex]  optionally restrict which declaring type is called
          callees   <Type::Method>   distinct calls a method makes, in order
          sig       <regex>          methods whose rendered signature matches
          members   <regex>          fields / properties whose TYPE name matches
          strings   <regex>          string literals matching, with enclosing method
          hierarchy <typeRegex>      base chain, interfaces, and derived types
          enum      <typeRegex>      enum members and values
          newobj    <typeRegex>      methods that construct a matching type
          writes    <fieldRegex>     methods that stfld/stsfld a matching field

        options
          -g --game <dir>   game directory (default: the SteamLibrary D: path)
          -a --asm  <set>   render | client | all | Csv,Of,Assembly,Names   (default: render)
          -m --max  <n>     cap on emitted entries (default 400)
             --il-max <n>   cap on instructions per body (default 400)
          -o --out  <file>  write to a file instead of stdout
          -q --quiet        suppress the load banner
        """);
}

internal sealed class World
{
    public readonly List<AssemblyDefinition> Assemblies = new();

    // The render/game core. Loading all 218 shipped assemblies (Avalonia, Roslyn,
    // EOS, the content pipeline) costs minutes and finds nothing.
    private static readonly string[] Render =
    {
        "VRage.Render12", "VRage.Render", "VRage.Core", "VRage.Core.Game", "VRage.Library",
        "VRage.Game", "VRage.Game.Client", "VRage.DCS", "VRage.Client",
        "Game2.Client", "Game2.Game", "Game2.Simulation",
    };

    // Everything first-party. Used when the question is "does this exist anywhere".
    private static readonly string[] ClientExtra =
    {
        "VRage.UI", "VRage.UI.Shared", "VRage.Input", "VRage.Voxels", "VRage.Voxels.Client",
        "VRage.Water", "VRage.Water.Client", "VRage.Animation", "VRage.Animation.Client",
        "VRage.Physics", "VRage.Physics.Client", "VRage.Platform.Windows",
        "Game2.AutoTests", "Game2.Plugin.Editor", "Game2.ContentBuilder",
        "VRage.Core.Editor", "VRage.Core.Game.Editor", "VRage.AutoTest",
    };

    public World(string gameDir, string filter, bool quiet)
    {
        IEnumerable<string> names = filter.ToLowerInvariant() switch
        {
            "render" => Render,
            "client" => Render.Concat(ClientExtra),
            "all" => Directory.GetFiles(gameDir, "*.dll")
                              .Select(Path.GetFileNameWithoutExtension)
                              .Where(n => n.StartsWith("VRage") || n.StartsWith("Game2") || n.StartsWith("Keen"))
                              .Where(n => !n.EndsWith(".Native")),
            _ => filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(gameDir);
        var pars = new ReaderParameters { AssemblyResolver = resolver };

        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(gameDir, name + ".dll");
            if (!File.Exists(path)) continue;
            try { Assemblies.Add(AssemblyDefinition.ReadAssembly(path, pars)); }
            catch (Exception e) { if (!quiet) Console.Error.WriteLine($"  (failed {name}: {e.Message})"); }
        }
        if (!quiet) Console.Error.WriteLine($"// loaded {Assemblies.Count} assemblies from {gameDir}");
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

    public IEnumerable<(AssemblyDefinition Asm, TypeDefinition Type, MethodDefinition Method)> AllMethods()
    {
        foreach (var (asm, t) in AllTypes())
            foreach (var m in t.Methods)
                yield return (asm, t, m);
    }
}

internal static class Cmd
{
    private static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Type::Method â€” both halves optional-ish, both regexes.
    private static (Regex T, Regex M) Split(string spec)
    {
        var parts = spec.Split("::", 2);
        if (parts.Length != 2) throw new ArgumentException($"expected Type::Method, got '{spec}'");
        return (Rx(parts[0]), Rx(parts[1]));
    }

    public static void Assemblies(World w, StringBuilder r)
    {
        foreach (var a in w.Assemblies)
        {
            int n = 0;
            try { n = a.MainModule.GetTypes().Count(); } catch { }
            r.AppendLine($"{a.Name.Name,-28} {n,6} types   v{a.Name.Version}");
        }
    }

    public static void Types(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t) in w.AllTypes().OrderBy(x => x.Type.FullName, StringComparer.Ordinal))
        {
            if (t.Name.StartsWith("<")) continue;
            if (!rx.IsMatch(t.FullName)) continue;
            if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap})"); break; }
            string kind = t.IsInterface ? "iface" : t.IsEnum ? "enum " : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static" : "class";
            r.AppendLine($"{asm.Name.Name,-18} {(t.IsPublic ? "pub" : "int")} {kind} {t.FullName}" +
                         (t.BaseType != null && t.BaseType.Name != "Object" ? $"  : {t.BaseType.Name}" : ""));
        }
        r.AppendLine($"-- {n} types");
    }

    public static void Dump(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t) in w.AllTypes().OrderBy(x => x.Type.FullName, StringComparer.Ordinal))
        {
            if (!rx.IsMatch(t.FullName)) continue;
            if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap} types)"); break; }

            r.AppendLine($"### {asm.Name.Name}: {t.FullName}");
            if (t.BaseType != null) r.AppendLine($"    base: {t.BaseType.FullName}");
            if (t.HasInterfaces) r.AppendLine($"    impl: {string.Join(", ", t.Interfaces.Select(i => i.InterfaceType.Name))}");
            if (t.IsEnum)
            {
                foreach (var f in t.Fields.Where(f => f.HasConstant))
                    r.AppendLine($"    = {f.Name} = {f.Constant}");
                r.AppendLine();
                continue;
            }
            foreach (var f in t.Fields)
                r.AppendLine($"    {Vis(f.IsPublic, f.IsStatic)} field  {Nm(f.FieldType)} {f.Name}{(f.HasConstant ? " = " + f.Constant : "")}");
            foreach (var p in t.Properties)
                r.AppendLine($"    {Vis(p.GetMethod?.IsPublic ?? false, p.GetMethod?.IsStatic ?? false)} prop   {Nm(p.PropertyType)} {p.Name} " +
                             $"{{{(p.GetMethod != null ? " get;" : "")}{(p.SetMethod != null ? " set;" : "")} }}");
            foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter))
                r.AppendLine($"    {Vis(m.IsPublic, m.IsStatic)} {Sig(m)}");
            r.AppendLine();
        }
        r.AppendLine($"-- {n} types");
    }

    public static void Il(World w, StringBuilder r, string spec, int ilMax)
    {
        var (tr, mr) = Split(spec);
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethods())
        {
            if (!tr.IsMatch(t.FullName) || !mr.IsMatch(m.Name)) continue;
            if (!m.HasBody) { r.AppendLine($"### {t.FullName}.{m.Name} â€” no body ({(m.IsAbstract ? "abstract" : "extern")})"); r.AppendLine(); continue; }
            if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap} methods)"); break; }

            r.AppendLine($"### {asm.Name.Name}: {t.FullName}.{Sig(m)}");
            foreach (var v in m.Body.Variables)
                r.AppendLine($"    .local {v.Index}: {Nm(v.VariableType)}");
            int i = 0;
            foreach (var ins in m.Body.Instructions)
            {
                if (i++ >= ilMax) { r.AppendLine($"    ... +{m.Body.Instructions.Count - ilMax} instructions"); break; }
                r.AppendLine($"    IL_{ins.Offset:x4}  {ins.OpCode.Name,-16} {Operand(ins.Operand)}");
            }
            if (m.Body.HasExceptionHandlers)
                foreach (var h in m.Body.ExceptionHandlers)
                    r.AppendLine($"    .handler {h.HandlerType} try IL_{h.TryStart?.Offset:x4}..IL_{h.TryEnd?.Offset:x4} " +
                                 $"handler IL_{h.HandlerStart?.Offset:x4}  {h.CatchType?.Name}");
            r.AppendLine();
        }
        r.AppendLine($"-- {n} bodies");
    }

    public static void Callers(World w, StringBuilder r, string methodPattern, string declPattern)
    {
        var mrx = Rx(methodPattern);
        var drx = declPattern == null ? null : Rx(declPattern);
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethods())
        {
            if (!m.HasBody) continue;
            var hits = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                if (!mrx.IsMatch(mr.Name)) continue;
                if (drx != null && !drx.IsMatch(mr.DeclaringType?.FullName ?? "")) continue;
                var s = $"{mr.DeclaringType?.Name}.{mr.Name}";
                if (!hits.Contains(s)) hits.Add(s);
            }
            if (hits.Count == 0) continue;
            if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap})"); break; }
            r.AppendLine($"{asm.Name.Name,-16} {t.FullName}.{m.Name}   ->  {string.Join(", ", hits)}");
        }
        r.AppendLine($"-- {n} callers");
    }

    public static void Callees(World w, StringBuilder r, string spec)
    {
        var (tr, mr) = Split(spec);
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethods())
        {
            if (!tr.IsMatch(t.FullName) || !mr.IsMatch(m.Name) || !m.HasBody) continue;
            if (n++ >= Cap) break;
            r.AppendLine($"### {t.FullName}.{Sig(m)}");
            var seen = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                string s = ins.Operand switch
                {
                    MethodReference x => $"call  {x.DeclaringType?.Name}.{x.Name}({string.Join(", ", x.Parameters.Select(p => Nm(p.ParameterType)))})",
                    FieldReference x => $"{(ins.OpCode.Name.StartsWith("st") ? "STORE" : "load ")} {x.DeclaringType?.Name}.{x.Name} : {Nm(x.FieldType)}",
                    _ => null,
                };
                if (s == null || seen.Contains(s)) continue;
                seen.Add(s);
                r.AppendLine("    " + s);
            }
            r.AppendLine();
        }
        r.AppendLine($"-- {n} methods");
    }

    public static void Sig(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethods())
        {
            var s = Sig(m);
            if (!rx.IsMatch(s) && !rx.IsMatch($"{t.Name}.{s}")) continue;
            if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap})"); break; }
            r.AppendLine($"{asm.Name.Name,-16} {Vis(m.IsPublic, m.IsStatic)} {t.FullName}.{s}");
        }
        r.AppendLine($"-- {n} methods");
    }

    public static void Members(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t) in w.AllTypes())
        {
            foreach (var f in t.Fields)
            {
                if (!rx.IsMatch(f.FieldType.Name) && !rx.IsMatch(f.Name)) continue;
                if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap})"); return; }
                r.AppendLine($"{asm.Name.Name,-16} field {Vis(f.IsPublic, f.IsStatic)} {t.FullName}.{f.Name} : {Nm(f.FieldType)}");
            }
            foreach (var p in t.Properties)
            {
                if (!rx.IsMatch(p.PropertyType.Name) && !rx.IsMatch(p.Name)) continue;
                if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap})"); return; }
                r.AppendLine($"{asm.Name.Name,-16} prop  {Vis(p.GetMethod?.IsPublic ?? false, p.GetMethod?.IsStatic ?? false)} " +
                             $"{t.FullName}.{p.Name} : {Nm(p.PropertyType)} {{{(p.SetMethod != null ? " set;" : " get-only;")} }}");
            }
        }
        r.AppendLine($"-- {n} members");
    }

    public static void Strings(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethods())
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Ldstr) continue;
                if (ins.Operand is not string s || !rx.IsMatch(s)) continue;
                if (n++ >= Cap) { r.AppendLine($"... (capped at {Cap})"); return; }
                r.AppendLine($"{t.FullName}.{m.Name}\n    \"{s}\"");
            }
        }
        r.AppendLine($"-- {n} literals");
    }

    public static void Hierarchy(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        var all = w.AllTypes().ToList();
        int n = 0;
        foreach (var (asm, t) in all.OrderBy(x => x.Type.FullName, StringComparer.Ordinal))
        {
            if (!rx.IsMatch(t.FullName)) continue;
            if (n++ >= Cap) break;
            r.AppendLine($"### {asm.Name.Name}: {t.FullName}");
            var b = t.BaseType;
            while (b != null)
            {
                r.AppendLine($"    base <- {b.FullName}");
                TypeDefinition bd = null;
                try { bd = b.Resolve(); } catch { }
                b = bd?.BaseType;
            }
            foreach (var i in t.Interfaces) r.AppendLine($"    impl <- {i.InterfaceType.FullName}");
            var derived = all.Where(x =>
            {
                if (x.Type.BaseType?.FullName == t.FullName) return true;
                return x.Type.HasInterfaces && x.Type.Interfaces.Any(i => i.InterfaceType.FullName == t.FullName);
            }).Select(x => x.Type.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var d in derived.Take(80)) r.AppendLine($"    derived -> {d}");
            if (derived.Count > 80) r.AppendLine($"    ... +{derived.Count - 80} derived");
            r.AppendLine();
        }
        r.AppendLine($"-- {n} types");
    }

    public static void Enum(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t) in w.AllTypes())
        {
            if (!t.IsEnum || !rx.IsMatch(t.FullName)) continue;
            if (n++ >= Cap) break;
            r.AppendLine($"### {asm.Name.Name}: {t.FullName}");
            foreach (var f in t.Fields.Where(f => f.HasConstant))
                r.AppendLine($"    {f.Name} = {f.Constant}");
            r.AppendLine();
        }
        r.AppendLine($"-- {n} enums");
    }

    public static void NewObj(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethods())
        {
            if (!m.HasBody) continue;
            var hits = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Newobj || ins.Operand is not MethodReference ctor) continue;
                if (!rx.IsMatch(ctor.DeclaringType?.FullName ?? "")) continue;
                var s = $"{ctor.DeclaringType.Name}({string.Join(", ", ctor.Parameters.Select(p => Nm(p.ParameterType)))})";
                if (!hits.Contains(s)) hits.Add(s);
            }
            if (hits.Count == 0) continue;
            if (n++ >= Cap) break;
            r.AppendLine($"{asm.Name.Name,-16} {t.FullName}.{m.Name}\n    new {string.Join("\n    new ", hits)}");
        }
        r.AppendLine($"-- {n} construction sites");
    }

    public static void Writes(World w, StringBuilder r, string pattern)
    {
        var rx = Rx(pattern);
        int n = 0;
        foreach (var (asm, t, m) in w.AllMethods())
        {
            if (!m.HasBody) continue;
            var hits = new List<string>();
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Stfld && ins.OpCode != OpCodes.Stsfld) continue;
                if (ins.Operand is not FieldReference f) continue;
                if (!rx.IsMatch(f.Name) && !rx.IsMatch($"{f.DeclaringType?.Name}.{f.Name}")) continue;
                var s = $"{f.DeclaringType?.Name}.{f.Name}";
                if (!hits.Contains(s)) hits.Add(s);
            }
            if (hits.Count == 0) continue;
            if (n++ >= Cap) break;
            r.AppendLine($"{asm.Name.Name,-16} {t.FullName}.{m.Name}  ->  {string.Join(", ", hits)}");
        }
        r.AppendLine($"-- {n} writers");
    }

    // ------------------------------------------------------------------ helpers

    private static int Cap => CapValue;
    private static int CapValue = int.MaxValue;
    public static void SetCap(int n) => CapValue = n;

    private static string Vis(bool pub, bool stat) => (pub ? "pub" : "int") + (stat ? " sta" : "    ");

    private static string Sig(MethodDefinition m)
    {
        var ps = m.Parameters.Select(p =>
        {
            string pre = p.ParameterType.IsByReference ? (p.IsOut ? "out " : p.IsIn ? "in " : "ref ") : "";
            return $"{pre}{Nm(p.ParameterType)} {p.Name}";
        });
        string gen = m.HasGenericParameters ? "<" + string.Join(",", m.GenericParameters.Select(g => g.Name)) + ">" : "";
        return $"{Nm(m.ReturnType)} {m.Name}{gen}({string.Join(", ", ps)})";
    }

    // Cecil names generics as `Nullable`1<Rectangle>`; keep it short but honest.
    private static string Nm(TypeReference t)
    {
        if (t == null) return "?";
        if (t.IsByReference) return Nm(t.GetElementType());
        if (t is GenericInstanceType g)
            return $"{g.Name.Split('`')[0]}<{string.Join(",", g.GenericArguments.Select(Nm))}>";
        if (t.IsArray) return Nm(t.GetElementType()) + "[]";
        return t.Name;
    }

    private static string Operand(object o) => o switch
    {
        null => "",
        MethodReference m => $"{m.DeclaringType?.Name}.{m.Name}({string.Join(", ", m.Parameters.Select(p => Nm(p.ParameterType)))}) : {Nm(m.ReturnType)}",
        FieldReference f => $"{f.DeclaringType?.Name}.{f.Name} : {Nm(f.FieldType)}",
        TypeReference t => t.FullName,
        Instruction i => $"IL_{i.Offset:x4}",
        Instruction[] a => string.Join(", ", a.Select(x => $"IL_{x.Offset:x4}")),
        VariableDefinition v => $"V_{v.Index}",
        ParameterDefinition p => $"arg:{p.Name}",
        string s => $"\"{(s.Length > 200 ? s[..200] + "â€¦" : s)}\"",
        _ => o.ToString(),
    };
}
