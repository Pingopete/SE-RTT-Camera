namespace RttProbe;

internal static class RttLog
{
    public const string OutDir = @"D:\Projects\Space Engineers Stuff\RTT Camera\output";
    private static readonly object Gate = new();
    private static readonly string LogFile;
    private static readonly string PrevFile;

    // ONE OPEN HANDLE INSTEAD OF ONE PER LINE.
    //
    // Every line used to go through File.AppendAllText, which is create-FileStream ->
    // write -> flush -> dispose, EVERY TIME, under this same global lock, on the render
    // thread. At the rates this mod logs at — 110,000 lines in the last 20 MB of rtt.log,
    // and the file had reached 238 MB — that is a per-frame syscall storm sitting directly
    // in the path this project has spent weeks trying to make smooth.
    //
    // The crash-safety requirement the old comment stated is real and is KEPT: this spike
    // hunts failures that kill the process from the render thread, so a line that is still
    // sitting in a managed buffer when that happens is a line lost. AutoFlush = true pushes
    // every line out of the StreamWriter and through the FileStream on each write, so the
    // bytes are in the OS page cache before the call returns. A process crash cannot lose
    // them; only losing the machine could, and that is not the failure being hunted. What
    // goes away is the open/close pair, not the flush.
    //
    // HOT-RELOAD NOTE, because this project has been bitten by exactly this shape before
    // (see the VRAM ratchet): this static lives in the COLLECTIBLE logic assembly, so a
    // reload orphans the writer. Unlike a GPU resource, a FileStream has a finalizer that
    // closes its handle, so the orphan costs one handle until the next GC rather than
    // leaking forever — and FileShare.ReadWrite means a not-yet-finalized old handle can
    // never stop the new assembly opening the file.
    private static StreamWriter _writer;

    // Rotation, because nothing was ever capping this. 238 MB of rtt.log and 1.4 GB of
    // frame dumps in output/ is not a tidiness problem, it is a disk that fills during an
    // unattended soak. One roll, two files, bounded at 2x.
    private const long MaxBytes = 64L * 1024 * 1024;

    static RttLog()
    {
        Directory.CreateDirectory(OutDir);
        LogFile = Path.Combine(OutDir, "rtt.log");
        PrevFile = Path.Combine(OutDir, "rtt.prev.log");
    }

    // Caller holds Gate.
    private static StreamWriter Writer()
    {
        if (_writer != null)
        {
            // Rotate on size. Checked against the stream's own length rather than a File
            // stat so it costs nothing — the FileStream already knows where it is.
            try
            {
                if (_writer.BaseStream.Length >= MaxBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                    File.Delete(PrevFile);
                    File.Move(LogFile, PrevFile);
                }
            }
            catch { _writer = null; }   // rotation failed: fall through and reopen
        }

        if (_writer == null)
        {
            var fs = new FileStream(LogFile, FileMode.Append, FileAccess.Write,
                                    FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(fs) { AutoFlush = true };
        }
        return _writer;
    }

    // SELF-MEASUREMENT, because this is now a suspect.
    //
    // With the feed running the engine reports MAX render-thread 369-721 ms and MAX main
    // thread 267-971 ms in the same minute, while MAX GPU is only 37-166 ms, CLR stalls are
    // 10-20 ms, our camera-swap window never once exceeded 50 ms and our Draw submit holds
    // at 3.2 ms. Two threads stalling together for hundreds of milliseconds, with the GPU
    // idle and the GC innocent, is the signature of a SHARED LOCK — and this class has the
    // only global lock the mod owns, held across a synchronous file write, taken from the
    // render thread and the sim-pump thread alike.
    //
    // That is a hypothesis, not a finding, so it gets measured before anything is rewritten.
    // WaitMs is time spent BLOCKED on the gate (someone else was writing); WriteMs is time
    // spent holding it. If the hitch is here, MaxWaitMs lands in the hundreds.
    // WaitTicks/MaxWaitTicks are gone with the synchronous path: nobody blocks on the gate
    // any more, so a "time spent waiting" counter would only ever report zero and read as
    // evidence of health rather than of a removed mechanism.
    internal static long Writes, WriteTicks, MaxWriteTicks, Dropped;

    // AND IT WAS. Measured, over consecutive 15 s windows:
    //
    //   LOG COST: 191 line(s) — writing   1.9 ms total /   0.2 ms worst
    //   LOG COST:  80 line(s) — writing 417.1 ms total / 177.7 ms worst
    //   LOG COST:  72 line(s) — writing 457.1 ms total / 113.2 ms worst
    //   LOG COST:  70 line(s) — writing 290.9 ms total / 110.4 ms worst
    //
    // Bimodal, and the slow mode is a SINGLE WriteLine costing 110-178 ms. That is not CPU
    // work, it is the disk. At the time rtt.log and SE2 shared drive D: — a DRAM-less SATA
    // SSD — and the game streams its textures off its install drive through DirectStorage,
    // so our synchronous per-line flush queued behind the streamer and blocked whichever
    // thread called it. On the render thread that IS the hitch, and it was self-reinforcing:
    // the harder the streamer worked, the longer we blocked, the worse the frame.
    //
    // SE2 MOVED TO H: ON 2026-08-02, so the contention that motivated this is gone — but the
    // async writer stays. It was never really about which drive: a synchronous file write on
    // the render thread is a latency bet on the whole storage stack, and D: measured 0% idle
    // at queue depth 9 while SE2 lived there. Do not "simplify" this back.
    //
    // SCALE, so nobody re-opens this on a hunch: rtt.log grows ~1.7 KB/s. Measured against
    // SE2's 279 MB/s of DirectStorage traffic that is 0.0006% of the drive — our logging was
    // never a THROUGHPUT problem, only a LATENCY one, and only on the calling thread.
    //
    // So logging comes off the calling thread entirely. Emit enqueues and returns; one
    // background thread owns the file. A 178 ms disk stall now costs a background thread
    // 178 ms and the render thread nothing.
    //
    // WHAT THIS COSTS, stated plainly: the original design flushed every line synchronously
    // so that a render-thread death lost nothing. Now a crash can lose whatever is still
    // queued — typically well under a second of lines. That trade is made deliberately and
    // with evidence: every diagnosis tonight came from the GAME'S log and its deferred
    // assertion summary, not from the tail of ours, and a permanent per-frame stall is a
    // worse instrument than a slightly lossy one.
    //
    // The queue is BOUNDED. A disk stall must never turn into unbounded memory growth, and
    // it must never block the caller — so past the cap we drop and count, and the count is
    // reported so a silent gap can never be mistaken for a quiet period.
    private const int MaxQueued = 20000;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Queue = new();
    private static int _queued;
    private static Thread _pump;
    private static readonly ManualResetEventSlim Pending = new(false);

    private static void Emit(string line)
    {
        if (Volatile.Read(ref _queued) >= MaxQueued) { Interlocked.Increment(ref Dropped); return; }
        Interlocked.Increment(ref _queued);
        Queue.Enqueue(line);
        Pending.Set();
        StartPump();
    }

    private static void StartPump()
    {
        if (_pump != null) return;
        lock (Gate)
        {
            if (_pump != null) return;
            // Background + low priority: this must never compete with the render thread, and
            // it must never keep the process alive at shutdown.
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "RttLogWriter",
                Priority = ThreadPriority.BelowNormal,
            };
            _pump.Start();
        }
    }

    private static void PumpLoop()
    {
        while (true)
        {
            try
            {
                if (!Queue.TryDequeue(out var line))
                {
                    Pending.Reset();
                    if (!Queue.TryDequeue(out line)) { Pending.Wait(250); continue; }
                }
                Interlocked.Decrement(ref _queued);

                var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (Gate) Writer().WriteLine(line);
                var t2 = System.Diagnostics.Stopwatch.GetTimestamp();

                Interlocked.Increment(ref Writes);
                var write = t2 - t1;
                Interlocked.Add(ref WriteTicks, write);
                if (write > Interlocked.Read(ref MaxWriteTicks)) Interlocked.Exchange(ref MaxWriteTicks, write);
            }
            catch { /* a logger that can die takes the evidence with it */ }
        }
    }

    // Read-and-reset for the 15 s report. Returns milliseconds. The write figures now
    // describe the BACKGROUND thread, so they are a disk-health reading rather than a
    // frame-time one — the number that matters for hitching is `queued`, which says whether
    // the writer is keeping up, and `dropped`, which says it stopped trying.
    internal static (long n, double writeMs, double maxWriteMs, int queued, long dropped) TakeStats()
    {
        double f = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        var r = (Interlocked.Exchange(ref Writes, 0),
                 Interlocked.Exchange(ref WriteTicks, 0) / f,
                 Interlocked.Exchange(ref MaxWriteTicks, 0) / f,
                 Volatile.Read(ref _queued),
                 Interlocked.Exchange(ref Dropped, 0));
        return r;
    }

    public static void Line(string msg)
    {
        try
        {
            Emit($"[{DateTime.Now:HH:mm:ss.fff}]{FeedTag()} {msg}");
        }
        catch { }
    }

    // For statements about the WHOLE MOD rather than about a feed — the gate quiescing, a
    // hook registering, the boot banner. Deliberately does not consult Feeds.Cur.
    //
    // Not a micro-optimisation: reading the ambient from an unscoped path trips the C1b
    // unscoped-access detector, and that detector is only worth having while its silence
    // means something. Before this existed, every global line logged from outside a feed
    // scope left a false positive with a full stack trace in the log — training the eye to
    // skip exactly the entries that matter. A line that is not about a feed should not be
    // asking which feed it is.
    public static void Global(string msg)
    {
        try
        {
            Emit($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
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
