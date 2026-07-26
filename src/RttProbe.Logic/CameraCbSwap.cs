using System.Reflection;

namespace RttProbe;

// The master lever: make the ENGINE'S passes render from our camera.
//
// There are two camera channels in Render12 and we have only ever fed the weaker one.
// Passes that take a `cameraCb` parameter — IndirectEnvironmentPassJob,
// IndirectPlanetEnvironmentJob — get ours because we hand it to them. Everything else
// reads one of two per-frame constant buffers off the global:
//
//     CoreSystems.CommonResources                  (public static)
//       -> CommonResourcesManager._settingsGroup   (private field)
//            -> SettingsGroup._jitteredCameraSettings     : Nullable<TransientConstantBuffer>
//            -> SettingsGroup._nonjitteredCameraSettings  : Nullable<TransientConstantBuffer>
//
// Roughly 92 methods read those, including every pass we want next: AmbientLightJob,
// DirectionalLightJob, AtmosphereMultiplyJob, ToneMappingJob, LocalFogJob,
// ScreenSpaceReflections, GBufferPassJob — and, already in our pass today,
// ClusteringJob.DoWork.
//
// That last one matters immediately. Our cluster grid is currently built from the
// PLAYER'S frustum while we rasterise from ours, so every clustered local light is
// binned into the wrong screen-space cluster. It is a live defect in the feed, not a
// future feature, and it is the cheapest visible proof that the swap works.
//
// Written by exactly one method — SettingsGroup.OnBeginDraw, once per bracket — so
// mutating SettingsManager._renderView mid-frame does NOT change what these ~92 methods
// bind. This field swap is the only way to steer them.
//
// Three rules, each learned from the IL rather than from a crash:
//
//   1. RESTORE INSIDE THE SAME BRACKET. CommonResources.OnEndDraw does
//      `Nullable.get_Value(); TransientConstantBuffer.Dispose(); initobj` on each field.
//      Leave ours in place and the engine disposes OUR buffer while the engine's own
//      leaks from the transient allocator.
//   2. NEVER WRITE NULL. The getter is `if (!HasValue) throw new NotImplementedException()`
//      — a hard throw, not a null return.
//   3. NEVER PUT THE SAME BUFFER IN BOTH FIELDS AND LEAVE IT. OnEndDraw would
//      double-dispose it. We restore both, so this only matters if a restore is skipped.
internal static class CameraCbSwap
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object _settingsGroup;
    private static FieldInfo _fJittered, _fNonjittered;
    private static int _state;          // 0 untried, 1 available, -1 unavailable
    private static bool _logged;
    private static int _errLogs;

    public static void Reset()
    {
        _settingsGroup = null;
        _fJittered = _fNonjittered = null;
        _state = 0;
        _logged = false;
        _errLogs = 0;
    }

    private static bool Resolve()
    {
        if (_state != 0) return _state == 1;
        _state = -1;
        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            var commonResources = core?.GetFields(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(f => f.FieldType.Name.Contains("CommonResourcesManager"))?.GetValue(null);
            if (commonResources == null)
            {
                RttLog.Line("Camera CB swap: CoreSystems.CommonResources not found.");
                return false;
            }

            // Private field on the manager; there is no public accessor for the group.
            _settingsGroup = commonResources.GetType().GetFields(Any)
                .FirstOrDefault(f => f.FieldType.Name == "SettingsGroup")?.GetValue(commonResources);
            if (_settingsGroup == null)
            {
                RttLog.Line("Camera CB swap: CommonResourcesManager._settingsGroup not found.");
                return false;
            }

            var t = _settingsGroup.GetType();
            _fJittered    = t.GetField("_jitteredCameraSettings", Any);
            _fNonjittered = t.GetField("_nonjitteredCameraSettings", Any);
            if (_fJittered == null || _fNonjittered == null)
            {
                RttLog.Line($"Camera CB swap: fields not found " +
                            $"(jittered={_fJittered != null} nonjittered={_fNonjittered != null}).");
                return false;
            }

            _state = 1;
            RttLog.Line("Camera CB swap: available — ~92 engine passes read these two fields, " +
                        "including ClusteringJob.DoWork, which is currently binning our lights " +
                        "using the PLAYER'S frustum.");
            return true;
        }
        catch (Exception e) { RttLog.Error("resolve camera CB swap", e); return false; }
    }

    // Point both camera constant buffers at ours. Returns the saved pair, to be handed
    // back to Restore in a finally. Null means nothing was swapped.
    public static object[] Install(object ourCameraCb)
    {
        if (!FeedConfig.SwapCameraCb || ourCameraCb == null) return null;
        if (!Resolve()) return null;
        try
        {
            // A Nullable<T> field reads back as a boxed T, or null when HasValue is false.
            // Null means we are outside a draw bracket — OnEndDraw has already initobj'd
            // the field — and nothing should be reading it. Swapping there would only
            // create work for the restore, so skip.
            var savedJ = _fJittered.GetValue(_settingsGroup);
            var savedN = _fNonjittered.GetValue(_settingsGroup);
            if (savedJ == null || savedN == null)
            {
                if (_errLogs++ < 3)
                    RttLog.Line("Camera CB swap: skipped — the engine's camera buffers are not " +
                                "live at this point in the frame (outside an OnBeginDraw bracket).");
                return null;
            }

            _fJittered.SetValue(_settingsGroup, ourCameraCb);
            _fNonjittered.SetValue(_settingsGroup, ourCameraCb);

            if (!_logged)
            {
                _logged = true;
                RttLog.Line("Camera CB swap: INSTALLED for the pass. Both jittered and nonjittered " +
                            "now point at our camera; restored in the finally.");
            }
            return new[] { savedJ, savedN };
        }
        catch (Exception e)
        {
            _state = -1;
            RttLog.Error("install camera CB swap", e);
            return null;
        }
    }

    // Unconditional, and the most important few lines in this file: leaving our buffer in
    // place means the engine's own remaining passes render the player's screen from our
    // 512x512 orbit camera, and then OnEndDraw disposes a buffer we own.
    public static void Restore(object[] saved)
    {
        if (saved == null || _state != 1) return;
        try
        {
            _fJittered.SetValue(_settingsGroup, saved[0]);
            _fNonjittered.SetValue(_settingsGroup, saved[1]);
        }
        catch (Exception e)
        {
            _state = -1;
            RttLog.Error("RESTORE CAMERA CB FAILED — the player's remaining passes are now " +
                         "using our camera for this frame", e);
        }
    }
}
