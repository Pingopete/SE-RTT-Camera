namespace RttProbe;

// THE RENDER THREAD MUST NOT TOUCH THE FILE SYSTEM. Not to write, and not to ask.
//
// Making the log asynchronous cut MAX frame from ~500 ms to ~300 ms and stopped there,
// because logging was only the loudest of our file calls. The rest are STATS, and a stat is
// not free when the disk is busy — it is a round trip to the same device that just took
// 178 ms to accept a 100-byte write. From the per-frame render path:
//
//   FeedGate.PollAll   -> File.Exists(PausePath)      EVERY FRAME, per feed, unthrottled
//   CameraRender       -> File.Exists(ArmPath/LivePath) in the per-frame pass
//   FeedHandover       -> File.Exists(ArmPath/LivePath)
//   BlitProbe          -> File.Exists(marker) every 2 s
//   FeedConfig.Poll    -> File.Exists + GetLastWriteTimeUtc every 2 s
//
// And the disk in question is the machine's SLOWEST: D: is a CT1000BX500, a DRAM-less SATA
// SSD, which also hosts SE2 and therefore its DirectStorage texture streaming. Our stats
// queue behind the game's own streaming reads.
//
// So the file system is polled HERE, on one background thread, and every caller reads a
// cached answer. The staleness that buys is bounded by PollMs and is irrelevant to every
// consumer: these are hand-edited marker files and a hand-edited config. Nobody creates
// `pause.marker` and needs it honoured within 16 ms; they need it honoured before they
// alt-tab back.
//
// Registration is implicit. A path is watched from the first time anyone asks about it,
// which means call sites do not have to be kept in sync with a list — the failure mode of
// a registry nobody remembers to update is a stat that silently goes stale forever.
internal static class FileWatch
{
    private const int PollMs = 500;

    private sealed class Entry
    {
        public volatile bool Exists;
        public long StampTicks;
        public volatile bool Primed;   // false until the poller has looked at least once
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> Paths = new();
    private static Thread _thread;
    private static readonly object Gate = new();

    private static Entry Track(string path)
    {
        var e = Paths.GetOrAdd(path, _ => new Entry());
        if (!e.Primed)
        {
            // FIRST ASK IS SYNCHRONOUS, once per path, and that is deliberate. Returning
            // "does not exist" for the first half-second would disarm a feed whose arm
            // marker is present, and an arming decision taken on a default is exactly the
            // kind of silent wrong answer this project keeps paying for. One stat at
            // startup is not the problem; one stat per frame forever is.
            Refresh(path, e);
            e.Primed = true;
        }
        Start();
        return e;
    }

    private static void Refresh(string path, Entry e)
    {
        try
        {
            var fi = new FileInfo(path);
            bool ex = fi.Exists;
            e.Exists = ex;
            Volatile.Write(ref e.StampTicks, ex ? fi.LastWriteTimeUtc.Ticks : 0L);
        }
        catch
        {
            // A path we cannot stat is reported as it last was, not as absent: a transient
            // sharing violation must not read as "the user deleted the marker".
        }
    }

    private static void Start()
    {
        if (_thread != null) return;
        lock (Gate)
        {
            if (_thread != null) return;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "RttFileWatch",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }
    }

    private static void Loop()
    {
        while (true)
        {
            try
            {
                foreach (var kv in Paths) Refresh(kv.Key, kv.Value);
            }
            catch { }
            try { Thread.Sleep(PollMs); } catch { return; }
        }
    }

    /// <summary>Cached File.Exists — at most PollMs stale, never touches the disk.</summary>
    internal static bool Exists(string path) => Track(path).Exists;

    /// <summary>Cached LastWriteTimeUtc.Ticks, or 0 when the file is absent.</summary>
    internal static long StampTicks(string path) => Volatile.Read(ref Track(path).StampTicks);

    /// <summary>
    /// Force a path up to date NOW. For the moment immediately after WE write a marker,
    /// where waiting up to PollMs to observe our own write would read as the write failing.
    /// </summary>
    internal static void Invalidate(string path)
    {
        if (Paths.TryGetValue(path, out var e)) Refresh(path, e);
    }

    internal static string Report() => $"FILE WATCH: {Paths.Count} path(s) polled every {PollMs} ms on a " +
                                       "background thread — the render thread never stats the disk.";
}
