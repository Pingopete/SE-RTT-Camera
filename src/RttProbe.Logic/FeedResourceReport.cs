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
        sb.AppendLine("Sizes are COMPUTED from each texture's resolution, format, mip count and array");
        sb.AppendLine("size — not sampled from the driver. They are what we asked the GPU for; actual");
        sb.AppendLine("residency can differ through pooling, aliasing and driver-side padding.");

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

    private const int MaxDepth = 4;
    private const int MaxNodes = 4000;
    private static int _nodes;

    private static void Walk(object node, string path, List<(string, long, string)> found,
                             HashSet<object> seen, int depth)
    {
        if (node == null || depth > MaxDepth) return;
        if (++_nodes > MaxNodes) return;
        var t = node.GetType();
        if (t.IsPrimitive || node is string || t.IsEnum) return;
        if (!t.IsValueType && !seen.Add(node)) return;

        // Is THIS a texture? Resolution + Format together is the signature — every bindable
        // texture in Render12 carries both, and nothing else does.
        if (TryTexture(node, out long bytes, out string detail))
        {
            found.Add((path, bytes, detail));
            return;                                  // do not descend into a texture
        }

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
