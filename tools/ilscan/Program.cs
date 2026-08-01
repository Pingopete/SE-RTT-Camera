// ilscan — a call-graph / member scanner over the shipped SE2 assemblies, built on the
// Mono.Cecil that ships in the game folder.
//
// WHY THIS EXISTS. The repeated question in this project is "does our nested Draw reach
// <some shared engine state>?", and the repeated failure mode is answering it by guessing
// at symbol names. Two greps earlier today reported "no sun symbols" when what was actually
// missing was the `strings` COMMAND. A structural question deserves a structural answer.
//
// VIRTUAL DISPATCH IS EXPANDED, and that is the whole point. A raw Cecil call edge names the
// DECLARED callee (IResourceStreaming.Update), not the implementation that actually runs. In
// an engine this interface-heavy, an unexpanded graph says "unreachable" for almost
// everything interesting. So a callvirt to M on type T also produces edges to every method
// in the loaded set with M's name and arity whose declaring type derives from / implements T.
//
// That direction of error is deliberate: this OVER-approximates. A path it finds is a path
// that MIGHT run, not one that must. Treat a hit as "go read this code", never as proof.
// A *miss*, by contrast, is fairly strong evidence — you would have to be dispatching through
// something reflective for the edge to be absent here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    static readonly List<ModuleDefinition> Modules = new();
    static readonly List<MethodDefinition> AllMethods = new();

    // callee -> callers, and caller -> callees. Built once, both directions, because
    // "who calls X" and "what does X reach" are both asked constantly.
    static readonly Dictionary<MethodDefinition, HashSet<MethodDefinition>> Callees = new();
    static readonly Dictionary<MethodDefinition, HashSet<MethodDefinition>> Callers = new();

    // (name, argc) -> candidate implementations, for virtual expansion.
    static readonly Dictionary<(string, int), List<MethodDefinition>> ByNameArity = new();

    // field -> methods touching it, split by whether they read or write. The read/write
    // split is what makes "who STAMPS this shared state" answerable in one query, which is
    // the exact shape of every bug this project has found in the nested render.
    static readonly Dictionary<FieldDefinition, HashSet<MethodDefinition>> FieldReads = new();
    static readonly Dictionary<FieldDefinition, HashSet<MethodDefinition>> FieldWrites = new();

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ilscan <gameDir> <command> [args]\n" +
                "  members  <typeRegex>                       type shape: fields, then methods\n" +
                "  callers  <methodRegex> [depth]             who calls it, transitively\n" +
                "  reach    <rootRegex> <targetRegex> [depth] shortest call path root -> target\n" +
                "  writes   <fieldRegex>                      who WRITES a field (and who reads)\n" +
                "  find     <typeRegex>                       just list matching type names");
            return 2;
        }

        var dir = args[0];

        // MODULE FILTER, and it is not a nicety. The full folder is 173 modules / 656k methods,
        // and building the call graph with virtual expansion over all of it exceeds two minutes.
        // Restricting to the assembly that owns the subsystem turns that into seconds.
        //
        // The cost is real and must be remembered when reading a result: callers living in a
        // module you filtered out are INVISIBLE, so a narrow --mod can manufacture a false
        // "nothing calls this". Widen the filter before believing any negative.
        string modFilter = null;
        var rest = new List<string>();
        foreach (var a in args.Skip(1))
        {
            if (a.StartsWith("--mod=")) modFilter = a.Substring(6);
            else rest.Add(a);
        }
        args = new[] { dir }.Concat(rest).ToArray();
        var modRx = modFilter == null ? null : new Regex(modFilter, RegexOptions.IgnoreCase);
        var cmd = args[1].ToLowerInvariant();

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred };

        foreach (var f in Directory.GetFiles(dir, "*.dll"))
        {
            var n = Path.GetFileName(f);
            // Native and third-party noise. Keeping them costs minutes and finds nothing.
            if (n.StartsWith("dxcompiler") || n.StartsWith("dxil") || n.StartsWith("D3D") ||
                n.StartsWith("GFSDK") || n.StartsWith("opencv") || n.StartsWith("steam_api") ||
                n.StartsWith("EOSSDK") || n.StartsWith("amd_") || n.StartsWith("nv"))
                continue;
            if (modRx != null && !modRx.IsMatch(n)) continue;
            try { Modules.Add(ModuleDefinition.ReadModule(f, rp)); }
            catch { /* native or unreadable — expected for a good fraction of the folder */ }
        }

        foreach (var m in Modules)
            foreach (var t in AllTypes(m))
                foreach (var meth in t.Methods)
                {
                    AllMethods.Add(meth);
                    var key = (meth.Name, meth.Parameters.Count);
                    if (!ByNameArity.TryGetValue(key, out var l)) ByNameArity[key] = l = new();
                    l.Add(meth);
                }

        Console.Error.WriteLine($"[ilscan] {Modules.Count} modules, {AllMethods.Count} methods");

        if (cmd == "find")
        {
            var rx = new Regex(args[2], RegexOptions.IgnoreCase);
            foreach (var t in Modules.SelectMany(AllTypes).Where(t => rx.IsMatch(t.FullName))
                                     .OrderBy(t => t.FullName))
                Console.WriteLine($"{t.FullName}   [{t.Module.Name}]");
            return 0;
        }

        if (cmd == "members")
        {
            var rx = new Regex(args[2], RegexOptions.IgnoreCase);
            foreach (var t in Modules.SelectMany(AllTypes).Where(t => rx.IsMatch(t.FullName))
                                     .OrderBy(t => t.FullName))
            {
                Console.WriteLine($"\n=== {t.FullName}   [{t.Module.Name}] ===");
                foreach (var f in t.Fields)
                    Console.WriteLine($"  field  {(f.IsStatic ? "static " : "")}{Short(f.FieldType)} {f.Name}");
                foreach (var p in t.Properties)
                    Console.WriteLine($"  prop   {Short(p.PropertyType)} {p.Name}");
                foreach (var me in t.Methods)
                    Console.WriteLine($"  method {(me.IsStatic ? "static " : "")}{(me.IsVirtual ? "virtual " : "")}" +
                                      $"{Short(me.ReturnType)} {me.Name}({string.Join(", ", me.Parameters.Select(p => Short(p.ParameterType) + " " + p.Name))})");
            }
            return 0;
        }

        // Raw IL. The `members` dump answers "what is the shape"; this answers "what does it
        // actually READ", which is the only way to settle questions like "does this static
        // helper sample the live back-buffer resolution or a stored one" — the difference
        // between our nested render being invisible to it and our nested render steering it.
        if (cmd == "il")
        {
            var rx = new Regex(args[2], RegexOptions.IgnoreCase);
            foreach (var m in AllMethods.Where(m => rx.IsMatch(m.FullName)).OrderBy(m => m.FullName))
            {
                Console.WriteLine($"\n=== {m.FullName}");
                if (!m.HasBody) { Console.WriteLine("  (no body)"); continue; }
                MethodBody b;
                try { b = m.Body; } catch (Exception e) { Console.WriteLine($"  (unreadable: {e.Message})"); continue; }
                foreach (var v in b.Variables) Console.WriteLine($"  .local {v.Index}: {Short(v.VariableType)}");
                foreach (var ins in b.Instructions)
                {
                    var op = ins.Operand switch
                    {
                        MethodReference mr2 => mr2.FullName,
                        FieldReference fr2 => fr2.FullName,
                        TypeReference tr2 => tr2.FullName,
                        string s2 => "\"" + s2 + "\"",
                        Instruction i2 => "IL_" + i2.Offset.ToString("x4"),
                        null => "",
                        var o => o.ToString(),
                    };
                    Console.WriteLine($"  IL_{ins.Offset:x4} {ins.OpCode.Name,-12} {op}");
                }
            }
            return 0;
        }

        BuildGraph();

        switch (cmd)
        {
            case "callers":
            {
                var rx = new Regex(args[2], RegexOptions.IgnoreCase);
                var depth = args.Length > 3 ? int.Parse(args[3]) : 1;
                var seeds = AllMethods.Where(m => rx.IsMatch(m.FullName)).ToList();
                Console.WriteLine($"{seeds.Count} method(s) match:\n");
                foreach (var s in seeds.OrderBy(m => m.FullName))
                {
                    Console.WriteLine($"=== {s.FullName}");
                    PrintCallers(s, depth, 1, new HashSet<MethodDefinition>());
                }
                return 0;
            }
            case "reach":
            {
                var rootRx = new Regex(args[2], RegexOptions.IgnoreCase);
                var tgtRx = new Regex(args[3], RegexOptions.IgnoreCase);
                var maxDepth = args.Length > 4 ? int.Parse(args[4]) : 12;
                var roots = AllMethods.Where(m => rootRx.IsMatch(m.FullName)).ToList();
                Console.WriteLine($"{roots.Count} root(s), searching to depth {maxDepth}\n");
                var any = false;
                foreach (var r in roots.OrderBy(m => m.FullName))
                    any |= Bfs(r, tgtRx, maxDepth);
                if (!any) Console.WriteLine("NO PATH FOUND (see the over-approximation note in the header)");
                return 0;
            }
            case "writes":
            {
                var rx = new Regex(args[2], RegexOptions.IgnoreCase);
                foreach (var kv in FieldWrites.Where(k => rx.IsMatch(k.Key.FullName))
                                              .OrderBy(k => k.Key.FullName))
                {
                    Console.WriteLine($"\n=== {kv.Key.FullName}");
                    Console.WriteLine("  WRITERS:");
                    foreach (var w in kv.Value.OrderBy(m => m.FullName)) Console.WriteLine($"    {w.FullName}");
                    if (FieldReads.TryGetValue(kv.Key, out var rs))
                    {
                        Console.WriteLine("  readers:");
                        foreach (var r in rs.OrderBy(m => m.FullName)) Console.WriteLine($"    {r.FullName}");
                    }
                }
                // A field with readers but NO writers still matters — report it rather than
                // silently showing nothing, because "nobody writes this" is itself a finding
                // (it is how emissivity was proven to be a dead knob).
                foreach (var kv in FieldReads.Where(k => rx.IsMatch(k.Key.FullName) && !FieldWrites.ContainsKey(k.Key))
                                             .OrderBy(k => k.Key.FullName))
                {
                    Console.WriteLine($"\n=== {kv.Key.FullName}");
                    Console.WriteLine("  WRITERS: (none found)");
                    Console.WriteLine("  readers:");
                    foreach (var r in kv.Value.OrderBy(m => m.FullName)) Console.WriteLine($"    {r.FullName}");
                }
                return 0;
            }
        }
        Console.Error.WriteLine($"unknown command '{cmd}'");
        return 2;
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types)
        {
            yield return t;
            foreach (var n in Nested(t)) yield return n;
        }
        static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
        {
            foreach (var n in t.NestedTypes)
            {
                yield return n;
                foreach (var d in Nested(n)) yield return d;
            }
        }
    }

    static string Short(TypeReference t) => t?.Name ?? "?";

    static void BuildGraph()
    {
        foreach (var m in AllMethods)
        {
            if (!m.HasBody) continue;
            MethodBody body;
            try { body = m.Body; } catch { continue; }
            foreach (var ins in body.Instructions)
            {
                if (ins.Operand is MethodReference mr &&
                    (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt ||
                     ins.OpCode.Code == Code.Newobj || ins.OpCode.Code == Code.Ldftn ||
                     ins.OpCode.Code == Code.Ldvirtftn))
                {
                    foreach (var target in Expand(mr, ins.OpCode.Code == Code.Callvirt || ins.OpCode.Code == Code.Ldvirtftn))
                        Link(m, target);
                }
                else if (ins.Operand is FieldReference fr)
                {
                    FieldDefinition fd;
                    try { fd = fr.Resolve(); } catch { continue; }
                    if (fd == null) continue;
                    var isWrite = ins.OpCode.Code == Code.Stfld || ins.OpCode.Code == Code.Stsfld;
                    var map = isWrite ? FieldWrites : FieldReads;
                    if (!map.TryGetValue(fd, out var set)) map[fd] = set = new();
                    set.Add(m);
                }
            }
        }
        Console.Error.WriteLine($"[ilscan] graph built: {Callees.Count} methods with outgoing edges");
    }

    static IEnumerable<MethodDefinition> Expand(MethodReference mr, bool virtualCall)
    {
        MethodDefinition declared = null;
        try { declared = mr.Resolve(); } catch { }
        if (declared != null) yield return declared;
        if (!virtualCall || declared == null) yield break;
        if (!declared.IsVirtual && !declared.DeclaringType.IsInterface) yield break;

        if (!ByNameArity.TryGetValue((declared.Name, declared.Parameters.Count), out var cands)) yield break;
        foreach (var c in cands)
        {
            if (c == declared) continue;
            if (DerivesFrom(c.DeclaringType, declared.DeclaringType)) yield return c;
        }
    }

    static readonly Dictionary<(TypeDefinition, TypeDefinition), bool> DerivesCache = new();

    static bool DerivesFrom(TypeDefinition sub, TypeDefinition base_)
    {
        if (sub == null || base_ == null) return false;
        if (sub == base_) return true;
        var key = (sub, base_);
        if (DerivesCache.TryGetValue(key, out var cached)) return cached;
        DerivesCache[key] = false;   // cycle guard, replaced below

        var result = false;
        try
        {
            if (sub.BaseType != null)
            {
                var bd = sub.BaseType.Resolve();
                if (bd != null && DerivesFrom(bd, base_)) result = true;
            }
            if (!result)
                foreach (var i in sub.Interfaces)
                {
                    var id = i.InterfaceType.Resolve();
                    if (id != null && DerivesFrom(id, base_)) { result = true; break; }
                }
        }
        catch { }
        DerivesCache[key] = result;
        return result;
    }

    static void Link(MethodDefinition from, MethodDefinition to)
    {
        if (!Callees.TryGetValue(from, out var ce)) Callees[from] = ce = new();
        ce.Add(to);
        if (!Callers.TryGetValue(to, out var cr)) Callers[to] = cr = new();
        cr.Add(from);
    }

    static void PrintCallers(MethodDefinition m, int maxDepth, int depth, HashSet<MethodDefinition> seen)
    {
        if (depth > maxDepth || !Callers.TryGetValue(m, out var cs)) return;
        foreach (var c in cs.OrderBy(x => x.FullName))
        {
            if (!seen.Add(c)) continue;
            Console.WriteLine($"{new string(' ', depth * 2)}<- {c.FullName}");
            PrintCallers(c, maxDepth, depth + 1, seen);
        }
    }

    static bool Bfs(MethodDefinition root, Regex target, int maxDepth)
    {
        var parent = new Dictionary<MethodDefinition, MethodDefinition>();
        var depth = new Dictionary<MethodDefinition, int> { [root] = 0 };
        var q = new Queue<MethodDefinition>();
        q.Enqueue(root);
        var found = false;
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur != root && target.IsMatch(cur.FullName))
            {
                Console.WriteLine($"PATH ({depth[cur]} hops):");
                var chain = new List<MethodDefinition>();
                for (var n = cur; n != null; n = parent.TryGetValue(n, out var p) ? p : null) chain.Add(n);
                chain.Reverse();
                for (var i = 0; i < chain.Count; i++)
                    Console.WriteLine($"  {new string(' ', i * 2)}{chain[i].FullName}");
                Console.WriteLine();
                found = true;
                continue;   // keep going: several distinct targets are usually interesting
            }
            if (depth[cur] >= maxDepth) continue;
            if (!Callees.TryGetValue(cur, out var next)) continue;
            foreach (var n in next)
            {
                if (depth.ContainsKey(n)) continue;
                depth[n] = depth[cur] + 1;
                parent[n] = cur;
                q.Enqueue(n);
            }
        }
        return found;
    }
}
