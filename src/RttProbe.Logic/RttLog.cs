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
            lock (Gate) File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
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
