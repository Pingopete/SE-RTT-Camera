using System.Reflection;
using System.Text;

namespace RttProbe;

// WHAT DOES A FEED ACTUALLY COST? — computed, not measured.
//
// Two attempts to MEASURE this failed for the same reason, and the second one cost a device
// removal, so the method is worth recording as much as the result:
//
//   D3 (resolution sweep) and B1 (per-layer sweep) both differenced nvidia-smi readings
//   between a dormant and an active gate. That means subtracting two ~14 GB numbers to find
//   a ~50 MB one while the game streams voxels and textures underneath. The noise floor came
//   out around +-200 MiB — B1 reported that turning flares OFF *increased* VRAM by 135 MiB,
//   and that smaller cascades cost MORE than larger ones. Three physically impossible rows
//   out of five.
//
//   Worse, each sample needed a pause/resume, and a pause/resume is a full teardown and
//   rebuild of everything we own. The B1 sweep drove ten of those in three minutes and the
//   device was removed at the end of it. The measurement was destabilising the thing it
//   measured.
//
// These are OUR OWN allocations. Their sizes are not an empirical question — every texture
// carries its resolution, format, mip count and array size, and bytes = arithmetic. This
// walks the object graph and adds it up: exact, immune to streaming drift, and requiring
// NO gate cycles at all.
//
// TRIGGERED BY A MARKER FILE (output/resource-report.marker), deliberately not by a config
// knob: config changes that touch the rebuild signature cost a gate cycle, which is the
// exact thing being avoided. Drop the file, get one report on the next LCD tick, and the
// file is deleted so it fires once.
//
// STRICTLY READ-ONLY. It reads fields and properties and calls nothing. Every access is
// individually guarded, and the walk is bounded, because a diagnostic that can take the
// game down is worth less than no diagnostic.
internal static class FeedResourceReport
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly string MarkerPath = Path.Combine(RttLog.OutDir, "resource-report.marker");
    private static readonly string ReportPath = Path.Combine(RttLog.OutDir, "feed-resources.txt");

    private static long _lastCheck;

    // Called from the LCD tick. Cheap: one File.Exists every 2 s until the marker appears.
    internal static void MaybeRun()
    {
        long now = Clock.Ms;
        if (now - _lastCheck < 2000) return;
        _lastCheck = now;

        try
        {
            if (!File.Exists(MarkerPath)) return;
            File.Delete(MarkerPath);
        }
        catch { return; }

        try { Run(); }
        catch (Exception e) { RttLog.Error("feed resource report", e); }
    }

    private static void Run()
    {
        _nodes = 0;
        _unsized.Clear();
        var sb = new StringBuilder();
        sb.AppendLine($"FEED RESOURCE REPORT — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"feedCount={Feeds.Count}  " +
                      $"{FeedConfig.WholeSceneWidth}x{FeedConfig.WholeSceneHeight}  " +
                      $"ownShadows={FeedConfig.WholeSceneOwnShadows} " +
                      $"({FeedConfig.WholeSceneCascadeCount}x{FeedConfig.WholeSceneCascadeResolution})  " +
                      $"ownFlares={(FeedConfig.WholeSceneOwnFlares ? 1 : 0)}  " +
                      $"ownProbes={(FeedConfig.WholeSceneOwnProbes ? 1 : 0)}");
        sb.AppendLine();

        long grand = 0;
        for (int i = 0; i < Feeds.Count; i++)
        {
            var f = Feeds.At(i);
            sb.AppendLine($"--- FEED {f.Id} ---");
            long feedTotal = 0;

            feedTotal += Section(sb, "ScreenBuffers", f.OurScreenBuffers);
            feedTotal += Section(sb, "DrawContextManager", f.OurDrawContexts);
            feedTotal += Section(sb, "LDR ring", f.LdrRing);
            feedTotal += Section(sb, "offscreen target", f.Rt);
            feedTotal += Section(sb, "probe manager", f.OurProbes);

            sb.AppendLine($"  {"FEED TOTAL",-40} {Mib(feedTotal),12}");
            sb.AppendLine();
            grand += feedTotal;
        }

        sb.AppendLine($"{"ALL FEEDS",-42} {Mib(grand),12}");
        sb.AppendLine();

        // THE COVERAGE GAP, printed every time. A total that silently omits whole classes of
        // resource is worse than no total, because it invites exactly the confident-and-wrong
        // conclusion this report exists to replace.
        if (_unsized.Count > 0)
        {
            sb.AppendLine($"UNSIZED ({_unsized.Count} distinct resource-shaped TYPES the walk could not measure —");
            sb.AppendLine("the total above EXCLUDES these, so treat it as a lower bound):");
            var list = new List<string>(_unsized.Keys);
            list.Sort();
            for (int i = 0; i < list.Count && i < 30; i++)
                sb.AppendLine($"    {list[i],-40} e.g. {_unsized[list[i]]}");
            if (list.Count > 30) sb.AppendLine($"    ... and {list.Count - 30} more types");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("UNSIZED: none — every resource-shaped object reached was measured.");
            sb.AppendLine();
        }

        sb.AppendLine($"(walked {_nodes} nodes, cap {MaxNodes})");
        sb.AppendLine("Sizes are COMPUTED from each resource's own description — resolution, format, mip");
        sb.AppendLine("count, array size, or element count x stride — not sampled from the driver. They");
        sb.AppendLine("are what we asked the GPU for; actual residency can differ through pooling,");
        sb.AppendLine("aliasing and driver-side padding.");

        try { File.WriteAllText(ReportPath, sb.ToString()); } catch { }
        RttLog.Line(sb.ToString());
    }

    // One owned root: walk it, list every texture found, return the total.
    private static long Section(StringBuilder sb, string label, object root)
    {
        if (root == null) { sb.AppendLine($"  {label,-40} {"(none)",12}"); return 0; }

        var found = new List<(string Path, long Bytes, string Detail)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try { Walk(root, label, found, seen, 0); }
        catch { /* partial results are still worth having */ }

        long total = 0;
        foreach (var t in found) total += t.Bytes;

        sb.AppendLine($"  {label,-40} {Mib(total),12}   ({found.Count} textures)");

        // Biggest first — the point of the report is to find what dominates.
        found.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        for (int i = 0; i < found.Count && i < 12; i++)
            sb.AppendLine($"      {found[i].Path,-36} {Mib(found[i].Bytes),12}   {found[i].Detail}");
        if (found.Count > 12)
            sb.AppendLine($"      ... and {found.Count - 12} smaller");

        return total;
    }

    private const int MaxDepth = 5;
    private const int MaxNodes = 20000;
    private static int _nodes;

    // Resource-shaped objects the walk could not size. Distinct type names only — the point
    // is to learn WHICH types need a sizing rule, not to list every instance.
    private static readonly Dictionary<string, string> _unsized = new();

    private static void Walk(object node, string path, List<(string, long, string)> found,
                             HashSet<object> seen, int depth)
    {
        if (node == null || depth > MaxDepth) return;
        if (++_nodes > MaxNodes) return;
        var t = node.GetType();
        if (t.IsPrimitive || node is string || t.IsEnum) return;
        if (!t.IsValueType && !seen.Add(node)) return;

        // THE UNDERLYING D3D ALLOCATION WINS, and this is a correction to the first version.
        //
        // A Render12 texture owns a `_d3dResourceWrap` (the actual allocation, carrying
        // SizeInBytes) AND a set of `_slices` / typed views INTO that same memory. Counting
        // both double-counts every texture in the report: character shadows appeared as
        // three 16 MiB slices PLUS a 16 MiB wrap, and the LDR ring as a 1.4 MiB wrap
        // alongside its own mip slices.
        //
        // The wrap is authoritative — it is the real allocation including driver padding,
        // where summing views is an estimate that also happens to be wrong. So: if this node
        // owns a wrap, count the wrap and DO NOT DESCEND, which drops the views with it.
        if (TryResourceWrap(node, out long wrapBytes, out string wrapDetail))
        {
            found.Add((path, wrapBytes, wrapDetail));
            return;
        }

        // Is THIS a texture? Resolution + Format together is the signature — every bindable
        // texture in Render12 carries both, and nothing else does.
        if (TryTexture(node, out long bytes, out string detail))
        {
            found.Add((path, bytes, detail));
            return;                                  // do not descend into a texture
        }

        // Is THIS a buffer? Visibility lists, occlusion contexts and geometry buffers are
        // structured buffers, not textures — they carry no Resolution or Format, so the
        // first version of this report could not see them at all. That matters: the code
        // describes the geometry buffers as ranged to the WHOLE SCENE, and the engine's own
        // accounting put a second feed at ~1.5 GB against 175 MiB of measured textures.
        // Whatever closes that 8x gap is most likely here.
        if (TryBuffer(node, out bytes, out detail))
        {
            found.Add((path, bytes, detail));
            return;
        }

        // Buffer-shaped but unsized: report it rather than skip it silently. The member
        // names are unknown ahead of time, so the report itself is how they get learned —
        // same principle as the unscoped-access detector, and much cheaper than guessing
        // at them offline and redeploying until something sticks.
        // Keyed on the TYPE NAME, not the path: the point is to learn which TYPES need a
        // sizing rule. The first version keyed on type+path and reported "3976 distinct
        // types", which was really one AutoResourceState per resource — a number that told
        // me nothing except that I had written the key wrong.
        if (LooksLikeResource(t) && depth > 0 && !_unsized.ContainsKey(t.Name))
            _unsized[t.Name] = path;

        // Collections: the GBuffer array, the cascade set, the LDR ring.
        if (node is System.Collections.IEnumerable seq && node is not string)
        {
            int i = 0;
            try
            {
                foreach (var item in seq)
                {
                    if (item == null) continue;
                    Walk(item, $"{path}[{i}]", found, seen, depth + 1);
                    if (++i > 64) break;             // cascade sets and GBuffers are small
                }
            }
            catch { }
            return;
        }

        // FIELDS FIRST, and properties only where a field did not already cover it.
        // A property getter can allocate, lazily initialise, or touch the GPU; a field read
        // cannot. This is a diagnostic running on the game thread while a feed is live.
        foreach (var f in SafeFields(t))
        {
            object v;
            try { v = f.GetValue(node); } catch { continue; }
            if (v == null) continue;
            Walk(v, $"{path}.{Clean(f.Name)}", found, seen, depth + 1);
        }
    }

    private static IEnumerable<FieldInfo> SafeFields(Type t)
    {
        FieldInfo[] fields;
        try { fields = t.GetFields(Any); } catch { yield break; }
        foreach (var f in fields)
        {
            if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) continue;
            if (f.FieldType == typeof(string)) continue;
            yield return f;
        }
    }

    // Backing fields come through as "<Name>k__BackingField".
    private static string Clean(string n)
    {
        int a = n.IndexOf('<'), b = n.IndexOf('>');
        return (a == 0 && b > 1) ? n.Substring(1, b - 1) : n;
    }

    // Type names that mean "this owns GPU memory", used only to decide whether an object we
    // could NOT size is worth reporting as a gap.
    private static bool LooksLikeResource(Type t)
    {
        string n = t.Name;
        return n.IndexOf("Buffer", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("Resource", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Does this node own the underlying D3D allocation directly? Looks one level down for a
    // wrapper field carrying an explicit byte size — that is the real allocation, and every
    // view hanging off the same node is a duplicate of it.
    private static bool TryResourceWrap(object o, out long bytes, out string detail)
    {
        bytes = 0; detail = null;
        var t = o.GetType();
        if (t.IsValueType || t.IsPrimitive) return false;

        FieldInfo[] fields;
        try { fields = t.GetFields(Any); } catch { return false; }

        foreach (var f in fields)
        {
            if (f.Name.IndexOf("d3dResource", StringComparison.OrdinalIgnoreCase) < 0) continue;
            object w;
            try { w = f.GetValue(o); } catch { continue; }
            if (w == null) continue;

            var size = AsLong(Member(w, w.GetType(), "SizeInBytes"))
                    ?? AsLong(Member(w, w.GetType(), "ByteSize"));
            if (size is > 0)
            {
                bytes = size.Value;
                // Keep the shape in the detail line — a bare byte count is unreadable, and
                // the resolution is what makes "this is character shadows at 2048" obvious.
                string shape = "";
                var res = Member(o, t, "Resolution");
                if (res != null && Vec2(res, out int w2, out int h2) && w2 > 0)
                    shape = $" {w2}x{h2} {Member(o, t, "Format")}";
                detail = $"d3d alloc{shape}";
                return true;
            }
        }
        return false;
    }

    // Buffers, sized by whatever the type actually exposes. Tried in order of directness:
    // an explicit byte size beats a count x stride, which beats nothing.
    private static bool TryBuffer(object o, out long bytes, out string detail)
    {
        bytes = 0; detail = null;
        var t = o.GetType();
        if (!LooksLikeResource(t)) return false;

        // A texture already handled above would also match some of these names.
        if (Member(o, t, "Resolution") != null) return false;

        foreach (var name in new[] { "SizeInBytes", "ByteWidth", "TotalSizeInBytes",
                                     "ByteSize", "TotalBytes", "BufferSize" })
        {
            var v = AsLong(Member(o, t, name));
            if (v is > 0)
            {
                bytes = v.Value;
                detail = $"{name}={v.Value}";
                return true;
            }
        }

        long? count = AsLong(Member(o, t, "ElementCount")) ?? AsLong(Member(o, t, "Count"))
                   ?? AsLong(Member(o, t, "Capacity")) ?? AsLong(Member(o, t, "NumElements"));
        long? stride = AsLong(Member(o, t, "Stride")) ?? AsLong(Member(o, t, "ElementSize"))
                    ?? AsLong(Member(o, t, "StructureByteStride")) ?? AsLong(Member(o, t, "ElementStride"));

        if (count is > 0 && stride is > 0)
        {
            bytes = count.Value * stride.Value;
            detail = $"{count.Value} x {stride.Value}B";
            return true;
        }
        return false;
    }

    private static long? AsLong(object o)
    {
        if (o == null) return null;
        try { return Convert.ToInt64(o); } catch { return null; }
    }

    private static bool TryTexture(object o, out long bytes, out string detail)
    {
        bytes = 0; detail = null;
        var t = o.GetType();

        object res = Member(o, t, "Resolution");
        object fmt = Member(o, t, "Format");
        if (res == null || fmt == null) return false;

        if (!Vec2(res, out int w, out int h) || w <= 0 || h <= 0) return false;

        string fname = fmt.ToString();
        double bpp = BytesPerPixel(fname);
        if (bpp <= 0) return false;

        int mips = AsInt(Member(o, t, "MipLevels")) ?? AsInt(Member(o, t, "NumMipLevels")) ?? 1;
        if (mips < 1) mips = 1;
        int slices = AsInt(Member(o, t, "ArraySize")) ?? AsInt(Member(o, t, "Depth")) ?? 1;
        if (slices < 1) slices = 1;
        // A cube texture is six faces; the type name is the only reliable tell.
        if (t.Name.IndexOf("Cube", StringComparison.OrdinalIgnoreCase) >= 0) slices *= 6;

        // Exact mip chain rather than the 4/3 approximation — at 1024 with 10 levels the
        // difference is small, but this costs nothing and the point of the report is that
        // its numbers can be trusted.
        double pixels = 0;
        for (int m = 0; m < mips; m++)
        {
            int mw = Math.Max(1, w >> m), mh = Math.Max(1, h >> m);
            pixels += (double)mw * mh;
        }

        bytes = (long)(pixels * bpp * slices);
        detail = $"{w}x{h} {fname} mips={mips}" + (slices > 1 ? $" x{slices}" : "");
        return true;
    }

    // Bits per pixel straight from the format NAME, by summing its channel widths:
    // R8G8B8A8 -> 32, R11G11B10 -> 32, R16G16B16A16 -> 64, D24_UNorm_S8_UInt -> 32.
    // Generic by construction, so a format this project has never seen still resolves,
    // rather than needing a lookup table that silently returns zero for anything new.
    private static double BytesPerPixel(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        // Block-compressed formats do not follow the channel-width pattern.
        if (name.StartsWith("BC", StringComparison.OrdinalIgnoreCase))
        {
            char k = name.Length > 2 ? name[2] : '0';
            return (k == '1' || k == '4') ? 0.5 : 1.0;
        }

        int bits = 0;
        for (int i = 0; i < name.Length; i++)
        {
            char c = char.ToUpperInvariant(name[i]);
            if (c != 'R' && c != 'G' && c != 'B' && c != 'A' && c != 'D' && c != 'S' && c != 'X')
                continue;
            int j = i + 1, v = 0, digits = 0;
            while (j < name.Length && char.IsDigit(name[j])) { v = v * 10 + (name[j] - '0'); j++; digits++; }
            if (digits > 0) { bits += v; i = j - 1; }
        }
        return bits > 0 ? bits / 8.0 : 0;
    }

    private static object Member(object o, Type t, string name)
    {
        try
        {
            var f = t.GetField(name, Any);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, Any);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(o);
        }
        catch { }
        return null;
    }

    private static bool Vec2(object v, out int x, out int y)
    {
        x = y = 0;
        try
        {
            var t = v.GetType();
            var fx = t.GetField("X", Any); var fy = t.GetField("Y", Any);
            if (fx == null || fy == null) return false;
            x = Convert.ToInt32(fx.GetValue(v));
            y = Convert.ToInt32(fy.GetValue(v));
            return true;
        }
        catch { return false; }
    }

    private static int? AsInt(object o)
    {
        if (o == null) return null;
        try { return Convert.ToInt32(o); } catch { return null; }
    }

    private static string Mib(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MiB";
}
