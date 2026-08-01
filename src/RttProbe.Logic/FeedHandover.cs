using System.Reflection;
using System.Text;

namespace RttProbe;

// Handing the camera frame to the panel.
//
// Two earlier routes died, both for timing/type reasons rather than because the
// pixels were wrong:
//   * DrawImage with the render target's handle — GetTexture asserts IsGuid(),
//     and an OffscreenRenderTarget handle is a generated RenderId handle. Fatal.
//   * CopyResource from the probe pass — the right call at the wrong moment. The
//     panel's texture is bound as a shader resource there, so writing it is a
//     resource-state fault and D3D12 answers with device removal.
//
// UIStage.OffscreenUIRenderer.DrawOne is where the engine itself copies into these
// targets, so at that instant the resource is provably in a writable state. This
// rides that moment: the camera pass parks a converted frame, and the copy happens
// in DrawOne's postfix.
internal static class FeedHandover
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string ArmPath = Path.Combine(RttLog.OutDir, "handover-armed.marker");
    private static readonly string LivePath = Path.Combine(RttLog.OutDir, "handover-live.marker");

    // The converted (LDR, panel-format) frame waiting to be handed over, plus the
    // pool borrow that owns it. Parked by the camera pass, consumed in the UI stage.
    // PER-FEED (phase C1a). One parked frame PER FEED: with a single slot, feed B's
    // park would displace feed A's before the UI stage consumed it, and the symptom
    // would be a panel showing the other camera's picture for a frame. The backing
    // fields stay volatile — parked on the camera pass, read in the UI stage.
    private static object _pendingFrame                // Borrowed<T>
    { get => Feeds.Cur.PendingFrame; set => Feeds.Cur.PendingFrame = value; }
    private static object _pendingResource             // the texture itself
    { get => Feeds.Cur.PendingResource; set => Feeds.Cur.PendingResource = value; }
    private static string _panelHandleText
    { get => Feeds.Cur.PanelHandleText; set => Feeds.Cur.PanelHandleText = value; }

    // PER-FEED: whether THIS feed's frames have reached THIS feed's panel. See the
    // FeedInstance comment — as global latches these made a dead feed and a live feed
    // produce identical logs.
    private static bool _argsLogged
    { get => Feeds.Cur.HandoverArgsLogged; set => Feeds.Cur.HandoverArgsLogged = value; }
    private static int _handovers
    { get => Feeds.Cur.Handovers; set => Feeds.Cur.Handovers = value; }
    private static bool _survivedLogged
    { get => Feeds.Cur.HandoverSurvivedLogged; set => Feeds.Cur.HandoverSurvivedLogged = value; }

    // Process-global: the arm/disarm markers are files that gate the whole route.
    private static bool _disarmed, _armed;
    private static long _lastArmCheck;
    private static int _errLogs;

    public static void Reset()
    {
        _pendingFrame = null;
        _pendingResource = null;
        _panelHandleText = null;
        _argsLogged = _survivedLogged = false;
        _handovers = _errLogs = _parkGeneration = _copiesInterval = 0;
        _lastArmCheck = 0;
        _lastRequestRender = 0;
        _srcTransitionOff = _dstTransitionOff = false;
        _transitionState = 0;
        _autoStateDiag.Clear();
        _pendingListField = null; _pendingListDiag = false;
        _diagged.Clear(); _diagCount = _skipDisabled = 0;
        _miCopy = null; _copyArgs = null;

        // Against the PROCESS, not this assembly — a hot reload is not a death. Same fix and
        // same reasoning as CameraRender's latch, which disabled a perfectly healthy camera
        // pass on 2026-08-01 because the previous logic instance was swapped mid-pass.
        if (File.Exists(LivePath) && !CameraRender.WrittenByThisProcess(LivePath))
        {
            _disarmed = true;
            RttLog.Line("!!! PREVIOUS SESSION DIED DURING HANDOVER — disabled. Delete " + LivePath + " to retry.");
        }
        else if (File.Exists(LivePath))
        {
            RttLog.Line("Handover: mid-copy marker present but written by THIS process — a hot " +
                        "reload landing mid-copy, not a death. Continuing, and clearing it.");
            try { File.Delete(LivePath); } catch { }
        }
        _armed = !_disarmed && File.Exists(ArmPath);
        RttLog.Line(_armed ? "Handover ARMED." : $"Handover not armed (observation only). Create {ArmPath} to arm.");
    }

    // DrawOne only runs for targets sitting in OffscreenTargetManager's pending
    // render list, and RequestRender is what puts them there — that is why the
    // handover never fired: the panel's target was never queued.
    private static object _otm;
    private static MethodInfo _miRequestRender;
    private static ConstructorInfo _genHandleCtor;
    private static bool _requestDiag;

    // PER-FEED (the user caught this one). The throttle is per-TARGET — each feed owns its
    // own offscreen target and must be able to request it every window. As a process-global
    // it admitted ONE request per window across the mod, and with both feeds' cadences
    // locked to the engine frame clock the same feed won every window until a hitch shifted
    // the phase: one panel live, one frozen on its last frame, swapping in minute-long
    // stretches. The counters half-hid it because the RATE counters here are process-wide
    // aggregates printed under whichever feed's tag fired the log — only the per-feed
    // cumulative `copies=` told the truth (feed 0: park#2158, copies 0 for 63 s).
    //
    // Same defect, same fix as FeedGate._lastPollMs on the first two-feed run: the throttle
    // covers the shared thing (there, a file stat; here, nothing — the targets are
    // disjoint), so it splits per feed.
    private static long _lastRequestRender
    { get => Feeds.Cur.LastRequestRender; set => Feeds.Cur.LastRequestRender = value; }

    public static void RequestPanelRender(object panelRenderTarget)
    {
        // Not gated on Armed: requesting a render for our own target is how its
        // submitted batches get drawn at all. Only the COPY is dangerous enough to arm.
        if (panelRenderTarget == null) return;
        try
        {
            var now = Clock.Ms;
            // Separate from the camera rate on purpose: RequestRender makes the ENGINE
            // run a whole DrawOne for our target, so its cost is nothing like the
            // camera pass's and the two must be tunable independently.
            if (now - _lastRequestRender < FeedConfig.EffectivePanelMs) return;
            _lastRequestRender = now;
            _requestCount++;

            EnsureManager();
            if (_otm == null || _miRequestRender == null || _genHandleCtor == null) return;

            // Only request a render for a target the manager still knows about. LCD
            // targets are evicted by distance, so a handle cached a moment ago may
            // already have been returned to the pool — asking the engine to render a
            // dead handle is asking for trouble.
            var id = panelRenderTarget.GetType().GetProperty("Id", Any)?.GetValue(panelRenderTarget);
            if (id == null || !IsRegistered(id.ToString())) return;

            _miRequestRender.Invoke(_otm, new[] { _genHandleCtor.Invoke(new[] { id }) });
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("request panel render", e); }
    }

    // Is this target still registered with the manager, i.e. still alive? The LCD
    // system unregisters targets it evicts.
    private static FieldInfo _regField;

    private static void EnsureManager()
    {
        if (_otm != null) return;
        try
        {
            var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var otmType = cs?.Assembly.GetTypes().FirstOrDefault(t => t.Name == "OffscreenTargetManager");
            if (otmType == null) return;

            _otm = cs.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => otmType.IsAssignableFrom(f.FieldType))
                .Select(f => { try { return f.GetValue(null); } catch { return null; } })
                .FirstOrDefault(v => v != null);

            _miRequestRender = otmType.GetMethod("RequestRender", Any);
            var ghType = Type.GetType("Keen.VRage.Library.Utils.GeneratedResourceHandle, VRage.Library");
            _genHandleCtor = ghType?.GetConstructors(Any).FirstOrDefault(c => c.GetParameters().Length == 1);

            if (!_requestDiag)
            {
                _requestDiag = true;
                RttLog.Line($"Handover: OffscreenTargetManager={(_otm == null ? "NOT FOUND" : "ok")} " +
                            $"RequestRender={(_miRequestRender == null ? "NOT FOUND" : "ok")} " +
                            $"GeneratedResourceHandle ctor={(_genHandleCtor == null ? "NOT FOUND" : "ok")}");
            }
        }
        catch (Exception e) { RttLog.Error("resolve offscreen target manager", e); }
    }

    // Is the panel's target still alive? This must be callable from the camera pass:
    // the LCD tick hook stops firing when the panel goes out of range, so anything
    // that relies on it will never notice the eviction — which is exactly how a dead
    // target kept being used.
    public static bool IsPanelTargetAlive(object panelRenderTarget)
    {
        if (panelRenderTarget == null) return false;

        // Resolve the manager here rather than relying on RequestPanelRender to have
        // done it. That ordering deadlocked: a null manager reported "not alive",
        // which paused the feed, which stopped RequestPanelRender running, which meant
        // the manager was never resolved.
        EnsureManager();
        if (_otm == null) return false;

        var id = panelRenderTarget.GetType().GetProperty("Id", Any)?.GetValue(panelRenderTarget);
        return id != null && IsRegistered(id.ToString());
    }

    private static bool IsRegistered(string idText)
    {
        try
        {
            if (string.IsNullOrEmpty(idText) || _otm == null) return false;
            _regField ??= _otm.GetType().GetField("_registeredTextures", Any);
            if (_regField?.GetValue(_otm) is not System.Collections.IDictionary dict) return false;
            foreach (System.Collections.DictionaryEntry kv in dict)
                if (kv.Key?.ToString()?.Contains(idText) == true) return true;
            return false;
        }
        catch { return false; }
    }

    // Which offscreen target belongs to the [RTC] panel. Compared as text because
    // the registry keys are GeneratedResourceHandle while targets carry a RenderId.
    public static void SetPanelTarget(object renderId)
    {
        var s = renderId?.ToString();
        if (!string.IsNullOrEmpty(s) && s != _panelHandleText)
        {
            _panelHandleText = s;
            RttLog.Line($"Handover: watching for offscreen target {s}.");
        }
    }

    // The texture the UI stage is entitled to read, parked by the camera pass.
    //
    // It stays parked until a different one replaces it, and DrawOne copies it on
    // EVERY servicing rather than consuming it once. Both halves of that matter:
    //
    //   * copying every servicing keeps the panel showing the last frame instead of
    //     whatever DrawOne left in the target, which is what made the image flick
    //     back to the test pattern between camera frames;
    //   * never clearing it means the camera pass can tell, with certainty, which
    //     texture is being read.
    //
    // The consume-handshake this replaces was the race that killed the feed a few
    // frames in: it cleared the in-flight flag on the FIRST copy, after which the
    // camera pass treated the buffer as free while later DrawOne calls were still
    // copying out of it. The producer now simply never writes the parked slot — see
    // the ring in CameraRender.CopyToFeed.
    public static object ParkedResource => _pendingResource;

    // PER-FEED: bumped when this feed's parked slot is replaced, so a late consumer can
    // tell "still the frame I was promised" from "a newer one landed".
    private static int _parkGeneration
    { get => Feeds.Cur.ParkGeneration; set => Feeds.Cur.ParkGeneration = value; }

    public static void ParkFrame(object borrowed, object resource)
    {
        if (resource == null || ReferenceEquals(_pendingResource, resource)) return;
        _pendingFrame = borrowed;
        _pendingResource = resource;
        _parkGeneration++;
    }

    // Un-park a specific resource, for owners about to dispose it. The whole-scene
    // route parks its FinalLDRTexture ONCE (ParkFrame dedupes by reference, and the UI
    // stage re-copies the texture's latest contents every servicing) — so on a hot
    // reload the parked pointer would outlive the texture unless cleared first, and the
    // UI stage would copy from a disposed resource.
    public static void ClearParkedIf(object resource)
    {
        if (resource != null && ReferenceEquals(_pendingResource, resource))
        {
            _pendingResource = null;
            _pendingFrame = null;
        }
    }

    public static object TakeStaleFrame()
    {
        // The previous frame was never consumed — hand it back for returning so the
        // pool does not leak when the UI stage is not running.
        var b = _pendingFrame;
        _pendingFrame = null;
        _pendingResource = null;
        return b;
    }

    public static bool Armed
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - _lastArmCheck >= 2000)
            {
                _lastArmCheck = now;

                // Deleting the crash marker is an explicit human decision to retry, so
                // honour it live. Latching until restart made a bisect silently test
                // nothing and report a false pass.
                if (_disarmed && !File.Exists(LivePath))
                {
                    _disarmed = false;
                    RttLog.Line("Handover re-enabled — crash marker cleared.");
                }

                bool a = !_disarmed && File.Exists(ArmPath);
                if (a != _armed) { _armed = a; RttLog.Line(_armed ? "Handover ARMED (marker appeared)." : "Handover DISARMED."); }
            }
            return _armed;
        }
    }

    // ------------------------------------------------------------- the UI stage
    // Rate telemetry. The crash is frequency-dependent, so the useful question is
    // which side is actually running hot: our requests, the engine's DrawOne for our
    // target, DrawOne for everyone else's, or our copies.
    private static int _requestCount, _drawOneOurs, _drawOneOther, _skipNoFrame, _skipNotAlive, _copiesInterval;
    private static long _lastRateLog;

    // "Ground to a halt then crashed" is a leak, not a race, and none of the rate
    // counters can tell the two apart. These can: if the managed heap or the
    // manager's pending-render list climbs monotonically, that IS the halt, and the
    // line that stops being written names the moment it became fatal.
    private static FieldInfo _pendingListField;
    private static bool _pendingListDiag;

    private static int PendingRenderCount()
    {
        try
        {
            if (_otm == null) return -1;
            if (_pendingListField == null)
            {
                foreach (var f in _otm.GetType().GetFields(Any))
                {
                    if (!f.Name.Contains("ending", StringComparison.Ordinal)) continue;   // _pendingRenderList
                    if (f.GetValue(_otm) is System.Collections.ICollection) { _pendingListField = f; break; }
                }
                if (!_pendingListDiag)
                {
                    _pendingListDiag = true;
                    RttLog.Line($"Health: pending render list = {(_pendingListField?.Name ?? "NOT FOUND")}");
                }
                if (_pendingListField == null) return -1;
            }
            return (_pendingListField.GetValue(_otm) as System.Collections.ICollection)?.Count ?? -1;
        }
        catch { return -1; }
    }

    private static void LogRates()
    {
        var now = Environment.TickCount64;
        if (_lastRateLog == 0) { _lastRateLog = now; return; }
        if (now - _lastRateLog < 500) return;   // fast: crashes have come inside 1s

        double secs = (now - _lastRateLog) / 1000.0;
        // "all:" prefixes the PROCESS-WIDE counters, "this:" the per-feed ones. They used
        // to print undifferentiated under whichever feed's tag fired the log, which made a
        // feed that was copying NOTHING look healthy — the aggregate rate (the other
        // feed's traffic) sat right next to its own park counter. That misattribution is
        // what let the alternating-delivery bug survive its first minute of diagnosis.
        RttLog.Line($"Rates/s all: request={_requestCount / secs:F1} drawOne(ours)={_drawOneOurs / secs:F1} " +
                    $"drawOne(other)={_drawOneOther / secs:F1} copies={_copiesInterval / secs:F1} " +
                    $"skip(noFrame)={_skipNoFrame / secs:F1} skip(off)={_skipDisabled / secs:F1} " +
                    $"heap={GC.GetTotalMemory(false) >> 20}MB pending={PendingRenderCount()} " +
                    $"| this: park#{_parkGeneration} copies={_handovers}");

        _lastRateLog = now;
        _requestCount = _drawOneOurs = _drawOneOther = _skipNoFrame = _skipNotAlive = _copiesInterval = _skipDisabled = 0;
    }

    public static void OnOffscreenUiDraw(object[] args)
    {
        if (args == null || args.Length == 0) return;

        // Allocation attribution for the GC-spike hunt — the UI-stage half. See
        // Perf.NoteUiAlloc; the whole-scene hook carries the other counter.
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            // TARGET-DRIVEN (phase C1b, resolved for real in C3). The engine names the offscreen
            // target it is drawing, so the feed is whoever parked a frame for it. The component
            // is picked out of the args here rather than below, because the scope has to be open
            // before ANY per-feed state is touched — including the _panelHandleText the
            // ownership test itself reads.
            using (Feeds.Enter(Feeds.ForTarget(TargetComponentOf(args))))
                OnOffscreenUiDrawScoped(args);
        }
        finally { Perf.NoteUiAlloc(GC.GetAllocatedBytesForCurrentThread() - alloc0); }
    }

    private static object TargetComponentOf(object[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a != null && a.GetType().Name.Contains("OffscreenRenderTargetComponent")) return a;
        }
        return null;
    }

    private static void OnOffscreenUiDrawScoped(object[] args)
    {
        if (!FeedGate.Active) return;           // nothing parked, nothing to deliver
        try
        {
            LogRates();
            // One-shot: what does DrawOne actually receive? Needed to find the
            // command list and the target component without guessing.
            if (!_argsLogged)
            {
                _argsLogged = true;
                var sb = new StringBuilder("Handover: OffscreenUIRenderer.DrawOne args:");
                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    sb.Append($"\n  [{i}] {(a == null ? "null" : a.GetType().Name)}");
                    if (a != null)
                    {
                        var h = Prop(a, "Handle"); var r = Prop(a, "Resolution"); var f = Prop(a, "Format");
                        if (h != null || r != null) sb.Append($"  handle={h} res={r} format={f}");
                    }
                }
                RttLog.Line(sb.ToString());
            }

            // Find the target component and the command list among the arguments.
            object component = null, commandList = null;
            foreach (var a in args)
            {
                if (a == null) continue;
                var n = a.GetType().Name;
                if (n.Contains("OffscreenRenderTargetComponent")) component = a;
                else if (n.Contains("CommandList")) commandList = a;
            }
            if (component == null || commandList == null) return;

            // Classify BEFORE any early-out. Deciding "no frame parked" first made
            // drawOne(ours) read 0.0 for a whole session while the engine was in fact
            // servicing our target several times a second — the telemetry hid the one
            // number that mattered.
            var handle = Prop(component, "Handle")?.ToString();
            bool ours = _panelHandleText != null && handle != null && handle.Contains(_panelHandleText);
            if (!ours)
            {
                _drawOneOther++;
                // The engine's own offscreen targets are the control group: a copy into
                // one of those ran 337 times without incident. Dump the first few so
                // ours can be diffed against a resource that provably accepts the copy.
                DumpCopyDiag(component, Prop(component, "Texture"), null);
                return;
            }
            _drawOneOurs++;

            // Copied on EVERY servicing, not consumed once: DrawOne clears the target
            // before drawing, so skipping the copy leaves the panel showing whatever
            // the UI stage put there. That alternation is what looked like "back to the
            // test pattern" between camera frames.
            var frame = _pendingResource;
            if (frame == null || !Armed) { _skipNoFrame++; return; }

            var dest = Prop(component, "Texture");
            if (dest == null) return;

            // Describe before copying, and — when copyMode=0 — describe INSTEAD of
            // copying. Three launches have now died on copy #1 into a target we
            // created ourselves, while hundreds of copies into the LCD system's own
            // target ran clean. That difference is a property of the two resources,
            // and no amount of reasoning about barriers is going to reveal which one.
            // So: dump both, survive, and read the file with the game still running.
            DumpCopyDiag(component, dest, frame);
            if (!FeedConfig.CopyEnabled) { _skipDisabled++; return; }

            var mi = ResolveCopy(commandList);
            if (mi == null)
            {
                if (_errLogs++ < 2) RttLog.Line("Handover: no usable copy method on the UI command list.");
                return;
            }

            if (_handovers == 0)
            {
                try { File.WriteAllText(LivePath, $"handover entered {DateTime.Now:O}\n"); } catch { }
                RttLog.Line("=== HANDOVER: copying camera frame into the panel target from the UI stage. ===");
            }

            // Forensics: overwrite a snapshot of exactly what is about to be copied.
            // The crash happens on the GPU after this returns, so the log line that
            // matters is the one describing the *attempt*, not any aftermath.
            Snapshot(frame, dest, component);

            // The source was written by the camera pass in a DIFFERENT command list,
            // so its resource state is whatever that list left it in — not the
            // COPY_SOURCE this copy requires. The engine never hits this because
            // DrawOne copies from a texture it wrote in this same list. Force it.
            //
            // The destination is deliberately NOT transitioned by default. Reasoning
            // that the barrier was "unbalanced" and adding CopyDest killed the game on
            // the very first copy, half a second in — so the engine's AutoResourceState
            // tracker transitions the CopyResource destination itself, and forcing a
            // state on top of that desynchronises it. The source barrier is a different
            // case: that texture was written in ANOTHER command list, which the tracker
            // has no visibility into. Both are switchable from feed-config.txt.
            if (FeedConfig.SrcTransition) TransitionForCopy(commandList, frame, forSource: true);
            if (FeedConfig.DestTransition) TransitionForCopy(commandList, dest, forSource: false);

            // Build the SOURCE's mip chain before copying, then copy every level.
            //
            // The panel's own texture is an ROTexture, so mips cannot be generated on it
            // directly — which is why DrawOne generates them on a borrowed RW texture and
            // copies the whole resource in. Our LDR ring targets ARE pool
            // RWRenderTargetTextures, so the same trick works on them, using the very
            // MipMapJob instance DrawOne just used for this target.
            int levels = (FeedConfig.PanelMipRegen && RegenerateMips(args, frame, commandList))
                ? CameraRender.LdrMips
                : 1;

            // VALIDATE BOTH RESOURCES IMMEDIATELY BEFORE THE COPY.
            //
            // This is the fix for the 2026-08-01 13:36:47 freeze:
            //
            //   Assertion Failure: 'IsValid' evaluated to false
            //     at RWRenderTargetTexture.get_D3DResource()
            //     at CopyCommandList.CopyTextureSubresource(...)
            //     at FeedHandover.OnOffscreenUiDrawScoped
            //   [Watchdog]: the application froze with RenderThreadFreeze
            //
            // One of the two textures had already had its D3D resource released. We park a
            // frame on one pass and copy it on another, and in between a gate cycle, a
            // rebuild or the LCD system's own distance eviction can free either end — the
            // parked frame belongs to our LDR ring, the destination belongs to the panel, and
            // neither is ours to keep alive. The engine does not return an error for this; it
            // asserts, on the render thread, which is a freeze rather than an exception we
            // could catch.
            //
            // Checked HERE rather than at park time because the gap is precisely what makes
            // it stale: validity a few hundred lines earlier proves nothing about validity
            // now. One property read per copy against a hard freeze is not a trade worth
            // thinking about.
            if (!ResourceUsable(frame) || !ResourceUsable(dest))
            {
                _skipInvalid++;
                if (_skipInvalidLogs++ < 5)
                    RttLog.Line($"Handover: SKIPPED a copy — {(ResourceUsable(frame) ? "destination" : "parked frame")} " +
                                "has no live D3D resource. Something released it between the park and the copy (gate " +
                                "cycle, rebuild, or the LCD system evicting the panel's target by distance). Copying " +
                                "into it asserts inside the engine and freezes the render thread, so the frame is " +
                                "dropped instead. The next park re-establishes both ends.");
                return;
            }

            // Level-by-level with the SAME call that has been copying mip 0 all along.
            // CopyResource would move the whole chain in one go — it is what DrawOne uses —
            // but this path has a device removal on its record from a mip-count mismatch,
            // and there is nothing to gain: ten subresource copies of a 512x512 chain is
            // noise next to the render that produced the image.
            for (int level = 0; level < levels; level++)
                mi.Invoke(commandList, _copyArgs(dest, frame, level));

            _handovers++;
            _copiesInterval++;

            // Real frames are landing now — stop the 2D test pattern overwriting them.
            BlitProbe.FeedOwnsTarget = true;

            if (_handovers >= 30 && !_survivedLogged)
            {
                _survivedLogged = true;
                try { File.Delete(LivePath); } catch { }
                RttLog.Line($"=== HANDOVER SURVIVED {_handovers} copies — the feed is on the panel. ===");
            }
        }
        catch (Exception e)
        {
            if (_errLogs++ < 3) RttLog.Error("handover", e);
            if (_errLogs >= 3) { _armed = false; RttLog.Line("Handover disabled after repeated errors."); }
        }
    }

    // Force the source into COPY_SOURCE. ExplicitStateTransition is public on
    // CopyCommandList; DirectCommandList inherits it.
    private static MethodInfo _miTransition;
    private static object _copySourceState, _copyDestState;
    private static int _transitionState;   // 0 untried, 1 ok, -1 unavailable

    // Each end is tracked separately. The source transition is known to work; if the
    // destination has no reachable AutoResourceState, that must disable the
    // destination transition only — folding both into one flag would silently drop a
    // barrier that is currently doing its job.
    private static bool _srcTransitionOff, _dstTransitionOff;

    private static void TransitionForCopy(object commandList, object resource, bool forSource)
    {
        if (_transitionState == -1) return;
        if (forSource ? _srcTransitionOff : _dstTransitionOff) return;
        try
        {
            if (_transitionState == 0)
            {
                _miTransition = commandList.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "ExplicitStateTransition" && m.GetParameters().Length == 3);
                if (_miTransition != null)
                {
                    var stateType = _miTransition.GetParameters()[1].ParameterType;
                    if (stateType.IsEnum)
                    {
                        _copySourceState = FirstState(stateType, "CopySource", "CopySourceState", "GenericRead");
                        _copyDestState = FirstState(stateType, "CopyDest", "CopyDestState", "CopyDestination");
                    }
                    RttLog.Line($"Handover: ExplicitStateTransition found, src={_copySourceState ?? (object)"NONE"} " +
                                $"dst={_copyDestState ?? (object)"NONE"}");
                }
                else RttLog.Line("Handover: ExplicitStateTransition NOT FOUND — copy will use whatever state the resources are in.");

                _transitionState = (_miTransition != null && _copySourceState != null) ? 1 : -1;
                if (_transitionState == -1) return;
                if (_copyDestState == null) _dstTransitionOff = true;
            }

            var state = forSource ? _copySourceState : _copyDestState;
            if (state == null) return;

            // AutoResourceState hangs off a *view*, not the texture. Get a view first.
            var autoState = AutoStateOf(resource);
            if (autoState == null)
            {
                if (forSource) _srcTransitionOff = true; else _dstTransitionOff = true;
                RttLog.Line($"Handover: no AutoResourceState reachable from {resource?.GetType().Name} " +
                            $"({(forSource ? "source" : "dest")}) — that transition is skipped.");
                return;
            }
            _miTransition.Invoke(commandList, new object[] { autoState, state, false });
        }
        catch (Exception e)
        {
            if (forSource) _srcTransitionOff = true; else _dstTransitionOff = true;
            RttLog.Error($"state transition ({(forSource ? "source" : "dest")})", e);
        }
    }

    private static object FirstState(Type enumType, params string[] names)
    {
        var have = Enum.GetNames(enumType);
        foreach (var n in names) if (have.Contains(n)) return Enum.Parse(enumType, n);
        return null;
    }

    // Walk texture -> view -> AutoResourceState. The texture may expose a view via a
    // Get*View() method, or already be one.
    // Per type, not once overall: source and destination are different classes
    // (RWRenderTargetTexture vs ROTexture) and a single flag would hide whichever
    // one was looked up second.
    private static readonly HashSet<string> _autoStateDiag = new();

    private static object AutoStateOf(object texture)
    {
        if (texture == null) return null;
        try
        {
            // Already a view?
            var direct = Prop(texture, "AutoResourceState");
            if (direct != null) return direct;

            var candidates = new List<string>();
            foreach (var m in texture.GetType().GetMethods(Any))
            {
                if (m.GetParameters().Length != 0) continue;
                if (!m.Name.StartsWith("Get") || !m.Name.Contains("View")) continue;
                candidates.Add(m.Name);
                object view = null;
                try { view = m.Invoke(texture, null); } catch { continue; }
                var st = Prop(view, "AutoResourceState");
                if (st != null)
                {
                    if (_autoStateDiag.Add(texture.GetType().Name))
                        RttLog.Line($"Handover: AutoResourceState via {texture.GetType().Name}.{m.Name}().");
                    return st;
                }
            }

            if (_autoStateDiag.Add(texture.GetType().Name))
                RttLog.Line($"Handover: no AutoResourceState on {texture.GetType().Name}. View-ish methods tried: " +
                            (candidates.Count == 0 ? "(none)" : string.Join(", ", candidates)));
        }
        catch (Exception e) { RttLog.Error("auto state lookup", e); }
        return null;
    }

    // ------------------------------------------------------------------- the copy
    // CopyResource was the wrong call, and copy-diag.txt says why in one number:
    //
    //     destination (our target, and every engine target)  MipLevels = 10
    //     source      (our LDR ring texture)                 MipLevels =  1
    //
    // CopyResource copies a WHOLE resource and requires identical descriptions, so a
    // 10-mip destination and a 1-mip source is invalid — undefined behaviour, which
    // in D3D12 without the debug layer means it may limp along for hundreds of copies
    // (it did: 337 into the LCD system's target) and then remove the device. That is
    // the real shape of every "worked at 2 Hz, died at 15 fps" result we have, and of
    // the eight fixes that each addressed a genuine defect without making it durable.
    //
    // CopyTextureSubresource copies one subresource, so mip counts need not match.
    // Mip 0 is what the panel samples at the range this is tested from; mips 1..9
    // keep whatever DrawOne's own mip generation left there.
    private static MethodInfo _miCopy;
    // (dest, source, mipLevel) -> the resolved copy call's argument array.
    private static Func<object, object, int, object[]> _copyArgs;

    // True only when the resolved copy addresses source and destination subresources
    // independently. Without it, filling anything but mip 0 is not expressible.
    private static bool CopyPerLevel;

    // ---- MIP REGENERATION ---------------------------------------------------------------
    //
    // THE PROBLEM. CopyTextureSubresource writes ONE subresource, so our handover fills mip
    // 0 and nothing else. The panel target has a full chain (10 levels at 512x512), and what
    // sits in levels 1..N is whatever DrawOne left there — which is NOT a stale frame of our
    // feed. DrawOne borrows an RWRenderTargetTexture from the POOL, clears it, draws the
    // panel's UI batch, generates mips on that borrowed texture, and copies it to the panel.
    // So levels 1..N carry the UI batch's content on a RECYCLED pool texture, and which
    // recycled slot it is changes from frame to frame.
    //
    // Result: correct up close (mip 0), progressively wrong as the player backs away and
    // trilinear filtering weights the higher levels — foreign, non-deterministic content,
    // which the player's FSR then happily accumulates. That is the reported "stars smear
    // along their motion path, worse the further back you stand, clears briefly when you
    // move". The old comment on ResolveCopy conceded exactly this and scoped it away with
    // "mip 0 is what the panel samples at the range this is tested from".
    //
    // THE FIX. Run the engine's own mip generator on the panel's texture immediately after
    // our copy, so every level is a downsample of the frame we just delivered.
    // OffscreenUIRenderer._mipMapJob is the very instance DrawOne used one call earlier for
    // this same target, which is why the bootstrap appends __instance to the args: reusing
    // it creates nothing (Rule 11), and it cannot collide with another system over its
    // descriptor table the way borrowing CloudShadowJob's MipMapJob could have.
    //
    // Everything here fails SOFT. A missing renderer, job, texture, mip count or overload
    // logs once and leaves the feed exactly as it was — correct up close, wrong at distance.
    // That is the pre-existing behaviour, so a failure here can only decline to improve
    // things, never break them. Written that way on purpose: this runs inside the UI stage's
    // command recording, and a throw there is a dead game.
    private static object _mipJob;
    private static MethodInfo _miMipDoWork;
    private static int _mipState;          // 0 = untried, 1 = ready, -1 = unavailable
    private static bool _mipLogged;

    private static bool RegenerateMips(object[] args, object source, object commandList)
    {
        if (_mipState < 0 || !CopyPerLevel || CameraRender.LdrMips <= 1 || source == null) return false;
        try
        {
            // Pool borrows arrive as Borrowed<T>, and the copy path is happy with that
            // because it converts implicitly to a view. MipMapJobExtensions wants the
            // RESOURCE, so unwrap .Resource when it is there — the same unwrap CameraRender
            // does for its blit source. Harmless when the object is already the resource.
            source = Prop(source, "Resource") ?? source;
            if (_mipState == 0)
            {
                // The renderer is appended to the args by the bootstrap. Absent on an older
                // one, which is a normal state and not an error.
                object renderer = null;
                foreach (var a in args)
                    if (a != null && a.GetType().Name.Contains("OffscreenUIRenderer")) { renderer = a; break; }

                if (renderer == null)
                {
                    _mipState = -1;
                    RttLog.Line("Panel mips: no OffscreenUIRenderer in the hook args — this bootstrap " +
                                "does not pass __instance yet. RESTART THE GAME to adopt it. Until then " +
                                "the panel's mips 1..N keep DrawOne's recycled pool content and the feed " +
                                "will look wrong from a distance.");
                    return false;
                }

                _mipJob = renderer.GetType()
                    .GetField("_mipMapJob", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(renderer);
                if (_mipJob == null)
                {
                    _mipState = -1;
                    RttLog.Line("Panel mips: OffscreenUIRenderer._mipMapJob not found — field renamed? " +
                                "Re-check with tools/EngineQuery. Distance appearance unchanged.");
                    return false;
                }

                // MipMapJobExtensions.DoWork(MipMapJob, ComputeCommandList, <target>, int).
                // Two overloads differing only in the target type; pick the one our SOURCE
                // satisfies rather than guessing. The source is one of CameraRender's LDR
                // ring targets — a pool RWRenderTargetTexture, which is exactly what these
                // overloads want, and the reason the generation happens here rather than on
                // the panel (whose own texture is read-only).
                var ext = Type.GetType("Keen.VRage.Render12.PostProcessStage.MipMapJobExtensions, VRage.Render12");
                _miMipDoWork = ext?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "DoWork") return false;
                        var p = m.GetParameters();
                        return p.Length == 4
                            && p[1].ParameterType.IsInstanceOfType(commandList)
                            && p[2].ParameterType.IsInstanceOfType(source)
                            && p[3].ParameterType == typeof(int);
                    });

                if (_miMipDoWork == null)
                {
                    _mipState = -1;
                    RttLog.Line($"Panel mips: no MipMapJobExtensions.DoWork overload accepts " +
                                $"({commandList.GetType().Name}, {source.GetType().Name}, int). " +
                                "Distance appearance unchanged.");
                    return false;
                }
                _mipState = 1;
            }

            int levels = CameraRender.LdrMips;
            _miMipDoWork.Invoke(null, new[] { _mipJob, commandList, source, (object)levels });

            if (!_mipLogged)
            {
                _mipLogged = true;
                RttLog.Line($"Panel mips: generating {levels - 1} level(s) below mip 0 on our LDR ring " +
                            "target with the engine's own OffscreenUIRenderer._mipMapJob, then copying " +
                            "every level to the panel. Before this, only mip 0 was ours and levels 1..N " +
                            "held DrawOne's recycled pool content, so the panel was correct only at " +
                            "close range.");
            }
            return true;
        }
        catch (Exception e)
        {
            _mipState = -1;   // one strike: never throw twice inside UI command recording
            if (!_mipLogged) { _mipLogged = true; RttLog.Error("generate feed mips", e); }
            return false;
        }
    }

    private static MethodInfo ResolveCopy(object commandList)
    {
        if (_miCopy != null) return _miCopy;
        var t = commandList.GetType();

        // CopyTextureSubresource(ICopyDestinationView dst, int dstSub, ICopySourceView src, int srcSub)
        // The ONLY overload that addresses source and destination subresources
        // independently, and therefore the only one that can fill a mip chain level by
        // level. CopyPerLevel is what tells the caller that.
        var m = t.GetMethods(Any).FirstOrDefault(x => x.Name == "CopyTextureSubresource" && x.GetParameters().Length == 4);
        if (m != null)
        {
            _copyArgs = (d, s, lv) => new object[] { d, lv, s, lv };
            CopyPerLevel = true;
            RttLog.Line("Handover: using CopyTextureSubresource(dst,N,src,N) — per-level, so the " +
                        "panel's whole mip chain can be filled from ours.");
            return _miCopy = m;
        }

        // CopySubresource(ICopyDestinationView dst, ICopySourceView src, int srcSubOffset)
        // One index only, so mip 0 is all this can safely address.
        m = t.GetMethods(Any).FirstOrDefault(x => x.Name == "CopySubresource" && x.GetParameters().Length == 3);
        if (m != null)
        {
            _copyArgs = (d, s, lv) => new object[] { d, s, 0 };
            CopyPerLevel = false;
            RttLog.Line("Handover: using CopySubresource(dst,src,0) — mip 0 only, so the feed will " +
                        "still look wrong at a distance.");
            return _miCopy = m;
        }

        // Last resort. Moves the whole resource in one call, which is what DrawOne uses —
        // but a mip-count mismatch here is a device removal on this project's record, and
        // per-level copying is available above, so this stays the fallback it always was.
        m = t.GetMethods(Any).FirstOrDefault(x => x.Name == "CopyResource" && x.GetParameters().Length == 2);
        if (m != null)
        {
            _copyArgs = (d, s, lv) => new object[] { d, s };
            CopyPerLevel = false;
            RttLog.Line("Handover: falling back to CopyResource — INVALID if mip counts differ. Expect device removal.");
            return _miCopy = m;
        }
        return null;
    }

    // ------------------------------------------------------------- copy diagnostics
    // Everything readable about a copy's two ends, once per distinct target handle.
    //
    // The fixed field list in Snapshot() was too narrow to be useful: it printed
    // "dest: ROTexture, Resolution 512,512" and nothing else, which is exactly the
    // information that made the two targets look interchangeable when they are not.
    // CopyResource requires identical resource descriptions — format, dimensions,
    // mip count, array size, sample count — so any of those differing is fatal, and
    // none of them were being logged.
    private static readonly string DiagPath = Path.Combine(RttLog.OutDir, "copy-diag.txt");
    private static readonly HashSet<string> _diagged = new();
    private static int _diagCount, _skipDisabled;

    private static void DumpCopyDiag(object component, object dest, object source)
    {
        try
        {
            var handle = Prop(component, "Handle")?.ToString() ?? "?";
            bool ours = _panelHandleText != null && handle.Contains(_panelHandleText);
            if (!_diagged.Add(handle)) return;
            if (!ours && _diagCount++ >= 4) return;   // a few controls is plenty

            var sb = new StringBuilder();
            sb.AppendLine($"=========== {(ours ? "OURS (the copy that dies)" : "ENGINE-OWNED (control)")} " +
                          $"{DateTime.Now:HH:mm:ss.fff} ===========");
            sb.AppendLine($"handle = {handle}");
            Deep(sb, "component", component, 0);
            Deep(sb, "dest", dest, 0);
            if (source != null) Deep(sb, "source", source, 0);
            sb.AppendLine();

            File.AppendAllText(DiagPath, sb.ToString());
            RttLog.Line($"Copy diag written for {(ours ? "OUR" : "an engine")} target {handle} -> {DiagPath}");
        }
        catch (Exception e) { RttLog.Error("copy diag", e); }
    }

    // Depth-limited property/field dump. Only cheap, side-effect-free members: no
    // indexers, no methods, nothing that takes arguments. Every read is guarded,
    // because some render-side getters assert rather than return.
    private static void Deep(StringBuilder sb, string label, object o, int depth)
    {
        string pad = new(' ', 2 + depth * 4);
        if (o == null) { sb.AppendLine($"{pad}{label}: NULL"); return; }
        sb.AppendLine($"{pad}{label}: {o.GetType().FullName}");
        if (depth >= 2) return;

        foreach (var (n, t, v) in Members(o))
        {
            if (v == null) { sb.AppendLine($"{pad}  {t,-28} {n,-26} = null"); continue; }

            var vt = v.GetType();
            if (vt.IsPrimitive || vt.IsEnum || v is string || v is decimal)
            { sb.AppendLine($"{pad}  {t,-28} {n,-26} = {v}"); continue; }

            // Value types print usefully via ToString (Vector2I, resolutions, ids).
            if (vt.IsValueType && !vt.IsGenericType)
            { sb.AppendLine($"{pad}  {t,-28} {n,-26} = {v}"); continue; }

            // Recurse only where a resource description would live.
            if (n.Contains("Desc", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Desc", StringComparison.OrdinalIgnoreCase) ||
                n is "Resource" or "Texture" or "View")
            { Deep(sb, $"{n} ({t})", v, depth + 1); continue; }

            sb.AppendLine($"{pad}  {t,-28} {n,-26} = <{vt.Name}>");
        }
    }

    private static IEnumerable<(string Name, string Type, object Value)> Members(object o)
    {
        var t = o.GetType();
        foreach (var f in t.GetFields(Any))
        {
            if (f.IsStatic) continue;
            object v = null; try { v = f.GetValue(o); } catch { continue; }
            yield return (Clean(f.Name), f.FieldType.Name, v);
        }
        foreach (var p in t.GetProperties(Any))
        {
            if (p.GetIndexParameters().Length != 0 || !p.CanRead) continue;
            object v = null; try { v = p.GetValue(o); } catch { continue; }
            yield return (p.Name, p.PropertyType.Name, v);
        }
    }

    // Backing fields read as <Prop>k__BackingField; the property name is the useful part.
    private static string Clean(string n)
    {
        int a = n.IndexOf('<'), b = n.IndexOf('>');
        return (a == 0 && b > 1) ? n[1..b] : n;
    }

    // A one-line-per-field snapshot of the pending copy, overwritten each time.
    private static readonly string SnapPath = Path.Combine(RttLog.OutDir, "last-copy.txt");

    private static void Snapshot(object src, object dst, object component)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"copy #{_handovers + 1} at {DateTime.Now:HH:mm:ss.fff}");
            sb.AppendLine($"  transitionState = {_transitionState}");
            Describe(sb, "source", src);
            Describe(sb, "dest  ", dst);
            sb.AppendLine($"  component handle = {Prop(component, "Handle")}");
            sb.AppendLine($"  component res    = {Prop(component, "Resolution")}");
            sb.AppendLine($"  component format = {Prop(component, "Format")}");
            File.WriteAllText(SnapPath, sb.ToString());
        }
        catch { }
    }

    private static void Describe(StringBuilder sb, string label, object o)
    {
        if (o == null) { sb.AppendLine($"  {label}: NULL"); return; }
        sb.AppendLine($"  {label}: {o.GetType().Name}");
        foreach (var n in new[] { "Resolution", "Size", "Format", "Width", "Height", "MipLevels", "IsValid", "DebugName" })
        {
            var v = Prop(o, n);
            if (v != null) sb.AppendLine($"      {n} = {v}");
        }
    }

    private static int _skipInvalid, _skipInvalidLogs;

    // Does this texture still have a live D3D resource behind it?
    //
    // FAILS OPEN when no IsValid member exists, because that is the pre-2026-08-01 behaviour
    // and a guard that silently blocks every copy would present as a permanently black panel —
    // the failure shape this project has spent the most time on. It says so loudly once, so an
    // inert guard cannot be mistaken for a working one.
    private static bool _validityProbeLogged;

    private static bool ResourceUsable(object tex)
    {
        if (tex == null) return false;
        try
        {
            var p = tex.GetType().GetProperty("IsValid", Any)
                 ?? tex.GetType().GetProperty("IsAllocated", Any);
            if (p == null)
            {
                if (!_validityProbeLogged)
                {
                    _validityProbeLogged = true;
                    RttLog.Line($"Handover: no IsValid on {tex.GetType().Name} — the copy guard is INERT for this " +
                                "resource type, so a released texture can still assert inside the engine and freeze " +
                                "the render thread. Find the right member before trusting this guard's silence.");
                }
                return true;                       // behave as before rather than go dark
            }
            return p.GetValue(tex) is not bool b || b;
        }
        catch { return true; }
    }

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var p = o.GetType().GetProperty(name, Any);
            if (p != null) return p.GetValue(o);
            return o.GetType().GetField(name, Any)?.GetValue(o);
        }
        catch { return null; }
    }
}
