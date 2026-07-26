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
    private static long _lastArmCheck;
    private const long ArmPollMs = 2000;
    private static int _renders, _errors;
    private static bool _survivedLogged;

    // Resolved once by the dry run.
    private static object _drawContexts, _settings, _texPool, _bufMgr;
    private static object _cullJob, _clusterJob, _envPass;
    private static object _cullCtx, _clusterCtx, _geomBuffers, _shadowResources, _lodSettings;
    private static MethodInfo _miDoCullingFirstPass, _miClusterDoWork, _miEnvPassDoWork;
    private static MethodInfo _miBorrowRt, _miBorrowDepth, _miCreateCb;
    private static Type _tRenderViewSlim, _tTrackedCam, _tCamSettings;
    private static object _hdrFormat, _srvFormat, _depthFormat;

    public static void Reset()
    {
        _dryRunDone = _resolvedOk = _armed = _disarmed = _survivedLogged = false;
        _lastRender = _lastArmCheck = 0; _renders = _errors = 0;
        Array.Clear(_ldrRing, 0, _ldrRing.Length); _ldrReady = null; _ringIndex = -1;
        _toneLogs = _skipLogs = _transLogs = 0; _resolvedPanelId = null; _blitLogged = false;
        _baseViewSnapshot = null; _baseViewMismatches = 0; _mismatchLogged = false;
        _viewSkips = _viewSkipLogs = 0;
        _toneBlocked = _bloomBlocked = _skyBlocked = false; _bloomLogs = _skyLogs = 0;
        _ldrResizable = null; _lastResizableRes = null; _toneInputsLogged = false; _exposureSrcLogs = 0;
        _firstPassAt = 0; _startupLogged = _startupDoneLogged = false;

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
            if (!_resolvedOk || _disarmed) return;

            // High-resolution: this drives the frame gate, and TickCount64's ~15.6 ms
            // quantum was silently capping the feed at 20 fps when 30 was asked for.
            var now = Clock.Ms;

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
        _geomBuffers     = Prop(_drawContexts, "MainOutputGeometryBuffers", sb, ref ok);

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

        // ---- LOD settings: Settings.LOD.EnvironmentProbe ----
        sb.AppendLine("-- lod settings --");
        var lod = Prop(_settings, "LOD", sb, ref ok);
        if (lod != null)
        {
            var f = lod.GetType().GetField("EnvironmentProbe", Any);
            _lodSettings = f?.GetValue(lod);
            sb.AppendLine($"  LOD.EnvironmentProbe            {(_lodSettings == null ? "NULL" : _lodSettings.GetType().Name)}");
            if (_lodSettings == null) ok = false;
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

        _planetEnvJob = sds?.GetType().GetField("_indirectPlanetEnvironmentJob", Any)?.GetValue(sds);
        _miPlanetEnvDoWork = _planetEnvJob?.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 6);

        sb.AppendLine();
        sb.AppendLine($"-- Fidelity: ComputeExposure={(_miComputeExposure == null ? "NOT FOUND" : "ok")} " +
                      $"ApplyToneMapping={(_miApplyToneMapping == null ? "NOT FOUND" : "ok")} " +
                      $"ApplyBloom={(_miApplyBloom == null ? "NOT FOUND" : "ok")} " +
                      $"PlanetEnvJob={(_miPlanetEnvDoWork == null ? "NOT FOUND" : "ok")} --");

        FidelitySurvey(sds, sb);

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
                foreach (var n in new[] { "All", "RGBA", "RGB" })
                    if (Enum.GetNames(chParam).Contains(n)) { _channelAll = Enum.Parse(chParam, n); break; }
                _channelAll ??= Enum.ToObject(chParam, Enum.GetValues(chParam).Cast<object>()
                    .Select(v => System.Convert.ToInt64(v)).Max());
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
        bool geomBorrowed = false;
        try
        {
            File.WriteAllText(LivePath, $"camera pass entered {DateTime.Now:O}\n");

            // The scene pass's PSOs are compiled for the HDR format. Binding a
            // render target of any other format is a pipeline-state mismatch, which
            // D3D12 answers with device removal — so the format is NOT negotiable,
            // whatever the copy destination would prefer.
            var res = _feedRes ?? MakeVector2I(RenderW, RenderH);
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

            // camera constant buffer: RenderViewSlim -> CameraSettings -> TrackedCameraSettings
            var cam = Convert(_tCamSettings, view, _tRenderViewSlim);
            var tracked = Convert(_tTrackedCam, cam, _tCamSettings);
            if (tracked == null) { RttLog.Line("Camera pass: camera conversion failed."); _armed = false; return; }
            cameraCb = _miCreateCb.MakeGenericMethod(_tTrackedCam)
                .Invoke(_bufMgr, new object[] { "rttCameraSettings", tracked });

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

            Call(_geomBuffers, "Borrow"); geomBorrowed = true;

            // cull -> cluster -> draw, exactly as the probe pass sequences them
            // Prefer a pooled culling context over the engine's probe one — sharing
            // that is what capped the frame rate. Returned in the finally below.
            borrowedCulling = FeedConfig.UsePooledCulling ? OwnContexts.Borrow() : null;
            var cullCtx = borrowedCulling ?? _cullCtx;

            _miDoCullingFirstPass.Invoke(_cullJob, new object[]
            {
                commandList, view, _lodSettings, cullCtx, _geomBuffers,
                null, null, null, null, -1, 0, -1,
            });

            // Far plane drives how much space the clustering job has to bin lights
            // into. 5 km was copied from the probe pass, which is sizing for a whole
            // environment probe; a camera orbiting 100 m from a ship needs a fraction
            // of that, and the cost scales with it. Free frame rate at no visual cost
            // until the far plane clips something you wanted to see.
            _miClusterDoWork.Invoke(_clusterJob, new object[]
            {
                commandList, Prop2(cullCtx, "EntityProxies"), _geomBuffers, _clusterCtx, res,
                (float)FeedConfig.CullFarPlane,
            });

            _miEnvPassDoWork.Invoke(_envPass, new object[]
            {
                commandList, _geomBuffers, cameraCb, view,
                Prop2(cullCtx, "FirstPass"), _clusterCtx, _shadowResources,
                rtv, dsv, true,
            });

            // Sky, on top of the geometry pass and using its depth so it fills only
            // what geometry did not cover.
            //
            //   IndirectPlanetEnvironmentJob.DoWork(cl, cameraSettingsBuffer,
            //       closeTarget, farTarget, depthTexture, view)
            //
            // Both target parameters get our one render target: the probe pipeline
            // splits near and far sky across two targets, and we have a single view
            // with no such split. Depth comes from the pass we just ran.
            if (FeedConfig.Sky && !_skyBlocked && _miPlanetEnvDoWork != null)
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
                        _miPlanetEnvDoWork.Invoke(_planetEnvJob, new object[]
                            { commandList, cameraCb, rtv, rtv, depthSrv, view });
                        if (_skyLogs++ == 0) RttLog.Line("=== SKY: IndirectPlanetEnvironmentJob applied. ===");
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
            try { if (geomBorrowed) Call(_geomBuffers, "Return"); } catch { }
            try { OwnContexts.Return(borrowedCulling); } catch { }
            try { if (depthBorrow != null) ReturnBorrowed(depthBorrow); } catch { }
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

            if (_feedState == 0) ResolveFeedTexture();
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
                                    if (exposure != null && !(exposure is null) &&
                                        !_miApplyToneMapping.GetParameters()[3].ParameterType.IsInstanceOfType(exposure))
                                    {
                                        if (_exposureSrcLogs++ == 0)
                                            RttLog.Line($"Exposure: EnvironmentProbeExposureJob.Exposure is " +
                                                        $"{exposure.GetType().Name}, not a texture view — it is the probe's " +
                                                        "scalar exposure. Needs wrapping in a 1x1 texture (or " +
                                                        "EyeAdaptationJob.ConstantExposure) before it can drive ApplyToneMapping. " +
                                                        "Using ComputeExposure for now.");
                                        exposure = null;
                                    }
                                    else if (exposure != null && _exposureSrcLogs++ == 0)
                                        RttLog.Line("Exposure: using EnvironmentProbeExposureJob.Exposure — no eye-adaptation " +
                                                    "pass, no interference with the main view's exposure.");
                                }

                                if (exposure == null)
                                {
                                    // ComputeExposure(cl, lBuffer, out exposure, out debugHistogram)
                                    var expArgs = new object[] { commandList, hdrTex, null, null };
                                    _miComputeExposure.Invoke(_sceneDrawSystem, expArgs);
                                    exposure = expArgs[2];
                                    if (_exposureSrcLogs++ == 0)
                                        RttLog.Line("Exposure: falling back to ComputeExposure (shared eye adaptation) — " +
                                                    "expect the feed to be exposed for the player's view, not ours.");
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

                                if (bloom == null && FeedConfig.Bloom && !_bloomBlocked && _miApplyBloom != null)
                                {
                                    try
                                    {
                                        // ApplyBloom(cl, toneMappingInput, exposure, out bloom)
                                        //
                                        // That out parameter is a Borrowed<T>, so it is a
                                        // pool loan and not a gift. Dropping it would leak
                                        // one texture per frame — 30 a second — which is
                                        // precisely the "grinds to a halt" shape we already
                                        // spent a night chasing. Returned below, after the
                                        // tonemap has consumed it.
                                        var bArgs = new object[] { commandList, hdrTex, exposure, null };
                                        _miApplyBloom.Invoke(_sceneDrawSystem, bArgs);
                                        bloomBorrow = bArgs[3];
                                        bloom = Prop2(bloomBorrow, "Resource") ?? bloomBorrow;
                                        if (_bloomLogs++ == 0)
                                            RttLog.Line($"=== BLOOM: applied (bloom={(bloom == null ? "null" : bloom.GetType().Name)}). ===");
                                    }
                                    catch (Exception e)
                                    {
                                        _bloomBlocked = true;
                                        bloom = null;
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
                    _miCopyDoWork.Invoke(_copyJob, new object[]
                        { commandList, dstRtv, blitSrc, null, null, _channelAll, null, null });

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
    private static object _channelAll;
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
    private static object _planetEnvJob, _probeExposureJob;
    private static int _exposureSrcLogs;
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

    private static string Chain(Type t)
    {
        var parts = new List<string>();
        for (var x = t; x != null && x != typeof(object); x = x.BaseType) parts.Add(x.Name);
        parts.AddRange(t.GetInterfaces().Select(i => "i:" + i.Name));
        return string.Join(" <- ", parts);
    }
    private static string _resolvedPanelId;   // panel target the feed is currently sized for
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
                    if (_feedState != 1)
                        sb.AppendLine($"  NO MATCH for id {wantId} — feed disabled rather than writing to an unknown target.");
                }
                else sb.AppendLine("  _registeredTextures is not a dictionary");
            }
            sb.AppendLine(_feedState == 1 ? "  FEED TEXTURE RESOLVED" : "  feed texture unavailable");
            RttLog.Line(sb.ToString().TrimEnd());
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
