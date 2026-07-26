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

    private static bool _dryRunDone;
    private static bool _resolvedOk;
    private static bool _armed;
    private static bool _disarmed;
    private static long _lastRender;
    private static long _lastArmCheck, _lastDisarmCheck;
    private const long ArmPollMs = 2000;
    private static int _renders, _errors;
    private static bool _survivedLogged;

    // Resolved once by the dry run.
    private static object _drawContexts, _settings, _texPool, _bufMgr;
    private static object _cullJob, _clusterJob, _envPass;
    private static object _cullCtx, _clusterCtx, _shadowResources;
    private static object _geomBuffersMain, _geomBuffersEffect;
    private static object _lodProbe, _lodMainView;

    // A throwaway for optional resolves, so a missing member cannot fail the dry run.
    private static bool _ignored;
    private static object _lastGeomBuffers;

    // Which OutputGeometryBufferContext this pass writes its draw commands into.
    // See the resolve-time comment: the choice is between a buffer eighteen engine
    // passes touch and one that a single pass touches.
    private static object GeomBuffers =>
        (FeedConfig.EffectGeomBuffers && _geomBuffersEffect != null) ? _geomBuffersEffect : _geomBuffersMain;
    private static MethodInfo _miDoCullingFirstPass, _miClusterDoWork, _miEnvPassDoWork;
    private static MethodInfo _miBorrowRt, _miBorrowDepth, _miCreateCb;
    private static Type _tRenderViewSlim, _tTrackedCam, _tCamSettings;
    private static object _hdrFormat, _srvFormat, _depthFormat;

    public static void Reset()
    {
        _dryRunDone = _resolvedOk = _armed = _disarmed = _survivedLogged = false;
        _lastRender = _lastArmCheck = _lastDisarmCheck = 0; _renders = _errors = 0;
        Array.Clear(_ldrRing, 0, _ldrRing.Length); _ldrReady = null; _ringIndex = -1;
        _toneLogs = _skipLogs = _transLogs = 0; _resolvedPanelId = null; _blitLogged = false;
        _baseViewSnapshot = null; _baseViewMismatches = 0; _mismatchLogged = false;
        _viewSkips = _viewSkipLogs = 0;
        _toneBlocked = _bloomBlocked = _skyBlocked = false; _bloomLogs = _skyLogs = 0;
        _ldrResizable = null; _lastResizableRes = null; _toneInputsLogged = _logProbeExp = false; _exposureSource = _loggedExposureSource = "";
        _firstPassAt = 0; _startupLogged = _startupDoneLogged = false;

        // The five routes-doc fixes. Every one of these is a "logged once" or "applied
        // once" latch, and a hot reload that left them set would silently skip the log
        // line that proves the fix took — which is the failure mode that has wasted the
        // most time on this project.
        ReleaseHeldBorrows();
        _sunBlocked = _sunSettingsLogged = false; _sunLogs = 0;
        _lodProbe = _lodMainView = _lastLodUsed = null;
        _screenResLog = null;
        _cbRenderView = null; _miCreateNonjittered = _miRvSetCamera = _miRvSetResolution = null;
        _fullCbBlocked = _fullCbLogged = false;
        _environmentField = null; _probeSettingsBlocked = _probeSettingsLogged = false;
        _dimApplied = double.NaN;

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
            ? "Camera pass ARMED — will issue real GPU work after the dry run succeeds."
            : $"Camera pass not armed (dry run only). Create {ArmPath} to arm.");
    }

    public static void OnProbePass(object sds, object commandList)
    {
        if (commandList == null) return;
        try
        {
            if (!_dryRunDone)
            {
                _dryRunDone = true;
                _resolvedOk = DryRun(sds);
            }
            if (!_resolvedOk) return;

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

    // ------------------------------------------------------------------ phase A
    // A dry run that throws still knows more than one that never ran, so the
    // report is written whatever happens.
    private static bool DryRun(object sds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Camera pass dry run {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        sb.AppendLine();
        try
        {
            return DryRunCore(sds, sb);
        }
        catch (Exception e)
        {
            sb.AppendLine();
            sb.AppendLine($"THREW: {e.GetType().Name}: {e.Message}");
            sb.AppendLine(e.StackTrace);
            RttLog.Error("camera dry run", e);
            return false;
        }
        finally { Write(sb); }
    }

    private static bool DryRunCore(object sds, StringBuilder sb)
    {
        bool ok = true;

        var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
        if (cs == null) { sb.AppendLine("FATAL: CoreSystems not found"); return false; }

        _drawContexts = StaticField(cs, "DrawContexts", sb, ref ok);
        _settings     = StaticField(cs, "Settings", sb, ref ok);
        _texPool      = StaticField(cs, "BindableTexturePool", sb, ref ok);
        _bufMgr       = StaticField(cs, "BindableBuffers", sb, ref ok);
        if (!ok) return false;

        // ---- jobs off the live SceneDrawSystem ----
        sb.AppendLine("-- jobs --");
        _cullJob    = InstField(sds, "_indirectCullingJob", sb, ref ok);
        _clusterJob = InstField(sds, "_clusterJob", sb, ref ok);
        _envPass    = InstField(sds, "_indirectEnvironmentPass", sb, ref ok);
        sb.AppendLine();

        if (ok)
        {
            _miDoCullingFirstPass = Pick(_cullJob.GetType(), "DoCullingFirstPass", 12, sb, ref ok);
            _miClusterDoWork      = Pick(_clusterJob.GetType(), "DoWork", 6, sb, ref ok);
            _miEnvPassDoWork      = Pick(_envPass.GetType(), "DoWork", 10, sb, ref ok);
        }
        sb.AppendLine();

        // ---- contexts ----
        sb.AppendLine("-- contexts --");
        var probeCulling = Prop(_drawContexts, "EnvProbeCulling", sb, ref ok);
        if (probeCulling is Array arr && arr.Length > 0)
        {
            // Reuse the face-0 context: culling contexts are per-pass scratch,
            // overwritten on every use, and we run in the postfix after a pass has
            // finished with one. Cheap for a first proof; a dedicated context can
            // come later if this proves to disturb probe lighting.
            _cullCtx = arr.GetValue(0);
            sb.AppendLine($"  EnvProbeCulling[0]              {(_cullCtx == null ? "NULL" : _cullCtx.GetType().Name)}");
        }
        else { sb.AppendLine("  EnvProbeCulling                UNUSABLE"); ok = false; }

        _clusterCtx      = Prop(_drawContexts, "EnvProbeClustering", sb, ref ok);
        // Two OutputGeometryBufferContexts exist. The difference decides the flicker.
        //
        // `Borrow()` on one of these is NOT an allocation — it is an `_isBorrowed` mutex
        // flag, and the same physical draw-command and instance buffers come back every
        // time. So whichever we pass to DoCullingFirstPass is where OUR draw commands
        // land, in a buffer the engine is also using.
        //
        //   MainOutputGeometryBuffers        read by EIGHTEEN engine methods across the
        //                                    frame — MainViewCulling, ExecuteForwardPasses,
        //                                    DrawUnlit, RenderTransparent, RenderHolograms,
        //                                    RenderFlares, DrawWater, SceneFinalize, DrawUI...
        //   MainOutputEffectGeometryBuffers  read by ONE: RenderHighlightsAndTransparentUnlit.
        //
        // This is the other half of the flash/flicker pair. Originally we shared BOTH the
        // culling context and this buffer, so our pass was self-consistent — it just drew
        // the engine's probe-view data, which is the single-frame flash from the player's
        // position. Taking a private culling context fixed the viewpoint but split the
        // pair: culling results in our context, draw commands in a buffer eighteen engine
        // passes rewrite around us. Geometry appearing and disappearing at certain angles
        // is exactly what that looks like.
        _geomBuffersMain   = Prop(_drawContexts, "MainOutputGeometryBuffers", sb, ref ok);
        _geomBuffersEffect = Prop(_drawContexts, "MainOutputEffectGeometryBuffers", sb, ref _ignored);
        sb.AppendLine($"  (effect buffers {(_geomBuffersEffect == null ? "NOT FOUND — flicker fix unavailable" : "available")})");

        // Phase 1: borrow a culling context from the engine's pool per pass instead of
        // sharing the probe one. _cullCtx below stays as the fallback for when the
        // pool is unavailable.
        OwnContexts.Resolve(_drawContexts, sb);

        // Deliberately NOT switching to MainOutputEffectGeometryBuffers. It is the
        // engine's effects context, paired with MainViewEffectsCulling; combining it
        // with the probe culling context mixes two unrelated pairings, and doing so
        // crashed on world load. The geometry buffers must match whatever culling
        // context is in use.
        _shadowResources = Prop(_drawContexts, "DirectionalLightShadowResources", sb, ref ok);
        sb.AppendLine();

        // ---- LOD settings: Settings.LOD.EnvironmentProbe and .MainView ----
        //
        // We copied the probe recipe wholesale, including which LOD profile it asks for.
        // The shipped DefaultFrameSettingsConfiguration.def gives them very different
        // floors:
        //
        //     "MainView":         { "MinLOD": 0, "FloraMinLOD": 0 }
        //     "EnvironmentProbe": { "MinLOD": 8, "FloraMinLOD": 8 }
        //
        // MinLOD 8 clamps EVERY mesh to its coarsest level — a whole-mesh swap, not a
        // subtle tweak — which is a large part of what reads as "missing detail" in the
        // feed. Both profiles are resolved so the choice is a config flip, and the
        // numbers are logged so the difference is visible rather than asserted.
        sb.AppendLine("-- lod settings --");
        var lod = Prop(_settings, "LOD", sb, ref ok);
        if (lod != null)
        {
            _lodProbe    = lod.GetType().GetField("EnvironmentProbe", Any)?.GetValue(lod);
            _lodMainView = lod.GetType().GetField("MainView", Any)?.GetValue(lod);
            sb.AppendLine($"  LOD.EnvironmentProbe            {DescribeLod(_lodProbe)}");
            sb.AppendLine($"  LOD.MainView                    {DescribeLod(_lodMainView)}");
            if (_lodProbe == null) ok = false;
        }
        sb.AppendLine();

        // ---- pool entry points and formats ----
        sb.AppendLine("-- pools / formats --");
        _miBorrowRt    = Pick(_texPool.GetType(), "BorrowRWRenderTargetTexture", 7, sb, ref ok);

        // Optional: its absence disables the fidelity layers, not the feed. Resolved
        // with a throwaway `ok` so a missing method cannot fail the whole dry run.
        bool fidelityOk = true;
        _miBorrowResizableRt = Pick(_texPool.GetType(), "BorrowResizableRWRenderTargetTexture", 7, sb, ref fidelityOk);
        _miBorrowDepth = Pick(_texPool.GetType(), "BorrowResizableDepthStencilTexture", 5, sb, ref ok);
        _miCreateCb    = _bufMgr.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "CreateTransientConstantBuffer" && m.IsGenericMethodDefinition
                              && m.GetParameters().Length == 2);
        sb.AppendLine($"  CreateTransientConstantBuffer<T> {(_miCreateCb == null ? "NOT FOUND" : "OK")}");
        if (_miCreateCb == null) ok = false;

        var sbType = Type.GetType("Keen.VRage.Render12.Core.Systems.ScreenBuffers, VRage.Render12");
        _hdrFormat = sbType?.GetField("HDR_FORMAT", Any)?.GetValue(null);
        _srvFormat = _hdrFormat;
        sb.AppendLine($"  HDR_FORMAT                      {_hdrFormat ?? (object)"NOT FOUND"}");
        if (_hdrFormat == null) ok = false;

        // DepthStencilFormat is a struct with static presets, not an enum — the
        // probe pass reads DepthStencilFormat.HighQuality as a static field.
        var dsf = FindType("DepthStencilFormat");
        if (dsf != null)
        {
            var statics = dsf.GetFields(BindingFlags.Public | BindingFlags.Static)
                             .Where(f => f.FieldType == dsf).ToList();
            foreach (var n in new[] { "HighQuality", "Default", "Standard" })
            {
                var f = statics.FirstOrDefault(x => x.Name == n);
                if (f != null) { try { _depthFormat = f.GetValue(null); } catch { } break; }
            }
            if (_depthFormat == null && statics.Count > 0)
                try { _depthFormat = statics[0].GetValue(null); } catch { }
            sb.AppendLine($"  DepthStencilFormat              {_depthFormat ?? (object)"NOT FOUND"}" +
                          $"  [{(dsf.IsEnum ? string.Join(",", Enum.GetNames(dsf)) : string.Join(",", statics.Select(x => x.Name)))}]");
        }
        else sb.AppendLine("  DepthStencilFormat              TYPE NOT FOUND");
        if (_depthFormat == null) ok = false;
        sb.AppendLine();

        // ---- camera types ----
        sb.AppendLine("-- camera types --");
        _tRenderViewSlim = FindType("RenderViewSlim");
        _tCamSettings    = FindType("CameraSettings");
        _tTrackedCam     = FindType("TrackedCameraSettings");
        sb.AppendLine($"  RenderViewSlim                  {(_tRenderViewSlim == null ? "NOT FOUND" : "OK")}");
        sb.AppendLine($"  CameraSettings                  {(_tCamSettings == null ? "NOT FOUND" : "OK")}");
        sb.AppendLine($"  TrackedCameraSettings           {(_tTrackedCam == null ? "NOT FOUND" : "OK")}");
        if (_tRenderViewSlim == null || _tCamSettings == null || _tTrackedCam == null) ok = false;
        sb.AppendLine();

        // ---- the unknowns: how to get views off borrowed textures ----
        sb.AppendLine("-- render target / depth view accessors (probing shapes) --");
        DescribeType(sb, FindType("ResizableRWRenderTargetTexture"), "IRenderTargetView");
        DescribeType(sb, FindType("ResizableDepthStencilTexture"), "IDepthStencilView");
        sb.AppendLine();

        // The engine's exposure + tonemap pair. Both take their textures as
        // parameters and do not read the global ScreenBuffers, so they can be pointed
        // at ours — this is what makes the feed look like the game rather than a
        // clamped HDR dump.
        _sceneDrawSystem = sds;
        _miComputeExposure   = sds?.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "ComputeExposure" && m.GetParameters().Length == 4);
        _miApplyToneMapping  = sds?.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "ApplyToneMapping" && m.GetParameters().Length == 5);
        _miApplyBloom = sds?.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "ApplyBloom" && m.GetParameters().Length == 4);

        // Sky. IndirectPlanetEnvironmentJob is the probe pipeline's own sky/atmosphere
        // pass, and its parameters are the same shape as the geometry pass we already
        // drive: a camera CB, two render targets, a depth SRV and the view. Nothing
        // global, which is what makes it the cheapest fidelity win after tonemapping.
        // The engine keeps a SEPARATE exposure for probe-style offscreen renders,
        // which is exactly what we are. ComputeExposure instead drives and reads
        // _eyeAdaptationJob — shared, temporal state adapting to the PLAYER's view —
        // so it both exposed our feed for the wrong scene and fed our HDR buffer into
        // the adaptation the main view depends on.
        _probeExposureJob = sds?.GetType().GetField("_environmentProbeExposureJob", Any)?.GetValue(sds);

        // The eye-adaptation job holds already-computed exposure textures in
        // _autoExposures. READING one is passive; ComputeExposure is what writes them,
        // drives the histogram, and advances the temporal adaptation the main view
        // depends on. Reusing an existing texture lets ApplyToneMapping be tested
        // WITHOUT ComputeExposure — which last night's test never separated.
        _eyeAdaptationJob = sds?.GetType().GetField("_eyeAdaptationJob", Any)?.GetValue(sds);
        _autoExposuresField = _eyeAdaptationJob?.GetType().GetField("_autoExposures", Any);

        _planetEnvJob = sds?.GetType().GetField("_indirectPlanetEnvironmentJob", Any)?.GetValue(sds);
        _miPlanetEnvDoWork = _planetEnvJob?.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 6);

        sb.AppendLine();
        sb.AppendLine($"-- Fidelity: ComputeExposure={(_miComputeExposure == null ? "NOT FOUND" : "ok")} " +
                      $"ApplyToneMapping={(_miApplyToneMapping == null ? "NOT FOUND" : "ok")} " +
                      $"ApplyBloom={(_miApplyBloom == null ? "NOT FOUND" : "ok")} " +
                      $"PlanetEnvJob={(_miPlanetEnvDoWork == null ? "NOT FOUND" : "ok")} --");

        CameraCarrierSurvey(sb);
        GiSwitchSurvey(sds, sb);
        FidelitySurvey(sds, sb);
        GBufferSurvey(sds, sb);
        ShadowSurvey(sds, sb);

        // CopyJob is the converting blit that bridges the scene's HDR output to the
        // LCD's sRGB target.
        _copyJob = sds?.GetType().GetField("_copyJob", Any)?.GetValue(sds);
        if (_copyJob != null)
        {
            _miCopyDoWork = _copyJob.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 8);
            var chParam = _miCopyDoWork?.GetParameters()[5].ParameterType;
            if (chParam != null && chParam.IsEnum)
            {
                // RGB, deliberately NOT alpha.
                //
                // The panel binds our feed as ColorMetalTexture — RGB is colour and
                // ALPHA IS METALNESS. Our HDR source is R11G11B10_Float, which has no
                // alpha channel at all, so blitting with Channel.All wrote whatever the
                // shader produced for alpha (effectively 1.0) straight into metalness.
                // A fully metallic surface has almost no diffuse response, so the colour
                // we were feeding barely contributed — which flattens the range no
                // matter what exposure is chosen, and is why a 12x exposure sweep did
                // nothing.
                long rgb = 0;
                foreach (var n in new[] { "R", "G", "B" })
                    if (Enum.GetNames(chParam).Contains(n))
                        rgb |= System.Convert.ToInt64(Enum.Parse(chParam, n));

                _channelRgb = rgb != 0 ? Enum.ToObject(chParam, rgb) : null;
                foreach (var n in new[] { "All", "RGBA" })
                    if (Enum.GetNames(chParam).Contains(n)) { _channelAll = Enum.Parse(chParam, n); break; }
                _channelAll ??= Enum.ToObject(chParam, Enum.GetValues(chParam).Cast<object>()
                    .Select(v => System.Convert.ToInt64(v)).Max());
                sb.AppendLine($"  blit channels: RGB={_channelRgb} All={_channelAll} " +
                              "(alpha is METALNESS on ColorMetalTexture)");
            }

            // Nullable<PostProcess>; its only member is Normalize.
            var ppParam = _miCopyDoWork?.GetParameters()[4].ParameterType;
            var ppType = ppParam == null ? null : Nullable.GetUnderlyingType(ppParam) ?? ppParam;
            if (ppType != null && ppType.IsEnum && Enum.GetNames(ppType).Contains("Normalize"))
                _postProcess = Enum.Parse(ppType, "Normalize");
        }
        sb.AppendLine();
        sb.AppendLine($"-- CopyJob: job={(_copyJob == null ? "NULL" : "ok")} DoWork={(_miCopyDoWork == null ? "NOT FOUND" : "ok")} channel={_channelAll ?? (object)"none"} postProcess={_postProcess ?? (object)"none"} --");

        // ---- the HDR -> LDR problem ----
        // The scene pass must render R11G11B10_Float, but the offscreen target the
        // LCD can display is R8G8B8A8_UNorm_SRgb. CopyResource cannot convert, so a
        // blit job has to. These are the candidates on the live SceneDrawSystem.
        sb.AppendLine();
        sb.AppendLine("-- converting-blit candidates --");
        foreach (var fname in new[] { "_copyJob", "_fxaaJob", "_deferredTexturingPass", "_bloomJob" })
        {
            var job = sds?.GetType().GetField(fname, Any)?.GetValue(sds);
            if (job == null) { sb.AppendLine($"  {fname,-26} NULL"); continue; }
            sb.AppendLine($"  {fname,-26} {job.GetType().Name}");
            foreach (var m in job.GetType().GetMethods(Any)
                         .Where(m => m.Name is "DoWork" or "Draw" or "Copy" or "Blit"))
                sb.AppendLine($"      {m.Name}({string.Join(", ", m.GetParameters().Select(p => (p.IsIn ? "in " : "") + (p.ParameterType.IsByRef ? p.ParameterType.GetElementType()?.Name : p.ParameterType.Name) + " " + p.Name))})");
        }

        sb.AppendLine();
        sb.AppendLine(ok ? "DRY RUN: all arguments resolved." : "DRY RUN: INCOMPLETE — see NOT FOUND / NULL above.");

        RttLog.Line(ok ? "Camera dry run OK — every argument resolved." : "Camera dry run INCOMPLETE — see camera-dryrun.txt.");
        return ok;
    }

    // ------------------------------------------------------------------ phase B
    // The engine's own sequence, with our view and our target.
    private static void RenderOnce(object commandList)
    {
        object rtBorrow = null, depthBorrow = null, cameraCb = null, borrowedCulling = null;
        object savedGBuffer = null, savedCamera = null, scratchBorrow = null, savedProbeSettings = null;
        object[] savedCameraCb = null;
        bool geomBorrowed = false;

        // Captured ONCE for the whole pass. The config is polled between passes, so
        // reading the property per use could Borrow one context and Return the other
        // if the switch flipped mid-pass — which would leave an engine context stuck
        // marked as borrowed.
        object geomBuffers = GeomBuffers;
        if (!ReferenceEquals(geomBuffers, _lastGeomBuffers))
        {
            _lastGeomBuffers = geomBuffers;
            RttLog.Line($"Geometry buffers: {(ReferenceEquals(geomBuffers, _geomBuffersEffect) ? "MainOutputEffect" : "Main")} " +
                        "— Main is shared with 18 engine passes; Effect with one " +
                        "(RenderHighlightsAndTransparentUnlit).");
        }
        try
        {
            File.WriteAllText(LivePath, $"camera pass entered {DateTime.Now:O}\n");

            // Persistent, self-gating on change. Not part of the scoped swap below —
            // see the comment on ApplyDimDistance for why it cannot be.
            ApplyDimDistance();

            // The scene pass's PSOs are compiled for the HDR format. Binding a
            // render target of any other format is a pipeline-state mismatch, which
            // D3D12 answers with device removal — so the format is NOT negotiable,
            // whatever the copy destination would prefer.
            // Supersample. The scene has always rendered at exactly the panel's
            // resolution, which is why the feed looks blocky and the starfield smears:
            // 512x512 with no anti-aliasing of any kind. Rendering larger and letting
            // the existing CopyJob blit downsample gives real AA for the cost of the
            // extra pixels, and needs no new pass — CopyJob is a shader blit, so it
            // rescales for free.
            //
            // Only the SCENE targets scale. The LDR ring stays at the panel's exact
            // resolution because it is the source for CopyTextureSubresource, and
            // subresource copies require matching dimensions.
            var res = ScaledRenderRes();
            var colourFormat = _hdrFormat;

            // Orbit the tagged panel once one has been found; fall back to the main
            // view ONLY before that, so the pass keeps proving itself on an untagged
            // world.
            //
            // After a panel is found the fallback is a defect, not a safety net: any
            // hiccup building the orbit view rendered the PLAYER'S viewpoint into the
            // feed for a single frame, which read on the panel as a flash from
            // somewhere inside the ship a couple of times a second. Dropping the pass
            // is invisible — the panel simply keeps the frame it already has.
            var view = OrbitViewSlim();
            if (view == null)
            {
                if (CameraFeed.EverFound)
                {
                    _viewSkips++;
                    if (_viewSkipLogs++ < 3)
                        RttLog.Line($"Camera pass: orbit view unavailable ({_orbitNull}) — pass SKIPPED " +
                                    "(previously this rendered the main view onto the panel).");
                    return;
                }
                view = CurrentViewSlim();
            }
            if (view == null) { RttLog.Line("Camera pass: could not build RenderViewSlim."); _armed = false; return; }
            if (_renders == 0)
                RttLog.Line($"Camera pass view source: {(CameraFeed.Current != null ? "ORBIT — " + CameraFeed.Describe() : "main view (no [RTC] panel yet)")}");

            cameraCb = BuildCameraCb(view, res);
            if (cameraCb == null) { RttLog.Line("Camera pass: camera conversion failed."); _armed = false; return; }

            // Point the ENGINE'S two camera constant buffers at ours for the pass.
            //
            // Passes that take a cameraCb parameter already get ours. Everything else —
            // ~92 methods, including ClusteringJob.DoWork which runs right below — reads
            // these two fields off the global. So our cluster grid is currently built
            // from the PLAYER'S frustum while we rasterise from ours, which mis-bins
            // every clustered local light in the feed.
            //
            // Restored in the finally. It must be restored inside this same OnBeginDraw
            // bracket: OnEndDraw disposes whatever is in the field.
            savedCameraCb = CameraCbSwap.Install(cameraCb);

            // The colour target is borrowed RESIZABLE when any fidelity layer is on.
            //
            // ResizableRWRenderTargetTexture and RWRenderTargetTexture are siblings —
            // same interfaces, unrelated classes — and every post pass in the engine is
            // declared against the resizable one. So this single choice is what gates
            // ComputeExposure, ApplyToneMapping, DrawSkybox, ApplyAtmosphere,
            // ComputeSSR, ExecuteVolumetricPasses, RenderTransparent and ExecuteLighting
            // all at once. Plain borrow stays the default because it is the one with
            // hours of proven runtime behind it.
            //
            //   BorrowResizableRWRenderTargetTexture(name, srvFormat, maxResolution,
            //                                        uavFormat, mipMaps, clearColor, lifetime)
            //   BorrowRWRenderTargetTexture(name, resourceFormat, srvFormat,
            //                               resolution, mipMaps, clearColor, lifetime)
            bool wantResizable = (FeedConfig.Tonemap || FeedConfig.Sky) && _miBorrowResizableRt != null;
            rtBorrow = wantResizable
                ? _miBorrowResizableRt.Invoke(_texPool, new object[]
                    { "RttCameraColor", colourFormat, res, null, 1, null, 128 })
                : _miBorrowRt.Invoke(_texPool, new object[]
                    { "RttCameraColor", colourFormat, colourFormat, res, 1, null, 128 });

            // Log whenever the ACTUAL resolution changes, not just once. "Resizable"
            // textures in this engine size themselves to the main viewport, so the
            // post chain may be running at full screen resolution rather than the
            // 512x512 we asked for — which would account for the whole cost of the
            // fidelity layers on its own. The first pass logged 256x256 before the
            // panel's resolution had resolved, so a one-shot log answered nothing.
            if (wantResizable)
            {
                var actual = Prop2(Prop2(rtBorrow, "Resource") ?? rtBorrow, "Resolution")?.ToString();
                if (actual != _lastResizableRes)
                {
                    _lastResizableRes = actual;
                    RttLog.Line($"Fidelity: colour target RESIZABLE — asked {res}, got {actual}. " +
                                "If 'got' is the screen resolution, the post chain is running at full size.");
                }
            }
            depthBorrow = _miBorrowDepth.Invoke(_texPool, new object[]
                { "RttCameraDepth", _depthFormat, res, null, 128 });

            var rtv = ViewOf(rtBorrow, "IRenderTargetView");
            var dsv = ViewOf(depthBorrow, "IDepthStencilView", prefer: "DepthStencilReadWrite");
            if (rtv == null || dsv == null)
            {
                RttLog.Line($"Camera pass: view accessor failed (rtv={rtv != null}, dsv={dsv != null}). Disarming.");
                _armed = false;
                return;
            }

            Call(geomBuffers, "Borrow"); geomBorrowed = true;

            // Step 2: own the GBuffer for the duration of this pass. Restored in the
            // finally below, unconditionally — leaving the engine pointed at our
            // 512x512 array would wreck the player's view.
            savedGBuffer = InstallOurGBuffer(res);

            // And the camera the GBuffer pass rasterises from.
            savedCamera = InstallOurCamera(_lastCamWorld, _lastViewD);

            // Swap the real IBL cubes in for the flat default, for our pass only.
            savedProbeSettings = InstallProbeSettings();

            // cull -> cluster -> draw, exactly as the probe pass sequences them
            // Prefer a pooled culling context over the engine's probe one — sharing
            // that is what capped the frame rate. Returned in the finally below.
            borrowedCulling = FeedConfig.UsePooledCulling ? OwnContexts.Borrow() : null;
            var cullCtx = borrowedCulling ?? _cullCtx;

            // A pooled context arrives ranged for ONE draw per category, because the
            // engine only ranges the contexts its own pending-work queues name and
            // nothing names ours. Everything past that floor is silently dropped —
            // which is the missing asteroids and the flickering structure.
            // EnvProbeCulling[0] never needed this: the engine ranges it every frame.
            if (borrowedCulling != null) OwnContexts.EnsureRanges(borrowedCulling, commandList);

            // MainView unclamps MinLOD from 8 to 0 — see the resolve-time comment. Falls
            // back to the probe profile if MainView could not be resolved, so a missing
            // field degrades to today's behaviour rather than a null argument.
            var lodSettings = (FeedConfig.LodMainView && _lodMainView != null) ? _lodMainView : _lodProbe;
            if (!ReferenceEquals(lodSettings, _lastLodUsed))
            {
                _lastLodUsed = lodSettings;
                RttLog.Line($"LOD profile: {(ReferenceEquals(lodSettings, _lodMainView) ? "MainView" : "EnvironmentProbe")} " +
                            $"— {DescribeLod(lodSettings)}");
            }

            _miDoCullingFirstPass.Invoke(_cullJob, new object[]
            {
                commandList, view, lodSettings, cullCtx, geomBuffers,
                null, null, null, null, -1, 0, -1,
            });

            // Far plane drives how much space the clustering job has to bin lights
            // into. 5 km was copied from the probe pass, which is sizing for a whole
            // environment probe; a camera orbiting 100 m from a ship needs a fraction
            // of that, and the cost scales with it. Free frame rate at no visual cost
            // until the far plane clips something you wanted to see.
            _miClusterDoWork.Invoke(_clusterJob, new object[]
            {
                commandList, Prop2(cullCtx, "EntityProxies"), geomBuffers, _clusterCtx, res,
                (float)FeedConfig.CullFarPlane,
            });

            // Step 3: fill our GBuffer with real surface data. Additive for now — the
            // forward-ish environment pass below still produces what the panel shows,
            // so this changes nothing visible. It proves the GBuffer gets WRITTEN,
            // which is the prerequisite for the lighting jobs in step 4.
            //
            //   GBufferPassJob.DoWork(cl, GeometryContext, OutputGeometryBufferContext,
            //                         clearRenderTargets, fsrMasks)
            //
            // Gated on owning the depth buffer: this job writes depth through the
            // ScreenBuffers global, so without that swap it would write the engine's.
            // Ordering matters more than swapping here. The camera is a CONSTANT BUFFER
            // bound to the command list, not an object on any global we can replace —
            // the carrier survey found no camera on OutputGeometryBufferContext, no
            // Frame type, nothing on CoreSystems. IndirectEnvironmentPassJob takes
            // cameraCb and BINDS ours; GBufferPassJob takes none and inherits whatever
            // is currently bound. So the GBuffer pass has to run AFTER something that
            // binds our camera, or it rasterises from the player's — which is exactly
            // what the feed showed.
            if (FeedConfig.GBufferAfterEnv) { /* runs below, after the env pass */ }
            else if (FeedConfig.GBufferPass && !_gbPassBlocked && savedGBuffer != null && _depthSwapOk)
            {
                if (_miGBufferPass == null)
                {
                    var job = _sceneDrawSystem?.GetType().GetField("_gBufferPass", Any)?.GetValue(_sceneDrawSystem);
                    _gBufferPassJob = job;
                    _miGBufferPass = job?.GetType().GetMethods(Any)
                        .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 5);
                    if (_miGBufferPass == null)
                    {
                        _gbPassBlocked = true;
                        RttLog.Line("GBuffer pass: DoWork(5 args) not found — step 3 disabled.");
                    }
                }

                if (_miGBufferPass != null)
                {
                    try
                    {
                        _miGBufferPass.Invoke(_gBufferPassJob, new object[]
                            { commandList, Prop2(cullCtx, "FirstPass"), geomBuffers, true, null });
                        if (_gbPassLogs++ == 0)
                            RttLog.Line("=== GBUFFER PASS: surface data written into our own GBuffer. ===");
                    }
                    catch (Exception e)
                    {
                        _gbPassBlocked = true;
                        RttLog.Error("gbuffer pass (disabled, feed continues)", e);
                    }
                }
            }

            // The env pass has TWO jobs, and with deferred lighting we only want one of
            // them.
            //
            // Its pixels are fully-lit colour, so leaving them in our target and then
            // running ExecuteLighting composites light twice — which blows highlights out
            // harder instead of filling shadows, and is why the range looked unchanged.
            //
            // Its SIDE EFFECT is binding our camera constant buffer, which GBufferPassJob
            // depends on because it takes no camera of its own. Without it the GBuffer
            // reverts to the player's viewpoint.
            //
            // So when deferred lighting is producing the image, render the env pass into a
            // scratch target: keep the camera binding, discard the lighting. One extra
            // 512x512 pass, which is cheap.
            object envRtv = rtv;
            bool deferredOwnsImage = FeedConfig.ExecuteLighting && FeedConfig.EnvPassToScratch;
            if (deferredOwnsImage && FeedConfig.EnvPass)
            {
                try
                {
                    scratchBorrow = _miBorrowRt.Invoke(_texPool, new object[]
                        { "RttCamScratch", colourFormat, colourFormat, res, 1, null, 128 });
                    var sRtv = ViewOf(scratchBorrow, "IRenderTargetView");
                    if (sRtv != null)
                    {
                        envRtv = sRtv;
                        if (_scratchLogs++ == 0)
                            RttLog.Line("Env pass -> scratch target: kept for the camera-CB binding only, " +
                                        "its lighting discarded so ExecuteLighting is the single light source.");
                    }
                }
                catch (Exception e) { if (_scratchLogs++ < 2) RttLog.Error("env scratch target", e); }
            }

            // Forward path: one pass that writes fully-lit colour. Switchable, because
            // running it AND the deferred chain would light the scene twice.
            if (FeedConfig.EnvPass)
                _miEnvPassDoWork.Invoke(_envPass, new object[]
                {
                    commandList, geomBuffers, cameraCb, view,
                    Prop2(cullCtx, "FirstPass"), _clusterCtx, _shadowResources,
                    envRtv, dsv, true,
                });

            // Our target was never cleared if the env pass went to scratch, so clear it
            // here — otherwise ExecuteLighting composites onto whatever the pool left in
            // the borrow.
            if (deferredOwnsImage && !ReferenceEquals(envRtv, rtv))
                ClearSlotToZero(commandList, rtv);

            // The GBuffer pass, now that the env pass above has bound OUR camera CB.
            if (FeedConfig.GBufferAfterEnv && FeedConfig.GBufferPass && !_gbPassBlocked
                && savedGBuffer != null && _depthSwapOk)
            {
                if (_miGBufferPass == null)
                {
                    var job = _sceneDrawSystem?.GetType().GetField("_gBufferPass", Any)?.GetValue(_sceneDrawSystem);
                    _gBufferPassJob = job;
                    _miGBufferPass = job?.GetType().GetMethods(Any)
                        .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 5);
                    if (_miGBufferPass == null)
                    {
                        _gbPassBlocked = true;
                        RttLog.Line("GBuffer pass: DoWork(5 args) not found.");
                    }
                }
                if (_miGBufferPass != null)
                {
                    try
                    {
                        _miGBufferPass.Invoke(_gBufferPassJob, new object[]
                            { commandList, Prop2(cullCtx, "FirstPass"), geomBuffers, true, null });
                        if (_gbPassLogs++ == 0)
                            RttLog.Line("=== GBUFFER PASS: written AFTER the env pass, so our camera CB is bound. ===");
                    }
                    catch (Exception e)
                    {
                        _gbPassBlocked = true;
                        RttLog.Error("gbuffer pass after env (disabled)", e);
                    }
                }
            }

            // THE fundamental one: the engine's whole deferred lighting stage.
            //
            //   ExecuteLighting(ResizableRWRenderTargetTexture lBuffer)
            //
            // Its single parameter made this look unreachable — everything else comes
            // from globals. But the GBuffer and depth globals ARE OURS for the duration
            // of this pass, which is the entire point of the swap.
            //
            // It is also the ORCHESTRATOR: it sets up the constant buffers and state the
            // individual light jobs need, then runs them in order. Calling AmbientLightJob
            // directly skipped that setup, which is precisely why it threw
            // InvalidCastException on a ResizableRWBuffer -> IConstantBufferView. Going in
            // at the top instead of the middle may be the difference.
            //
            // This is what replaces IndirectEnvironmentPassJob — the probe path, which is
            // pre-exposed and range-compressed for cube-map storage, and is the reason the
            // feed's lighting range is so limited.
            if (FeedConfig.ExecuteLighting && !_execLightBlocked && savedGBuffer != null && _depthSwapOk)
            {
                try
                {
                    if (_miExecLighting == null)
                    {
                        _miExecLighting = _sceneDrawSystem?.GetType().GetMethods(Any)
                            .FirstOrDefault(m => m.Name == "ExecuteLighting" && m.GetParameters().Length == 1);
                        if (_miExecLighting == null)
                        {
                            _execLightBlocked = true;
                            RttLog.Line("ExecuteLighting: not found.");
                        }
                    }

                    if (_miExecLighting != null)
                    {
                        // Must be the resizable flavour — the same type gate as the post
                        // passes. rtBorrow is resizable whenever a fidelity layer is on.
                        var lBuffer = Prop2(rtBorrow, "Resource") ?? rtBorrow;
                        var want = _miExecLighting.GetParameters()[0].ParameterType;
                        if (!want.IsInstanceOfType(lBuffer))
                        {
                            _execLightBlocked = true;
                            RttLog.Line($"ExecuteLighting: lBuffer is {lBuffer?.GetType().Name}, needs {want.Name} — " +
                                        "enable tonemap or sky so the colour target is borrowed resizable.");
                        }
                        else
                        {
                            // Close the engine's own RTX gate for the duration, so the GI
                            // work inside ExecuteLighting is skipped by its guard rather
                            // than by pulling a job out from under it. Restored in the
                            // finally — not doing so costs the player ray tracing.
                            bool giGated = FeedConfig.GateGi && SuppressGiViaRtxGate();
                            try
                            {
                                _miExecLighting.Invoke(_sceneDrawSystem, new[] { lBuffer });
                                if (_execLightLogs++ == 0)
                                    RttLog.Line($"=== EXECUTE LIGHTING: the engine's full deferred lighting stage ran " +
                                                $"against OUR GBuffer (GI gated={giGated}). ===");
                            }
                            finally { RestoreGiRtxGate(); }
                        }
                    }
                }
                catch (Exception e)
                {
                    _execLightBlocked = true;
                    RttLog.Error("ExecuteLighting (disabled, feed continues)", e);
                }
            }

            // Deferred path: composite lighting by reading the GBuffer we just wrote.
            if (FeedConfig.Deferred && savedGBuffer != null && _depthSwapOk)
                RunDeferredLighting(commandList, rtv, cullCtx);

            // Atmosphere. Two arguments — a command list and a target — so by the test
            // that has decided everything else, this one should be reachable.
            if (FeedConfig.Atmosphere && !_atmoBlocked)
            {
                try
                {
                    if (_miAtmo == null)
                    {
                        _atmoJob = _sceneDrawSystem?.GetType().GetField("_atmosphereAdditiveJob", Any)?.GetValue(_sceneDrawSystem);
                        _miAtmo = _atmoJob?.GetType().GetMethods(Any)
                            .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 2);
                        if (_miAtmo == null) { _atmoBlocked = true; RttLog.Line("Atmosphere: DoWork(2 args) not found."); }
                    }
                    if (_miAtmo != null)
                    {
                        _miAtmo.Invoke(_atmoJob, new object[] { commandList, rtv });
                        if (_atmoLogs++ == 0) RttLog.Line("=== ATMOSPHERE: AtmosphereAdditiveJob applied. ===");
                    }
                }
                catch (Exception e) { _atmoBlocked = true; RttLog.Error("atmosphere (disabled, feed continues)", e); }
            }

            // AtmosphereMultiplyJob — the OTHER half of atmosphere, and the half we have
            // never had.
            //
            // What we run today (IndirectPlanetEnvironmentJob) is BlendState.Additive: it
            // adds in-scatter and nothing else. Geometry never gets aerial-perspective
            // EXTINCTION — the haze that desaturates and washes out distant surfaces — and
            // there is no atmospheric sun disc. Multiply is that half, and it is a
            // single-RTV job.
            //
            // Its inputs, read from the IL rather than guessed:
            //     CommonResources.FrameSettings           camera-independent
            //     CommonResources.JitteredCameraSettings  <- needs the camera CB swap
            //     CommonResources.AllPlanetEnvSetups      the player's culled planet list
            //     ScreenBuffers.DepthStencilBuffer        <- needs OUR depth
            //     CommonResources.AtmosphereLUTTables     per-planet, camera-independent
            //
            // The depth is the subtle one. It must be the buffer our GEOMETRY pass just
            // wrote — the per-pass "RttCameraDepth" — not the persistent "RttGBufferDepth"
            // that the GBuffer swap installs, which only GBufferPassJob ever writes. Get
            // that wrong and the job samples a depth our scene never touched, which would
            // look exactly like "invokes cleanly, does nothing".
            if (FeedConfig.AtmosphereMultiply && !_atmoMulBlocked)
            {
                object savedDepth = null;
                try
                {
                    if (_miAtmoMul == null)
                    {
                        _atmoMulJob = _sceneDrawSystem?.GetType().GetField("_atmosphereMultiplyJob", Any)?.GetValue(_sceneDrawSystem);
                        _miAtmoMul = _atmoMulJob?.GetType().GetMethods(Any)
                            .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 2);
                        if (_miAtmoMul == null)
                        {
                            _atmoMulBlocked = true;
                            RttLog.Line("Atmosphere multiply: _atmosphereMultiplyJob / DoWork(2 args) not found.");
                        }
                        else if (!FeedConfig.SwapCameraCb)
                        {
                            // Not fatal, but it would compute extinction along the PLAYER'S
                            // rays and look like the pass is broken. Say so once rather
                            // than let it be mistaken for a failed pass.
                            RttLog.Line("Atmosphere multiply: swapCameraCb is OFF — this job reads the " +
                                        "global camera CB, so it will compute along the PLAYER'S view rays. " +
                                        "Turn swapCameraCb on before judging the result.");
                        }
                    }

                    if (_miAtmoMul != null)
                    {
                        savedDepth = InstallPassDepth(depthBorrow);
                        _miAtmoMul.Invoke(_atmoMulJob, new object[] { commandList, rtv });
                        if (_atmoMulLogs++ == 0)
                            RttLog.Line("=== ATMOSPHERE MULTIPLY: extinction / aerial perspective applied. ===");
                    }
                }
                catch (Exception e) { _atmoMulBlocked = true; RttLog.Error("atmosphere multiply (disabled, feed continues)", e); }
                finally { RestorePassDepth(savedDepth); }
            }

            // Sky, on top of the geometry pass and using its depth so it fills only
            // what geometry did not cover.
            //
            //   IndirectPlanetEnvironmentJob.DoWork(cl, cameraSettingsBuffer,
            //       closeTarget, farTarget, depthTexture, view)
            //
            // Both target parameters get our one render target: the probe pipeline
            // splits near and far sky across two targets, and we have a single view
            // with no such split. Depth comes from the pass we just ran.
            // Alternative sky: DrawSkybox(cl, lBuffer).
            //
            // IndirectPlanetEnvironmentJob is the PROBE pipeline's sky, and it evidently
            // takes its orientation from the engine's cube-face state rather than the view
            // we hand it — which is the original "spinning sky", and now shows up as a sky
            // that is too zoomed and rotates far faster than the orbit. Giving it separate
            // near/far targets reduced the symptom without addressing the cause.
            //
            // DrawSkybox takes only a command list and a target, so it uses the camera
            // constant buffer that is BOUND — and by this point that is ours, bound by the
            // env pass. Two arguments, no probe state.
            // The SUN, as a layer rather than a mode.
            //
            // skyMode is a CHOICE — 0/1/2 — so setting it to 2 did not add the sun, it
            // replaced IndirectPlanetEnvironmentJob, which is the pass supplying the
            // starfield AND the planetary atmosphere. That is why the sun arrived and
            // the stars and atmosphere left together.
            //
            // They compose in one order only. DrawSkybox WRITES SkyLight to SV_Target0;
            // IndirectPlanetEnvironmentJob's PSO is BlendState.Additive. So the sun goes
            // down first and the atmosphere adds on top. The reverse loses the atmosphere.
            //
            // Two hard requirements, both from the IL rather than from a guess:
            //
            //   * ScreenBuffers.DepthStencilBuffer must be OUR pass depth. The shader
            //     depth-tests so the sky only fills where geometry did not draw; against
            //     the player's 4K depth it fills the wrong pixels entirely.
            //   * gbufferSwap MUST be on. The shader declares TWO outputs — SkyLight to
            //     SV_Target0 and MotionVectors to SV_Target1 — and target 1 is
            //     ScreenBuffers.GBuffer[Motion]. Without the swap we write our motion
            //     vectors into the PLAYER'S GBuffer, which is their FSR input. That is a
            //     corruption of their view, not ours, and it would be easy to blame on
            //     something else.
            if (FeedConfig.SunPass && !_sunBlocked)
            {
                object savedSunDepth = null;
                try
                {
                    if (!FeedConfig.GBufferSwap)
                    {
                        _sunBlocked = true;
                        RttLog.Line("Sun: REFUSING to run — gbufferSwap is off, and DrawSkybox writes motion " +
                                    "vectors to SV_Target1 = ScreenBuffers.GBuffer[Motion]. Without the swap " +
                                    "that is the PLAYER'S motion-vector buffer and would corrupt their FSR.");
                    }
                    else
                    {
                        _miDrawSkybox ??= _sceneDrawSystem?.GetType().GetMethods(Any)
                            .FirstOrDefault(m => m.Name == "DrawSkybox" && m.GetParameters().Length == 2);
                        var want = _miDrawSkybox?.GetParameters()[1].ParameterType;
                        var lBuf = Prop2(rtBorrow, "Resource") ?? rtBorrow;

                        if (_miDrawSkybox == null || want == null || !want.IsInstanceOfType(lBuf))
                        {
                            _sunBlocked = true;
                            RttLog.Line($"Sun: unavailable — DrawSkybox={(_miDrawSkybox == null ? "NOT FOUND" : "ok")}, " +
                                        $"target is {lBuf?.GetType().Name} and it wants {want?.Name}.");
                        }
                        else
                        {
                            LogSunSettings();

                            // Whether to hand the skybox OUR depth.
                            //
                            // It binds ScreenBuffers.DepthStencilBuffer.DepthStencilReadOnly
                            // and depth/stencil-tests against it, so in principle it should
                            // be ours — the sky must fill only where geometry did not draw.
                            //
                            // In practice installing it produced patches of sky rather than
                            // a full one, while leaving the engine's in place drew the whole
                            // skybox. Partial rejection means the buffer is bound but its
                            // stencil is not what the pass expects: nothing has written our
                            // stencil, because GBufferPassJob is what marks it and that is
                            // gated on gbufferPass. Switchable rather than argued about.
                            if (FeedConfig.SunPassDepth)
                                savedSunDepth = InstallPassDepth(depthBorrow);
                            _miDrawSkybox.Invoke(_sceneDrawSystem, new[] { commandList, lBuf });
                            if (_sunLogs++ == 0)
                                RttLog.Line("=== SUN: DrawSkybox applied as a layer (sun disc + starfield); " +
                                            "the atmosphere pass adds on top. ===");
                        }
                    }
                }
                catch (Exception e) { _sunBlocked = true; RttLog.Error("sun layer (disabled, sky continues)", e); }
                finally { RestorePassDepth(savedSunDepth); }
            }

            if (FeedConfig.SkyMode == 2 && !_skyBlocked)
            {
                try
                {
                    _miDrawSkybox ??= _sceneDrawSystem?.GetType().GetMethods(Any)
                        .FirstOrDefault(m => m.Name == "DrawSkybox" && m.GetParameters().Length == 2);
                    if (_miDrawSkybox == null)
                    {
                        _skyBlocked = true;
                        RttLog.Line("Sky: DrawSkybox(2 args) not found.");
                    }
                    else
                    {
                        var want = _miDrawSkybox.GetParameters()[1].ParameterType;
                        var lBuf = Prop2(rtBorrow, "Resource") ?? rtBorrow;
                        if (!want.IsInstanceOfType(lBuf))
                        {
                            _skyBlocked = true;
                            RttLog.Line($"Sky: DrawSkybox wants {want.Name}, our target is {lBuf?.GetType().Name} — " +
                                        "enable tonemap or sky so the colour target is borrowed resizable.");
                        }
                        else
                        {
                            _miDrawSkybox.Invoke(_sceneDrawSystem, new[] { commandList, lBuf });
                            if (_skyboxLogs++ == 0)
                                RttLog.Line("=== SKY: DrawSkybox applied (uses the BOUND camera CB, not probe face state). ===");
                        }
                    }
                }
                catch (Exception e) { _skyBlocked = true; RttLog.Error("DrawSkybox (disabled)", e); }
            }
            else if (FeedConfig.SkyMode == 1 && !_skyBlocked && _miPlanetEnvDoWork != null)
            {
                try
                {
                    var depthSrv = ViewOf(depthBorrow, "ITexture2DView");
                    if (depthSrv == null)
                    {
                        _skyBlocked = true;
                        RttLog.Line("Sky: no ITexture2DView on the depth borrow — disabled.");
                    }
                    else
                    {
                        // Two DISTINCT targets. Handing the same render target to both
                        // closeTarget and farTarget is what made the sky spin: the job
                        // draws a near and a far sky layer with different projections,
                        // so a shared target means the second overwrites the first
                        // every frame and the result tumbles. The engine's probe path
                        // keeps them separate.
                        //
                        // We only want one image, so the far layer goes to a scratch
                        // target that is borrowed and immediately returned. Wasteful by
                        // one small draw, and the alternative is guessing at which
                        // layer we want.
                        object farBorrow = null;
                        try
                        {
                            farBorrow = _miBorrowRt.Invoke(_texPool, new object[]
                                { "RttSkyFar", colourFormat, colourFormat, res, 1, null, 128 });
                            var farRtv = ViewOf(farBorrow, "IRenderTargetView") ?? rtv;

                            _miPlanetEnvDoWork.Invoke(_planetEnvJob, new object[]
                                { commandList, cameraCb, rtv, farRtv, depthSrv, view });

                            if (_skyLogs++ == 0)
                                RttLog.Line($"=== SKY: IndirectPlanetEnvironmentJob applied " +
                                            $"(close=ours, far={(farBorrow == null ? "SHARED — expect spin" : "separate scratch")}). ===");
                        }
                        finally
                        {
                            if (farBorrow != null) try { ReturnBorrowed(farBorrow); } catch { }
                        }
                    }
                }
                catch (Exception e)
                {
                    _skyBlocked = true;
                    RttLog.Error("sky (disabled, geometry pass continues)", e);
                }
            }

            // Publish the result: copy our colour target into the offscreen target
            // the LCD side owns, exactly as OffscreenUIRenderer.DrawOne does for UI.
            CopyToFeed(commandList, rtBorrow);

            _renders++;
            if (_renders == 1)
                RttLog.Line("=== CAMERA PASS SUBMITTED — if the game survives, a second 3D view rendered. ===");
        }
        finally
        {
            // FIRST, before anything else can throw: the engine must not be left
            // pointing at our 512x512 array.
            CameraCbSwap.Restore(savedCameraCb);
            RestoreProbeSettings(savedProbeSettings);
            RestoreCamera(savedCamera);
            RestoreGBuffer(savedGBuffer);

            try { if (geomBorrowed) Call(geomBuffers, "Return"); } catch { }
            try { OwnContexts.Return(borrowedCulling); } catch { }
            try { if (depthBorrow != null) ReturnBorrowed(depthBorrow); } catch { }
            try { if (scratchBorrow != null) ReturnBorrowed(scratchBorrow); } catch { }
            try { if (rtBorrow != null) ReturnBorrowed(rtBorrow); } catch { }
            try { (cameraCb as IDisposable)?.Dispose(); } catch { }
            try { File.Delete(LivePath); } catch { }

            // Survival is the real result: the crash, if any, lands during replay
            // well after this method returns.
            if (_renders >= 20 && !_survivedLogged)
            {
                _survivedLogged = true;
                RttLog.Line($"=== CAMERA PASS SURVIVED {_renders} submissions. The second scene render works. ===");
            }
        }
    }

    // ------------------------------------------------------------ publish
    // The LCD side owns an OffscreenRenderTarget (BlitProbe stage 2). Its Render12
    // counterpart lives in OffscreenTargetManager._registeredTextures, keyed by
    // handle. Copying into that texture is how our pixels become something a panel
    // can display.
    private static object _feedTexture;
    private static object _feedRes, _feedFormat;   // dictate our render target's shape
    private static object _feedComponent;          // Render12 OffscreenRenderTargetComponent
    private static int _feedState;      // 0 untried, 1 ready, -1 unavailable
    private static int _copyLogs;

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
                Array.Clear(_ldrRing, 0, _ldrRing.Length); _ldrReady = null; _ringIndex = -1;
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
                        for (int i = 0; i < _ldrRing.Length; i++)
                            _ldrRing[i] = _miBorrowRt.Invoke(_texPool, new object[]
                                { "RttCameraLdr" + (char)('A' + i), _feedFormat, _feedFormat, res ?? _feedRes, 1, null, 128 });
                        _ringIndex = -1;
                        RttLog.Line($"Feed: allocated {_ldrRing.Length} persistent LDR targets (ring; write N, hand over N-1).");
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
                    var srcSrv = ViewOf(rtBorrow, "ITexture2DView");
                    if (dstRtv == null || srcSrv == null)
                    {
                        if (_copyLogs++ < 2)
                            RttLog.Line($"Feed copy: view lookup failed (dstRtv={dstRtv != null}, srcSrv={srcSrv != null}).");
                        _feedState = -1;
                        return;
                    }

                    // Preferred: the engine's own exposure + tonemap chain. Neither
                    // ComputeExposure nor ApplyToneMapping reads the global
                    // ScreenBuffers, so both work on our textures. This is what turns
                    // a clamped HDR image into one that looks like the main view.
                    bool toneMapped = false;
                    if (FeedConfig.Tonemap && !_toneBlocked
                        && _miComputeExposure != null && _miApplyToneMapping != null)
                    {
                        try
                        {
                            var hdrTex = Prop2(rtBorrow, "Resource") ?? rtBorrow;

                            // ApplyToneMapping writes a ResizableRWRenderTargetTexture,
                            // which the ring slots are not — and must not become, because
                            // the ring is the copy source for CopyTextureSubresource and
                            // that needs exact 512x512 subresources. So tonemap into a
                            // resizable scratch target and let the existing CopyJob blit
                            // handle the (possibly rescaling) trip into the ring.
                            if (_ldrResizable == null && _miBorrowResizableRt != null)
                            {
                                var lres = Prop2(_feedTexture, "Resolution") ?? _feedRes;
                                _ldrResizable = _miBorrowResizableRt.Invoke(_texPool, new object[]
                                    { "RttCameraLdrTonemap", _feedFormat, lres ?? _feedRes, null, 1, null, 128 });
                                RttLog.Line("Fidelity: allocated a resizable LDR target for the tonemap output.");
                            }
                            var ldrTex = Prop2(_ldrResizable, "Resource") ?? _ldrResizable;

                            // Check the types BEFORE invoking. Reflection's failure mode
                            // is an ArgumentException from deep inside Invoke, which says
                            // nothing about which parameter of which pass was wrong.
                            if (ldrTex == null ||
                                !TypeFits(_miComputeExposure, 1, hdrTex, "ComputeExposure.lBuffer") ||
                                !TypeFits(_miApplyToneMapping, 1, hdrTex, "ApplyToneMapping.input") ||
                                !TypeFits(_miApplyToneMapping, 2, ldrTex, "ApplyToneMapping.output"))
                            {
                                _toneBlocked = true;
                                RttLog.Line("Tonemap BLOCKED on texture type — see output/fidelity-survey.txt. " +
                                            "The whole cheap tier needs a Resizable borrow if this is the case.");
                            }
                            else
                            {
                                // Prefer the engine's PROBE exposure over running the eye
                                // adaptation chain. It is the right exposure for an
                                // offscreen render, it costs nothing per frame (no
                                // histogram pass), and — the part that matters beyond our
                                // own image — it stops us perturbing the player's eye
                                // adaptation by feeding our HDR buffer into it.
                                object exposure = null;
                                if (FeedConfig.ProbeExposure && _probeExposureJob != null)
                                {
                                    exposure = Prop2(_probeExposureJob, "Exposure");

                                    // It is a Single — a scalar exposure value, not the
                                    // ITexture2DView ApplyToneMapping wants. Right number,
                                    // wrong shape. Checked rather than passed, because
                                    // handing it over throws an ArgumentException that
                                    // disables tonemapping AND bloom for the session.
                                    if (exposure != null &&
                                        !_miApplyToneMapping.GetParameters()[3].ParameterType.IsInstanceOfType(exposure))
                                    {
                                        if (!_logProbeExp)
                                        {
                                            _logProbeExp = true;
                                            RttLog.Line($"Exposure: EnvironmentProbeExposureJob.Exposure is " +
                                                        $"{exposure.GetType().Name}, not a texture view — it is the probe's " +
                                                        "scalar exposure, so it cannot drive ApplyToneMapping directly.");
                                        }
                                        exposure = null;
                                    }
                                    else if (exposure != null) _exposureSource = "probe";
                                }

                                // Reuse an exposure the engine already computed, rather
                                // than running ComputeExposure ourselves. This is the
                                // bisect last night's test never did: ComputeExposure and
                                // ApplyToneMapping ran together, so "the post passes
                                // corrupt the main view" was concluded about the pair.
                                // ComputeExposure is the one that WRITES shared state
                                // (histogram, _autoExposures, temporal adaptation);
                                // ApplyToneMapping may be a pure blit.
                                // Preferred: our own constant. Reuse is the fallback,
                                // kept because it is what proved ApplyToneMapping safe.
                                if (exposure == null && FeedConfig.ExposureValue > 0.0)
                                {
                                    exposure = OwnConstantExposure(ReuseExistingExposure());
                                    if (exposure != null)
                                    {
                                        // Write the value EXPLICITLY rather than trusting
                                        // the pool's clearColor argument to have been
                                        // applied eagerly. That was an assumption, and an
                                        // uncleared texture holds whatever the pool last
                                        // left there — which would make exposureValue a
                                        // number that does nothing. One pixel, so free.
                                        ClearExposure(commandList);
                                        _exposureSource = $"constant {FeedConfig.ExposureValue} (owned 1x1, explicit clear)";
                                    }
                                }

                                if (exposure == null && FeedConfig.ReuseExposure)
                                {
                                    exposure = ReuseExistingExposure();
                                    if (exposure != null) _exposureSource = "reused (ComputeExposure NOT called)";
                                }

                                if (exposure == null)
                                {
                                    // ComputeExposure(cl, lBuffer, out exposure, out debugHistogram)
                                    var expArgs = new object[] { commandList, hdrTex, null, null };
                                    _miComputeExposure.Invoke(_sceneDrawSystem, expArgs);
                                    exposure = expArgs[2];
                                    _exposureSource = "ComputeExposure (WRITES shared eye-adaptation state)";
                                }

                                // One unambiguous line naming the source actually used.
                                // The previous version shared a log counter across all
                                // three branches, so whichever ran second and third was
                                // silent — the bisect could not be read from the log at
                                // all, which is the same defect as the drawOne telemetry.
                                if (_exposureSource != _loggedExposureSource)
                                {
                                    _loggedExposureSource = _exposureSource;
                                    RttLog.Line($"Exposure source: {_exposureSource}");
                                }

                                // ApplyToneMapping threw a NullReferenceException from
                                // inside the engine on the first attempt, so say which of
                                // its inputs were null rather than guessing at it. Both are
                                // reference types it is free to dereference.
                                if (!_toneInputsLogged)
                                {
                                    _toneInputsLogged = true;
                                    RttLog.Line($"Tonemap inputs: exposure={(exposure == null ? "NULL" : exposure.GetType().Name)} " +
                                                $"hdr={hdrTex.GetType().Name} ldr={ldrTex.GetType().Name}");
                                }

                                if (exposure == null)
                                {
                                    _toneBlocked = true;
                                    RttLog.Line("Tonemap: ComputeExposure returned a null exposure — it needs eye-adaptation " +
                                                "state the probe pass does not set up. Disabled; flat blit continues.");
                                    throw new InvalidOperationException("null exposure");
                                }

                                // Bloom feeds the tonemap's last parameter. Attempted
                                // whenever tonemapping is on unless explicitly disabled:
                                // ApplyToneMapping takes a non-nullable
                                // ResizableRenderTargetTexture there, so a null bloom is a
                                // prime suspect for the NRE inside it.
                                object bloom = null, bloomBorrow = null;

                                // Cheap path: skip ApplyBloom's downsample/upsample chain
                                // and hand the tonemap a flat borrowed target. It only has
                                // to be non-null — that is what the NRE was about — and
                                // this also removes one of the three main-view post passes
                                // we borrow, which is a stability win as well as a speed
                                // one. Borrowed per pass and returned, never held: a
                                // resizable pooled texture kept across frames is suspect
                                // for the main-view corruption.
                                if (FeedConfig.CheapBloom && _miBorrowResizableRt != null)
                                {
                                    var bloomType = _miApplyToneMapping.GetParameters()[4].ParameterType;
                                    bloomBorrow = BorrowBloomStandIn(bloomType);
                                    bloom = Prop2(bloomBorrow, "Resource") ?? bloomBorrow;
                                    if (bloom != null && !bloomType.IsInstanceOfType(bloom))
                                    {
                                        if (_bloomLogs++ == 0)
                                            RttLog.Line($"Cheap bloom: stand-in is {bloom.GetType().Name}, tonemap wants " +
                                                        $"{bloomType.Name} — falling back to ApplyBloom.");
                                        try { if (bloomBorrow != null) ReturnBorrowed(bloomBorrow); } catch { }
                                        bloom = bloomBorrow = null;
                                    }
                                    else if (bloom != null && _bloomLogs++ == 0)
                                        RttLog.Line("=== BLOOM: cheap stand-in (ApplyBloom skipped). ===");
                                }

                                // Real bloom, amortised.
                                //
                                // ApplyBloom is a multi-pass downsample/upsample chain and
                                // it is what halved the frame rate when this was first
                                // tried. But bloom is inherently LOW FREQUENCY — a blurred
                                // copy of the bright parts — so recomputing it 28 times a
                                // second buys almost nothing on a camera that orbits once
                                // every 30 s. bloomEveryN computes it on one pass in N and
                                // reuses the result in between, which is the standard trade
                                // and turns the cost into cost/N.
                                //
                                // The safety question is settled rather than assumed:
                                // ApplyBloom reads Settings.PostProcess.Bloom (a bool),
                                // DebugViewHelper.GetCurrentEngineDebugView(), and calls
                                // BloomJob.DoWork(cl, source, exposure) with both inputs as
                                // parameters. No ScreenBuffers, no camera CB, no temporal
                                // state. It was ComputeExposure that corrupted the player's
                                // view, not this.
                                //
                                // Holding the Borrowed<T> across passes is the part that
                                // needs care — that is a pool loan, and the pool asserts on
                                // shutdown if it is never handed back. ReleaseHeldBorrows
                                // returns it.
                                if (bloom == null && FeedConfig.Bloom && !_bloomBlocked && _miApplyBloom != null)
                                {
                                    try
                                    {
                                        int everyN = Math.Max(1, FeedConfig.BloomEveryN);
                                        bool recompute = _bloomHeld == null || (_renders % everyN) == 0;

                                        if (recompute)
                                        {
                                            // Return the previous loan BEFORE taking a new
                                            // one, or we accumulate one texture per recompute.
                                            if (_bloomHeld != null)
                                            {
                                                try { ReturnBorrowed(_bloomHeld); } catch { }
                                                _bloomHeld = null;
                                            }

                                            // ApplyBloom(cl, toneMappingInput, exposure, out bloom)
                                            var bArgs = new object[] { commandList, hdrTex, exposure, null };
                                            _miApplyBloom.Invoke(_sceneDrawSystem, bArgs);
                                            _bloomHeld = bArgs[3];
                                            _bloomRecomputes++;
                                        }

                                        // NOT returned in the finally below — it is reused
                                        // until the next recompute. bloomBorrow stays null
                                        // so that path cannot take it back from under us.
                                        bloom = Prop2(_bloomHeld, "Resource") ?? _bloomHeld;

                                        if (_bloomLogs++ == 0)
                                            RttLog.Line($"=== BLOOM: real ApplyBloom, recomputed every {everyN} pass(es) " +
                                                        $"and reused in between (bloom={(bloom == null ? "null" : bloom.GetType().Name)}). ===");
                                    }
                                    catch (Exception e)
                                    {
                                        _bloomBlocked = true;
                                        bloom = null;
                                        if (_bloomHeld != null) { try { ReturnBorrowed(_bloomHeld); } catch { } _bloomHeld = null; }
                                        RttLog.Error("bloom (disabled, tonemap continues)", e);
                                    }
                                }

                                // ApplyToneMapping(cl, input, output, exposure, bloom)
                                //
                                // bloom is NOT optional despite being a reference type:
                                // passing null threw a NullReferenceException from inside
                                // the engine method. That is why bloom is computed as part
                                // of tonemapping rather than as an independent layer.
                                try
                                {
                                    _miApplyToneMapping.Invoke(_sceneDrawSystem,
                                        new object[] { commandList, hdrTex, ldrTex, exposure, bloom });
                                }
                                finally
                                {
                                    if (bloomBorrow != null)
                                        try { ReturnBorrowed(bloomBorrow); } catch { }
                                }

                                toneMapped = true;
                                if (_toneLogs++ == 0)
                                    RttLog.Line($"=== TONEMAP: engine exposure+tonemap applied (exposure={(exposure == null ? "null" : "ok")}). ===");
                            }
                        }
                        catch (Exception e)
                        {
                            if (_toneLogs++ < 3) RttLog.Error("tonemap", e);
                            _toneBlocked = true;
                            RttLog.Line("Tonemap disabled — falling back to the flat CopyJob blit.");
                        }
                    }

                    // The CopyJob always runs: it is what lands the frame in the
                    // exact-sized ring slot the panel copy reads from. Tonemapping only
                    // changes where it reads FROM — the tonemapped LDR scratch instead
                    // of the raw HDR target. Without tonemapping this is a bare format
                    // conversion with no tone response, which is why highlights clamp.
                    //
                    // postProcess stays null — PostProcess.Normalize crashes, it needs
                    // resources this call site does not set up.
                    var blitSrc = srcSrv;
                    if (toneMapped)
                    {
                        var toneSrv = ViewOf(_ldrResizable, "ITexture2DView");
                        if (toneSrv != null) blitSrc = toneSrv;
                        else if (_toneLogs++ < 2)
                            RttLog.Line("Tonemap: no ITexture2DView on the tonemapped target — blitting raw HDR instead.");
                    }
                    // cropRect is the SOURCE region, and leaving it null makes CopyJob
                    // read a rect the size of the DESTINATION rather than the whole
                    // source. With a 1024x1024 render into a 512x512 panel that copies
                    // the top-left quadrant 1:1 — which is precisely the "feed zoomed
                    // into the top left" symptom, not a projection problem.
                    //
                    // Naming the full source rect makes the blit scale instead of crop,
                    // which is what turns the extra pixels into anti-aliasing.
                    object crop = null;
                    var srcRes = Prop2(Prop2(rtBorrow, "Resource") ?? rtBorrow, "Resolution");
                    if (srcRes != null) crop = MakeRect(_miCopyDoWork.GetParameters()[7].ParameterType, srcRes);

                    // DEBUG: put a GBuffer slot on the panel instead of the rendered image.
                    //
                    // "GBufferPassJob ran without throwing" has never meant "wrote correct
                    // pixels" on this project — the range-culling bug is the most recent
                    // case, where a pass completed cleanly and silently dropped most of its
                    // output. The whole deferred path is built on that GBuffer, so it is
                    // worth one switch to see it rather than three passes to infer it.
                    //
                    // Slots, from GBufferIndex: 1=BaseColor/Emissivity, 2=Normal,
                    // 3=Metalness/Roughness/AO, 4=Parallax, 5=MotionVectors.
                    //
                    // What to expect: BaseColor should be a recognisable albedo image from
                    // the ORBIT camera's angle. Normal should be smooth colour gradients
                    // that shift as the camera moves. Either being black, garbage, or the
                    // PLAYER'S viewpoint kills the deferred path until it is fixed, and
                    // that is much cheaper to learn now.
                    if (FeedConfig.DebugGBuffer > 0)
                    {
                        var slotSrv = DebugGBufferSrv(FeedConfig.DebugGBuffer - 1);
                        if (slotSrv != null)
                        {
                            blitSrc = slotSrv;
                            crop = null;   // the GBuffer is our render resolution already
                        }
                    }

                    // Zero the slot first, so the alpha (= metalness) we deliberately
                    // stop writing is 0 rather than whatever the pool left behind.
                    if (FeedConfig.ZeroMetalness) ClearSlotToZero(commandList, dstRtv);

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
    private static object _postProcess;   // CopyJob.PostProcess.Normalize, or null
    private static readonly object[] _ldrRing = new object[3];   // session-owned; see CopyToFeed
    private static object _ldrReady;                             // the slot handed to the UI stage
    private static int _ringIndex = -1;
    private static int _skipLogs;
    private static bool _blitLogged;
    private static long _firstPassAt;
    private static bool _startupLogged, _startupDoneLogged;
    private static object _sceneDrawSystem;
    private static MethodInfo _miComputeExposure, _miApplyToneMapping, _miApplyBloom;
    private static object _planetEnvJob, _probeExposureJob, _eyeAdaptationJob;
    private static FieldInfo _autoExposuresField;
    private static string _exposureSource = "", _loggedExposureSource = "";
    private static bool _logProbeExp;

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
    private static object _ownExposureBorrow;
    private static double _ownExposureFor = double.NaN;
    private static MethodInfo _miBorrowRenderTarget;
    private static bool _ownExposureBlocked;

    private static object OwnConstantExposure(object referenceTex)
    {
        if (_ownExposureBlocked) return null;
        try
        {
            double want = FeedConfig.ExposureValue;

            // Re-borrow only when the value actually changes, so live tuning works
            // without churning the pool every frame.
            if (_ownExposureBorrow != null && Math.Abs(want - _ownExposureFor) < 1e-9)
                return ViewOf(_ownExposureBorrow, "ITexture2DView") ?? _ownExposureBorrow;

            if (_miBorrowRenderTarget == null)
                _miBorrowRenderTarget = _texPool?.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "BorrowRenderTargetTexture" && m.GetParameters().Length == 5);
            if (_miBorrowRenderTarget == null) { _ownExposureBlocked = true; return null; }

            // Match the engine's own exposure format so the tonemap samples it the
            // same way it samples the real thing.
            var fmt = Prop2(referenceTex, "Format") ?? _feedFormat;
            var clear = MakeClearColour(_miBorrowRenderTarget.GetParameters()[3].ParameterType, (float)want);
            if (clear == null)
            {
                _ownExposureBlocked = true;
                RttLog.Line("Constant exposure: could not build a clear colour — falling back to the reused texture.");
                return null;
            }

            var old = _ownExposureBorrow;
            _ownExposureBorrow = _miBorrowRenderTarget.Invoke(_texPool, new object[]
                { "RttConstExposure", fmt, MakeVector2I(1, 1), clear, 128 });
            _ownExposureFor = want;
            if (old != null) try { ReturnBorrowed(old); } catch { }

            RttLog.Line($"Constant exposure: 1x1 {fmt} cleared to {want} — owned, no shared state.");
            return ViewOf(_ownExposureBorrow, "ITexture2DView") ?? _ownExposureBorrow;
        }
        catch (Exception e)
        {
            _ownExposureBlocked = true;
            RttLog.Error("constant exposure", e);
            return null;
        }
    }

    // The clear-colour parameter is Nullable<something>; discover how to build it
    // rather than assuming a type. Tries a 4-float ctor, then 1-float, then a
    // default instance with its channel fields set.
    private static object MakeClearColour(Type nullableType, float v)
    {
        var t = Nullable.GetUnderlyingType(nullableType) ?? nullableType;
        try
        {
            foreach (var ctor in t.GetConstructors())
            {
                var ps = ctor.GetParameters();
                if (ps.Length == 4 && ps.All(p => p.ParameterType == typeof(float)))
                    return ctor.Invoke(new object[] { v, v, v, v });
                if (ps.Length == 1 && ps[0].ParameterType == typeof(float))
                    return ctor.Invoke(new object[] { v });
            }

            var box = Activator.CreateInstance(t);
            int set = 0;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType == typeof(float)) { f.SetValue(box, v); set++; }
            if (set > 0) return box;

            RttLog.Line($"Constant exposure: {t.Name} has no float ctor or float fields — " +
                        $"members: {string.Join(", ", t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.FieldType.Name + " " + f.Name))}");
        }
        catch (Exception e) { RttLog.Error("clear colour", e); }
        return null;
    }

    private static object ReuseExistingExposure()
    {
        try
        {
            if (_autoExposuresField == null || _eyeAdaptationJob == null) return null;
            if (_autoExposuresField.GetValue(_eyeAdaptationJob) is not Array arr || arr.Length == 0) return null;

            for (int i = 0; i < arr.Length; i++)
            {
                var tex = arr.GetValue(i);
                if (tex == null) continue;
                var srv = ViewOf(tex, "ITexture2DView") ?? tex;
                if (srv != null) return srv;
            }
        }
        catch (Exception e) { RttLog.Error("reuse exposure", e); }
        return null;
    }
    private static MethodInfo _miPlanetEnvDoWork;
    private static MethodInfo _miBorrowResizableRt, _miBorrowResizableRenderTarget;

    // A small, cleared render target to stand in for a computed bloom. Small because
    // the tonemap samples it — a 64x64 black texture contributes nothing visible,
    // which is the point: tone response without the bloom chain's cost.
    private static object BorrowBloomStandIn(Type want)
    {
        try
        {
            // ApplyToneMapping wants ResizableRenderTargetTexture (not RW), which is a
            // different borrow from the colour target's.
            if (_miBorrowResizableRenderTarget == null && _texPool != null)
                _miBorrowResizableRenderTarget = _texPool.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "BorrowResizableRenderTargetTexture" && m.GetParameters().Length == 5);
            if (_miBorrowResizableRenderTarget == null) return null;

            // (debugName, format, maxResolution, clearColor, lifetime)
            return _miBorrowResizableRenderTarget.Invoke(_texPool, new object[]
                { "RttBloomStandIn", _hdrFormat, MakeVector2I(64, 64), null, 128 });
        }
        catch (Exception e) { RttLog.Error("bloom stand-in borrow", e); return null; }
    }
    private static object _ldrResizable;     // tonemap output, before the downscale
    private static string _lastResizableRes;

    // Blocked = tried and cannot work, as opposed to switched off in config. Kept
    // separate so flipping the config back on does not silently retry something that
    // already failed, while a hot reload still gets a clean attempt.
    private static bool _toneBlocked, _bloomBlocked, _skyBlocked, _toneInputsLogged;
    private static int _bloomLogs, _skyLogs;

    // Would this argument actually bind? Reflection's failure mode here is an
    // ArgumentException from deep inside Invoke, which says nothing useful about
    // which parameter of which pass was wrong.
    private static bool TypeFits(MethodInfo m, int index, object arg, string label)
    {
        var want = m.GetParameters()[index].ParameterType;
        if (want.IsByRef) want = want.GetElementType();
        if (arg == null || want == null) return true;          // let Invoke decide
        if (want.IsInstanceOfType(arg)) return true;

        RttLog.Line($"  type mismatch: {label} wants {want.Name}, we have {arg.GetType().Name}");
        return false;
    }

    // What the post passes want, versus what we can actually hand them.
    //
    // Every cheap fidelity pass takes ResizableRWRenderTargetTexture while our HDR
    // target is a pooled RWRenderTargetTexture. If those are unrelated types, the
    // whole tier is blocked at once — so resolve the question by dumping the exact
    // parameter types and every Borrow* the pool offers, rather than by trying one
    // call and inferring from a crash.
    // ------------------------------------------------- can GI be switched off cleanly?
    // The ambient term is what the feed is missing, and ExecuteLighting supplies it
    // properly — it is the orchestrator that sets up the constant buffer AmbientLightJob
    // was missing when called directly. Its only defect was polluting the player's
    // TEMPORAL ray-traced GI accumulator.
    //
    // Nulling the GI job objects threw NullReferenceException — ExecuteLighting does not
    // null-check them. But emptying a LIST worked perfectly for local light shadows, and
    // flipping a flag the engine ITSELF checks would be better still: it takes the
    // engine's own non-RTX path rather than a path it never expects.
    //
    // So: find a writable RTX/GI switch. Read-only survey.
    // ------------------------------------------- suppress GI via the engine's own gate
    // ExecuteLighting is what the feed actually needs: it supplies the missing AMBIENT
    // term, and it is the orchestrator that sets up the constant buffer AmbientLightJob
    // lacked when called directly. Its only defect was polluting the player's temporal
    // ray-traced GI accumulator — visible as flickering noise in the main view.
    //
    // Nulling _raytraceGiJob threw NullReferenceException: ExecuteLighting does not
    // null-check it, because the engine never expects that field to be null. Clearing
    // SceneDrawSystem.IsRtxInitialized instead uses the flag the engine ITSELF tests, so
    // the GI work is skipped by its own guard rather than by removing something from
    // under it. That is the difference between an unexpected state and a supported one.
    //
    // AtomicFlag is a value type, so a default instance is the "not set" state.
    private static FieldInfo _rtxInitField, _rtxStartedField;
    private static object _savedRtxInit;
    private static bool _rtxSaved, _rtxGateBlocked, _rtxGateLogged;

    private static bool SuppressGiViaRtxGate()
    {
        if (_rtxGateBlocked || _sceneDrawSystem == null) return false;
        try
        {
            _rtxInitField ??= _sceneDrawSystem.GetType().GetField("IsRtxInitialized", Any);
            if (_rtxInitField == null || _rtxInitField.IsInitOnly)
            {
                _rtxGateBlocked = true;
                RttLog.Line("GI gate: IsRtxInitialized not writable — cannot suppress GI cleanly.");
                return false;
            }

            _savedRtxInit = _rtxInitField.GetValue(_sceneDrawSystem);
            _rtxInitField.SetValue(_sceneDrawSystem,
                _rtxInitField.FieldType.IsValueType
                    ? Activator.CreateInstance(_rtxInitField.FieldType)   // default = unset
                    : null);
            _rtxSaved = true;

            if (!_rtxGateLogged)
            {
                _rtxGateLogged = true;
                RttLog.Line($"GI gate: IsRtxInitialized cleared for our pass ({_rtxInitField.FieldType.Name} " +
                            "default = unset) — ExecuteLighting should skip GI through its own guard, " +
                            "leaving the player's temporal accumulator alone.");
            }
            return true;
        }
        catch (Exception e)
        {
            _rtxGateBlocked = true;
            RttLog.Error("suppress GI via rtx gate", e);
            RestoreGiRtxGate();
            return false;
        }
    }

    private static void RestoreGiRtxGate()
    {
        if (!_rtxSaved) return;
        _rtxSaved = false;
        try { _rtxInitField?.SetValue(_sceneDrawSystem, _savedRtxInit); }
        catch (Exception e)
        {
            // Not restoring means the player loses ray tracing for the session.
            _rtxGateBlocked = true;
            RttLog.Error("RESTORE RTX GATE FAILED — the player may lose ray tracing", e);
        }
    }

    private static void GiSwitchSurvey(object sds, StringBuilder outer)
    {
        try
        {
            var sb = new StringBuilder($"=== GI switch survey {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("-- SceneDrawSystem RTX / GI state (writable = a candidate) --");
            foreach (var f in sds.GetType().GetFields(Any).OrderBy(f => f.Name))
            {
                var n = f.Name;
                if (!n.Contains("Rtx", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("rtx", StringComparison.Ordinal)
                    && !n.Contains("Gi", StringComparison.Ordinal)
                    && !n.Contains("raytrace", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("surfel", StringComparison.OrdinalIgnoreCase)) continue;
                object v = null; try { v = f.GetValue(f.IsStatic ? null : sds); } catch { }
                sb.AppendLine($"    {(f.IsInitOnly ? "readonly " : "WRITABLE ")}{f.FieldType.Name,-34} {Clean2(n),-36} = {Short(v)}");
            }
            sb.AppendLine();

            sb.AppendLine("-- SceneDrawSystem RTX properties --");
            foreach (var p in sds.GetType().GetProperties(Any))
            {
                if (!p.Name.Contains("Rtx", StringComparison.OrdinalIgnoreCase)
                    && !p.Name.Contains("Raytrac", StringComparison.OrdinalIgnoreCase)) continue;
                object v = null; try { v = p.GetValue(sds); } catch { }
                sb.AppendLine($"    {p.PropertyType.Name,-34} {p.Name,-36} = {Short(v)}  " +
                              $"{(p.CanWrite ? "[settable]" : "[read-only]")}");
            }
            sb.AppendLine();

            // The settings object is the engine's own feature switchboard, so anything
            // here is a path the engine's guards already respect.
            sb.AppendLine("-- SettingsManager raytracing / GI --");
            if (_settings == null) sb.AppendLine("  settings not held");
            else
            {
                foreach (var p in _settings.GetType().GetProperties(Any).OrderBy(p => p.Name))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    if (!p.Name.Contains("Raytrac", StringComparison.OrdinalIgnoreCase)
                        && !p.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                        && !p.Name.Contains("GI", StringComparison.Ordinal)) continue;
                    object v = null; try { v = p.GetValue(_settings); } catch { }
                    sb.AppendLine($"    {p.PropertyType.Name,-30} {p.Name,-32} = {Short(v)}  " +
                                  $"{(p.CanWrite ? "[settable]" : "[read-only]")}");
                }
                foreach (var f in _settings.GetType().GetFields(Any).OrderBy(f => f.Name))
                {
                    if (!f.Name.Contains("raytrac", StringComparison.OrdinalIgnoreCase)
                        && !f.Name.Contains("rtx", StringComparison.OrdinalIgnoreCase)
                        && !f.Name.Contains("gi", StringComparison.OrdinalIgnoreCase)) continue;
                    object v = null; try { v = f.GetValue(f.IsStatic ? null : _settings); } catch { }
                    sb.AppendLine($"    {(f.IsInitOnly ? "readonly " : "WRITABLE ")}{f.FieldType.Name,-30} {Clean2(f.Name),-32} = {Short(v)}");
                }

                // Nested settings groups (RTX, Environment, LOD...) are where the real
                // toggles usually live.
                sb.AppendLine();
                sb.AppendLine("-- nested settings groups --");
                foreach (var p in _settings.GetType().GetProperties(Any).OrderBy(p => p.Name))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object grp = null; try { grp = p.GetValue(_settings); } catch { }
                    if (grp == null || grp.GetType().IsPrimitive || grp is string) continue;
                    var hits = grp.GetType().GetProperties(Any)
                        .Where(x => x.Name.Contains("GI", StringComparison.Ordinal)
                                 || x.Name.Contains("Raytrac", StringComparison.OrdinalIgnoreCase)
                                 || x.Name.Contains("Enable", StringComparison.Ordinal)).ToArray();
                    if (hits.Length == 0) continue;
                    sb.AppendLine($"  {p.Name} ({grp.GetType().Name}):");
                    foreach (var x in hits)
                    {
                        object v = null; try { v = x.GetValue(grp); } catch { }
                        sb.AppendLine($"      {x.PropertyType.Name,-24} {x.Name,-30} = {Short(v)}  " +
                                      $"{(x.CanWrite ? "[settable]" : "[read-only]")}");
                    }
                }
            }

            var path = Path.Combine(RttLog.OutDir, "gi-switch-survey.txt");
            File.WriteAllText(path, sb.ToString());
            outer.AppendLine($"-- GI switch survey -> {path} --");
            RttLog.Line($"GI switch survey -> {path}");
        }
        catch (Exception e) { RttLog.Error("gi switch survey", e); }
    }

    private static void FidelitySurvey(object sds, StringBuilder outer)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Fidelity survey {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();
            sb.AppendLine("What we can hand them:");
            sb.AppendLine($"  our HDR borrow      : {_texPool?.GetType().Name} -> (see BorrowRWRenderTargetTexture below)");
            sb.AppendLine();

            sb.AppendLine("-- candidate passes and their parameter types --");
            foreach (var name in new[]
            {
                "ComputeExposure", "ApplyToneMapping", "ApplyBloom", "DrawSkybox", "ApplyAtmosphere",
                "ComputeSSR", "ExecuteVolumetricPasses", "RenderTransparent", "ProcessParticles",
                "ExecuteLighting", "RenderShadowCascades",
            })
            {
                foreach (var m in sds.GetType().GetMethods(Any).Where(m => m.Name == name))
                    sb.AppendLine($"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            }
            sb.AppendLine();

            // Every way the pool will lend us a texture, so the right one can be
            // picked by signature instead of by name-guessing.
            sb.AppendLine("-- BindableTexturePoolManager borrow methods --");
            if (_texPool != null)
                foreach (var m in _texPool.GetType().GetMethods(Any)
                             .Where(m => m.Name.StartsWith("Borrow"))
                             .OrderBy(m => m.Name))
                    sb.AppendLine($"  {m.ReturnType.Name,-22} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            sb.AppendLine();

            // The type relationship that decides everything.
            var resizable = FindType("ResizableRWRenderTargetTexture");
            var plain = FindType("RWRenderTargetTexture");
            sb.AppendLine("-- the question --");
            sb.AppendLine($"  ResizableRWRenderTargetTexture = {resizable?.FullName ?? "NOT FOUND"}");
            sb.AppendLine($"  RWRenderTargetTexture          = {plain?.FullName ?? "NOT FOUND"}");
            if (resizable != null && plain != null)
                sb.AppendLine($"  Resizable.IsAssignableFrom(plain) = {resizable.IsAssignableFrom(plain)}   " +
                              "<- if False, the cheap tier needs a Resizable borrow");
            if (resizable != null)
                sb.AppendLine($"  Resizable base chain: {Chain(resizable)}");
            if (plain != null)
                sb.AppendLine($"  Plain base chain    : {Chain(plain)}");

            var path = Path.Combine(RttLog.OutDir, "fidelity-survey.txt");
            File.WriteAllText(path, sb.ToString());
            outer.AppendLine($"-- fidelity survey written to {path} --");
            RttLog.Line($"Fidelity survey -> {path}");
        }
        catch (Exception e) { RttLog.Error("fidelity survey", e); }
    }

    // ------------------------------------------------ deferred lighting (step 4)
    // With a GBuffer written, the engine's own lighting jobs can composite into our
    // HDR target by reading surface data instead of us relying on the probe path's
    // single-pass shading.
    //
    // This REPLACES IndirectEnvironmentPassJob rather than adding to it. That pass
    // already writes fully-lit colour, so running both would light the scene twice —
    // brighter and wronger, not softer. `envPass` keeps the forward path available so
    // the two can be compared live.
    //
    // AmbientLightJob is the one that matters for the reported harshness: direct sun
    // plus clustered lights with no ambient term is exactly why lit faces blow out and
    // unlit faces go flat.
    private static object _dirLightJob, _localLightsJob, _ambientJob, _atmoJob, _atmoMulJob;
    private static MethodInfo _miAtmoMul;
    private static bool _atmoMulBlocked;
    private static int _atmoMulLogs;
    private static MethodInfo _miAtmo, _miExecLighting;
    private static bool _execLightBlocked;
    private static int _execLightLogs, _scratchLogs, _eyeJumpLogs;
    private static Keen.VRage.Library.Mathematics.Vector3D _lastEye;
    private static bool _haveLastEye;
    // An SRV onto one of OUR GBuffer slots, for the debug blit. Reads the array we
    // installed rather than ScreenBuffers, so it reports what WE wrote even if the
    // swap were somehow not in effect — which is itself part of what is being tested.
    private static string _debugGbLog;

    private static object DebugGBufferSrv(int slot)
    {
        try
        {
            if (_ourGBufferArray == null || slot < 0 || slot >= _ourGBufferArray.Length)
            {
                LogDebugGb($"slot {slot + 1} unavailable — our GBuffer array is " +
                           $"{(_ourGBufferArray == null ? "null (gbufferSwap off?)" : $"length {_ourGBufferArray.Length}")}");
                return null;
            }
            var tex = _ourGBufferArray.GetValue(slot);
            var srv = ViewOf(tex, "ITexture2DView");
            LogDebugGb(srv == null
                ? $"slot {slot + 1} is {tex?.GetType().Name ?? "null"} with no ITexture2DView"
                : $"slot {slot + 1} ({tex?.GetType().Name}, {Prop2(tex, "Resolution")}) on the panel — " +
                  "expect albedo for slot 1, normals for slot 2. The player's viewpoint or " +
                  "black means the GBuffer pass is not writing our view.");
            return srv;
        }
        catch (Exception e) { LogDebugGb("threw: " + e.Message); return null; }
    }

    private static void LogDebugGb(string what)
    {
        if (what == _debugGbLog) return;
        _debugGbLog = what;
        RttLog.Line("GBuffer debug: " + what);
    }

    private static MethodInfo _miDrawSkybox;
    private static bool _sunBlocked, _sunSettingsLogged;
    private static int _sunLogs;

    // Why the sun came out as a BLACK CIRCLE last time, and how to tell in one line.
    //
    // SkyboxMotionVectorsPixel.hlsl composites it as:
    //
    //     sunOpacity = smoothstep(outerDot, innerDot, dot(L, -V));
    //     sunDisc    = SunDiscColor * SunDiscIntensity * <that same factor>;
    //     result     = result * (1 - sunOpacity) + sunDisc;
    //
    // The mask and the colour share one factor, so the disc PUNCHES THE SKYBOX OUT and
    // replaces it with SunDiscColor x SunDiscIntensity. A black circle therefore means
    // the mask worked — the sun is in the right place and the right size — and the
    // colour arrived as zero. It is not a missing pass or a wrong camera.
    //
    // The shipped values are healthy (SunDisc true, SunDiscIntensity 570, disc about a
    // degree across), so if the live values differ, something is zeroing them. Print
    // them once rather than guess, because "read it from the config file" already
    // proved nothing about what the shader is actually handed.
    private static void LogSunSettings()
    {
        if (_sunSettingsLogged || _settings == null) return;
        _sunSettingsLogged = true;
        try
        {
            var env = Prop2(_settings, "Environment");
            if (env == null) { RttLog.Line("Sun: SettingsManager.Environment unreadable."); return; }
            RttLog.Line($"Sun settings: SunDisc={Prop2(env, "SunDisc")} " +
                        $"Intensity={Prop2(env, "SunDiscIntensity")} " +
                        $"Color={Prop2(env, "SunDiscColor")} " +
                        $"InnerDot={Prop2(env, "SunDiscInnerDot")} OuterDot={Prop2(env, "SunDiscOuterDot")}. " +
                        "A BLACK disc means Color x Intensity reached the shader as zero — " +
                        "the mask punches out the skybox and substitutes that product.");
        }
        catch (Exception e) { RttLog.Error("sun settings", e); }
    }
    private static int _skyboxLogs;
    private static bool _atmoBlocked;
    private static int _atmoLogs;
    private static MethodInfo _miDirLight, _miLocalLights, _miAmbient;
    private static bool _dirBlocked, _localBlocked, _ambientBlocked, _lightingResolved;
    private static int _dirLogs, _localLogs, _ambientLogs;
    private static bool _localDiffuseNull = true;   // try null first, fall back to rtv

    private static void ResolveLightingJobs()
    {
        if (_lightingResolved) return;
        _lightingResolved = true;
        try
        {
            var t = _sceneDrawSystem?.GetType();
            _dirLightJob    = t?.GetField("_directionalLightJob", Any)?.GetValue(_sceneDrawSystem);
            _localLightsJob = t?.GetField("_localLightsJob", Any)?.GetValue(_sceneDrawSystem);
            _ambientJob     = t?.GetField("_ambientLightJob", Any)?.GetValue(_sceneDrawSystem);

            _miDirLight     = Pick4(_dirLightJob, 4);
            _miLocalLights  = Pick4(_localLightsJob, 5);
            // Prefer the 4-arg overload (explicit GI buffers); fall back to 2-arg.
            _miAmbient      = Pick4(_ambientJob, 4) ?? Pick4(_ambientJob, 2);

            RttLog.Line($"Deferred lighting: directional={(_miDirLight == null ? "NOT FOUND" : "ok")} " +
                        $"local={(_miLocalLights == null ? "NOT FOUND" : "ok")} " +
                        $"ambient={(_miAmbient == null ? "NOT FOUND" : $"ok({_miAmbient.GetParameters().Length} args)")}");
        }
        catch (Exception e) { RttLog.Error("resolve lighting jobs", e); }
    }

    private static MethodInfo Pick4(object job, int argc) => job?.GetType().GetMethods(Any)
        .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == argc);

    // DirectionalLightJob wants an ITexture2DView of the shadow atlas. Guessing at
    // "ShadowMap"/"Texture" found nothing and the job threw on a null, so enumerate
    // DirectionalLightShadowResources and take the first texture view on it —
    // reporting every member once, so a miss is one log line from a fix.
    private static object _shadowSrvCache;
    private static bool _shadowSrvLogged;

    private static object FindShadowView()
    {
        if (_shadowSrvCache != null) return _shadowSrvCache;
        try
        {
            if (_shadowResources == null) return null;
            var t = _shadowResources.GetType();
            var sb = new StringBuilder($"Deferred: {t.Name} members —");

            foreach (var (name, val) in t.GetFields(Any).Select(f => (f.Name, Get(f)))
                     .Concat(t.GetProperties(Any).Where(p => p.GetIndexParameters().Length == 0)
                                                .Select(p => (p.Name, Get(p)))))
            {
                if (val == null) { sb.Append($"\n    {name} = null"); continue; }
                sb.Append($"\n    {name} = {val.GetType().Name}");

                var srv = ViewOf(val, "ITexture2DView");
                if (srv != null && _shadowSrvCache == null)
                {
                    _shadowSrvCache = srv;
                    sb.Append("   <-- using this as shadowRtView");
                }
            }

            if (!_shadowSrvLogged) { _shadowSrvLogged = true; RttLog.Line(sb.ToString()); }
            return _shadowSrvCache;

            object Get(MemberInfo m)
            {
                try { return m is FieldInfo f ? f.GetValue(_shadowResources) : ((PropertyInfo)m).GetValue(_shadowResources); }
                catch { return null; }
            }
        }
        catch (Exception e) { RttLog.Error("find shadow view", e); return null; }
    }

    // AmbientLightJob rejects null GI buffers. It has no 2-argument overload on this
    // build (the 2-arg DoWork in the recon belongs to AtmosphereAdditiveJob), so the
    // only way in is to supply real textures. Ours are empty, which means zero GI
    // contribution — but ambient itself is computed from the GBuffer and sky, and that
    // is the term the feed is missing.
    private static object _giDiffuse, _giSpecular;

    // The real bloom result, held between recomputes. A pool loan, returned by
    // ReleaseHeldBorrows — never dropped, or the pool asserts at shutdown.
    private static object _bloomHeld;
    private static long _bloomRecomputes;
    private static bool _ambientPrereqLogged;

    private static bool EnsureGiBuffers(object res)
    {
        if (_giDiffuse != null) return true;
        try
        {
            // These were borrowed with BorrowRWRenderTargetTexture — the WRONG sibling,
            // and the likely source of the InvalidCastException that has blocked
            // AmbientLightJob since it was first tried.
            //
            // ResizableRWRenderTargetTexture and RWRenderTargetTexture share interfaces
            // but are unrelated classes, and the engine's own recipe is unambiguous.
            // SceneDrawSystem.ComputeGI — the only caller of AmbientLightJob.DoWork —
            // does exactly this:
            //
            //     BorrowResizableRWRenderTargetTexture(name, format, res, uav, mips, clear, lifetime)
            //       -> Borrowed.Resource
            //       -> ResizableRWRenderTargetTexture.Resize(commandList, resolution)
            //
            // so the GI buffers are RESIZABLE render-target textures, and we were handing
            // the job a different class family that happens to satisfy the parameter type.
            if (_miBorrowResizableRt == null)
            {
                RttLog.Line("Deferred: BorrowResizableRWRenderTargetTexture unavailable — " +
                            "GI buffers cannot be allocated in the shape AmbientLightJob wants.");
                return false;
            }

            _giDiffuse  = _miBorrowResizableRt.Invoke(_texPool, new object[]
                { "RttGiDiffuse",  _hdrFormat, res, null, 1, null, 128 });
            _giSpecular = _miBorrowResizableRt.Invoke(_texPool, new object[]
                { "RttGiSpecular", _hdrFormat, res, null, 1, null, 128 });

            RttLog.Line($"Deferred: GI buffers allocated as RESIZABLE render targets " +
                        $"({Prop2(Prop2(_giDiffuse, "Resource") ?? _giDiffuse, "Resolution")}) — " +
                        "matching SceneDrawSystem.ComputeGI. They stay empty: no GI contribution, " +
                        "but AmbientLightJob needs both parameters non-null and it is the ambient " +
                        "term we are after, not the GI.");
            return true;
        }
        catch (Exception e) { RttLog.Error("gi buffers", e); return false; }
    }

    private static void RunDeferredLighting(object commandList, object rtv, object cullCtx)
    {
        ResolveLightingJobs();

        // Directional sun. shadowRtView is a texture view we do not obviously own —
        // look for one on the shadow resources the engine handed us, and skip the job
        // rather than guess if it is not there. Shadows fitted to the PLAYER's frustum
        // remain a known limitation (see "own shadow cascades" in the roadmap).
        if (FeedConfig.DeferredDirectional && !_dirBlocked && _miDirLight != null)
        {
            try
            {
                var shadowSrv = FindShadowView();
                _miDirLight.Invoke(_dirLightJob, new object[] { commandList, shadowSrv, _shadowResources, rtv });
                if (_dirLogs++ == 0)
                    RttLog.Line($"=== DEFERRED: directional light applied (shadowRtView={(shadowSrv == null ? "null" : "ok")}). ===");
            }
            catch (Exception e) { _dirBlocked = true; RttLog.Error("directional light (disabled)", e); }
        }

        // Clustered local lights. Takes OUR clustering context and geometry buffers,
        // which is why this one is a good fit.
        if (FeedConfig.DeferredLocal && !_localBlocked && _miLocalLights != null)
        {
            try
            {
                _miLocalLights.Invoke(_localLightsJob, new object[]
                    { commandList, rtv, _localDiffuseNull ? null : rtv, _clusterCtx, GeomBuffers });
                if (_localLogs++ == 0)
                    RttLog.Line($"=== DEFERRED: local lights applied (diffuseOnly={(_localDiffuseNull ? "null" : "shared rtv")}). ===");
            }
            catch (Exception e)
            {
                // A null diffuse-only target is the likeliest thing it dislikes; retry
                // once sharing our target before giving up on the job entirely.
                if (_localDiffuseNull)
                {
                    _localDiffuseNull = false;
                    RttLog.Line("Local lights: null rtViewDiffuseOnly rejected — retrying with our own target.");
                }
                else { _localBlocked = true; RttLog.Error("local lights (disabled)", e); }
            }
        }

        // Ambient. THE fix for the harshness — the missing indirect term.
        //
        // AmbientLightJob.DoWork hands off to LightJobSnapshot.Draw, whose binding list
        // names every prerequisite explicitly:
        //
        //     CommonResources.JitteredCameraSettings          <- swapCameraCb
        //     ScreenBuffers.DepthStencilBuffer.DepthTexture   <- our depth (SRV)
        //     ScreenBuffers.GBuffer                           <- our GBuffer (SRV)
        //     ScreenBuffers...DepthStencilReadOnly            <- our depth (DSV)
        //     SetStencilRef(n)                                <- written by our GBuffer pass
        //
        // Missing any of them does not throw; it produces a wrong or empty result, which
        // is the failure mode this project keeps mistaking for "the pass is unusable".
        // Say so once, up front, instead.
        if (FeedConfig.DeferredAmbient && !_ambientPrereqLogged)
        {
            _ambientPrereqLogged = true;
            var missing = new List<string>();
            if (!FeedConfig.SwapCameraCb) missing.Add("swapCameraCb (it would light from the PLAYER'S camera)");
            if (!FeedConfig.GBufferSwap) missing.Add("gbufferSwap (it would read the player's 4K GBuffer)");
            if (!FeedConfig.GBufferPass) missing.Add("gbufferPass (nothing would have written our GBuffer or its stencil)");
            if (missing.Count > 0)
                RttLog.Line("Ambient: PREREQUISITES MISSING — " + string.Join("; ", missing) +
                            ". The job will run without throwing and produce a wrong or empty " +
                            "result, which is not the same as being unusable.");
        }

        if (FeedConfig.DeferredAmbient && !_ambientBlocked && _miAmbient != null)
        {
            try
            {
                object[] args;
                if (_miAmbient.GetParameters().Length == 2)
                    args = new object[] { commandList, rtv };
                else
                {
                    // Null GI buffers throw inside the job, so supply real (empty) ones.
                    if (!EnsureGiBuffers(_feedRes ?? MakeVector2I(RenderW, RenderH)))
                    { _ambientBlocked = true; RttLog.Line("Ambient: no GI buffers — disabled."); return; }

                    args = new object[]
                    {
                        commandList, rtv,
                        ViewOf(_giDiffuse, "ITexture2DView"),
                        ViewOf(_giSpecular, "ITexture2DView"),
                    };
                }
                _miAmbient.Invoke(_ambientJob, args);
                if (_ambientLogs++ == 0)
                    RttLog.Line($"=== DEFERRED: ambient light applied ({args.Length} args). ===");
            }
            catch (Exception e) { _ambientBlocked = true; RttLog.Error("ambient light (disabled)", e); }
        }
    }

    // ---------------------------------------------------- GBuffer swap (step 2)
    // Own the GBuffer so the deferred lighting passes have surface data to read.
    //
    // ScreenBuffers.GBuffer is a public settable ResizableRWRenderTargetTexture[5],
    // so this is a swap around our pass rather than a whole second ScreenBuffers:
    //
    //   save global -> install ours -> (our passes) -> restore global
    //
    // The window is inside a single synchronous postfix, so no engine code runs in
    // between, and resource bindings are recorded into the command list at record
    // time — the engine's own recorded commands keep referring to its textures.
    //
    // Step 2 deliberately runs NO passes. If allocate/swap/restore survives on its
    // own, the mechanism is sound and the passes can go on top one at a time. Doing
    // both at once would leave a failure ambiguous, which has cost a launch before.
    private static object[] _ourGBuffer;          // the borrows, kept for the session
    private static Array _ourGBufferArray;        // typed array handed to the setter
    private static PropertyInfo _gbProp, _dsProp;
    private static FieldInfo _dsField;
    private static FieldInfo[] _resFields;
    private static bool _resSwapLogged;
    private static string _ourGBufferRes;
    private static FieldInfo[] _giFields;
    private static bool _giSuppressLogged;
    private static object _ourDepthBorrow, _ourDepthTex;
    private static MethodInfo _miBorrowResizableDepth;
    private static bool _gbBlocked, _gbLogged, _depthSwapOk, _gbPassBlocked;
    private static object _gBufferPassJob;
    private static MethodInfo _miGBufferPass;
    private static int _gbPassLogs;
    private static int _gbSwaps;

    // Clear a target to (0,0,0,0). Reuses the same ClearRenderTargetView the exposure
    // path proved works. Purpose: alpha 0 = non-metal, so the feed renders as a diffuse
    // surface instead of a mirror with nothing to reflect.
    private static object _zeroColour;
    private static int _zeroState;   // 0 untried, 1 ok, -1 unavailable

    private static void ClearSlotToZero(object commandList, object rtv)
    {
        if (_zeroState == -1 || rtv == null) return;
        try
        {
            if (_zeroState == 0)
            {
                _miClearRtv ??= commandList.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "ClearRenderTargetView" && m.GetParameters().Length == 2);
                if (_miClearRtv == null) { _zeroState = -1; return; }
                _zeroColour = MakeClearColour(_miClearRtv.GetParameters()[1].ParameterType, 0f);
                _zeroState = _zeroColour != null ? 1 : -1;
                RttLog.Line(_zeroState == 1
                    ? "Blit: clearing the panel slot to (0,0,0,0) first — alpha 0 = NON-METAL."
                    : "Blit: could not build a zero clear colour; alpha stays as-is.");
                if (_zeroState == -1) return;
            }
            _miClearRtv.Invoke(commandList, new[] { rtv, _zeroColour });
        }
        catch (Exception e) { _zeroState = -1; RttLog.Error("zero slot", e); }
    }

    // Write the exposure value into our 1x1 texture with an explicit clear.
    //
    // Sweeping exposureValue from 0.25 to 0.02 — a 12x change — produced NO visible
    // difference, which means the texture is inert. The pool's clearColor argument was
    // an assumption; this removes it. If the image still does not respond after this,
    // the fault is in what ApplyToneMapping does with the exposure, not in our value.
    private static MethodInfo _miClearRtv;
    private static int _clearExpState;   // 0 untried, 1 ok, -1 unavailable
    private static object _clearColourCache;
    private static double _clearColourFor = double.NaN;

    private static void ClearExposure(object commandList)
    {
        if (_clearExpState == -1 || _ownExposureBorrow == null) return;
        try
        {
            if (_clearExpState == 0)
            {
                _miClearRtv = commandList.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "ClearRenderTargetView" && m.GetParameters().Length == 2);
                _clearExpState = _miClearRtv != null ? 1 : -1;
                RttLog.Line(_miClearRtv != null
                    ? $"Exposure: explicit ClearRenderTargetView found " +
                      $"({string.Join(", ", _miClearRtv.GetParameters().Select(p => p.ParameterType.Name))})."
                    : "Exposure: ClearRenderTargetView(2 args) NOT FOUND — cannot write the value explicitly.");
                if (_clearExpState == -1) return;
            }

            var rtv = ViewOf(_ownExposureBorrow, "IRenderTargetView");
            if (rtv == null) { _clearExpState = -1; RttLog.Line("Exposure: no IRenderTargetView on our 1x1 target."); return; }

            double want = FeedConfig.ExposureValue;
            if (_clearColourCache == null || Math.Abs(want - _clearColourFor) > 1e-9)
            {
                _clearColourCache = MakeClearColour(_miClearRtv.GetParameters()[1].ParameterType, (float)want);
                _clearColourFor = want;
                if (_clearColourCache == null)
                {
                    _clearExpState = -1;
                    RttLog.Line($"Exposure: could not build a clear colour of type " +
                                $"{_miClearRtv.GetParameters()[1].ParameterType.Name}.");
                    return;
                }
                RttLog.Line($"Exposure: clearing our 1x1 target to {want} explicitly each pass.");
            }

            _miClearRtv.Invoke(commandList, new[] { rtv, _clearColourCache });
        }
        catch (Exception e) { _clearExpState = -1; RttLog.Error("clear exposure", e); }
    }

    // Property setter if the engine exposes one, backing field otherwise.
    // Install THIS PASS'S depth — the one the geometry pass just wrote — into
    // ScreenBuffers for the duration of a single job, and put the engine's back.
    //
    // Deliberately separate from InstallOurGBuffer's depth swap, which installs the
    // persistent "RttGBufferDepth" that only GBufferPassJob writes. A pass that wants to
    // read the depth of the scene we just rendered needs the per-pass borrow instead, and
    // conflating the two would hand it a buffer full of nothing.
    private static bool _passDepthBlocked, _passDepthLogged;

    private static object InstallPassDepth(object depthBorrow)
    {
        if (_passDepthBlocked || depthBorrow == null) return null;
        try
        {
            var screenBuffers = ScreenBuffersInstance();
            if (screenBuffers == null) { _passDepthBlocked = true; return null; }

            _dsProp ??= screenBuffers.GetType().GetProperty("DepthStencilBuffer", Any);
            var tex = Prop2(depthBorrow, "Resource") ?? depthBorrow;
            var want = _dsProp?.PropertyType;
            if (tex == null || want == null || !want.IsInstanceOfType(tex))
            {
                _passDepthBlocked = true;
                RttLog.Line($"Pass depth: cannot install — have {tex?.GetType().Name ?? "null"}, " +
                            $"ScreenBuffers wants {want?.Name ?? "?"}.");
                return null;
            }

            var saved = _dsProp.GetValue(screenBuffers);
            SetDepthBuffer(screenBuffers, tex);
            if (!_passDepthLogged)
            {
                _passDepthLogged = true;
                RttLog.Line("Pass depth: our render's depth installed for the atmosphere pass " +
                            "(the per-pass borrow, not the GBuffer one).");
            }
            return saved;
        }
        catch (Exception e) { _passDepthBlocked = true; RttLog.Error("install pass depth", e); return null; }
    }

    private static void RestorePassDepth(object saved)
    {
        if (saved == null) return;
        try
        {
            var screenBuffers = ScreenBuffersInstance();
            if (screenBuffers != null) SetDepthBuffer(screenBuffers, saved);
        }
        catch (Exception e)
        {
            _passDepthBlocked = true;
            RttLog.Error("RESTORE PASS DEPTH FAILED — the engine is now pointed at our 512x512 depth", e);
        }
    }

    private static void SetDepthBuffer(object screenBuffers, object value)
    {
        if (_dsProp is { CanWrite: true }) _dsProp.SetValue(screenBuffers, value);
        else _dsField?.SetValue(screenBuffers, value);
    }

    // The panel's resolution multiplied by renderScale, clamped so a typo cannot ask
    // for a 16K render target.
    private static string _resLogged;

    private static object ScaledRenderRes()
    {
        var baseRes = _feedRes ?? MakeVector2I(RenderW, RenderH);
        double s = Math.Clamp(FeedConfig.RenderScale, 1.0, 8.0);
        if (s <= 1.0) return baseRes;

        try
        {
            int bx = (int)(Prop2(baseRes, "X") ?? RenderW);
            int by = (int)(Prop2(baseRes, "Y") ?? RenderH);
            int x = Math.Clamp((int)(bx * s), 16, 8192);
            int y = Math.Clamp((int)(by * s), 16, 8192);
            var scaled = MakeVector2I(x, y);

            var tag = $"{bx}x{by} x{s} -> {x}x{y}";
            if (tag != _resLogged)
            {
                _resLogged = tag;
                RttLog.Line($"Render scale: supersampling {tag}; the panel copy downsamples it.");
            }
            return scaled ?? baseRes;
        }
        catch { return baseRes; }
    }

    // System.Drawing.Rectangle(0, 0, w, h) built by reflection, since the parameter's
    // assembly is not referenced here.
    private static bool _rectLogged;

    private static object MakeRect(Type nullableRect, object resolution)
    {
        try
        {
            var t = Nullable.GetUnderlyingType(nullableRect) ?? nullableRect;
            int w = (int)(Prop2(resolution, "X") ?? 0);
            int h = (int)(Prop2(resolution, "Y") ?? 0);
            if (w <= 0 || h <= 0) return null;

            var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 4
                && c.GetParameters().All(p => p.ParameterType == typeof(int)));
            if (ctor == null)
            {
                if (!_rectLogged) { _rectLogged = true; RttLog.Line($"Blit: {t.Name} has no (int,int,int,int) ctor — crop rect unavailable."); }
                return null;
            }

            var r = ctor.Invoke(new object[] { 0, 0, w, h });
            if (!_rectLogged) { _rectLogged = true; RttLog.Line($"Blit: source crop rect {w}x{h} — the blit scales to the panel instead of cropping."); }
            return r;
        }
        catch (Exception e) { RttLog.Error("crop rect", e); return null; }
    }

    // Hand the whole set back to the pool. Called only when re-allocating at a new
    // resolution, and only from the pass itself, so nothing is reading them.
    private static void ReleaseOurGBuffer()
    {
        try
        {
            if (_ourGBuffer != null)
                foreach (var b in _ourGBuffer)
                    if (b != null) try { ReturnBorrowed(b); } catch { }
            if (_ourDepthBorrow != null) try { ReturnBorrowed(_ourDepthBorrow); } catch { }
        }
        catch { }
        _ourGBuffer = null;
        _ourGBufferArray = null;
        _ourGBufferRes = null;
        _ourDepthBorrow = _ourDepthTex = null;
        _depthSwapOk = false;
    }

    // ------------------------------------------- where the rasterising camera lives
    // Swapping SettingsManager._renderView changed nothing, and the reason is timing:
    // _renderView is the CPU-side source the engine converts into a camera CONSTANT
    // BUFFER earlier in the frame. By our postfix that conversion has happened, so the
    // bound CB still describes the player. IndirectEnvironmentPassJob takes cameraCb
    // explicitly, which is why the forward path was never affected; GBufferPassJob takes
    // none and uses whatever is bound.
    //
    // So find the carrier. Most likely OutputGeometryBufferContext (the per-view bundle
    // we hand the pass), or a per-frame constants object. Read-only survey.
    private static void CameraCarrierSurvey(StringBuilder outer)
    {
        try
        {
            var sb = new StringBuilder($"=== Camera carrier survey {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("-- OutputGeometryBufferContext (what we pass to GBufferPassJob) --");
            if (GeomBuffers == null) sb.AppendLine("  null");
            else
            {
                sb.AppendLine($"  {GeomBuffers.GetType().FullName}");
                foreach (var f in GeomBuffers.GetType().GetFields(Any).OrderBy(f => f.Name))
                {
                    object v = null; try { v = f.GetValue(GeomBuffers); } catch { }
                    var mark = f.FieldType.Name.Contains("ConstantBuffer") || f.FieldType.Name.Contains("Camera")
                               || f.Name.Contains("amera", StringComparison.Ordinal) ? "   <-- CAMERA?" : "";
                    sb.AppendLine($"    {(f.IsInitOnly ? "readonly " : "         ")}{f.FieldType.Name,-36} {Clean2(f.Name),-32} = {Short(v)}{mark}");
                }
                foreach (var p in GeomBuffers.GetType().GetProperties(Any).Where(p => p.GetIndexParameters().Length == 0))
                {
                    object v = null; try { v = p.GetValue(GeomBuffers); } catch { }
                    var mark = p.PropertyType.Name.Contains("ConstantBuffer") || p.Name.Contains("amera", StringComparison.Ordinal)
                               ? "   <-- CAMERA?" : "";
                    sb.AppendLine($"    prop     {p.PropertyType.Name,-36} {p.Name,-32} = {Short(v)}{mark}{(p.CanWrite ? "  [settable]" : "")}");
                }
            }
            sb.AppendLine();

            // A per-frame constants holder is the other candidate.
            sb.AppendLine("-- Frame / per-frame camera holders --");
            var asm = _sceneDrawSystem?.GetType().Assembly;
            foreach (var tn in new[] { "Frame", "FrameConstants", "RenderFrame" })
            {
                var t = asm?.GetTypes().FirstOrDefault(x => x.Name == tn);
                if (t == null) { sb.AppendLine($"  {tn}: not found"); continue; }
                sb.AppendLine($"  {t.FullName}");
                foreach (var f in t.GetFields(Any).Where(f => f.FieldType.Name.Contains("Camera")
                                                           || f.Name.Contains("amera", StringComparison.Ordinal)
                                                           || f.FieldType.Name.Contains("ConstantBuffer")))
                    sb.AppendLine($"    {(f.IsStatic ? "static " : "       ")}{f.FieldType.Name,-36} {Clean2(f.Name)}");
            }
            sb.AppendLine();

            // And whatever on CoreSystems looks like it holds a bound camera.
            sb.AppendLine("-- CoreSystems statics mentioning camera/constant --");
            var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            if (cs != null)
                foreach (var f in cs.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    if (f.Name.Contains("amera", StringComparison.Ordinal) || f.FieldType.Name.Contains("Camera")
                        || f.FieldType.Name.Contains("Frame"))
                        sb.AppendLine($"    {f.FieldType.Name,-36} CoreSystems.{f.Name}");

            var path = Path.Combine(RttLog.OutDir, "camera-carrier-survey.txt");
            File.WriteAllText(path, sb.ToString());
            outer.AppendLine($"-- camera carrier survey -> {path} --");
            RttLog.Line($"Camera carrier survey -> {path}");
        }
        catch (Exception e) { RttLog.Error("camera carrier survey", e); }
    }

    // ---------------------------------------------- the global camera (RenderView)
    // GBufferPassJob and ExecuteLighting take NO camera parameter, so they rasterise
    // and light from SettingsManager._renderView — the player's. The geometry list was
    // ours (culled from our view) but drawn from where the player stands, which is
    // exactly the "viewpoint stuck inside the ship" symptom.
    //
    // SetCameraParameters would be the tidy route, but it writes _freezedRenderView and
    // maintains temporal state (camera speed buffer, last-frame positions). Calling it
    // twice a frame would corrupt the player's motion vectors.
    //
    // So: our own RenderView instance, every field copied from the player's so we
    // inherit projection, clipping, resolution and anything else we do not understand,
    // with only the camera overridden. Swapped in for the pass, restored after.
    private static FieldInfo _renderViewField;
    private static object _ourRenderView;
    private static FieldInfo[] _rvFields;
    private static bool _camSwapBlocked, _camSwapLogged;
    private static object _lastCamWorld, _lastViewD;

    private static object InstallOurCamera(object camWorld, object viewD)
    {
        if (_camSwapBlocked || !FeedConfig.SwapCamera || _settings == null) return null;
        try
        {
            _renderViewField ??= _settings.GetType().GetFields(Any)
                .FirstOrDefault(f => f.Name.Contains("renderView", StringComparison.OrdinalIgnoreCase)
                                  && !f.Name.Contains("previous", StringComparison.OrdinalIgnoreCase)
                                  && !f.Name.Contains("freez", StringComparison.OrdinalIgnoreCase));
            if (_renderViewField == null)
            {
                _camSwapBlocked = true;
                RttLog.Line("Camera swap: SettingsManager._renderView not found — " +
                            "the GBuffer pass will keep using the player's viewpoint.");
                return null;
            }

            var theirs = _renderViewField.GetValue(_settings);
            if (theirs == null) return null;
            var rvType = theirs.GetType();

            _rvFields ??= rvType.GetFields(Any).Where(f => !f.IsStatic).ToArray();
            _ourRenderView ??= System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(rvType);

            // Copy everything, then override the camera. Copying first means projection,
            // clipping, FOV and resolution stay current with the player's settings.
            foreach (var f in _rvFields)
            {
                try { f.SetValue(_ourRenderView, f.GetValue(theirs)); } catch { }
            }

            int set = 0;
            set += SetRv("ViewD", viewD);
            set += SetRv("InvViewD", camWorld);
            set += SetRv("CameraPosition", Prop2(camWorld, "Translation"));

            if (!_camSwapLogged)
            {
                _camSwapLogged = true;
                RttLog.Line($"Camera swap: own RenderView built ({_rvFields.Length} fields copied, " +
                            $"{set}/3 camera fields overridden). ViewAt0/InvViewAt0 are inherited — " +
                            "if the feed looks translated, those are the floating-origin pair to fix next.");
            }

            _renderViewField.SetValue(_settings, _ourRenderView);
            return theirs;
        }
        catch (Exception e) { _camSwapBlocked = true; RttLog.Error("install camera", e); return null; }
    }

    private static int SetRv(string name, object value)
    {
        if (value == null) return 0;
        var f = _rvFields.FirstOrDefault(x =>
            x.Name.Contains($"<{name}>", StringComparison.Ordinal) || x.Name == name);
        if (f == null || !f.FieldType.IsInstanceOfType(value)) return 0;
        try { f.SetValue(_ourRenderView, value); return 1; } catch { return 0; }
    }

    private static void RestoreCamera(object saved)
    {
        if (saved == null || _renderViewField == null || _settings == null) return;
        try { _renderViewField.SetValue(_settings, saved); }
        catch (Exception e)
        {
            _camSwapBlocked = true;
            RttLog.Error("RESTORE CAMERA FAILED — the main view is now using our camera", e);
        }
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

        return _miCreateCb.MakeGenericMethod(_tTrackedCam)
            .Invoke(_bufMgr, new object[] { "rttCameraSettings", tracked });
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
            fScreen.SetValue(tracked, screen);
            LogScreenResOnce($"Screen.Resolution {was} -> {want} " +
                             "(the engine's value is the player's screen; every ScreenToUV was scaled by the ratio)");
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
    private static object _cbRenderView;
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
            float far       = System.Convert.ToSingle(Prop2(_cbRenderView, "FarClipping") ?? 0f);
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
    private static bool _probeSettingsBlocked, _probeSettingsLogged;

    private static object InstallProbeSettings()
    {
        if (_probeSettingsBlocked || FeedConfig.RecursiveReflections < 0 || _settings == null) return null;
        try
        {
            _environmentField ??= _settings.GetType().GetField("_environment", Any);
            if (_environmentField == null)
            {
                _probeSettingsBlocked = true;
                RttLog.Line("Probe settings: SettingsManager._environment not found — recursiveReflections ignored.");
                return null;
            }

            // GetValue on a struct field boxes a fresh copy each call, so `saved` and
            // `ours` are genuinely independent.
            var saved = _environmentField.GetValue(_settings);
            var ours = _environmentField.GetValue(_settings);

            var fProbe = ours.GetType().GetField("ProbeSettings", Any);
            var probe = fProbe?.GetValue(ours);
            var fRecursive = probe?.GetType().GetField("EnableRecursiveReflections", Any);
            if (fRecursive == null)
            {
                _probeSettingsBlocked = true;
                RttLog.Line("Probe settings: ProbeSettings.EnableRecursiveReflections not found.");
                return null;
            }

            bool want = FeedConfig.RecursiveReflections == 1;
            if (!_probeSettingsLogged)
            {
                _probeSettingsLogged = true;
                RttLog.Line($"Probe settings: EnableRecursiveReflections {fRecursive.GetValue(probe)} -> {want} " +
                            "for our pass only (read live inside IndirectEnvironmentPassJob.DoWork).");
            }
            fRecursive.SetValue(probe, want);
            fProbe.SetValue(ours, probe);
            _environmentField.SetValue(_settings, ours);
            return saved;
        }
        catch (Exception e) { _probeSettingsBlocked = true; RttLog.Error("install probe settings", e); return null; }
    }

    private static void RestoreProbeSettings(object saved)
    {
        if (saved == null || _environmentField == null || _settings == null) return;
        try { _environmentField.SetValue(_settings, saved); }
        catch (Exception e)
        {
            _probeSettingsBlocked = true;
            RttLog.Error("RESTORE PROBE SETTINGS FAILED — the engine's own probes now use our value", e);
        }
    }

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

    private static object _lastLodUsed;

    private static string DescribeLod(object lod)
    {
        if (lod == null) return "NULL";
        return $"MinLOD={Prop2(lod, "MinLOD")} FloraMinLOD={Prop2(lod, "FloraMinLOD")} " +
               $"LODShift={Prop2(lod, "LODShift")} LODShiftVisible={Prop2(lod, "LODShiftVisible")}";
    }

    private static object MakeVector2(float x, float y)
    {
        var t = Type.GetType("Keen.VRage.Library.Mathematics.Vector2, VRage.Library");
        return t == null ? null : Activator.CreateInstance(t, x, y);
    }

    private static object ScreenBuffersInstance()
    {
        try
        {
            var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            if (cs == null) return null;
            foreach (var f in cs.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType.Name.Contains("ScreenBuffers"))
                    return f.GetValue(null);
        }
        catch { }
        return null;
    }

    // Allocate our five targets once, at OUR resolution. The engine's are 3840x2160;
    // ours are 512x512, which is where the cost saving lives.
    private static bool EnsureOurGBuffer(object screenBuffers, object res)
    {
        // Re-allocate if the resolution has moved.
        //
        // This was allocated once, on the FIRST pass — which runs before _feedRes is
        // known, because that resolves later inside CopyToFeed. So the GBuffer came out
        // at RenderW/RenderH (256x256) while the colour target settled at the panel's
        // 512x512, and the lighting stage covered exactly one quadrant of it. That is
        // the "top left quadrant" symptom, and it is an initialisation-order bug rather
        // than anything to do with viewports.
        var wantRes = res?.ToString();
        if (_ourGBufferArray != null && wantRes == _ourGBufferRes) return true;

        if (_ourGBufferArray != null)
        {
            RttLog.Line($"GBuffer: resolution changed {_ourGBufferRes} -> {wantRes}; re-allocating.");
            ReleaseOurGBuffer();
        }

        try
        {
            if (_miBorrowResizableRt == null) return false;
            if (Prop2(screenBuffers, "GBufferFormats") is not Array fmts || fmts.Length == 0) return false;

            _gbProp ??= screenBuffers.GetType().GetProperty("GBuffer", Any);
            if (_gbProp == null || !_gbProp.CanWrite) return false;

            var elemType = _gbProp.PropertyType.GetElementType();
            if (elemType == null) return false;

            var borrows = new object[fmts.Length];
            var arr = Array.CreateInstance(elemType, fmts.Length);
            for (int i = 0; i < fmts.Length; i++)
            {
                // (debugName, srvFormat, maxResolution, uavFormat, mipMaps, clearColor, lifetime)
                borrows[i] = _miBorrowResizableRt.Invoke(_texPool, new object[]
                    { $"RttGBuffer{i}", fmts.GetValue(i), res, null, 1, null, 128 });
                var tex = Prop2(borrows[i], "Resource") ?? borrows[i];
                if (tex == null || !elemType.IsInstanceOfType(tex))
                {
                    RttLog.Line($"GBuffer: slot {i} came back as {(tex == null ? "null" : tex.GetType().Name)}, " +
                                $"need {elemType.Name} — swap disabled.");
                    return false;
                }
                arr.SetValue(tex, i);
            }

            _ourGBuffer = borrows;
            _ourGBufferArray = arr;
            _ourGBufferRes = res?.ToString();
            RttLog.Line($"GBuffer: allocated {fmts.Length} targets at {res} " +
                        $"(engine's are {Prop2(screenBuffers, "PreUpscaleResolution")}).");

            // Depth as well. GBufferPassJob takes NO depth parameter, so it writes
            // through ScreenBuffers.DepthStencilBuffer — the engine's 4K one. Running
            // it without swapping that would scribble our 512x512 scene's depth into
            // the buffer the player's view depends on.
            _dsProp = screenBuffers.GetType().GetProperty("DepthStencilBuffer", Any);
            _miBorrowResizableDepth ??= _texPool?.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "BorrowResizableDepthStencilTexture" && m.GetParameters().Length == 5);

            // The property is CanWrite=False, but an auto-property still has a
            // settable backing field. GBuffer's setter is public and this one is not,
            // which is a hint that Keen did not intend it to move — worth knowing, and
            // worth doing anyway since the alternative is writing the player's depth.
            if (_dsProp is { CanWrite: false })
            {
                // Case-INSENSITIVE: the field is `_depthStencilBuffer`, lowercase after
                // the underscore, so an exact-case Contains missed it and reported "no
                // backing field" when the storage was sitting right there.
                _dsField = screenBuffers.GetType().GetFields(Any)
                    .FirstOrDefault(f => !f.IsInitOnly
                                      && f.FieldType == _dsProp.PropertyType
                                      && f.Name.Contains("depthstencilbuffer", StringComparison.OrdinalIgnoreCase));
                RttLog.Line($"GBuffer: DepthStencilBuffer setter is private; backing field " +
                            $"{(_dsField == null ? "NOT FOUND" : "'" + _dsField.Name + "' found")}.");
            }

            RttLog.Line($"GBuffer: DepthStencilBuffer property found={_dsProp != null} " +
                        $"CanWrite={_dsProp?.CanWrite} field={_dsField != null} " +
                        $"resizableDepthBorrow={_miBorrowResizableDepth != null}");

            if ((_dsProp is { CanWrite: true } || _dsField != null) && _miBorrowResizableDepth != null)
            {
                // (debugName, format, maxResolution, clearValue, lifetime)
                _ourDepthBorrow = _miBorrowResizableDepth.Invoke(_texPool, new object[]
                    { "RttGBufferDepth", _depthFormat, res, null, 128 });
                _ourDepthTex = Prop2(_ourDepthBorrow, "Resource") ?? _ourDepthBorrow;

                var want = _dsProp.PropertyType;
                _depthSwapOk = _ourDepthTex != null && want.IsInstanceOfType(_ourDepthTex);
                RttLog.Line(_depthSwapOk
                    ? "GBuffer: own depth allocated — the GBuffer pass can be driven safely."
                    : $"GBuffer: depth is {(_ourDepthTex == null ? "null" : _ourDepthTex.GetType().Name)}, " +
                      $"need {want.Name} — GBuffer PASS must stay off (swap alone is still safe).");
            }
            else
            {
                _depthSwapOk = false;
                RttLog.Line("GBuffer: cannot own the depth buffer — GBuffer PASS must stay off " +
                            "(it would write the engine's 4K depth). The array swap alone remains safe.");
            }
            return true;
        }
        catch (Exception e) { RttLog.Error("allocate gbuffer", e); return false; }
    }

    // Install ours, returning the engine's array so the caller can restore it. Null
    // means "not installed, do not restore".
    private static object InstallOurGBuffer(object res)
    {
        if (_gbBlocked || !FeedConfig.GBufferSwap) return null;
        try
        {
            var screenBuffers = ScreenBuffersInstance();
            if (screenBuffers == null) { _gbBlocked = true; RttLog.Line("GBuffer: ScreenBuffers not reachable — swap disabled."); return null; }
            if (!EnsureOurGBuffer(screenBuffers, res)) { _gbBlocked = true; return null; }

            var saved = new object[4];
            saved[0] = _gbProp.GetValue(screenBuffers);
            _gbProp.SetValue(screenBuffers, _ourGBufferArray);

            // Resolution too. ExecuteLighting sizes its dispatch from
            // ScreenBuffers.PreUpscaleResolution (3840x2160), so with a 512x512 GBuffer
            // it lit a 4K-sized area and only a small top-left corner landed on our
            // target — the "tiny segment top left, rest black" symptom.
            //
            // Prev is set to the same value on purpose: a mismatch reads as "the
            // resolution just changed", which is how temporal passes decide to reset.
            if (_resFields == null)
            {
                _resFields = screenBuffers.GetType().GetFields(Any)
                    .Where(f => !f.IsInitOnly && f.FieldType.Name == "Vector2I"
                             && (f.Name.Contains("PreUpscaleResolution", StringComparison.OrdinalIgnoreCase)
                              || f.Name.Contains("usedMaxResolution", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                RttLog.Line($"GBuffer: resolution fields to swap — " +
                            $"{(_resFields.Length == 0 ? "NONE FOUND" : string.Join(", ", _resFields.Select(f => Clean2(f.Name))))}");
            }

            // Suppress GI for the duration of our pass.
            //
            // ExecuteLighting includes ray-traced GI, and GI is TEMPORAL — it accumulates
            // across frames. Running it a second time per frame against a different
            // camera and a 512x512 GBuffer pollutes that accumulator, which is what the
            // flickering noise in the player's view is. Nulling the jobs means
            // ExecuteLighting skips GI (if it null-checks) and keeps ambient, directional
            // and local, which are what the feed actually needs — GI contributes least at
            // this resolution anyway.
            //
            // If it does NOT null-check it will throw, which is caught and disables
            // ExecuteLighting rather than the feed.
            if (FeedConfig.SuppressGi && _sceneDrawSystem != null)
            {
                if (_giFields == null)
                    _giFields = new[] { "_raytraceGiJob", "_surfelGenerationJob", "_legacyRaytraceGiJob" }
                        .Select(n => _sceneDrawSystem.GetType().GetField(n, Any))
                        .Where(f => f != null).ToArray();

                if (_giFields.Length > 0)
                {
                    var savedGi = new object[_giFields.Length];
                    for (int i = 0; i < _giFields.Length; i++)
                    {
                        savedGi[i] = _giFields[i].GetValue(_sceneDrawSystem);
                        if (savedGi[i] != null) _giFields[i].SetValue(_sceneDrawSystem, null);
                    }
                    saved[3] = savedGi;
                    if (!_giSuppressLogged)
                    {
                        _giSuppressLogged = true;
                        RttLog.Line($"GI suppressed for our pass: {string.Join(", ", _giFields.Select(f => f.Name))} " +
                                    "— keeps the player's temporal GI accumulator clean.");
                    }
                }
            }

            if (_resFields.Length > 0 && FeedConfig.SwapResolution)
            {
                var savedRes = new object[_resFields.Length];
                for (int i = 0; i < _resFields.Length; i++)
                {
                    savedRes[i] = _resFields[i].GetValue(screenBuffers);
                    _resFields[i].SetValue(screenBuffers, res);
                }
                saved[2] = savedRes;
                if (!_resSwapLogged)
                {
                    _resSwapLogged = true;
                    RttLog.Line($"GBuffer: ScreenBuffers resolution swapped to {res} for the pass " +
                                $"(was {savedRes[0]}).");
                }
            }

            // Depth only when we can actually own it, and only when the pass that
            // needs it is being driven.
            if (_depthSwapOk && FeedConfig.GBufferPass && _ourDepthTex != null)
            {
                saved[1] = _dsProp.GetValue(screenBuffers);
                SetDepthBuffer(screenBuffers, _ourDepthTex);
            }
            _gbSwaps++;

            if (!_gbLogged)
            {
                _gbLogged = true;
                RttLog.Line($"=== GBUFFER SWAP: ours installed for the camera pass " +
                            $"(depth={(saved[1] != null ? "also ours" : "engine's, pass not driven")}). ===");
            }
            return saved;
        }
        catch (Exception e) { _gbBlocked = true; RttLog.Error("install gbuffer", e); return null; }
    }

    private static void RestoreGBuffer(object savedObj)
    {
        if (savedObj is not object[] saved) return;
        try
        {
            var screenBuffers = ScreenBuffersInstance();
            if (screenBuffers == null) return;
            if (saved[0] != null && _gbProp != null) _gbProp.SetValue(screenBuffers, saved[0]);
            if (saved[1] != null) SetDepthBuffer(screenBuffers, saved[1]);

            // Resolution last, and never skipped: leaving the engine believing its
            // viewport is 512x512 would wreck the player's view outright.
            if (saved[2] is object[] savedRes && _resFields != null)
                for (int i = 0; i < _resFields.Length && i < savedRes.Length; i++)
                    if (savedRes[i] != null) _resFields[i].SetValue(screenBuffers, savedRes[i]);

            // GI jobs back. Never skipped — leaving these null would disable the
            // player's global illumination entirely.
            if (saved[3] is object[] savedGi && _giFields != null && _sceneDrawSystem != null)
                for (int i = 0; i < _giFields.Length && i < savedGi.Length; i++)
                    if (savedGi[i] != null) _giFields[i].SetValue(_sceneDrawSystem, savedGi[i]);
        }
        catch (Exception e)
        {
            // Failing to restore leaves the ENGINE rendering into our 512x512 array,
            // which would wreck the player's view. Loud, and disable further swaps.
            _gbBlocked = true;
            RttLog.Error("RESTORE GBUFFER FAILED — main view is now writing our array", e);
        }
    }

    // ------------------------------------------------------- GBuffer recon (step 1)
    // READ ONLY. Nothing here allocates, swaps or invokes anything — it answers the
    // questions the GBuffer swap depends on before a single byte of it is written.
    // Guessing at engine internals has cost a launch every time; surveying first has
    // worked every time.
    private static void GBufferSurvey(object sds, StringBuilder outer)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== GBuffer survey {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();

            // 1. Reach ScreenBuffers off CoreSystems.
            var cs = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            object screenBuffers = null;
            string sbField = null;
            if (cs != null)
                foreach (var f in cs.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!f.FieldType.Name.Contains("ScreenBuffers")) continue;
                    try { screenBuffers = f.GetValue(null); sbField = f.Name; } catch { }
                    break;
                }
            sb.AppendLine($"CoreSystems.{sbField ?? "?"} -> {(screenBuffers == null ? "NULL" : screenBuffers.GetType().FullName)}");
            sb.AppendLine();

            // 2. The array we would swap, and whether it can be set.
            if (screenBuffers != null)
            {
                var t = screenBuffers.GetType();
                var gbProp = t.GetProperty("GBuffer", Any);
                sb.AppendLine("-- GBuffer property --");
                sb.AppendLine($"  found      : {gbProp != null}");
                sb.AppendLine($"  type       : {gbProp?.PropertyType.Name}");
                sb.AppendLine($"  CanWrite   : {gbProp?.CanWrite}   <- the swap depends on this");
                sb.AppendLine($"  setter     : {(t.GetMethod("set_GBuffer", Any) != null ? "set_GBuffer present" : "NOT FOUND")}");

                object gb = null;
                try { gb = gbProp?.GetValue(screenBuffers); } catch (Exception e) { sb.AppendLine($"  read threw : {e.GetType().Name}"); }
                if (gb is Array arr)
                {
                    sb.AppendLine($"  length     : {arr.Length}");
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var e = arr.GetValue(i);
                        sb.AppendLine($"    [{i}] {(e == null ? "null" : e.GetType().Name)}" +
                                      $"  fmt={Prop2(e, "Format")}  res={Prop2(e, "Resolution")}  mips={Prop2(e, "MipLevels")}");
                    }
                }
                sb.AppendLine();

                // Formats we would have to match when allocating our own.
                sb.AppendLine("-- GBufferFormats --");
                try
                {
                    if (Prop2(screenBuffers, "GBufferFormats") is Array fmts)
                        for (int i = 0; i < fmts.Length; i++) sb.AppendLine($"    [{i}] {fmts.GetValue(i)}");
                    else sb.AppendLine("    NOT FOUND");
                }
                catch (Exception e) { sb.AppendLine($"    threw {e.GetType().Name}"); }
                sb.AppendLine();

                // What each slot means.
                var idxType = t.Assembly.GetTypes().FirstOrDefault(x => x.Name == "GBufferIndex");
                sb.AppendLine("-- GBufferIndex --");
                sb.AppendLine(idxType == null
                    ? "    NOT FOUND"
                    : "    " + string.Join(", ", Enum.GetNames(idxType).Select(n => $"{n}={(int)(object)Enum.Parse(idxType, n)}")));
                sb.AppendLine();

                // DepthStencilBuffer is CanWrite=False with no backing field, so it is
                // computed from something else. Whatever that is may be settable, and
                // GBufferPassJob cannot be driven safely until it is — the job takes no
                // depth parameter and would otherwise write the player's 4K depth.
                sb.AppendLine("-- ALL ScreenBuffers fields (hunting the depth storage) --");
                foreach (var f in t.GetFields(Any).OrderBy(f => f.Name))
                {
                    object v = null; try { v = f.GetValue(screenBuffers); } catch { }
                    var mark = f.FieldType.Name.Contains("Depth") ? "   <-- DEPTH" : "";
                    sb.AppendLine($"    {(f.IsInitOnly ? "readonly " : "         ")}{f.FieldType.Name,-34} {f.Name,-32} = {Short(v)}{mark}");
                }
                sb.AppendLine();

                // Anything else on ScreenBuffers we may need to keep consistent.
                sb.AppendLine("-- other ScreenBuffers members --");
                foreach (var p in t.GetProperties(Any).OrderBy(p => p.Name))
                {
                    if (p.GetIndexParameters().Length != 0 || p.Name == "GBuffer") continue;
                    object v = null; try { v = p.GetValue(screenBuffers); } catch { }
                    sb.AppendLine($"    {p.PropertyType.Name,-34} {p.Name,-28} = {Short(v)}");
                }
                sb.AppendLine();
            }

            // 3. The jobs the swap would drive, and exactly what they want.
            sb.AppendLine("-- candidate jobs on SceneDrawSystem --");
            foreach (var fieldName in new[]
            {
                "_gBufferPass", "_ambientLightJob", "_directionalLightJob", "_localLightsJob",
                "_hbaoJob", "_raytraceGiJob", "_surfelGenerationJob", "_atmosphereAdditiveJob",
            })
            {
                var f = sds.GetType().GetField(fieldName, Any);
                object job = null; try { job = f?.GetValue(sds); } catch { }
                sb.AppendLine($"  {fieldName}: {(f == null ? "FIELD NOT FOUND" : job == null ? "null" : job.GetType().Name)}");
                if (job == null) continue;
                foreach (var m in job.GetType().GetMethods(Any).Where(m => m.Name.StartsWith("DoWork")))
                    sb.AppendLine($"      {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            }

            var path = Path.Combine(RttLog.OutDir, "gbuffer-survey.txt");
            File.WriteAllText(path, sb.ToString());
            outer.AppendLine($"-- GBuffer survey written to {path} --");
            RttLog.Line($"GBuffer survey -> {path}");
        }
        catch (Exception e) { RttLog.Error("gbuffer survey", e); }
    }

    private static string Short(object v)
    {
        if (v == null) return "null";
        var s = v.ToString() ?? "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 70 ? s[..70] + "..." : s;
    }

    // -------------------------------------------------- shadow recon (read only)
    // The feed's shadows are the PLAYER's cascades, fitted to their frustum, which is
    // why they are partial and unreliable from a camera 100 m away. Owning them also
    // produces the screen-space shadow mask that DirectionalLightJob wanted and we
    // could not supply.
    //
    // Read-only. The question is which shadow jobs take their state as parameters,
    // since that is what has decided reachability every single time.
    private static void ShadowSurvey(object sds, StringBuilder outer)
    {
        try
        {
            var sb = new StringBuilder($"=== Shadow survey {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("-- shadow / depth / cascade jobs on SceneDrawSystem --");
            foreach (var f in sds.GetType().GetFields(Any).OrderBy(f => f.Name))
            {
                var tn = f.FieldType.Name;
                if (!tn.Contains("Shadow") && !tn.Contains("Cascade") && !tn.Contains("Depth")
                    && !tn.Contains("DepthJob") && !f.Name.Contains("depth", StringComparison.OrdinalIgnoreCase)) continue;

                object v = null; try { v = f.GetValue(sds); } catch { }
                sb.AppendLine($"  {tn,-34} {f.Name,-32} = {(v == null ? "null" : "set")}");
                if (v == null) continue;
                foreach (var m in v.GetType().GetMethods(Any)
                             .Where(m => m.Name is "DoWork" or "Render" or "Execute" || m.Name.StartsWith("DoWork")))
                    sb.AppendLine($"      {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            }
            sb.AppendLine();

            // Can we build the resources bundle DirectionalLightJob wants, or only
            // read the engine's?
            sb.AppendLine("-- DirectionalLightShadowResources --");
            var resType = _shadowResources?.GetType();
            if (resType == null) sb.AppendLine("  engine instance not held");
            else
            {
                sb.AppendLine($"  {resType.FullName}  (isValueType={resType.IsValueType})");
                foreach (var c in resType.GetConstructors(Any))
                    sb.AppendLine($"    ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                foreach (var f in resType.GetFields(Any))
                    sb.AppendLine($"    field {f.FieldType.Name,-30} {Clean2(f.Name)}");
            }
            sb.AppendLine();

            // The shadow-mask format is a strong hint at what shadowRtView must be.
            sb.AppendLine("-- shadow-related SceneDrawSystem methods --");
            foreach (var m in sds.GetType().GetMethods(Any)
                         .Where(m => m.Name.Contains("Shadow") || m.Name.Contains("Cascade"))
                         .OrderBy(m => m.Name))
                sb.AppendLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            sb.AppendLine();

            // Also cheap and requested: atmosphere takes a command list and a target,
            // nothing else. Confirm the signature before driving it.
            sb.AppendLine("-- AtmosphereAdditiveJob (2-arg candidate) --");
            var atmo = sds.GetType().GetField("_atmosphereAdditiveJob", Any)?.GetValue(sds);
            if (atmo == null) sb.AppendLine("  not held");
            else foreach (var m in atmo.GetType().GetMethods(Any).Where(m => m.Name.StartsWith("DoWork") || m.Name.StartsWith("Draw")))
                sb.AppendLine($"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");

            var path = Path.Combine(RttLog.OutDir, "shadow-survey.txt");
            File.WriteAllText(path, sb.ToString());
            outer.AppendLine($"-- shadow survey written to {path} --");
            RttLog.Line($"Shadow survey -> {path}");
        }
        catch (Exception e) { RttLog.Error("shadow survey", e); }
    }

    private static string Clean2(string n)
    {
        int a = n.IndexOf('<'), b = n.IndexOf('>');
        return (a == 0 && b > 1) ? n[1..b] : n;
    }

    private static string Chain(Type t)
    {
        var parts = new List<string>();
        for (var x = t; x != null && x != typeof(object); x = x.BaseType) parts.Add(x.Name);
        parts.AddRange(t.GetInterfaces().Select(i => "i:" + i.Name));
        return string.Join(" <- ", parts);
    }
    private static string _resolvedPanelId;   // panel target the feed is currently sized for
    private static long _lastResolveAttempt, _lastResolveFailLog;
    private static int _resolveFailLogs;
    private static int _toneLogs;

    // Force a resource into COPY_SOURCE or COPY_DEST. ExplicitStateTransition takes an
    // AutoResourceState, which hangs off a *view* rather than the texture, so the view
    // has to be found first.
    private static MethodInfo _miTransition;
    private static object _copySrcState, _copyDstState;
    private static int _transState;   // 0 untried, 1 ok, -1 unavailable
    private static int _transLogs;

    private static void TransitionForCopy(object commandList, object texture, bool copySource)
    {
        if (_transState == -1 || texture == null) return;
        try
        {
            if (_transState == 0)
            {
                _miTransition = commandList.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "ExplicitStateTransition" && m.GetParameters().Length == 3);
                var st = _miTransition?.GetParameters()[1].ParameterType;
                if (st != null && st.IsEnum)
                {
                    if (Enum.GetNames(st).Contains("CopySource")) _copySrcState = Enum.Parse(st, "CopySource");
                    if (Enum.GetNames(st).Contains("CopyDest")) _copyDstState = Enum.Parse(st, "CopyDest");
                }
                _transState = (_miTransition != null && _copySrcState != null && _copyDstState != null) ? 1 : -1;
                RttLog.Line($"Feed: state transitions {(_transState == 1 ? "available" : "UNAVAILABLE")}.");
                if (_transState == -1) return;
            }

            var autoState = AutoStateOf(texture);
            if (autoState == null)
            {
                if (_transLogs++ < 2)
                    RttLog.Line($"Feed: no AutoResourceState on {texture.GetType().Name} — copy left untransitioned.");
                return;
            }
            _miTransition.Invoke(commandList, new[] { autoState, copySource ? _copySrcState : _copyDstState, false });
        }
        catch (Exception e) { if (_transLogs++ < 3) RttLog.Error("copy state transition", e); }
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

    private static void CopyRaw(object commandList, object srcBorrow)
    {
        try
        {

            var src = Prop2(srcBorrow, "Resource") ?? srcBorrow;
            if (src == null) return;

            // Both ends must be in the right state. This copy runs in the camera pass,
            // not the UI stage, so nothing has prepared them: the destination is an
            // offscreen ROTexture that is otherwise bound for sampling, and the source
            // was just written as a render target. Copying without transitioning is the
            // fault that sent us down the DrawOne handover route in the first place.
            TransitionForCopy(commandList, src, copySource: true);
            TransitionForCopy(commandList, _feedTexture, copySource: false);

            // DirectCommandList derives from CopyCommandList, so CopyResource is
            // available on the list we already hold. Formats match by this point.
            var mi = commandList.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "CopyResource" && m.GetParameters().Length == 2);
            if (mi == null)
            {
                if (_copyLogs++ < 2) RttLog.Line("Feed: CopyResource(2 args) not found on the command list.");
                _feedState = -1;
                return;
            }
            mi.Invoke(commandList, new object[] { _feedTexture, src });

            // Real frames are landing now, so stop the 2D test pattern overwriting them.
            BlitProbe.FeedOwnsTarget = true;
            if (_copyLogs++ == 0) RttLog.Line("=== FEED COPIED into the LCD offscreen target. ===");
        }
        catch (Exception e)
        {
            if (_copyLogs++ < 3) RttLog.Error("feed copy", e);
            if (_copyLogs >= 3) _feedState = -1;
        }
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
    private static long _feedStartTicks;

    // Why the last null, for the skip log. Nulls here used to be silently papered
    // over by the main-view fallback, so nothing ever recorded the reason.
    private static string _orbitNull = "";
    private static int _viewSkips, _viewSkipLogs;

    // The main view, snapshotted from the per-frame pass. See the comment at the
    // call site in SceneDrawRecon: reading it from the probe hook picks up the
    // engine's probe cube-face view instead, which is the single-frame flash from
    // inside the ship.
    private static volatile object _baseViewSnapshot;
    private static int _baseViewMismatches;
    private static bool _mismatchLogged;

    public static void CaptureBaseView()
    {
        if (!_resolvedOk) return;
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

    // Give the pool back everything we hold across passes.
    //
    // These are borrowed once and cached for the session, which is correct for
    // performance and wrong for lifetime: nothing ever returned them. The engine
    // notices at shutdown —
    //
    //     Assertion Failure: '_cpuBorrowedObjects == 0'  GPUResourcePool.cs:52
    //     Assertion Failure: '_gpuBorrowedObjects == 0'  GPUResourcePool.cs:53
    //
    // and because asserts are DEFERRED-FATAL in a release build (DiagnosticReporter
    // throws FirstAssertionException from VRageCore.Dispose), a leak here turns every
    // quit into a crash report. Worse, it drowns the assert summary in known noise, so
    // a NEW assert from an experiment is invisible in the pile — which is exactly what
    // happened while chasing the black screen.
    private static void ReleaseHeldBorrows()
    {
        foreach (var b in new[] { _giDiffuse, _giSpecular, _ourDepthBorrow, _bloomHeld })
        {
            if (b == null) continue;
            try { ReturnBorrowed(b); } catch { }
        }
        _giDiffuse = _giSpecular = _ourDepthBorrow = _bloomHeld = null;
        _bloomRecomputes = 0;
        _ourDepthTex = null;
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

    private static object MakeVector2I(int x, int y)
    {
        var t = Type.GetType("Keen.VRage.Library.Mathematics.Vector2I, VRage.Library");
        return t == null ? null : Activator.CreateInstance(t, x, y);
    }

    private static void Call(object o, string name)
    {
        o?.GetType().GetMethod(name, Any, null, Type.EmptyTypes, null)?.Invoke(o, null);
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

    private static object StaticField(Type t, string name, StringBuilder sb, ref bool ok)
    {
        var v = t.GetField(name, Any)?.GetValue(null);
        sb.AppendLine($"  CoreSystems.{name,-24} {(v == null ? "NULL" : "OK")}");
        if (v == null) ok = false;
        return v;
    }

    private static object InstField(object o, string name, StringBuilder sb, ref bool ok)
    {
        var v = o?.GetType().GetField(name, Any)?.GetValue(o);
        sb.AppendLine($"  {name,-32} {(v == null ? "NULL" : v.GetType().Name)}");
        if (v == null) ok = false;
        return v;
    }

    private static object Prop(object o, string name, StringBuilder sb, ref bool ok)
    {
        var v = Prop2(o, name);
        sb.AppendLine($"  {name,-32} {(v == null ? "NULL" : v.GetType().Name)}");
        if (v == null) ok = false;
        return v;
    }

    private static MethodInfo Pick(Type t, string name, int argc, StringBuilder sb, ref bool ok)
    {
        var m = t.GetMethods(Any).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == argc)
             ?? t.GetMethods(Any).FirstOrDefault(x => x.Name == name);
        sb.AppendLine($"  {t.Name}.{name,-28} {(m == null ? "NOT FOUND" : $"OK ({m.GetParameters().Length} args)")}");
        if (m == null) ok = false;
        return m;
    }

    private static void DescribeType(StringBuilder sb, Type t, string wantInterface)
    {
        if (t == null) { sb.AppendLine($"  (type for {wantInterface} not found)"); return; }
        sb.AppendLine($"  {t.Name} -> members assignable to {wantInterface}:");
        var want = FindType(wantInterface);
        bool any = false;
        foreach (var p in t.GetProperties(Any))
            if (want != null && want.IsAssignableFrom(p.PropertyType)) { sb.AppendLine($"      prop {p.Name} : {p.PropertyType.Name}"); any = true; }
        foreach (var f in t.GetFields(Any))
            if (want != null && want.IsAssignableFrom(f.FieldType)) { sb.AppendLine($"      field {f.Name} : {f.FieldType.Name}"); any = true; }
        if (want != null && want.IsAssignableFrom(t)) { sb.AppendLine("      (the type itself implements it)"); any = true; }
        if (!any)
        {
            sb.AppendLine("      none found — all members follow:");
            foreach (var p in t.GetProperties(Any).Take(25)) sb.AppendLine($"        prop {p.PropertyType.Name} {p.Name}");
        }
    }

    private static void Write(StringBuilder sb)
    {
        try { File.WriteAllText(Path.Combine(RttLog.OutDir, "camera-dryrun.txt"), sb.ToString()); }
        catch (Exception e) { RttLog.Error("dry run write", e); }
    }
}
