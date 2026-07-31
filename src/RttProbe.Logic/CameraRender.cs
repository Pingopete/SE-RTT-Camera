using System.Reflection;
using System.Text;

namespace RttProbe;

// Steps 2 and 3: run the engine's own scene passes a second time, into our own
// render target.
//
// Modelled directly on SceneDrawSystem.ExecuteEnvironmentProbeUpdate, which does
// exactly this for environment probes ~7 times a frame. Everything it needs is a
// parameter, a pooled borrow, or a context — it mutates no global state, which is
// what makes a second pass viable at all.
//
// Two phases, because guessing wrong here means GPU work on the render thread:
//
//   Phase A (dry run, always)  resolve every argument, log what was found, report
//                              whether the full call could be assembled. No GPU work.
//   Phase B (armed only)       actually invoke cull -> cluster -> draw.
//
// Phase B is gated on output\camera-armed.marker existing, and disarms itself if
// a previous run died with it present.
internal static class CameraRender
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private const int RenderW = 256, RenderH = 256;

    private static readonly string ArmPath = Path.Combine(RttLog.OutDir, "camera-armed.marker");
    private static readonly string LivePath = Path.Combine(RttLog.OutDir, "camera-live.marker");

    private static bool _armed;
    private static bool _disarmed;

    // PER-FEED (phase C1a): this feed's camera-pass cadence stamp. Two feeds run on two
    // clocks — that is what "feed fps divides by N" means at the scheduler.
    private static long _lastRender
    { get => Feeds.Cur.LastRender; set => Feeds.Cur.LastRender = value; }

    private static long _lastArmCheck, _lastDisarmCheck;
    private const long ArmPollMs = 2000;
    private static int _errors;

    // Resolved once by the dry run.
    private static object _settings, _texPool, _bufMgr;
    private static bool _wsBlitLogged;
    private static int _wsBlitErrs;

    // A throwaway for optional resolves, so a missing member cannot fail the dry run.

    // Which OutputGeometryBufferContext this pass writes its draw commands into.
    // See the resolve-time comment: the choice is between a buffer eighteen engine
    private static MethodInfo _miBorrowRt, _miBorrowResizableRt, _miCreateCb;
    private static Type _tRenderViewSlim, _tTrackedCam, _tCamSettings;
    private static object _hdrFormat;

    // ---- the installed camera / render view -------------------------------------
    private static FieldInfo _renderViewField;
    private static FieldInfo[] _rvFields;

    // The orbit transform. This pair is the ONLY thing the whole-scene route takes from
    // the camera pass: WholeSceneRenderView reads them to build its view.
    //
    // PER-FEED (phase C1a), and the pair that makes a feed a distinct VIEW at all. Goal
    // 5's caller-driven camera lands exactly here: a consumer supplying its own
    // transform provider writes this instance's pair and nothing else's.
    private static object _lastCamWorld
    { get => Feeds.Cur.LastCamWorld; set => Feeds.Cur.LastCamWorld = value; }
    private static object _lastViewD
    { get => Feeds.Cur.LastViewD; set => Feeds.Cur.LastViewD = value; }

    // Orbit continuity, for the eye-jump warning in OrbitViewSlim.
    private static Keen.VRage.Library.Mathematics.Vector3D _lastEye
    { get => Feeds.Cur.LastEye; set => Feeds.Cur.LastEye = value; }
    private static bool _haveLastEye
    { get => Feeds.Cur.HaveLastEye; set => Feeds.Cur.HaveLastEye = value; }
    private static int _eyeJumpLogs;

    // ---- feed delivery -----------------------------------------------------------
    // PER-FEED: the panel target THIS feed is currently sized for. A stale value here
    // is what silently broke every live resolution change before the Reset fix.
    private static string _resolvedPanelId
    { get => Feeds.Cur.ResolvedPanelId; set => Feeds.Cur.ResolvedPanelId = value; }
    private static long _lastResolveAttempt, _lastResolveFailLog;
    private static int _resolveFailLogs;


    public static void Reset()
    {
        _armed = _disarmed = false;
        _lastRender = _lastArmCheck = _lastDisarmCheck = 0; _errors = 0;
        Array.Clear(_ldrRing, 0, _ldrRing.Length); _ldrReady = null; _ringIndex = -1; _ldrMips = 1;
        _resolvedPanelId = null; _blitLogged = _blitResLogged = false; _farClipLogged = double.NaN;
        // The retry streak AND its log budget. Clearing the streak without clearing the
        // budget would make the second startup silent, which is the failure mode this whole
        // fix exists to stop: a feed that says nothing while producing nothing.
        _viewLookupFails = _viewLookupLogs = 0;
        _baseViewSnapshot = null; _baseViewMismatches = 0; _mismatchLogged = false;
        _viewSkips = _viewSkipLogs = 0;
        _firstPassAt = 0; _startupLogged = _startupDoneLogged = false;

        // Every one of these is a "logged once" or "applied once" latch, and a hot reload
        // that left one set would silently skip the log line that proves a fix took —
        // the failure mode that has wasted the most time on this project.
        _screenResLog = null;
        _cbRenderView = null; _miCreateNonjittered = _miRvSetCamera = _miRvSetResolution = null;

        // THE CACHED RENDER RESOLUTION, and it must die here. Built once from
        // FeedConfig.WholeSceneWidth/Height behind an `if (_wsResolution == null)`, it used
        // to survive every config change, gate cycle and hot reload — so a LIVE resolution
        // change resized ScreenBuffers and FinalLDR while the camera CB kept stamping the
        // OLD value into Screen.Resolution / PrevResolution.
        //
        // Every shader turns that into ScreenToUV() = rcp(Screen_.Resolution), so the whole
        // render came out scaled by the ratio between the two: 768 stamped into a 1024
        // render is 1.33x out in each axis. Symptoms, all user-confirmed 2026-07-29 and all
        // predicted verbatim by StampScreenResolution's own comment: planet atmospheres
        // misaligned with their planets, and "objects rotating in non-recurring patterns"
        // (that comment says "the sky being far too zoomed and rotating far too fast", plus
        // mis-scaled view vectors, specular response and depth-based dimming).
        //
        // It cost a wrong conclusion, which is the real lesson. 512 and 768 both looked
        // CLEAN and 1024 looked BROKEN, so resolution appeared to be the variable — but 512
        // and 768 had each been the value at GAME LAUNCH, and 1024 was only ever set live.
        // Reverting to 768 "fixed" it purely because 768 was what this static already held.
        // A stale cache that happens to agree with the config is indistinguishable from a
        // correct one; it only shows up when the config moves.
        //
        // _wsRenderView goes too. Its resolution is re-set from _wsResolution on every
        // build, so it is not independently stale today — but it is the same
        // built-once-and-kept object, and _miRvSetResolution (the setter used on it) IS
        // cleared on the line above, which is exactly the kind of half-cleared pair that
        // produced this bug.
        _wsResolution = null; _wsRenderView = null;
        _fullCbBlocked = _fullCbLogged = false;
        // Previous-camera history must not survive a reload: the orbit may have been
        // rebuilt, and a stale "previous" is worse than none — it reprojects into a view
        // that never existed. One noisy render after a reload, then correct.
        _wsPrevCameraSettings = null; _miPrevCamFromView = null; _fTrackedPrevCam = null;
        _prevCamState = 0; _prevCamLogged = false;
        _environmentField = null;
        _dimApplied = double.NaN;
        _resolved = false; _resolveOk = false;

        // A live marker left from last session means we issued GPU work and never
        // came back. Refuse to repeat it until a human clears it.
        if (File.Exists(LivePath))
        {
            _disarmed = true;
            RttLog.Line("!!! PREVIOUS SESSION DIED MID-RENDER — camera pass DISABLED.");
            RttLog.Line($"!!! Delete {LivePath} to try again.");
        }
        _armed = !_disarmed && File.Exists(ArmPath);
        RttLog.Line(_armed
            ? "Camera pass ARMED — will advance the orbit and deliver frames to the panel."
            : $"Camera pass not armed. Create {ArmPath} to arm.");
    }

    // Resolve the engine members this pass reaches by reflection.
    //
    // This replaces a ~300-line dry-run survey that resolved the whole probe pipeline and
    // wrote a report to output/camera-dryrun.txt. The report answered its questions long
    // ago, and tools/EngineQuery answers the same ones offline without a running game.
    // What is left is the set the surviving code actually reads — nothing more, so a
    // failure here names the one member that is missing.
    //
    // One-shot and latched: on failure the pass stays off for the session rather than
    // retrying reflection thirty times a second.
    private static bool _resolved, _resolveOk;

    private static bool Resolve(object sds)
    {
        try
        {
            var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            _settings = cs?.GetField("Settings", Any)?.GetValue(null);
            _texPool  = cs?.GetField("BindableTexturePool", Any)?.GetValue(null);
            _bufMgr   = cs?.GetField("BindableBuffers", Any)?.GetValue(null);

            _miBorrowRt = _texPool?.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "BorrowRWRenderTargetTexture"
                                  && m.GetParameters().Length == 7);
            _miBorrowResizableRt = _texPool?.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "BorrowResizableRWRenderTargetTexture"
                                  && m.GetParameters().Length == 7);
            _miCreateCb = _bufMgr?.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "CreateTransientConstantBuffer"
                                  && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);

            var sbType = Type.GetType("Keen.VRage.Render12.Core.Systems.ScreenBuffers, VRage.Render12");
            _hdrFormat = sbType?.GetField("HDR_FORMAT", Any)?.GetValue(null);

            // The camera constant buffer is built from these three.
            _tRenderViewSlim = FindType("RenderViewSlim");
            _tCamSettings    = FindType("CameraSettings");
            _tTrackedCam     = FindType("TrackedCameraSettings");

            // CopyJob is the converting blit that bridges the render's HDR output to the
            // LCD's sRGB target. Without it nothing reaches the panel.
            _copyJob = sds?.GetType().GetField("_copyJob", Any)?.GetValue(sds);
            _miCopyDoWork = _copyJob?.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 8);

            var chParam = _miCopyDoWork?.GetParameters()[5].ParameterType;
            if (chParam != null && chParam.IsEnum)
            {
                // RGB, deliberately NOT alpha. The panel binds our feed as
                // ColorMetalTexture — RGB is colour and ALPHA IS METALNESS. Blitting with
                // Channel.All wrote the shader's alpha straight into metalness, and a
                // fully metallic surface has almost no diffuse response, which is why an
                // exposure sweep once appeared to do nothing.
                long rgb = 0;
                foreach (var n in new[] { "R", "G", "B" })
                    if (Enum.GetNames(chParam).Contains(n))
                        rgb |= System.Convert.ToInt64(Enum.Parse(chParam, n));
                _channelRgb = rgb != 0 ? Enum.ToObject(chParam, rgb) : null;

                foreach (var n in new[] { "All", "RGBA" })
                    if (Enum.GetNames(chParam).Contains(n)) { _channelAll = Enum.Parse(chParam, n); break; }
                _channelAll ??= Enum.ToObject(chParam, Enum.GetValues(chParam).Cast<object>()
                    .Select(v => System.Convert.ToInt64(v)).Max());
            }

            var missing = new List<string>();
            if (_settings == null)      missing.Add("CoreSystems.Settings");
            if (_texPool == null)       missing.Add("CoreSystems.BindableTexturePool");
            if (_bufMgr == null)        missing.Add("CoreSystems.BindableBuffers");
            if (_hdrFormat == null)     missing.Add("ScreenBuffers.HDR_FORMAT");
            if (_copyJob == null)       missing.Add("SceneDrawSystem._copyJob");
            if (_miCopyDoWork == null)  missing.Add("CopyJob.DoWork(8 args)");
            if (_miBorrowRt == null)    missing.Add("BorrowRWRenderTargetTexture(7 args)");
            if (_miCreateCb == null)    missing.Add("CreateTransientConstantBuffer<T>(2 args)");
            if (_tRenderViewSlim == null) missing.Add("RenderViewSlim");
            if (_tCamSettings == null)  missing.Add("CameraSettings");
            if (_tTrackedCam == null)   missing.Add("TrackedCameraSettings");

            if (missing.Count > 0)
            {
                RttLog.Line("Camera pass DISABLED — engine members not found: " + string.Join(", ", missing) +
                            ". The game version probably moved them.");
                return false;
            }

            RttLog.Line("Camera pass resolved: settings, texture pool, buffer manager, HDR format, " +
                        "CopyJob blit and the three camera-CB types are all present.");
            return true;
        }
        catch (Exception e) { RttLog.Error("camera pass resolve", e); return false; }
    }

    public static void OnProbePass(object sds, object commandList)
    {
        if (commandList == null) return;
        if (!FeedGate.Active) return;           // no tagged panel: issue no GPU work at all
        try
        {
            if (!_resolved) { _resolved = true; _resolveOk = Resolve(sds); }
            if (!_resolveOk) return;

            // High-resolution: this drives the frame gate, and TickCount64's ~15.6 ms
            // quantum was silently capping the feed at 20 fps when 30 was asked for.
            var now = Clock.Ms;

            // Re-check the crash latch BEFORE honouring it. A hot reload that lands
            // mid-pass leaves camera-live.marker behind, Reset() reads that as "died
            // mid-render", and then the in-flight pass's finally deletes the file — so
            // the pass ends up disabled with nothing left to delete and no way back.
            // This check sat after the early-out, which made the latch permanent for
            // the session. Exactly the deadlock the handover had, fixed there and not
            // here.
            if (_disarmed && now - _lastDisarmCheck >= ArmPollMs)
            {
                _lastDisarmCheck = now;
                if (!File.Exists(LivePath))
                {
                    _disarmed = false;
                    RttLog.Line("Camera pass re-enabled — the mid-render marker is gone " +
                                "(it was a hot reload landing mid-pass, not a death).");
                }
            }
            if (_disarmed) return;

            // Arming is a runtime switch, so the marker is polled rather than read
            // once at load. Creating or deleting it takes effect within ~2 s with no
            // rebuild and no reload.
            if (now - _lastArmCheck >= ArmPollMs)
            {
                _lastArmCheck = now;
                bool armedNow = File.Exists(ArmPath);
                if (armedNow != _armed)
                {
                    _armed = armedNow;
                    RttLog.Line(_armed
                        ? "Camera pass ARMED (marker appeared) — issuing real GPU work."
                        : "Camera pass DISARMED (marker removed).");
                }
            }
            if (!_armed) return;

            FeedConfig.Poll();

            // Grace period measured from the first render frame we see, not from logic
            // load: during world load the renderer is still settling (pools resizing,
            // panels acquiring targets, streaming catching up) and issuing GPU work
            // into that has repeatedly crashed on load.
            if (_firstPassAt == 0) _firstPassAt = now;
            if (now - _firstPassAt < FeedConfig.StartupDelayMs)
            {
                if (!_startupLogged)
                {
                    _startupLogged = true;
                    RttLog.Line($"Feed: holding off {FeedConfig.StartupDelayMs} ms after load before issuing GPU work.");
                }
                return;
            }
            if (!_startupDoneLogged)
            {
                _startupDoneLogged = true;
                RttLog.Line("Feed: startup grace period over — camera pass live.");
            }

            if (now - _lastRender < FeedConfig.IntervalMs) return;
            _lastRender = now;

            RenderOnce(commandList);
        }
        catch (Exception e)
        {
            if (_errors++ < 5) RttLog.Error("camera pass", e);
            if (_errors >= 5) { _armed = false; RttLog.Line("Camera pass disabled after repeated errors."); }
        }
    }

    // ------------------------------------------------------------------ phase B
    // Advance the orbit, then publish whatever the whole-scene render produced.
    //
    // This used to be a ~1000-line second scene render of its own: cull, geometry,
    // deferred texturing, environment, deferred lighting, exposure, bloom, tonemap —
    // the probe route, which was how the panel got its picture before
    // SceneDrawSystem.Draw could be run a second time. Every pixel of it was thrown
    // away by the blit below the moment the whole-scene route started producing
    // frames, and the probe terrain shader samples no textures, so it had a hard
    // fidelity ceiling this route does not.
    //
    // What survives is the part the whole-scene route actually consumes:
    // OrbitViewSlim() advances the orbit and stores _lastCamWorld / _lastViewD, which
    // is all WholeSceneRenderView reads, and CopyToFeed delivers the image.
    private static void RenderOnce(object commandList)
    {
        try
        {
            File.WriteAllText(LivePath, $"camera pass entered {DateTime.Now:O}\n");

            // Persistent and self-gating on change — see ApplyDimDistance for why it
            // cannot be part of a scoped swap.
            ApplyDimDistance();

            // One-shot per rebuild: shrink our FinalLDR from the swapchain size its ctor
            // gave it down to the feed size. Here because this hook has what Resize needs
            // — the render thread and a live DirectCommandList (a CopyCommandList by
            // inheritance). See WholeSceneRender.EnsureFinalLdrSize for the full story.
            WholeSceneRender.EnsureFinalLdrSize(commandList);

            // Advances the orbit. The return value is only checked for failure; the side
            // effect on _lastCamWorld / _lastViewD is what the whole-scene route reads.
            if (OrbitViewSlim() == null)
            {
                _viewSkips++;
                if (_viewSkips % 200 == 1 && _viewSkipLogs++ < 5)
                    RttLog.Line($"Orbit view unavailable ({_orbitNull ?? "unknown"}) — " +
                                $"{_viewSkips} pass(es) so far. The panel keeps its last frame.");
            }

            CopyToFeed(commandList, null);
        }
        finally
        {
            try { File.Delete(LivePath); } catch { }
        }
    }

    // ------------------------------------------------------------ publish
    // The LCD side owns an OffscreenRenderTarget (BlitProbe stage 2). Its Render12
    // counterpart lives in OffscreenTargetManager._registeredTextures, keyed by
    // handle. Copying into that texture is how our pixels become something a panel
    // can display.
    // PER-FEED (phase C1a): the destination and its shape. Per-feed is what makes
    // "many LCD panel sizes and aspect ratios" tractable — each feed resolves its own
    // panel's resolution and format, and phase E2's shared-source crop then sits on top
    // for the case where several panels want ONE camera.
    private static object _feedTexture
    { get => Feeds.Cur.FeedTexture; set => Feeds.Cur.FeedTexture = value; }
    private static object _feedRes                 // dictates our render target's shape
    { get => Feeds.Cur.FeedRes; set => Feeds.Cur.FeedRes = value; }
    private static object _feedFormat
    { get => Feeds.Cur.FeedFormat; set => Feeds.Cur.FeedFormat = value; }
    private static object _feedComponent           // Render12 OffscreenRenderTargetComponent
    { get => Feeds.Cur.FeedComponent; set => Feeds.Cur.FeedComponent = value; }
    private static int _feedState       // 0 untried, 1 ready, -1 unavailable
    { get => Feeds.Cur.FeedState; set => Feeds.Cur.FeedState = value; }
    // PER-FEED: a copy failure on feed 1 must not be silenced by feed 0's healthy budget,
    // and the third failure disables THAT feed (_feedState = -1), not the route.
    private static int _copyLogs
    { get => Feeds.Cur.CopyLogs; set => Feeds.Cur.CopyLogs = value; }

    // The view-lookup retry budget. ~20 s at the observed ~30 passes/s — long enough to
    // cover any plausible "first render has not landed yet" window, short enough that a
    // genuinely broken feed stops re-attempting a copy for the rest of the session.
    private const int ViewLookupGiveUp = 600;
    private static int _viewLookupFails
    { get => Feeds.Cur.ViewLookupFails; set => Feeds.Cur.ViewLookupFails = value; }
    private static int _viewLookupLogs
    { get => Feeds.Cur.ViewLookupLogs; set => Feeds.Cur.ViewLookupLogs = value; }

    // Separately gated from the render. The camera pass alone has run for 17
    // minutes without incident; the copy is the part that killed the game, so it
    // must not ride on the same switch.
    private static readonly string CopyArmPath = Path.Combine(RttLog.OutDir, "feed-copy-armed.marker");

    private static void CopyToFeed(object commandList, object rtBorrow)
    {
        try
        {
            // Queue our target for servicing BEFORE the arm gate. RequestRender only
            // adds it to the manager's pending list — that is what causes its submitted
            // batches to be drawn at all, and without it the target stays empty (the
            // black panel). Only the copy below is dangerous enough to need arming.
            var queueRt = BlitProbe.FeedTarget;
            if (queueRt != null)
            {
                FeedHandover.SetPanelTarget(Prop2(queueRt, "Id"));
                FeedHandover.RequestPanelRender(queueRt);
            }

            if (!File.Exists(CopyArmPath)) return;

            // The LCD system re-creates the panel's render target periodically
            // (observed changing id mid-session). Everything derived from it — the
            // resolved component, its resolution and format, and the persistent LDR
            // texture sized to match — goes stale at that moment, and copying a
            // stale-sized source into the fresh destination is a size mismatch and a
            // device removal. Re-resolve whenever the id moves.
            // Phase 2: the panel samples OUR target, so none of the panel's own
            // render-target lifecycle matters any more — no eviction checks, no
            // re-resolve on churn, no handover. We own this resource outright, which
            // is the entire point of the change.
            var ownRt = BlitProbe.FeedTarget;
            if (ownRt == null) return;

            // Our target is only ever serviced if it sits in the manager's pending
            // render list — RequestRender is what puts it there, and without it a
            // submitted batch is never drawn and the target stays empty (black panel).
            // Aiming this at OUR target rather than the panel's is the point of Phase 2:
            // the UI stage remains the only legal write site, but the resource being
            // written is one nothing else owns, evicts or rebuilds.
            FeedHandover.SetPanelTarget(Prop2(ownRt, "Id"));
            FeedHandover.RequestPanelRender(ownRt);

            var currentPanelId = Prop2(ownRt, "Id")?.ToString();
            if (currentPanelId != null && currentPanelId != _resolvedPanelId)
            {
                if (_resolvedPanelId != null)
                    RttLog.Line($"Feed: panel render target changed ({_resolvedPanelId} -> {currentPanelId}); re-resolving.");
                _resolvedPanelId = currentPanelId;
                _feedState = 0;
                _feedTexture = _feedComponent = _feedRes = _feedFormat = null;
                // The old texture is deliberately not returned: the UI stage may still
                // be reading it this frame, and returning it there is the use-after-free
                // that cost us two crashes. Leaking one pooled texture per panel-target
                // churn is the cheaper mistake.
                Array.Clear(_ldrRing, 0, _ldrRing.Length); _ldrReady = null; _ringIndex = -1; _ldrMips = 1;
            }

            // Throttled: the resolve now retries rather than latching, so without a gate
            // it would run — and log — every pass while the target is still pending.
            if (_feedState == 0 && Clock.Ms - _lastResolveAttempt >= 250)
            {
                _lastResolveAttempt = Clock.Ms;
                ResolveFeedTexture();
            }
            if (_feedState != 1 || _feedTexture == null) return;

            // CopyResource cannot convert formats, and the scene renders HDR float
            // while the displayable target is sRGB LDR. CopyJob is a shader blit and
            // can convert, but it writes through a render target view — which the
            // offscreen texture does not expose. So: convert into a scratch LDR
            // target first, then CopyResource that (now format-matched) into place.
            object ldrBorrow = null;
            try
            {
                bool needsConvert = _feedFormat != null && _hdrFormat != null && !_feedFormat.Equals(_hdrFormat);
                object copySource = rtBorrow;

                // The no-convert path parks rtBorrow itself, so it cannot serve a
                // stripped pass. In practice needsConvert is always true (LDR panel
                // format vs HDR scene format) — this is a guard against parking null,
                // not a case expected to fire.
                if (!needsConvert && rtBorrow == null)
                {
                    if (_copyLogs++ < 2)
                        RttLog.Line("Feed copy: probe render was stripped but the panel format matches the " +
                                    "scene format, so there is no blit step to carry the whole-scene image. " +
                                    "Turn wholeSceneStripProbe off.");
                    return;
                }

                if (needsConvert)
                {
                    if (_copyJob == null)
                    {
                        if (_copyLogs++ < 2) RttLog.Line("Feed copy: no CopyJob — cannot convert HDR to LDR.");
                        _feedState = -1;
                        return;
                    }

                    // Long-lived LDR textures, borrowed once and never returned.
                    //
                    // Borrowing per frame and returning the previous one is a
                    // use-after-free: the handover reads the parked texture from the UI
                    // stage, asynchronously, so the camera pass can hand it back to the
                    // pool while the copy is still reading it. That raced for ~7 s and
                    // then took the game down. Owned textures remove the lifetime
                    // question entirely.
                    //
                    // THREE of them, as a ring. Two was not enough, and that was the
                    // race that killed the feed a few frames in:
                    //
                    //   pass N   writes A, parks A
                    //   pass N+1 writes B, parks B   <- A is still what DrawOne reads
                    //   pass N+2 writes A ...        <- while DrawOne may still read A
                    //
                    // The old consume-handshake was supposed to cover that, but it
                    // cleared the in-flight flag on the FIRST copy while the UI stage
                    // kept copying the same texture on every subsequent servicing. A
                    // three-slot ring gives both properties without any handshake:
                    //   * the slot we park was written a FULL pass ago, so the GPU has
                    //     certainly finished writing it;
                    //   * the slot we write has not been the parked one for a full pass,
                    //     so nothing is reading it.
                    if (_ldrRing[0] == null)
                    {
                        var res = Prop2(_feedTexture, "Resolution") ?? _feedRes;

                        // MIP COUNT MATCHED TO THE PANEL, not 1.
                        //
                        // These used to be single-mip, which meant the handover could only
                        // ever fill the panel's mip 0 — leaving levels 1..N holding
                        // DrawOne's own mip chain, built from the UI batch on a RECYCLED
                        // pool texture. Correct up close, progressively wrong as the player
                        // backs away and trilinear filtering weights the higher levels.
                        //
                        // With a matching chain the ring becomes a valid source for EVERY
                        // level: FeedHandover generates mips on it with the engine's own
                        // MipMapJob and then copies each subresource. Costs a third more
                        // memory per target (a full chain is 4/3 of mip 0) on three 512x512
                        // targets — a few hundred KB.
                        // The count comes from the D3D RESOURCE DESCRIPTION, not from the
                        // texture wrapper. ROTexture exposes Resolution and Format but no mip
                        // count of its own — MipLevels lives on OffscreenRenderTargetComponent,
                        // which we do not have here, and on the underlying
                        // D3DResourceDescription, which we do. That is also the authoritative
                        // answer: it is what the resource was actually created with, rather
                        // than what a full chain for this resolution would be.
                        int mips = 1;
                        try
                        {
                            var desc = Prop2(_feedTexture, "D3DResourceDescription");
                            var lv = Prop2(desc, "MipLevels");   // Prop2 falls back to fields
                            if (lv != null) mips = Math.Max(1, System.Convert.ToInt32(lv));
                        }
                        catch { mips = 1; }
                        _ldrMips = mips;

                        for (int i = 0; i < _ldrRing.Length; i++)
                            _ldrRing[i] = _miBorrowRt.Invoke(_texPool, new object[]
                                { "RttCameraLdr" + (char)('A' + i), _feedFormat, _feedFormat, res ?? _feedRes, mips, null, 128 });
                        _ringIndex = -1;
                        RttLog.Line($"Feed: allocated {_ldrRing.Length} persistent LDR targets " +
                                    $"(ring; write N, hand over N-1) with {mips} mip level(s) to match the " +
                                    (mips > 1
                                        ? "panel — the handover can now fill every level, not just mip 0."
                                        : "panel. Only ONE level: either the panel has no mip chain or its " +
                                          "level count was unreadable, so distance appearance is unchanged."));
                    }

                    // Advance only on a pass that actually writes, so the ring never
                    // desyncs from what the UI stage is holding.
                    int prev = _ringIndex;
                    _ringIndex = (_ringIndex + 1) % _ldrRing.Length;
                    ldrBorrow = _ldrRing[_ringIndex];

                    // Hand over the slot written on the previous pass. On the very first
                    // pass there is none, so nothing is parked and the panel keeps the
                    // test pattern for one more period.
                    _ldrReady = prev >= 0 ? _ldrRing[prev] : null;

                    var dstRtv = ViewOf(ldrBorrow, "IRenderTargetView");
                    var srcSrv = ViewOf(rtBorrow, "ITexture2DView");   // null when the probe pass was stripped

                    // THE WHOLE-SCENE FEED, resolved BEFORE the guard and before the
                    // tonemap. It used to be picked up further down, after the probe
                    // image had already been tonemapped into a scratch target — work
                    // whose only consumer was then overwritten. Resolving it here lets
                    // both the tonemap chain and (with wholeSceneStripProbe) the entire
                    // probe render be skipped, and lets rtBorrow be legitimately null.
                    //
                    // Parking the whole-scene texture DIRECTLY was tried and CTD'd
                    // (E_INVALIDARG in CopyCommandList.Replay — the raw copy path chokes
                    // on resizable engine-internal textures); a resizable texture as a
                    // CopyJob blit SOURCE is exactly what _ldrResizable does every frame,
                    // so this rides a path already in production.
                    object wsSource = null, wsSrv = null;
                    var wsPanel = WholeSceneRender.PanelSource;
                    if (wsPanel != null)
                    {
                        wsSrv = ViewOf(wsPanel, "ITexture2DView");
                        if (wsSrv != null)
                        {
                            wsSource = wsPanel;
                            if (!_wsBlitLogged)
                            {
                                _wsBlitLogged = true;
                                RttLog.Line("=== WHOLE-SCENE -> PANEL: the feed blit now sources the full " +
                                            "renderer's FinalLDRTexture. The panel shows the whole-scene render. ===");
                            }
                        }
                        else if (_wsBlitErrs++ < 2)
                        {
                            RttLog.Line("Whole-scene panel: no ITexture2DView on FinalLDRTexture — " +
                                        "probe image stays on the panel.");
                        }
                    }

                    // RETRY, DO NOT LATCH — and this is the SECOND time this exact bug has
                    // been written on this route. ResolveFeedTexture's comment already
                    // records the first (a startup-ordering race turned into a permanent
                    // disable because the retry gate only fires at 0, never at -1); this
                    // call site latched -1 on the very first failure and nobody noticed,
                    // because at one feed the failure window is usually shorter than the
                    // arm delay.
                    //
                    // At two feeds it is not. Feed 1's camera pass legitimately runs before
                    // its OWN whole-scene render has produced a frame, so PanelSource is
                    // null for the first second or so of that feed's life. Measured
                    // 2026-07-30 19:21: one such failure at 19:21:00.565, first render
                    // completed 19:21:01.639, and the feed then rendered 291 more frames
                    // into a panel that never received one — park#0 copies=0 for its whole
                    // life while feed 0 sat at park#290. The panel was black and every
                    // counter except these two read healthy.
                    //
                    // So: a streak, not a single sample. The budget is deliberately generous
                    // (~20 s at 30 passes/s) because the only thing on the other side of it
                    // is log spam, whereas the cost of latching early is a dead feed. Its
                    // own counter, NOT _copyLogs: that one is the EXCEPTION budget and
                    // trips -1 at three, so priming it here would arm a latch this path has
                    // just been proven not to deserve.
                    if (dstRtv == null || (srcSrv == null && wsSrv == null))
                    {
                        _viewLookupFails++;
                        if (_viewLookupLogs++ < 2)
                            RttLog.Line($"Feed copy: view lookup failed (dstRtv={dstRtv != null}, " +
                                        $"srcSrv={srcSrv != null}, wholeSceneSrv={wsSrv != null}) — " +
                                        $"retrying, {ViewLookupGiveUp - _viewLookupFails} pass(es) of budget left. " +
                                        "Expected once or twice at feed start, before the first whole-scene render lands.");
                        if (_viewLookupFails == ViewLookupGiveUp)
                            RttLog.Line($"Feed copy: view lookup has failed {ViewLookupGiveUp} consecutive passes — " +
                                        "giving up on THIS feed (others are unaffected). A gate cycle re-arms it.");
                        if (_viewLookupFails >= ViewLookupGiveUp) _feedState = -1;
                        return;
                    }

                    // A good pass clears the streak, so an intermittent failure can never
                    // accumulate its way to the give-up threshold across a whole session.
                    _viewLookupFails = 0;

                    // The CopyJob is what lands the frame in the exact-sized ring slot the
                    // panel copy reads from.
                    //
                    // There used to be an exposure + tonemap chain here, for the probe
                    // image. The whole-scene render's output has already been through the
                    // full pipeline's own exposure and tonemap, so that chain was
                    // tonemapping an image this blit then discarded — pure waste on every
                    // pass since the day the whole-scene route started feeding the panel.
                    //
                    // postProcess stays null — PostProcess.Normalize crashes, it needs
                    // resources this call site does not set up.
                    var blitSrc = wsSrv;

                    // cropRect is the SOURCE region, and leaving it null makes CopyJob
                    // read a rect the size of the DESTINATION rather than the whole
                    // source. With a 1024x1024 render into a 512x512 panel that copies
                    // the top-left quadrant 1:1 — which is precisely the "feed zoomed
                    // into the top left" symptom, not a projection problem.
                    //
                    // Naming the full source rect makes the blit scale instead of crop,
                    // which is what turns the extra pixels into anti-aliasing.
                    //
                    // For the whole-scene source the rect comes from ITS resolution, not
                    // the probe target's — they can legitimately differ.
                    // PER-PANEL ASPECT CROP (phase E2). The rect type is System.Drawing.
                    // Rectangle — the BCL type, resolved via Cecil after every game assembly
                    // came up empty — so the 4-int ctor is unambiguously (x, y, w, h), and
                    // ScreenQuadJob.Draw maps the getters straight into a D3D12 Viewport.
                    //
                    // Naming a SOURCE rect with the destination's aspect makes the blit crop
                    // then scale, instead of squashing: a 1024x1024 render onto a 512x256
                    // panel reads the centred 1024x512 band rather than compressing the
                    // sphere into an ellipse. On a matching aspect the crop degenerates to
                    // the full source rect, byte-identical to the old behaviour — which is
                    // why this is safe to land on the current square panels.
                    object crop = null;
                    var srcRes = Prop2(Prop2(wsSource ?? rtBorrow, "Resource") ?? wsSource ?? rtBorrow, "Resolution");
                    if (srcRes != null) crop = MakeRect(_miCopyDoWork.GetParameters()[7].ParameterType, srcRes, _feedRes);

                    // Identity log for the copy probe: the bootstrap's [copyprobe] lines
                    // print every unique CopyJob src->dst pair with the same #hash format,
                    // so OUR blit can be matched against any OTHER copy consuming the same
                    // resource. Also settles whether the engine's deferred assert
                    // "Source and destination should have the same resolution" is this
                    // call (ours legitimately rescales) or someone else's.
                    if (!_blitResLogged)
                    {
                        _blitResLogged = true;
                        var srcResource = Prop2(wsSource, "Resource") ?? wsSource;
                        var dstResource = Prop2(_feedTexture, "Resource") ?? _feedTexture;
                        RttLog.Line($"Feed blit identity: src={srcRes}#{(srcResource == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(srcResource)):x8} " +
                                    $"dst={Prop2(dstResource, "Resolution")}#{(dstResource == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(dstResource)):x8} " +
                                    "— match these hashes against [copyprobe] lines to find any foreign consumer of our image.");
                    }

                    var channels = (FeedConfig.BlitAlpha ? _channelAll : _channelRgb) ?? _channelAll;
                    _miCopyDoWork.Invoke(_copyJob, new object[]
                        { commandList, dstRtv, blitSrc, null, null, channels, null, crop });

                    copySource = ldrBorrow;
                    // Its own flag, not _copyLogs. That counter gates ERROR logging and
                    // trips _feedState = -1 at three, so incrementing it from the success
                    // path would silently swallow the next real failure. Left as it was,
                    // this line printed twice a second forever, into the same file the
                    // crash forensics come out of.
                    if (!_blitLogged) { _blitLogged = true; RttLog.Line("Feed: HDR->LDR blit via CopyJob."); }
                }

                // Park for the UI stage rather than copying here. Writing an offscreen
                // target from the camera pass is fatal even with explicit state
                // transitions — proven twice. DrawOne is the only legal write site.
                //
                // What Phase 2 changed is *which* target: it is now one we own, so the
                // handover no longer contends with the LCD system over eviction,
                // rebuilds or id churn. Same mechanism, no shared ownership.
                if (_ldrReady != null)
                    FeedHandover.ParkFrame(null, Prop2(_ldrReady, "Resource") ?? _ldrReady);
                ldrBorrow = null;   // persistent — must not be returned by the finally
            }
            finally { try { if (ldrBorrow != null) ReturnBorrowed(ldrBorrow); } catch { } }
            return;
        }
        catch (Exception e)
        {
            if (_copyLogs++ < 3) RttLog.Error("feed copy", e);
            if (_copyLogs >= 3) _feedState = -1;
        }
    }

    private static object _copyJob;
    private static MethodInfo _miCopyDoWork;
    private static object _channelAll, _channelRgb;
    // PER-FEED (phase C1a): the LDR ring. session-owned; see CopyToFeed. Get-only —
    // the array is allocated once per instance and only ever indexed, never reassigned.
    // This is also the biggest per-feed VRAM item, so it is the thing D3 measures when
    // it puts a number on the max-resident-feeds constant.
    private static object[] _ldrRing => Feeds.Cur.LdrRing;

    // Mip levels the ring was allocated with, matched to the panel. Read by FeedHandover to
    // decide how many subresources to generate and copy. 1 = no chain, old behaviour.
    internal static int LdrMips => _ldrMips;
    private static int _ldrMips
    { get => Feeds.Cur.LdrMips; set => Feeds.Cur.LdrMips = value; }
    private static object _ldrReady                              // the slot handed to the UI stage
    { get => Feeds.Cur.LdrReady; set => Feeds.Cur.LdrReady = value; }
    private static int _ringIndex
    { get => Feeds.Cur.RingIndex; set => Feeds.Cur.RingIndex = value; }
    private static bool _blitLogged;
    private static bool _blitResLogged;

    // The far-clip lever, and its one-shot log. Kept next to the blit fields because both
    // are per-session latches cleared by Reset().
    private static double _farClipLogged = double.NaN;

    private static float FarClip(float engineFar)
    {
        double clip = FeedConfig.WholeSceneFarClip;
        return clip > 0 && engineFar > clip ? (float)clip : engineFar;
    }

    private static void LogFarClipOnce(float far, float veryFar)
    {
        if (_farClipLogged.Equals((double)far)) return;
        _farClipLogged = far;
        RttLog.Line(FeedConfig.WholeSceneFarClip > 0
            ? $"Feed far clip: {far:F0} m (veryFar {veryFar:F0} m untouched — planets/sky still render). " +
              "Watch ourDraw(cpu submit) in PERF for the draw-count saving."
            : $"Feed far clip: engine value {far:F0} m (wholeSceneFarClip=0, no override).");
    }
    private static long _firstPassAt;
    private static bool _startupLogged, _startupDoneLogged;

    // An exposure texture the engine already computed, read without writing anything.
    // Its VALUE is the player's adaptation, so brightness will not be ideal — but the
    // question this answers is whether ApplyToneMapping alone is safe, and for that
    // any valid exposure texture will do.
    // A 1x1 texture holding a CONSTANT exposure we choose.
    //
    // Reusing the engine's eye-adaptation texture is safe but gives the wrong value:
    // it is adapted to the player's surroundings, so standing inside a dim ship
    // produces a high exposure, and multiplying a sunlit space scene by that keeps
    // everything clamped — which is exactly the "brightness looks the same" result.
    //
    // The pool clears a borrowed target to a colour we supply, so a 1x1 target with
    // clearColor = exposure IS a constant-exposure texture. Fully ours, nothing
    // shared, no ComputeExposure, and tunable live from feed-config.txt.

    // The panel's resolution multiplied by renderScale, clamped so a typo cannot ask
    // for a 16K render target.


    // System.Drawing.Rectangle(0, 0, w, h) built by reflection, since the parameter's
    // assembly is not referenced here.
    private static bool _rectLogged;

    // Build the SOURCE rect for the blit: the largest centred region of the source whose
    // aspect matches the destination panel. dstResolution null (or degenerate) falls back
    // to the full source rect — the pre-E2 behaviour, and the correct one when the panel's
    // shape is unknown.
    private static object MakeRect(Type nullableRect, object resolution, object dstResolution)
    {
        try
        {
            var t = Nullable.GetUnderlyingType(nullableRect) ?? nullableRect;
            int w = (int)(Prop2(resolution, "X") ?? 0);
            int h = (int)(Prop2(resolution, "Y") ?? 0);
            if (w <= 0 || h <= 0) return null;

            // The centred aspect crop. Integer math ordered to avoid rounding drift:
            // compare aspects as cross-products (sw*dh vs dw*sh — exact, no floats), then
            // derive the cropped axis from the kept one.
            int cx = 0, cy = 0, cw = w, ch = h;
            int dw = (int)(Prop2(dstResolution, "X") ?? 0);
            int dh = (int)(Prop2(dstResolution, "Y") ?? 0);
            if (dw > 0 && dh > 0 && (long)w * dh != (long)dw * h)
            {
                if ((long)w * dh > (long)dw * h)
                {
                    // Source wider than the panel: keep full height, crop the sides.
                    cw = (int)((long)h * dw / dh);
                    cx = (w - cw) / 2;
                }
                else
                {
                    // Source taller: keep full width, crop top and bottom.
                    ch = (int)((long)w * dh / dw);
                    cy = (h - ch) / 2;
                }
            }

            var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 4
                && c.GetParameters().All(p => p.ParameterType == typeof(int)));
            if (ctor == null)
            {
                if (!_rectLogged) { _rectLogged = true; RttLog.Line($"Blit: {t.Name} has no (int,int,int,int) ctor — crop rect unavailable."); }
                return null;
            }

            // (x, y, width, height) — System.Drawing.Rectangle, so the convention is the
            // BCL's, not a guess. Verified via Cecil: the cropRect parameter's inner type
            // scope is System.Drawing.Primitives.
            var r = ctor.Invoke(new object[] { cx, cy, cw, ch });
            if (!_rectLogged)
            {
                _rectLogged = true;
                RttLog.Line(cw == w && ch == h
                    ? $"Blit: source crop rect {w}x{h} (full source; aspects match) — the blit scales to the panel instead of cropping."
                    : $"Blit: ASPECT CROP {cw}x{ch} at ({cx},{cy}) of a {w}x{h} source, matching the panel's {dw}x{dh} — " +
                      "centred crop then scale, no distortion.");
            }
            return r;
        }
        catch (Exception e) { RttLog.Error("crop rect", e); return null; }
    }


    // A RenderView on the orbit camera, for the whole-scene route.
    //
    // Shares the machinery InstallOurCamera already uses — copy every field off the
    // player's live RenderView so projection, clipping, FOV and resolution stay current,
    // then override the three that place the camera. The difference is that the
    // whole-scene route needs the OBJECT rather than the swap: it installs and restores
    // around its own Draw call, on its own schedule.
    //
    // Deliberately NOT gated on FeedConfig.SwapCamera. That flag scopes the probe pass's
    // swap; this is a different pass with a different lifetime, and tying them together
    // would mean one route's safety switch silently disabling the other.
    //
    // Returns null when the orbit camera has not been placed yet — the caller then runs
    // from the player's viewpoint, which is the intended stage-3a behaviour rather than
    // a failure.
    public static object WholeSceneRenderView()
    {
        if (_lastCamWorld == null || _lastViewD == null || _settings == null) return null;
        try
        {
            _renderViewField ??= _settings.GetType().GetFields(Any)
                .FirstOrDefault(f => f.Name == "_renderView");
            var theirs = _renderViewField?.GetValue(_settings);
            if (theirs == null) return null;

            var rvType = theirs.GetType();
            _rvFields ??= rvType.GetFields(Any).Where(f => !f.IsStatic).ToArray();
            _wsRenderView ??= System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(rvType);

            // Baseline: everything the player's live view has — clipping planes, FOV,
            // smoothing state, environment roots — so anything we do not explicitly
            // rebuild stays current with their settings.
            foreach (var f in _rvFields)
            {
                try { f.SetValue(_wsRenderView, f.GetValue(theirs)); } catch { }
            }

            // The first version stopped here plus three field overrides (ViewD, InvViewD,
            // CameraPosition), and the feed showed the cost of every field it did NOT
            // override: the player's 16:9 PROJECTION squashed into the 1:1 panel, and the
            // sky stationary because the skybox renders through the floating-origin
            // ViewAt0/InvViewAt0 pair — which stayed glued to the player's head. Patching
            // matrices one by one is a losing game; the engine has the consistent-rebuild
            // API, and the _cbRenderView recipe below in this file already proved it safe:
            //
            //   1. null _cameraSpeedBuffer — Update() -> ResetContext() Clears it, and the
            //      uninitialized clone's copy is shared with the PLAYER'S live view.
            //      smooth:true should keep ResetContext unreachable; belt and braces.
            //   2. SetResolution FIRST — Update() derives FovV from _resolution, so the
            //      other order computes our FOV from the player's screen. This is also
            //      what makes the projection aspect 1:1 instead of the player's 16:9.
            //   3. SetCameraParameters with the orbit transform — rebuilds ViewD, InvViewD,
            //      CameraPosition, ViewAt0/InvViewAt0 AND the projection set coherently.
            foreach (var f in _rvFields)
                if (f.Name == "_cameraSpeedBuffer")
                    try { f.SetValue(_wsRenderView, null); } catch { }

            _miRvSetResolution ??= rvType.GetMethod("SetResolution", Any);
            _miRvSetCamera ??= rvType.GetMethod("SetCameraParameters", Any);
            if (FeedConfig.WholeSceneCameraRebuild == 0 || _miRvSetResolution == null || _miRvSetCamera == null)
            {
                // The PROVEN baseline: three field overrides. Costs a squashed aspect
                // (player's 16:9 projection into our 1:1 target) and a stationary sky
                // (inherited ViewAt0/InvViewAt0), but it ran the milestone session
                // without incident. The full rebuild below fixed both — and then the
                // session died on a GPU page fault in a bloom chain 45 renders later,
                // with THREE camera sub-changes landed at once (resolution, rebuild,
                // jitter zeroing), so the guilty one is unknown. Hence the flag: flip
                // wholeSceneCameraRebuild live to re-test, bisect the sub-changes if it
                // reproduces, and always have this baseline to fall back to.
                SetRvOn(_wsRenderView, "ViewD", _lastViewD);
                SetRvOn(_wsRenderView, "InvViewD", _lastCamWorld);
                SetRvOn(_wsRenderView, "CameraPosition", Prop2(_lastCamWorld, "Translation"));
                return _wsRenderView;
            }

            // 1:1 render target, built from the same Vector2I type the view carries.
            if (_wsResolution == null)
            {
                var resField = _rvFields.FirstOrDefault(f => f.Name == "_resolution");
                var ctor = resField?.FieldType.GetConstructor(new[] { typeof(int), typeof(int) });
                _wsResolution = ctor?.Invoke(new object[] { FeedConfig.WholeSceneWidth, FeedConfig.WholeSceneHeight });
                if (_wsResolution == null)
                {
                    if (_wsRvErrs++ < 2) RttLog.Line("Whole-scene camera: could not build Vector2I resolution.");
                    return null;
                }
            }
            _miRvSetResolution.Invoke(_wsRenderView, new[] { _wsResolution });

            float fovH    = System.Convert.ToSingle(Prop2(_wsRenderView, "FovH") ?? 0f);
            float near    = System.Convert.ToSingle(Prop2(_wsRenderView, "NearClipping") ?? 0f);
            float far     = System.Convert.ToSingle(Prop2(_wsRenderView, "FarClipping") ?? 0f);
            float veryFar = System.Convert.ToSingle(Prop2(_wsRenderView, "VeryFarClipping") ?? 0f);
            var projOff   = Prop2(_wsRenderView, "ProjectionOffset");
            bool ortho    = System.Convert.ToBoolean(Prop2(_wsRenderView, "IsOrthographic") ?? false);

            // Far-clip override, OUR view only. The second render's cost is CPU submit —
            // command-building for every culled-in draw — so the draw COUNT is the lever,
            // and FarClipping is what the culling reads. VeryFarClipping is deliberately
            // untouched: the planet/sky layer renders through it, so distant planets and
            // their atmospheres survive while asteroid/grid geometry beyond the clip is
            // dropped. Applied identically to the camera CB build below, so culling and
            // shaders agree on the projection.
            far = FarClip(far);

            _miRvSetCamera.Invoke(_wsRenderView, new object[]
                { _lastCamWorld, fovH, near, far, veryFar, projOff, /* smooth */ true, ortho });
            LogFarClipOnce(far, veryFar);

            // Kill TAA jitter for our render: the inherited jitter phase was computed for
            // the player's 4K target, and sub-pixel offsets that large at 512x512 are a
            // live suspect for the surface banding. A 5 fps feed gains nothing from
            // temporal jitter anyway. JitteredProjection := Projection, offset := zero.
            // Mode 3 skips this, to isolate it if the fault ever needs a further bisect.
            if (FeedConfig.WholeSceneCameraRebuild != 3)
            {
                try
                {
                    var proj = _rvFields.FirstOrDefault(f => f.Name.Contains("<Projection>", StringComparison.Ordinal));
                    var jitProj = _rvFields.FirstOrDefault(f => f.Name.Contains("<JitteredProjection>", StringComparison.Ordinal));
                    var jitOff = _rvFields.FirstOrDefault(f => f.Name.Contains("<JitterPixelOffset>", StringComparison.Ordinal));
                    if (proj != null && jitProj != null)
                        jitProj.SetValue(_wsRenderView, proj.GetValue(_wsRenderView));
                    if (jitOff != null)
                        jitOff.SetValue(_wsRenderView, Activator.CreateInstance(jitOff.FieldType));
                }
                catch { }
            }

            // PREVIOUS-FRAME state must be OURS, not the player's. The field copy above
            // stamped the PLAYER'S LastFrameCameraPosition into our view, so motion
            // vectors were computed as (orbit position − player position) — kilometres of
            // false motion feeding FSR/TAA, which is the ghost-trail smear. Our real
            // previous position is the orbit's last render; first render uses current
            // (zero motion), which is exactly right for a fresh view.
            try
            {
                var cur = Prop2(_lastCamWorld, "Translation");
                var prev = _wsPrevCamPos ?? cur;
                SetRvOn(_wsRenderView, "LastFrameCameraPosition", prev);
                SetRvOn(_wsRenderView, "LastFrameCameraPositionWithCuts", prev);
                _wsPrevCamPos = cur;
            }
            catch { }

            // MODE 2 — THE RACE FIX, and the intended production setting.
            //
            // Both rebuild CTDs faulted at DIFFERENT sites (mid-bloom with a real VA,
            // then ScenePreparation with a null descriptor): the signature of a race,
            // not a deterministic bad size. While our view is INSTALLED in the shared
            // Settings._renderView, main-thread systems read that field freely — and a
            // view whose RESOLUTION diverges from the player's poisons whatever
            // buffer-size math the unlucky reader feeds. The baseline survives because
            // camera-position divergence never sizes anything.
            //
            // So: let SetResolution shape the PROJECTION (the 1:1 aspect is baked into
            // the matrices by SetCameraParameters above), then put the PLAYER'S
            // resolution back in the field. Readers can no longer observe a divergent
            // size; our render's actual pixel count comes from the LDR buffer we hand
            // Draw, not from this field.
            if (FeedConfig.WholeSceneCameraRebuild == 2)
            {
                try
                {
                    var resField = _rvFields.FirstOrDefault(f => f.Name == "_resolution");
                    if (resField != null)
                        resField.SetValue(_wsRenderView, resField.GetValue(theirs));
                }
                catch { }
            }

            if (!_wsRvLogged)
            {
                _wsRvLogged = true;
                RttLog.Line($"Whole-scene camera: rebuilt via SetResolution({FeedConfig.WholeSceneWidth}x" +
                            $"{FeedConfig.WholeSceneHeight}) + SetCameraParameters(orbit, fovH={fovH:F2}) — " +
                            "projection aspect, ViewAt0/InvViewAt0 (the stationary-sky pair) and camera " +
                            "position now all come from one coherent engine rebuild. Jitter zeroed.");
            }

            // VERIFY THE REBUILD TOOK, with numbers rather than optimism. The panel
            // showed baseline symptoms (squash + head-tracked sky) while the rebuild
            // path logged success, so one of two things is true: SetCameraParameters
            // did not actually change the matrices, or downstream consumes the player's
            // values regardless. M11==M22 means square aspect; ours differing from the
            // player's on InvViewAt0 means the sky pair really was rebuilt. Whichever
            // half is FALSE names the bug.
            long dnow = Clock.Ms;
            if (dnow - _wsDiagMs >= 10000)
            {
                _wsDiagMs = dnow;
                try
                {
                    string DescProj(object rv)
                    {
                        var pm = _rvFields.FirstOrDefault(f => f.Name.Contains("<Projection>", StringComparison.Ordinal))?.GetValue(rv);
                        var m = Prop2(pm, "Projection");
                        return m == null ? "?" :
                            $"M11={System.Convert.ToSingle(Prop2(m, "M11") ?? MField(m, "M11")):F4} " +
                            $"M22={System.Convert.ToSingle(Prop2(m, "M22") ?? MField(m, "M22")):F4}";
                    }
                    string DescAt0(object rv)
                    {
                        var m = _rvFields.FirstOrDefault(f => f.Name.Contains("<InvViewAt0>", StringComparison.Ordinal))?.GetValue(rv);
                        return m == null ? "?" :
                            $"M31={System.Convert.ToSingle(Prop2(m, "M31") ?? MField(m, "M31")):F3} " +
                            $"M32={System.Convert.ToSingle(Prop2(m, "M32") ?? MField(m, "M32")):F3}";
                    }
                    RttLog.Line($"Whole-scene camera CHECK: ours proj[{DescProj(_wsRenderView)}] " +
                                $"at0[{DescAt0(_wsRenderView)}]  player proj[{DescProj(theirs)}] " +
                                $"at0[{DescAt0(theirs)}]  (square aspect = M11==M22; rebuilt sky pair = " +
                                "our at0 differing from the player's and changing as the orbit turns)");
                }
                catch (Exception e) { RttLog.Error("whole-scene camera check", e); }
            }
            return _wsRenderView;
        }
        catch (Exception e) { RttLog.Error("whole-scene render view", e); return null; }
    }

    // A camera CONSTANT BUFFER for the whole-scene render, built by the same machinery
    // the probe pass uses every 33ms (FullCameraSettings + tracked conversion).
    //
    // WHY THIS EXISTS. The matrix check proved the installed RenderView rebuild is
    // perfect — square projection, orbiting At0 pair — and the panel still showed the
    // player's aspect and a head-tracked sky. The shaders never read the view: they
    // read the per-frame camera CB, which the engine builds from the PLAYER'S view
    // before Draw runs, and our nested Draw inherits it. Culling and camera-relative
    // positioning read the installed view directly, which is why geometry orbited
    // while the sky did not — the exact split the symptoms showed.
    //
    // So the whole-scene bracket must do what the probe pass already does: build our
    // own CB and CameraCbSwap it in for the duration of the call.
    public static object WholeSceneCameraCb()
    {
        try
        {
            var view = CurrentViewSlim();
            if (view == null || _wsResolution == null) return null;
            return BuildCameraCb(view, _wsResolution);
        }
        catch (Exception e) { RttLog.Error("whole-scene camera CB", e); return null; }
    }

    // PER-FEED (phase C1a). _wsResolution in particular: as a static it was cached once
    // and never cleared, which silently broke EVERY live resolution change and cost a
    // day of misdiagnosis ("1024 is broken", which it was not). With N feeds at N sizes
    // a shared slot would not be a subtle bug — it would be the wrong size on purpose.
    private static object _wsRenderView
    { get => Feeds.Cur.WsRenderView; set => Feeds.Cur.WsRenderView = value; }
    private static object _wsResolution
    { get => Feeds.Cur.WsResolution; set => Feeds.Cur.WsResolution = value; }

    private static bool _wsRvLogged;
    private static int _wsRvErrs;
    private static long _wsDiagMs;

    // PER-FEED: previous-frame camera position for motion vectors.
    private static object _wsPrevCamPos
    { get => Feeds.Cur.WsPrevCamPos; set => Feeds.Cur.WsPrevCamPos = value; }

    private static object MField(object o, string name)
    {
        try { return o?.GetType().GetField(name, Any)?.GetValue(o); } catch { return null; }
    }

    private static void SetRvOn(object rv, string name, object value)
    {
        if (value == null || rv == null) return;
        var f = _rvFields.FirstOrDefault(x =>
            x.Name.Contains($"<{name}>", StringComparison.Ordinal) || x.Name == name);
        if (f == null || !f.FieldType.IsInstanceOfType(value)) return;
        try { f.SetValue(rv, value); } catch { }
    }



    // ---------------------------------------------------------------- camera CB

    // The camera constant buffer, and the two independent defects that live in it.
    // Both fixes are separately switchable because they fail in different ways and
    // batching them would make an ambiguous result.
    private static object BuildCameraCb(object view, object res)
    {
        object cam = null;

        if (FeedConfig.FullCameraCb)
            cam = FullCameraSettings(res);

        // The cheap path, and the fallback if the full build is unavailable: it is what
        // the engine's own probe pass uses, so it is never wrong, only incomplete.
        cam ??= Convert(_tCamSettings, view, _tRenderViewSlim);

        var tracked = Convert(_tTrackedCam, cam, _tCamSettings);
        if (tracked == null) return null;

        if (FeedConfig.FixScreenRes) StampScreenResolution(tracked, res);
        StampPreviousCamera(tracked);

        return _miCreateCb.MakeGenericMethod(_tTrackedCam)
            .Invoke(_bufMgr, new object[] { "rttCameraSettings", tracked });
    }

    // TrackedCameraSettings.PreviousCamera — the field nothing on our path ever wrote.
    //
    // Only TWO methods in the engine write it: SettingsGroup.CreateCameraSettings (the
    // per-frame CB build, which reads SettingsManager.PreviousRenderView) and one surfel
    // job. Our CB goes through CameraSettings.op_Explicit, which is neither — so
    // PreviousCamera_.ViewTransform has been sitting at the ZERO MATRIX in every camera
    // constant buffer this feed has ever rendered with.
    //
    // Anything doing temporal reprojection reads it. SkyboxMotionVectorsPixel.hlsl builds
    // its motion vector as
    //     mul(positionWorld, (float3x3) MatrixFromWorldTransform(PreviousCamera_.ViewTransform))
    // and the RT denoiser reprojects last frame's samples the same way. Through a zero
    // matrix every reprojection lands nowhere, history never matches, and the accumulator
    // discards it — which is a permanent-noise machine, not a converging one. That is the
    // ray-traced ambient "wobbling around" in the feed.
    //
    // Fixing it is bookkeeping: hold the value built from OUR view last render, stamp it
    // into this render's CB, then compute this render's value for next time. The engine
    // does exactly this with SettingsManager.PreviousRenderView, one frame apart; ours are
    // one SECOND-RENDER apart, which is the correct pairing for our own history.
    //
    // First render has no previous, so it keeps the zero — one noisy frame, then correct.
    // PER-FEED (phase C1a): feed B's previous frame is not feed A's, and pairing them
    // across feeds is a motion-vector smear by construction.
    private static object _wsPrevCameraSettings
    { get => Feeds.Cur.WsPrevCameraSettings; set => Feeds.Cur.WsPrevCameraSettings = value; }

    private static MethodInfo _miPrevCamFromView;
    private static FieldInfo _fTrackedPrevCam;
    private static int _prevCamState;          // 0 untried, 1 ok, -1 unavailable
    private static bool _prevCamLogged;

    private static void StampPreviousCamera(object tracked)
    {
        if (_prevCamState == -1) return;
        try
        {
            if (_prevCamState == 0)
            {
                _prevCamState = -1;
                _fTrackedPrevCam = _tTrackedCam?.GetField("PreviousCamera", Any);
                var prevType = _fTrackedPrevCam?.FieldType;
                // op_Implicit(in RenderView) -> PreviousCameraSettings. RenderView, not
                // RenderViewSlim: it reads ViewD/InvViewD, which only the full view has.
                _miPrevCamFromView = prevType?.GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == prevType
                                      && m.GetParameters().Length == 1);
                if (_fTrackedPrevCam == null || _miPrevCamFromView == null)
                {
                    RttLog.Line("Previous camera: TrackedCameraSettings.PreviousCamera or its " +
                                "op_Implicit not found — the feed's temporal reprojection keeps " +
                                "reading a zero matrix and RT ambient will not converge.");
                    return;
                }
                _prevCamState = 1;
            }

            // Stamp LAST render's value into THIS render's buffer.
            if (_wsPrevCameraSettings != null)
            {
                _fTrackedPrevCam.SetValue(tracked, _wsPrevCameraSettings);
                if (!_prevCamLogged)
                {
                    _prevCamLogged = true;
                    RttLog.Line("=== PREVIOUS CAMERA stamped into the feed's camera CB. It was the ZERO " +
                                "matrix on every previous render — CameraSettings.op_Explicit does not " +
                                "set PreviousCamera, and only the engine's own CreateCameraSettings " +
                                "does. Temporal reprojection (RT history, skybox motion vectors) now " +
                                "has a real previous view to project through. ===");
                }
            }

            // Then record THIS render's view as next render's previous.
            if (_wsRenderView != null)
                _wsPrevCameraSettings = _miPrevCamFromView.Invoke(null, new[] { _wsRenderView });
        }
        catch (Exception e) { _prevCamState = -1; RttLog.Error("stamp previous camera", e); }
    }

    // CameraSettings -> TrackedCameraSettings (op_Explicit) stamps
    // CoreSystems.ScreenBuffers.PreUpscaleResolution into Screen.Resolution — the
    // PLAYER'S 3840x2160 — while we rasterise 512x512. Shaders reconstruct their view
    // ray with ScreenToUV() = rcp(Screen_.Resolution), so every one of them is out by
    // 3840/512 = 7.5x. That is the sky being far too zoomed and rotating far too fast,
    // and it is not only the sky: Pass_Pixel_Indirect.hlsli uses the same ScreenToUV, so
    // view vectors, specular response and the depth-based dimming in the geometry pass
    // are all mis-scaled too.
    //
    // The engine's own probe path never hits this because ExecuteEnvironmentProbeUpdate
    // hand-builds the struct with Screen.Resolution = the cube face's own resolution.
    // This does the same thing to the boxed copy before the CB is created; nothing
    // global is touched.
    private static void StampScreenResolution(object tracked, object res)
    {
        try
        {
            var fScreen = _tTrackedCam.GetField("Screen", Any);
            if (fScreen == null) { LogScreenResOnce("TrackedCameraSettings.Screen not found"); return; }

            var screen = fScreen.GetValue(tracked);
            var fRes = screen?.GetType().GetField("Resolution", Any);
            if (fRes == null) { LogScreenResOnce("ScreenSettings.Resolution not found"); return; }

            var was = fRes.GetValue(screen);
            int x = System.Convert.ToInt32(Prop2(res, "X") ?? 0);
            int y = System.Convert.ToInt32(Prop2(res, "Y") ?? 0);
            var want = MakeVector2(x, y);
            if (want == null) { LogScreenResOnce("could not construct Vector2"); return; }

            fRes.SetValue(screen, want);

            // PrevResolution, and it is the same bug one field over. ScreenSettings
            // carries Resolution, PrevResolution and JitterUVDelta; this stamp only ever
            // fixed the first, so PrevResolution stayed at the PLAYER'S 3840x2160 while we
            // rasterise 512x512. Anything scaling a previous-frame lookup by it — every
            // temporal reprojection, RT history included — was out by 7.5x in each axis,
            // which is indistinguishable from having no history at all.
            //
            // Our render is a fixed 512x512 every time, so previous == current by
            // construction. JitterUVDelta goes to zero for the same reason our projection
            // has no jitter (mode 2 zeroes JitteredProjection): a non-zero delta would
            // reproject against a sub-pixel offset that is not in our matrices.
            string extra = "";
            var fPrev = screen.GetType().GetField("PrevResolution", Any);
            if (fPrev != null)
            {
                extra += $" PrevResolution {fPrev.GetValue(screen)} -> {want}.";
                fPrev.SetValue(screen, want);
            }
            var fJitter = screen.GetType().GetField("JitterUVDelta", Any);
            if (fJitter != null)
            {
                var wasJ = fJitter.GetValue(screen);
                var zero = MakeVector2(0, 0);
                if (zero != null) { fJitter.SetValue(screen, zero); extra += $" JitterUVDelta {wasJ} -> 0."; }
            }

            fScreen.SetValue(tracked, screen);
            LogScreenResOnce($"Screen.Resolution {was} -> {want} " +
                             "(the engine's value is the player's screen; every ScreenToUV was scaled by the ratio)." +
                             extra);
        }
        catch (Exception e) { LogScreenResOnce("threw: " + e.Message); }
    }

    private static string _screenResLog;

    private static void LogScreenResOnce(string what)
    {
        if (what == _screenResLog) return;   // change-detection, not one-shot: a one-shot
        _screenResLog = what;                // log here would print the pre-resolve value
        RttLog.Line("Camera CB: " + what);
    }

    // Build the camera as a full RenderView and run it through the engine's own
    // CameraSettings.CreateNonjitteredCameraSettings, instead of the RenderViewSlim
    // conversion.
    //
    // RenderViewSlim -> CameraSettings writes 7 of CameraSettings' 14 fields and leaves
    // these at ZERO: ViewTransform, InvViewTransform (the double-precision camera world
    // transform), TanFOV, FOVScaleFactor, CameraFlags, PositionDelta, CameraSpeed. Any
    // shader reconstructing a world position from the camera therefore believes the
    // camera sits at the world origin.
    //
    // It is also not self-contained — it reads CoreSystems.Settings.RenderView and stamps
    // the PLAYER's camera position into MainViewCameraPos, which SpherizationCommon.hlsli
    // (planet horizon curvature) and TriplanarSingle/MultiVertex.hlsl (voxel surface
    // texturing) both sample. CreateNonjitteredCameraSettings sets it to zero.
    // PER-FEED (phase C1a): the RenderView this feed's camera constant buffer is built
    // from. It carries this feed's resolution, so it cannot be shared across sizes.
    private static object _cbRenderView
    { get => Feeds.Cur.CbRenderView; set => Feeds.Cur.CbRenderView = value; }

    private static MethodInfo _miCreateNonjittered, _miRvSetCamera, _miRvSetResolution;
    private static bool _fullCbBlocked, _fullCbLogged;

    private static object FullCameraSettings(object res)
    {
        if (_fullCbBlocked || _settings == null || _lastCamWorld == null) return null;
        try
        {
            var theirs = _renderViewField?.GetValue(_settings)
                      ?? ResolveRenderViewField()?.GetValue(_settings);
            if (theirs == null) { BlockFullCb("SettingsManager._renderView not readable"); return null; }
            var rvType = theirs.GetType();

            _miCreateNonjittered ??= _tCamSettings?.GetMethod("CreateNonjitteredCameraSettings",
                BindingFlags.Public | BindingFlags.Static);
            _miRvSetCamera ??= rvType.GetMethod("SetCameraParameters", Any);
            _miRvSetResolution ??= rvType.GetMethod("SetResolution", Any);
            if (_miCreateNonjittered == null || _miRvSetCamera == null || _miRvSetResolution == null)
            {
                BlockFullCb($"missing entry point (create={_miCreateNonjittered != null} " +
                            $"setCam={_miRvSetCamera != null} setRes={_miRvSetResolution != null})");
                return null;
            }

            // Start from a copy of the engine's view so projection conventions, clipping
            // planes and FOV are the engine's rather than guessed at.
            _rvFields ??= rvType.GetFields(Any).Where(f => !f.IsStatic).ToArray();
            _cbRenderView ??= System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(rvType);
            foreach (var f in _rvFields)
                try { f.SetValue(_cbRenderView, f.GetValue(theirs)); } catch { }

            // RenderView is a struct whose ONE reference field is Queue<double>
            // _cameraSpeedBuffer, so every copy — ours included — aliases the same queue.
            // SetCameraParameters -> Update() -> ResetContext() calls Clear() on it, which
            // would wipe the PLAYER's camera-speed history through our copy. ResetContext
            // null-guards the field, so nulling it on ours makes that path inert. We also
            // pass smooth: true, which should stop Update() reaching ResetContext at all —
            // belt and braces, because the failure is silent and lands on the player.
            foreach (var f in _rvFields)
                if (f.Name == "_cameraSpeedBuffer")
                    try { f.SetValue(_cbRenderView, null); } catch { }

            // Resolution FIRST. Update() derives FovV from _resolution, so setting the
            // camera before the resolution computes our FOV from the player's screen.
            _miRvSetResolution.Invoke(_cbRenderView, new[] { res });

            float fovH      = System.Convert.ToSingle(Prop2(_cbRenderView, "FovH") ?? 0f);
            float near      = System.Convert.ToSingle(Prop2(_cbRenderView, "NearClipping") ?? 0f);
            float far       = FarClip(System.Convert.ToSingle(Prop2(_cbRenderView, "FarClipping") ?? 0f));
            float veryFar   = System.Convert.ToSingle(Prop2(_cbRenderView, "VeryFarClipping") ?? 0f);
            var projOffset  = Prop2(_cbRenderView, "ProjectionOffset");
            bool ortho      = System.Convert.ToBoolean(Prop2(_cbRenderView, "IsOrthographic") ?? false);

            _miRvSetCamera.Invoke(_cbRenderView, new object[]
            {
                _lastCamWorld, fovH, near, far, veryFar, projOffset, /* smooth */ true, ortho,
            });

            var cam = _miCreateNonjittered.Invoke(null, new[] { _cbRenderView });

            if (!_fullCbLogged)
            {
                _fullCbLogged = true;
                // Reflection on a BOXED struct only mutates the box if Invoke passes the
                // box's interior as `this`. It does — but that is exactly the kind of
                // assumption that has cost this project a session, so it is measured.
                RttLog.Line($"Camera CB: FULL build — camera position {Prop2(_cbRenderView, "CameraPosition")}, " +
                            $"TanFOV={Prop2(cam, "TanFOV")} FOVScaleFactor={Prop2(cam, "FOVScaleFactor")} " +
                            $"MainViewCameraPos={Prop2(cam, "MainViewCameraPos")}. " +
                            "Position at the origin or TanFOV zero means the boxed-struct mutation did not take.");
            }
            return cam;
        }
        catch (Exception e) { BlockFullCb("threw: " + e.Message); RttLog.Error("full camera cb", e); return null; }
    }

    private static void BlockFullCb(string why)
    {
        if (_fullCbBlocked) return;
        _fullCbBlocked = true;
        RttLog.Line($"Camera CB: full build unavailable ({why}) — falling back to the slim conversion.");
    }

    private static FieldInfo ResolveRenderViewField()
    {
        _renderViewField ??= _settings?.GetType().GetFields(Any)
            .FirstOrDefault(f => f.Name.Contains("renderView", StringComparison.OrdinalIgnoreCase)
                              && !f.Name.Contains("previous", StringComparison.OrdinalIgnoreCase)
                              && !f.Name.Contains("freez", StringComparison.OrdinalIgnoreCase));
        return _renderViewField;
    }

    // ------------------------------------------------------- probe settings

    // EnvironmentProbeSettings.EnableRecursiveReflections is read LIVE inside
    // IndirectEnvironmentPassJob.DoWork — it picks between the job's own _defaultTexture
    // (a flat cube) and EnvironmentProbeManager.CloseIBL/FarIBL. It ships FALSE, so the
    // ambient term in our feed is a constant colour rather than the real environment.
    //
    // Because it is read live, a scoped set/restore around our pass is enough: the
    // player's own probe updates keep the shipped value.
    //
    // DimDistance is NOT here, and the difference matters. It reaches the shader through
    // CommonResources.SettingsGroup.CreateFrameSettings, which builds the frame's
    // GlobalSettings constant buffer once in OnBeginDraw — long before our hook runs — so
    // a scoped swap would be a silent no-op. See ApplyDimDistance.
    private static FieldInfo _environmentField;

    // Turn OCCLUSION CULLING off for our pass.
    //
    // The engine's two-pass main-view cull is: first pass -> depth prepass -> BUILD HiZ ->
    // second pass. We run both passes back to back and never build HiZ, so the second pass
    // tests visibility against whatever the engine's own main view left in the occlusion
    // context — Hi-Z from the PLAYER'S viewpoint, at the player's depth. Geometry plainly
    // visible to our camera sits "behind" something in that buffer and is rejected.
    //
    // The tell was precise: exactly ONE correct frame — ship and distant planet — and then
    // a single blob. On the first pass the context happened to hold usable data; after
    // that it was ours, stale, and rejected nearly everything. A pass that works once and
    // then degrades is a state-carryover problem, not a wiring one.
    //
    // CullingSetup.IsOcclusionCullingAllowed reads SettingsManager.HZBO live, so a scoped
    // set/restore around our pass is enough. We lose occlusion culling — some overdraw,
    // no correctness cost — which is the right trade at 512x512 where fill is nearly free.
    //
    // The real fix is a private OcclusionContext plus our own depth prepass and HiZ build.
    // This is the cheap version that makes the deferred route usable now.
    // Take the player's LOD transition state out of reach for the duration of our pass.
    //
    // THE ACTUAL CAUSE OF THE SHIP-LIGHT FLICKER, and it outlived two plausible fixes
    // because it is neither of the things they addressed. CullingJob.DoWork does not
    // accept a LODTransitionContext — it reads the global:
    //
    //     ldsfld   CoreSystems.DrawContexts
    //     callvirt DrawContextManager.get_LODTransitions()
    //
    // The parameter test, failing exactly where it is meant to. LODTransitionContext holds
    // per-view temporal state: which objects are part-way through an LOD crossfade and how
    // far. Our camera sits at a different distance from the same objects, so our cull
    // writes different transition state for them, the player's view reads it next frame,
    // and their geometry pops between levels. Private visibility lists did not help.
    // Private geometry buffers did not help. Neither was ever the shared thing.
    //
    // NULL rather than a private context, because CullingGeometryJob.DoWork null-guards
    // every use of it and the engine's own assert says null is legal given a forced LOD
    // method:
    //
    //     "lodTransitions != null || (_forcedLODMethod.HasValue &&
    //                                 _forcedLODMethod != LODMethod.TransitionTimeBased)"
    //
    // CustomCullJob forces SingleLevel, satisfying that. A private LODTransitionContext
    // would also work but would need Flush / ProcessFinishedFrame / PrepareReadback driven
    // every frame by us, and would buy nothing — LOD crossfade is a sub-pixel nicety at
    // 512x512.
    //
    // Restored unconditionally in the finally. Leaving the engine's own main-view cull
    // without its transition context would trip that assert, and an assert tripped
    // mid-session turns the next quit into a crash report via DiagnosticReporter.
    // Give the GBuffer pass a clean slate. THE thing that was never happening.
    //
    // GBufferPassJob takes a clearRenderTargets flag and we pass true, but the IL shows it
    // is not the switch it looks like:
    //
    //     ldarg.s   clearRenderTargets
    //     brfalse.s skip
    //     call      VRageCore.GlobalDebugSettings.get_EnabledDebugDraw()
    //     brfalse.s skip                      <- and debug draw is off
    //
    // So the clear never ran, not once. _ourGBufferArray is held for the whole session, so
    // its contents accumulate across every frame and every experiment.
    //
    // DEPTH IS THE BIGGER HALF. gbufferAfterEnv makes the environment pass run first,
    // because GBufferPassJob takes no camera and inherits whatever is bound. But the env
    // pass fills the depth buffer with the entire scene on its way past — and the GBuffer
    // pass then draws THE SAME SCENE through different pass groups, so every fragment
    // fails the depth test against geometry already sitting at that exact depth. Only
    // slivers where the two paths disagree survive, which is what the thin wedges and
    // streaks in the debug blit actually were.
    //
    // It also explains the one good frame: the very first pass ran before anything had
    // populated depth.








    // DimDistance, applied PERSISTENTLY rather than scoped.
    //
    // Pass_Pixel_Indirect.hlsli multiplies all shaded output by
    // clamp(surface.ZDepth / Environment_.DimDistance, 0, 1) SQUARED, and the shipped
    // value is 5 metres — so geometry 1 m from the camera comes out at 4% brightness.
    // It exists so an environment probe does not contaminate itself with the hull it is
    // sitting inside.
    //
    // Environment_ is part of the per-frame GlobalSettings CB, built once per frame in
    // SettingsGroup.OnBeginDraw, so this cannot be a scoped swap the way
    // EnableRecursiveReflections can — it has to be set and left. That also means it
    // applies to the engine's OWN probes, which will read slightly brighter near the
    // player's hull. Applied only when the configured value changes.
    private static double _dimApplied = double.NaN;

    private static object _stockDimDistance;

    // Undo the persistent probe mutation. Paired with FeedGate.Shutdown; everything else
    // this class touches is either scoped per-pass or released by Reset().
    public static void RestoreEngineState()
    {
        if (_stockDimDistance == null || _settings == null) return;
        try
        {
            _environmentField ??= _settings.GetType().GetField("_environment", Any);
            var ours = _environmentField?.GetValue(_settings);
            var fProbe = ours?.GetType().GetField("ProbeSettings", Any);
            var probe = fProbe?.GetValue(ours);
            var fDim = probe?.GetType().GetField("DimDistance", Any);
            if (fDim == null) return;

            fDim.SetValue(probe, _stockDimDistance);
            fProbe.SetValue(ours, probe);
            _environmentField.SetValue(_settings, ours);
            RttLog.Line($"Probe settings: DimDistance restored to the stock {_stockDimDistance}.");
        }
        catch (Exception e) { RttLog.Error("restore dim distance", e); }
        finally { _stockDimDistance = null; _dimApplied = double.NaN; }
    }

    private static void ApplyDimDistance()
    {
        double want = FeedConfig.DimDistance;
        if (want < 0.0 || want == _dimApplied || _settings == null) return;
        try
        {
            _environmentField ??= _settings.GetType().GetField("_environment", Any);
            var ours = _environmentField?.GetValue(_settings);
            var fProbe = ours?.GetType().GetField("ProbeSettings", Any);
            var probe = fProbe?.GetValue(ours);
            var fDim = probe?.GetType().GetField("DimDistance", Any);
            if (fDim == null) { _dimApplied = want; RttLog.Line("Probe settings: DimDistance not found."); return; }

            var was = fDim.GetValue(probe);
            // DimDistance reaches the engine's OWN probes, so it has to be put back when
            // the feed gate goes dormant or the comparison is not against vanilla.
            _stockDimDistance ??= was;
            fDim.SetValue(probe, (float)want);
            fProbe.SetValue(ours, probe);
            _environmentField.SetValue(_settings, ours);
            _dimApplied = want;
            RttLog.Line($"Probe settings: DimDistance {was} -> {want} (PERSISTENT — it reaches the shader " +
                        "through the per-frame GlobalSettings CB, so a scoped swap would do nothing. " +
                        "This also affects the engine's own probes.)");
        }
        catch (Exception e) { _dimApplied = want; RttLog.Error("apply dim distance", e); }
    }



    private static object MakeVector2(float x, float y)
    {
        var t = Type.GetType("Keen.VRage.Library.Mathematics.Vector2, VRage.Library");
        return t == null ? null : Activator.CreateInstance(t, x, y);
    }



    private static object AutoStateOf(object texture)
    {
        var direct = Prop2(texture, "AutoResourceState");
        if (direct != null) return direct;
        foreach (var m in texture.GetType().GetMethods(Any))
        {
            if (m.GetParameters().Length != 0 || !m.Name.StartsWith("Get") || !m.Name.Contains("View")) continue;
            try
            {
                var st = Prop2(m.Invoke(texture, null), "AutoResourceState");
                if (st != null) return st;
            }
            catch { }
        }
        return null;
    }


    private static void ResolveFeedTexture()
    {
        _feedState = -1;
        try
        {
            // Size and format must come from the PANEL's target, because that is what
            // the handover copies into and CopyResource requires an exact match. Taking
            // them from any other target is how a 1024x1024 source ended up being copied
            // into a 512x512 destination — a size mismatch, and device removal.
            var rt = BlitProbe.FeedTarget;
            if (rt == null) { _feedState = 0; return; }
            var wantId = Prop2(rt, "Id");

            var otm = FindType("OffscreenTargetManager");
            var reg = otm?.GetField("_registeredTextures", Any);
            // The manager is reachable either as a CoreSystems static or via any
            // live instance field; try the statics first.
            object mgr = otm?.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                            .Select(f => { try { return f.GetValue(null); } catch { return null; } })
                            .FirstOrDefault(v => v != null && otm.IsInstanceOfType(v));
            var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            if (mgr == null && cs != null)
                mgr = cs.GetFields(BindingFlags.Public | BindingFlags.Static)
                        .Where(f => otm != null && otm.IsAssignableFrom(f.FieldType))
                        .Select(f => { try { return f.GetValue(null); } catch { return null; } })
                        .FirstOrDefault(v => v != null);

            var sb = new StringBuilder();
            sb.AppendLine($"Feed resolve: OffscreenTargetManager type={(otm == null ? "NOT FOUND" : "ok")}, instance={(mgr == null ? "NOT FOUND" : "ok")}, _registeredTextures={(reg == null ? "NOT FOUND" : "ok")}");

            if (mgr != null && reg != null)
            {
                if (reg.GetValue(mgr) is System.Collections.IDictionary dict)
                {
                    sb.AppendLine($"  registered offscreen textures: {dict.Count}, want PANEL id={wantId}");
                    object fallbackTex = null, fallbackRes = null, fallbackFmt = null, fallbackComp = null;

                    foreach (System.Collections.DictionaryEntry kv in dict)
                    {
                        var v = kv.Value;
                        var tex = Prop2(v, "Texture");
                        var resv = Prop2(v, "Resolution");
                        var fmt = Prop2(v, "Format");
                        var handle = Prop2(v, "Handle");
                        // The dictionary is keyed by GeneratedResourceHandle
                        // ("generated:<id>") while the target carries a bare RenderId,
                        // so the two never compare equal directly — match on the id
                        // text the handle wraps.
                        var wantStr = wantId?.ToString();
                        bool match = !string.IsNullOrEmpty(wantStr)
                                  && ((kv.Key?.ToString()?.Contains(wantStr) ?? false)
                                   || (handle?.ToString()?.Contains(wantStr) ?? false));
                        sb.AppendLine($"    component key={kv.Key} handle={handle} texture={(tex == null ? "null" : tex.GetType().Name)} res={resv} format={fmt}{(match ? "   <-- MATCH" : "")}");

                        if (tex == null) continue;
                        // Keep the component itself: its TakeScreenshotToMemory enqueues
                        // the readback directly, while the contracts struct only
                        // dispatches a command that never came back.
                        if (match) { _feedTexture = tex; _feedRes = resv; _feedFormat = fmt; _feedComponent = v; _feedState = 1; }
                        else if (fallbackTex == null) { fallbackTex = tex; fallbackRes = resv; fallbackFmt = fmt; fallbackComp = v; }
                    }

                    // No fallback. Picking "the first registered target" when the id
                    // does not match means writing into somebody else's offscreen
                    // texture — another panel's, or the game UI's. That is a crash,
                    // and a confusing one. Better to render nothing.
                    // RETRY, do not latch. ResolveFeedTexture sets _feedState = -1 on
                    // entry and the call site only re-resolves at 0, so leaving it at -1
                    // here turned a startup-ordering race into a permanent disable: the
                    // camera pass goes live ~2 s after load, but our offscreen target is
                    // not registered until the first LCD tick, so the very first resolve
                    // legitimately sees an EMPTY registry. Observed exactly that —
                    // "registered offscreen textures: 0" — and the feed never recovered
                    // for the rest of the session.
                    if (_feedState != 1)
                    {
                        _feedState = 0;
                        sb.AppendLine($"  no match for id {wantId} yet ({dict.Count} registered) — " +
                                      "will retry; refusing to write to an unknown target meanwhile.");
                    }
                }
                else sb.AppendLine("  _registeredTextures is not a dictionary");
            }
            sb.AppendLine(_feedState == 1 ? "  FEED TEXTURE RESOLVED" : "  feed texture unavailable");

            // Success always logs. Failure logs the first time and then at most every
            // 5 s, so a target that is merely pending cannot bury the log while it waits.
            bool resolved = _feedState == 1;
            if (resolved || _resolveFailLogs == 0 || Clock.Ms - _lastResolveFailLog >= 5000)
            {
                if (!resolved) { _resolveFailLogs++; _lastResolveFailLog = Clock.Ms; }
                RttLog.Line(sb.ToString().TrimEnd());
            }
        }
        catch (Exception e) { RttLog.Error("feed resolve", e); }
    }

    // ---------------------------------------------------------------- helpers
    private static object CurrentViewSlim()
    {
        // SettingsManager.RenderView is a RenderView; RenderViewSlim has an
        // implicit conversion from it.
        var rv = Prop2(_settings, "RenderView");
        if (rv == null) return null;
        return Convert(_tRenderViewSlim, rv, rv.GetType());
    }

    // The orbiting camera, as a RenderViewSlim. Only the two view matrices are
    // ours — projection and far plane are copied from the live main view, so the
    // engine's own conventions (handedness, reversed-Z, infinite far) carry over
    // instead of being guessed at.
    // PER-FEED (phase C1a): the orbit's time origin. Per-feed is what lets C3's second
    // camera sit at an OFFSET orbit rather than shadowing the first one exactly.
    private static long _feedStartTicks
    { get => Feeds.Cur.FeedStartTicks; set => Feeds.Cur.FeedStartTicks = value; }

    // Why the last null, for the skip log. Nulls here used to be silently papered
    // over by the main-view fallback, so nothing ever recorded the reason.
    private static int _viewSkips, _viewSkipLogs;

    // Why the last OrbitViewSlim() returned null. Reported by RenderOnce rather than
    // logged at each return site, so a persistent failure prints once and a transient
    // one during load prints not at all.
    private static string _orbitNull;

    // The main view, snapshotted from the per-frame pass. See the comment at the
    // call site in SceneDrawRecon: reading it from the probe hook picks up the
    // engine's probe cube-face view instead, which is the single-frame flash from
    // inside the ship.
    private static volatile object _baseViewSnapshot;
    private static int _baseViewMismatches;
    private static bool _mismatchLogged;

    public static void CaptureBaseView()
    {
        if (!_resolveOk) return;
        try
        {
            var v = CurrentViewSlim();
            if (v != null) _baseViewSnapshot = v;
        }
        catch { /* never let a diagnostic take the render thread down */ }
    }

    private static object OrbitViewSlim()
    {
        var target = CameraFeed.Current;
        if (target == null) { _orbitNull = "no target"; return null; }

        // Snapshot only. Falling back to a live read here would reintroduce exactly
        // the bug this exists to fix.
        var baseView = _baseViewSnapshot;
        if (baseView == null) { _orbitNull = "no main-view snapshot yet"; return null; }

        // Confirm the diagnosis rather than assume it: if the live view inside the
        // probe hook differs from the per-frame snapshot, the probe really is
        // swapping RenderView underneath us and this fix is the reason the flash
        // stopped. Counted, not logged per occurrence.
        try
        {
            var live = CurrentViewSlim();
            if (live != null && !live.Equals(baseView))
            {
                _baseViewMismatches++;
                if (!_mismatchLogged && _baseViewMismatches >= 5)
                {
                    _mismatchLogged = true;
                    RttLog.Line($"Camera pass: CONFIRMED — RenderView inside the probe hook differs from the " +
                                $"per-frame view ({_baseViewMismatches} times so far). Those would have been " +
                                "the flash frames; the snapshot is now used instead.");
                }
            }
        }
        catch { }

        // Also high-resolution: at 30 fps a 15.6 ms clock quantum means several
        // consecutive frames share an orbit angle and the motion judders.
        if (_feedStartTicks == 0) _feedStartTicks = Clock.Ms;
        double t = (Clock.Ms - _feedStartTicks) / 1000.0;

        // PER-FEED ORBIT PHASE (plan phase C3).
        //
        // Feeds are distinguished by their SUBJECT — each claims its own tagged panel and
        // orbits that panel's grid — so two feeds on two ships already differ. But two feeds
        // on panels of the SAME grid would otherwise sit at the same orbit angle at the same
        // instant and produce pixel-identical pictures, which is the one arrangement that
        // makes a multi-feed bug invisible: cross-contamination between feeds looks exactly
        // like correct output when both feeds show the same thing.
        //
        // Offsetting each feed's phase around the orbit makes them visibly distinct, so
        // "panel B is showing feed A's picture" is something you can SEE. It is a test
        // affordance first and a nicety second — and it is also what the C3 exit gate
        // ("both feeds live and correct") is actually checking.
        t += Feeds.Cur.Id * (FeedConfig.OrbitPeriod / Math.Max(1, Feeds.Count));

        // Grid centre by default; the panel itself is the close-up shot.
        bool grid = FeedConfig.OrbitGrid && target.Extent > 0.0;
        var camWorld = CameraFeed.OrbitCameraWorld(
            grid ? target.Centre : target.Position,
            grid ? target.Extent : 0.0,
            t);
        var view = InvertMatrixD(camWorld);
        _lastCamWorld = camWorld; _lastViewD = view;   // for the global camera swap

        // Flash detector. An intermittent single frame from the PLAYER's position means
        // either our camera was wrong for one pass, or the panel received someone else's
        // pixels. Those need completely different fixes, so measure rather than guess:
        // log any eye position that jumps further than an orbit diameter from the last
        // pass. Silence here means the camera was always right and the flash is content.
        try
        {
            if (Prop2(camWorld, "Translation") is Keen.VRage.Library.Mathematics.Vector3D eye)
            {
                if (_haveLastEye)
                {
                    double dx = eye.X - _lastEye.X, dy = eye.Y - _lastEye.Y, dz = eye.Z - _lastEye.Z;
                    double jump = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    double limit = Math.Max(50.0, FeedConfig.OrbitRadius * 2.0);
                    if (jump > limit && _eyeJumpLogs++ < 8)
                        RttLog.Line($"CAMERA JUMP: eye moved {jump:F0} m in one pass (limit {limit:F0}) — " +
                                    $"{_lastEye.X:F0},{_lastEye.Y:F0},{_lastEye.Z:F0} -> {eye.X:F0},{eye.Y:F0},{eye.Z:F0}. " +
                                    "This is a flash frame.");
                }
                _lastEye = eye; _haveLastEye = true;
            }
        }
        catch { }
        if (view == null) { _orbitNull = "view matrix inversion failed"; return null; }

        // RenderViewSlim is internal but its fields are public; mutate a boxed copy
        // of the main view so everything we do not set stays engine-supplied.
        object box = baseView;
        var f1 = _tRenderViewSlim.GetField("InvViewD", Any);
        var f2 = _tRenderViewSlim.GetField("ViewD", Any);
        if (f1 == null || f2 == null) { _orbitNull = "RenderViewSlim fields not found"; return null; }
        f1.SetValue(box, camWorld);
        f2.SetValue(box, view);
        return box;
    }

    private static object InvertMatrixD(object m)
    {
        try
        {
            var t = m.GetType();
            var mi = t.GetMethod("Invert", BindingFlags.Public | BindingFlags.Static,
                                 null, new[] { t }, null);
            if (mi != null) return mi.Invoke(null, new[] { m });
            var mi2 = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                       .FirstOrDefault(x => x.Name == "Invert" && x.GetParameters().Length == 1);
            return mi2?.Invoke(null, new[] { m });
        }
        catch (Exception e) { if (_errors++ < 3) RttLog.Error("matrix invert", e); return null; }
    }

    private static object Convert(Type to, object value, Type from)
    {
        if (value == null || to == null) return null;
        if (to.IsInstanceOfType(value)) return value;
        foreach (var m in to.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Concat(from?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Enumerable.Empty<MethodInfo>()))
        {
            if (m.Name is not ("op_Implicit" or "op_Explicit")) continue;
            if (m.ReturnType != to) continue;
            var p = m.GetParameters();
            if (p.Length != 1) continue;
            var pt = p[0].ParameterType.IsByRef ? p[0].ParameterType.GetElementType() : p[0].ParameterType;
            if (!pt.IsInstanceOfType(value)) continue;
            try { return m.Invoke(null, new[] { value }); } catch { }
        }
        return null;
    }

    // Borrowed<T>.Resource, then whatever member exposes the requested view type.
    // `prefer` disambiguates where several members qualify — the depth texture
    // exposes both DepthStencilReadWrite and DepthStencilReadOnly, and taking the
    // read-only one would silently break depth writes rather than fail loudly.
    private static object ViewOf(object borrowed, string viewInterface, string prefer = null)
    {
        var resource = Prop2(borrowed, "Resource") ?? borrowed;
        if (resource == null) return null;
        var want = FindType(viewInterface);
        if (want == null) return null;
        if (want.IsInstanceOfType(resource)) return resource;

        if (prefer != null)
        {
            var pp = resource.GetType().GetProperty(prefer, Any);
            if (pp != null && want.IsAssignableFrom(pp.PropertyType))
            {
                try { var pv = pp.GetValue(resource); if (pv != null) return pv; } catch { }
            }
        }

        foreach (var p in resource.GetType().GetProperties(Any))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (!want.IsAssignableFrom(p.PropertyType)) continue;
            try { var v = p.GetValue(resource); if (v != null) return v; } catch { }
        }
        foreach (var f in resource.GetType().GetFields(Any))
        {
            if (!want.IsAssignableFrom(f.FieldType)) continue;
            try { var v = f.GetValue(resource); if (v != null) return v; } catch { }
        }
        return null;
    }
    private static void ReturnBorrowed(object borrowed)
    {
        foreach (var m in _texPool.GetType().GetMethods(Any))
        {
            if (m.Name != "Return") continue;
            var p = m.GetParameters();
            if (p.Length != 1 || !p[0].ParameterType.IsInstanceOfType(borrowed)) continue;
            m.Invoke(_texPool, new[] { borrowed });
            return;
        }
    }



    private static object Prop2(object o, string name)
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

    private static Type _cachedAsmMarker;
    private static Type[] _types;

    private static Type FindType(string simpleName)
    {
        if (_types == null)
        {
            _cachedAsmMarker = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            try { _types = _cachedAsmMarker?.Assembly.GetTypes() ?? Type.EmptyTypes; }
            catch (ReflectionTypeLoadException e) { _types = e.Types.Where(t => t != null).ToArray(); }
            catch { _types = Type.EmptyTypes; }
        }
        foreach (var t in _types) if (t.Name == simpleName) return t;
        return null;
    }






}
