namespace RttProbe;

internal static class RttLog
{
    public const string OutDir = @"D:\Projects\Space Engineers Stuff\RTT Camera\output";
    private static readonly object Gate = new();
    private static readonly string LogFile;

    static RttLog()
    {
        Directory.CreateDirectory(OutDir);
        LogFile = Path.Combine(OutDir, "rtt.log");
    }

    // Every line is appended and flushed immediately. The failure this spike is
    // hunting kills the process from the render thread, so anything buffered at
    // the moment of the crash is exactly the information we lose.
    public static void Line(string msg)
    {
        try
        {
            lock (Gate) File.AppendAllText(LogFile,
                $"[{DateTime.Now:HH:mm:ss.fff}]{FeedTag()} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // WHICH FEED IS TALKING (phase C3).
    //
    // Silent with one feed, so every existing line and every log-reading habit is unchanged.
    // With two, it is the difference between reading the log and guessing at it: the gate,
    // rebuild and handover lines all describe per-feed work and none of them said whose. The
    // first two-feed run produced two "FEED GATE: ACTIVE" lines 22 ms apart and there was no
    // way to tell whether that was both feeds starting or one feed cycling twice.
    //
    // Never throws and never recurses: a logger that can fail is a logger that loses the
    // evidence of the thing that made it fail, and this runs on the render thread.
    private static string FeedTag()
    {
        try
        {
            if (Feeds.Count <= 1) return "";
            return $" [feed {Feeds.Cur.Id}]";
        }
        catch { return ""; }
    }

    // Reflection wraps everything in TargetInvocationException, whose own message
    // says nothing. Unwrap to the exception that actually happened.
    public static void Error(string context, Exception e)
    {
        var inner = e;
        while (inner is System.Reflection.TargetInvocationException && inner.InnerException != null)
            inner = inner.InnerException;
        Line($"ERROR {context}: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
    }
}
