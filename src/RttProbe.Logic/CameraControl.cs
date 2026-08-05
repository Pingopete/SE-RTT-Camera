using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.VRage.Library.Mathematics;

namespace RttProbe;

// MANUAL CAMERA CONTROL — fly the feed camera by hand from the command seat.
//
// WHY THIS FILE EXISTS SEPARATELY from CameraFeed: the orbit is a pure function of time and
// an anchor, with no state to keep. Manual control is the opposite — a position, an
// orientation, a speed and a zoom that all persist across sessions. Mixing a stateful mode
// into a stateless one is how the orbit's "just call it with the clock" contract would rot.
//
// ---- STAGE 1 (this file, now): THE KEY-ENCODING PROBE -----------------------------------
//
// The engine has two input layers and they do NOT obviously share a numbering:
//
//   UI/action layer:  InputActionDefinition -> Avalonia.Input.Key   (verified in IL:
//                     23=Left, 24=Up, 25=Right, 26=Down — Avalonia's own enum)
//   DEVICE layer:     IInputDevice.GetDigitalState(InputId), InputId = (Index, Type, class)
//
// Nothing states that InputId.Index IS the Avalonia key code. A hard-coded WASD table built
// on that assumption would bind the wrong keys and look like "input is not working" — the
// exact failure shape that cost this project hours elsewhere. So stage 1 publishes what is
// ACTUALLY held, and names each index using Avalonia's enum when it matches. One press of W
// settles it:
//
//   held index 66 -> "W"     => Index IS the Avalonia key code; the table is safe to write
//   held index 66 -> no name => it is a scan code or device-local id; map it from observation
//
// Once the encoding is known, stage 2 binds WASD/Space/Ctrl/QE, mouse pan, wheel zoom and
// Ctrl+wheel speed, and persists position/orientation/speed to OUR OWN sidecar file keyed by
// world — never into the world save. The user asked explicitly (2026-08-03) that nothing of
// ours enters saves, and camera state in a save would contradict that guarantee immediately.
internal static class CameraControl
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static;

    private static Type _bridge;
    private static FieldInfo _fHeld, _fHeldCount, _fSeatAlive, _fProbeHook;
    private static Type _avKey;
    private static bool _looked, _armed;

    // ---- READING INPUT FROM THE LOGIC SIDE (2026-08-03) -------------------------------
    //
    // WHY NOT THE BOOTSTRAP PREFIX. It works, but a Harmony patch installs once at game
    // start, so every iteration on the input code costs a full restart. The IL for
    // GameInputProcessorComponent_GeneratedContainer shows the engine reaches the component
    // through InstanceBind<GameInputProcessorComponent>.Instance — a static accessor we can
    // use too. That puts the whole input path in the HOT-RELOADABLE assembly: binding,
    // key mapping and (later) the control scheme can all be iterated without a world load.
    //
    // The bootstrap seat stays as the per-frame pump for when sub-frame timing matters; this
    // path polls from the camera pass (~30 Hz), which is ample for flying a camera.
    private static PropertyInfo _piInstance, _piKeyboard, _piMouse;
    private static MethodInfo _miFillActive, _miSetClear;
    private static object _activeSet;
    private static FieldInfo _fiIndex;
    private static bool _bindTried, _bindLogged;

    // Returns the keyboard IInputDevice, or null. Everything is resolved by interface so an
    // EXPLICIT interface implementation is handled: GetProperty("Keyboard") on the concrete
    // type returns null in that case, which is what defeated the first two attempts.
    private static object Keyboard()
    {
        if (!_bindTried)
        {
            _bindTried = true;
            try
            {
                var comp = Type.GetType("Keen.VRage.Input.GameInputProcessorComponent, VRage.Input");
                var bindOpen = Type.GetType("Keen.VRage.DCS.CoreData.InstanceBind`1, VRage.DCS");
                var iface = Type.GetType("Keen.VRage.Input.IInputManager, VRage.Input");
                if (comp == null || bindOpen == null || iface == null)
                {
                    RttLog.Line($"MANUAL CAMERA: cannot bind input — GameInputProcessorComponent={(comp == null ? "X" : "ok")} " +
                                $"InstanceBind`1={(bindOpen == null ? "X" : "ok")} IInputManager={(iface == null ? "X" : "ok")}. " +
                                "This is a SHAPE MISS, not 'no keys held'.");
                    return null;
                }
                _piInstance = bindOpen.MakeGenericType(comp).GetProperty("Instance", Any);
                _piKeyboard = iface.GetProperty("Keyboard");
                _piMouse = iface.GetProperty("Mouse");
                if (_piInstance == null || _piKeyboard == null)
                    RttLog.Line("MANUAL CAMERA: InstanceBind<GameInputProcessorComponent>.Instance or IInputManager.Keyboard " +
                                "not resolvable — SHAPE MISS.");
            }
            catch (Exception e) { RttLog.Error("manual camera bind", e); }
        }
        if (_piInstance == null || _piKeyboard == null) return null;

        try
        {
            var comp = _piInstance.GetValue(null);
            if (comp == null) return null;

            // The manager is a PRIVATE field on a BASE type (ActionInputProcessorBaseComponent
            // ._inputManager). GetFields(NonPublic|Instance) does NOT return private fields of
            // base classes, so the hierarchy has to be walked explicitly — that omission is
            // what made the first attempt report "no Keyboard device" for a field that was
            // there all along.
            object mgr = null;
            var ifaceT = _piKeyboard.DeclaringType;
            for (var t = comp.GetType(); t != null && mgr == null; t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    var v = f.GetValue(comp);
                    if (v != null && ifaceT.IsInstanceOfType(v)) { mgr = v; break; }
                }
            if (mgr == null) return null;

            if (!_bindLogged)
            {
                _bindLogged = true;
                RttLog.Line($"MANUAL CAMERA: input bound from the LOGIC side ({mgr.GetType().Name}) — " +
                            "no bootstrap patch needed, so the control scheme can be iterated on hot reloads.");
            }
            return _piKeyboard.GetValue(mgr);
        }
        catch { return null; }
    }

    // Fills _held with the indices currently down. Returns the count.
    private static readonly int[] _held = new int[16];
    private static int ReadHeld()
    {
        var kb = Keyboard();
        if (kb == null) return -1;
        try
        {
            if (_miFillActive == null)
            {
                _miFillActive = kb.GetType().GetMethods(Any)
                    .FirstOrDefault(m => m.Name == "FillActive" && m.GetParameters().Length == 1);
                if (_miFillActive == null) return -1;
                var setType = _miFillActive.GetParameters()[0].ParameterType;
                _activeSet = Activator.CreateInstance(setType);
                _miSetClear = setType.GetMethod("Clear");
            }
            _miSetClear.Invoke(_activeSet, null);
            _miFillActive.Invoke(kb, new[] { _activeSet });

            int n = 0;
            foreach (var id in (System.Collections.IEnumerable)_activeSet)
            {
                if (n >= _held.Length) break;
                _fiIndex ??= id.GetType().GetField("Index");
                if (_fiIndex?.GetValue(id) is int idx) _held[n++] = idx;
            }
            return n;
        }
        catch { return -1; }
    }

    private static void Look()
    {
        if (_looked) return;
        _looked = true;
        _bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
        _fHeld = _bridge?.GetField("InputHeld");
        _fHeldCount = _bridge?.GetField("InputHeldCount");
        _fSeatAlive = _bridge?.GetField("InputSeatAlive");
        _fProbeHook = _bridge?.GetField("InputProbeHook");

        // Avalonia ships with the game (its UI is Avalonia-based). Resolved by name so no
        // numeric key table is baked in anywhere; absent simply means indices print unnamed.
        _avKey = Type.GetType("Avalonia.Input.Key, Avalonia.Base")
              ?? Type.GetType("Avalonia.Input.Key, Avalonia.InputSystem")
              ?? Type.GetType("Avalonia.Input.Key, Avalonia");
    }

    // Called every camera pass. Cheap: two field reads until something is actually held.
    internal static void Poll()
    {
        Look();
        if (_fHeld == null) return;

        if (!_armed)
        {
            _armed = true;
            RttLog.Line("MANUAL CAMERA: key-encoding probe armed. Sit in the command seat and press W. " +
                        "The next line names what the DEVICE layer reports, which is the one thing that " +
                        "decides whether a WASD table can be written safely — see CameraControl.");
        }

        // TWO SOURCES, BRIDGE FIRST.
        //
        // The bootstrap seat binds successfully ("INPUT SEAT: bound the input manager
        // (InputEngineComponent)") and publishes held indices every frame, so it is the
        // primary. The logic-side reader stays as a fallback for a bootstrap that predates
        // the seat — but it is NOT the preferred path: InstanceBind<T>.Instance came back
        // null here, and switching to it silently disabled a working capture. Preferring the
        // source that is PROVEN live, and saying so when neither works, is the whole lesson.
        //
        // Note this costs nothing in iteration speed: the bootstrap only CAPTURES. Every
        // decision — key mapping, movement, zoom, persistence — lives in this file and still
        // hot reloads.
        int count;
        int[] held;
        var bridgeCount = _fHeldCount?.GetValue(null) as int?;
        if (bridgeCount.HasValue && _fHeld.GetValue(null) is int[] bridgeHeld)
        {
            count = bridgeCount.Value;
            held = bridgeHeld;
        }
        else
        {
            count = ReadHeld();
            held = _held;
            if (count < 0)
            {
                if (!_noSourceSaid)
                {
                    _noSourceSaid = true;
                    RttLog.Line("MANUAL CAMERA: NO INPUT SOURCE — neither the bootstrap seat nor the logic-side " +
                                "reader could supply key state. This is a BROKEN INSTRUMENT, not 'no keys pressed'.");
                }
                return;
            }
        }
        PollMouse();
        if (count == 0) { _sawNone = true; return; }

        // Report only on CHANGE, and only after a gap of nothing held, so holding a key does
        // not spam and a genuine second press is still reported.
        var sig = 0;
        for (int i = 0; i < count && i < held.Length; i++) sig = sig * 397 ^ held[i];
        if (sig == _lastSig && !_sawNone) return;
        _lastSig = sig;
        _sawNone = false;

        var parts = new List<string>();
        for (int i = 0; i < count && i < held.Length; i++)
        {
            var idx = held[i];
            var name = AvaloniaName(idx);
            parts.Add(name == null ? $"{idx}=?" : $"{idx}={name}");
        }

        // SETTLED 2026-08-04 by three presses: W=87, A=65, Space=32 — Windows VIRTUAL-KEY
        // codes (VK_W=0x57, VK_A=0x41, VK_SPACE=0x20), NOT Avalonia key codes.
        //
        // AND THE FIRST VERDICT WAS A FALSE POSITIVE WORTH REMEMBERING: it asked
        // Enum.GetName(Avalonia.Input.Key, index) and called any non-null answer a match. In a
        // dense enum nearly every small integer has SOME name, so 87 came back "Subtract",
        // 65 "V", 32 "Delete" — three wrong names read as three confirmations. Had the table
        // been written from that, every key would have bound wrong and presented as "input
        // does not work". A mapping check must compare against the key ACTUALLY PRESSED, not
        // merely confirm that a name exists.
        RttLog.Line($"MANUAL CAMERA: held index(es) {string.Join(", ", parts)}  [VK: {VkNames(count, held)}]");
    }

    private static int _lastSig;
    private static bool _sawNone = true;
    private static bool _noSourceSaid;

    // ---- MOUSE DISCOVERY ---------------------------------------------------------------
    //
    // Reports which mouse InputIds change and what analog value each carries, so ONE waggle
    // and ONE scroll identify the pan axes and the wheel. Deliberately NOT guessed: the
    // keyboard turned out to be virtual-key codes and an assumed table would have bound every
    // key wrong, so the same evidence-first step applies here.
    private static FieldInfo _fMChanged, _fMCount, _fMAnalog;
    private static int _mSig;

    // ---- THE WHEEL: id 7, signed analog (settled in game 2026-08-04) -------------------
    //
    //   scroll up   -> id 7, +2.000
    //   scroll down -> id 7, -2.000
    //
    // Mouse MOVEMENT reports through ids 1 (X) and 2 (Y) but reads 0.000 from GetAnalogState,
    // which is the proof they are POINTER inputs needing GetPointerState(id, PointerStateKind)
    // — that is the remaining bootstrap piece. The wheel needs nothing further: the seat
    // already publishes its analog value.
    private const int MouseWheelId = 7;
    private const int MouseXId = 1, MouseYId = 2;

    // Zoom is a MULTIPLIER on the engine's own FovH, not an absolute angle: the base FOV
    // comes from the render view and may itself change, so a stored absolute would silently
    // fight it. 1 = unzoomed; smaller = tighter.
    private static double _zoom = 1.0;
    internal static double ApplyZoom(double fovH)
        => FeedConfig.CameraManualControl ? fovH * _zoom : fovH;

    // Move the baseline forward WITHOUT acting on what arrived. Used while the controls are
    // blocked, so notches turned during freelook are dropped rather than banked up and applied
    // as one jump on release.
    private static bool _keyBlocked;

    private static void DiscardWheel()
    {
        _fWheelAccum ??= _bridge?.GetField("MouseWheelAccum");
        if (_fWheelAccum == null) return;
        try { _lastAccum = Convert.ToSingle(_fWheelAccum.GetValue(null)); _wheelPrimed = true; }
        catch { }
    }

    // Any configured freelook modifier down = hands off. Empty list disables the check.
    private static bool BlockedByHeldKey(int count, int[] held)
    {
        var keys = FeedConfig.CameraBlockKeys;
        if (keys == null || keys.Length == 0) return false;
        for (int i = 0; i < keys.Length; i++)
            if (Held(count, held, keys[i])) return true;
        return false;
    }

    private static void ConsumeWheel(int count, int[] held)
    {
        _fWheelAccum ??= _bridge?.GetField("MouseWheelAccum");
        if (_fWheelAccum == null) return;

        // SUBTRACT FROM A RUNNING TOTAL. The seat accumulates every notch it sees; we act on
        // whatever arrived since the last pass. That cannot lose an event and cannot apply
        // one twice — unlike the hash-dedupe it replaces, which swallowed consecutive
        // identical notches and made ordinary scrolling feel dead.
        float accum;
        try { accum = Convert.ToSingle(_fWheelAccum.GetValue(null)); } catch { return; }
        if (!_wheelPrimed) { _wheelPrimed = true; _lastAccum = accum; return; }

        var delta = accum - _lastAccum;
        _lastAccum = accum;
        if (Math.Abs(delta) < 0.001f) return;

        // PROPORTIONAL, not per-event: a notch is worth 2.0, so scale by delta/2 and small
        // scrolls produce small changes instead of nothing at all.
        var steps = delta / 2.0;

        if (Held(count, held, VkCtrl) || Held(count, held, VkLCtrl))
        {
            // CTRL+WHEEL = SPEED, geometric so it is usable across three orders of magnitude:
            // crawling around a base and crossing a landscape want very different numbers, and
            // a linear step would be glacial at one end and unusable at the other.
            Speed = Math.Max(0.5, Math.Min(5000.0, Speed * Math.Pow(1.25, steps)));
            RttLog.Line($"MANUAL CAMERA: speed {Speed:F1} m/s.");
        }
        else
        {
            _zoom = Math.Max(0.05, Math.Min(3.0, _zoom * Math.Pow(0.9, steps)));
            RttLog.Line($"MANUAL CAMERA: zoom x{1.0 / _zoom:F2} (FOV multiplier {_zoom:F3}).");
        }
        SaveState();
    }

    private static FieldInfo _fWheelAccum;
    private static float _lastAccum;
    private static bool _wheelPrimed;
    private static FieldInfo _fMdx, _fMdy;

    // YAW AND PITCH ABOUT THE CAMERA'S OWN AXES, never about a world axis. This camera flies
    // around a planet where "up" is radial and changes as you travel; yawing about a fixed
    // world axis would tilt the horizon and gimbal-lock overhead. Rotating the basis in place
    // has neither failure, and keeps roll (Q/E) independent rather than fighting it.
    private static void MouseLook(double dt)
    {
        _fMdx ??= _bridge?.GetField("MouseDX");
        _fMdy ??= _bridge?.GetField("MouseDY");
        if (_fMdx == null) return;
        try
        {
            var dx = Convert.ToDouble(_fMdx.GetValue(null));
            var dy = Convert.ToDouble(_fMdy.GetValue(null));
            if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return;

            // Sensitivity scales with ZOOM: at 4x the same hand movement should sweep a
            // quarter of the arc, or aiming while zoomed is unusable.
            // SIGNS ARE LIVE KNOBS, not constants. Yaw was reported inverted in game, and
            // pitch could not be judged at all because it was dead — so rather than guess a
            // sign, ship both as config. A sign question must never cost a restart, and on
            // this machine a restart is the only safe way to deploy.
            var s = FeedConfig.CameraLookSensitivity * 0.0015 * _zoom;
            Yaw(dx * s * (FeedConfig.CameraInvertLookX ? -1 : 1));
            Pitch(dy * s * (FeedConfig.CameraInvertLookY ? -1 : 1));
        }
        catch { }
    }

    private static void Yaw(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        var f = _fwd * c + _right * s;
        var r = _right * c - _fwd * s;
        _fwd = Normalize(f); _right = Normalize(r);
    }

    private static void Pitch(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        var f = _fwd * c + _up * s;
        var u = _up * c - _fwd * s;
        _fwd = Normalize(f); _up = Normalize(u);
    }

    private static void PollMouse()
    {
        _fMCount ??= _bridge?.GetField("MouseChangedCount");
        _fMChanged ??= _bridge?.GetField("MouseChanged");
        _fMAnalog ??= _bridge?.GetField("MouseAnalog");
        if (_fMCount == null) return;
        try
        {
            var n = (int)(_fMCount.GetValue(null) ?? 0);
            if (n <= 0) return;
            if (_fMChanged.GetValue(null) is not int[] ids || _fMAnalog.GetValue(null) is not float[] vals) return;

            // Report on change of the ID SET, not every frame: a moving mouse changes its
            // analog value constantly and would flood the log.
            var sig = 0;
            for (int i = 0; i < n && i < ids.Length; i++) sig = sig * 397 ^ ids[i];
            if (sig == _mSig) return;
            _mSig = sig;

            var parts = new List<string>();
            for (int i = 0; i < n && i < ids.Length; i++) parts.Add($"{ids[i]}:{vals[i]:F3}");
            RttLog.Line($"MANUAL CAMERA MOUSE: changed id:value = {string.Join(", ", parts)}. " +
                        "Waggle the mouse for the pan axes and scroll for the wheel — these ids become the " +
                        "bindings for look and FOV zoom.");
        }
        catch { }
    }

    // ---- THE KEY TABLE: Windows virtual-key codes, confirmed by observation --------------
    internal const int VkW = 87, VkA = 65, VkS = 83, VkD = 68, VkQ = 81, VkE = 69;
    internal const int VkSpace = 32;
    // DOWN is C (VK_C 0x43), not Ctrl: Ctrl is the modifier for wheel speed adjustment, and a
    // key cannot be a movement axis and a modifier at once without descending on every speed
    // change. Ctrl is still ACCEPTED as a down key below so the old habit keeps working.
    internal const int VkC = 67;
    // Both forms accepted: the device may report the generic control or the sided one, and
    // which of those it is has NOT been observed yet — accepting both costs nothing and
    // avoids a second round trip to find out.
    internal const int VkCtrl = 17, VkLCtrl = 162;

    private static string VkNames(int count, int[] held)
    {
        var names = new List<string>();
        for (int i = 0; i < count && i < held.Length; i++)
        {
            var k = held[i];
            names.Add(k switch
            {
                VkW => "W", VkA => "A", VkS => "S", VkD => "D",
                VkQ => "Q", VkE => "E", VkC => "C", VkSpace => "Space",
                VkCtrl => "Ctrl", VkLCtrl => "LeftCtrl",
                _ => k >= 32 && k <= 126 ? ((char)k).ToString() : k.ToString()
            });
        }
        return string.Join("+", names);
    }

    internal static bool Held(int count, int[] held, int vk)
    {
        for (int i = 0; i < count && i < held.Length; i++) if (held[i] == vk) return true;
        return false;
    }

    // ================= MANUAL FLIGHT =====================================================
    //
    // State is a POSITION plus a BASIS (forward/right/up), not Euler angles. Angles would
    // need a fixed reference up, and this camera flies around a planet where "up" is radial
    // and changes as you travel — pitch/yaw against a fixed axis gimbal-locks overhead and
    // rolls the horizon on the way. Rotating the basis vectors in place has neither problem
    // and makes roll (Q/E) a first-class axis instead of a special case.
    private static bool _flying;
    private static Vector3D _pos, _fwd, _right, _up;
    private static long _lastStepMs;

    // Live so it can be tuned without a reload; persisted so it survives a world reload.
    internal static double Speed = 20.0;

    internal static bool Flying => _flying;
    internal static Vector3D Position => _pos;

    // Called from the render path with the orbit's matrix. Returns it unchanged unless manual
    // flight is armed, in which case it returns OUR matrix — seeded from the orbit on the
    // first frame so the takeover is seamless.
    internal static MatrixD Steer(MatrixD orbit)
    {
        if (!FeedConfig.CameraManualControl) { _flying = false; return orbit; }

        if (!_flying)
        {
            _flying = true;
            if (!LoadState())
            {
                // Seed from the orbit: row 0/1/2 are right/up/backward in this engine's
                // convention (see CameraFeed.OrbitCameraWorld — row 2 points AWAY from the
                // subject), so forward is the NEGATED third row.
                _pos = orbit.Translation;
                _right = new Vector3D(orbit.M11, orbit.M12, orbit.M13);
                _up = new Vector3D(orbit.M21, orbit.M22, orbit.M23);
                _fwd = new Vector3D(-orbit.M31, -orbit.M32, -orbit.M33);
            }
            RttLog.Line($"MANUAL CAMERA: flight ARMED at {_pos.X:F0},{_pos.Y:F0},{_pos.Z:F0}, speed {Speed:F1} m/s. " +
                        "WASD moves, Space/Ctrl rise and fall, Q/E roll. The orbit is suspended while this is on.");
        }

        Step();
        return BuildMatrix();
    }

    // ---- IS THE PLAYER ALLOWED TO FLY THE FEED RIGHT NOW? ------------------------------
    //
    // Two conditions, both requested directly: the player must be SEATED, and must not be in
    // FREELOOK. Freelook matters because it is how you look around from inside a seat — the
    // mouse is then aiming the player's head, and it must not simultaneously swing a camera
    // 279 km away.
    //
    // Both are answered from the engine's own ACTIVE INPUT LAYERS, published per frame by the
    // bootstrap. Layer names live in config (see FeedConfig.CameraRequireLayers) because the
    // seat here is on a STATIC grid the game does not treat as a vehicle, so which layer means
    // "seated" is measured, not assumed.
    //
    // PERMISSIVE WHEN BLIND, AND LOUD ABOUT IT. If the bootstrap could not read the layer set
    // at all, this returns true — the pre-gating behaviour. Failing CLOSED would be worse in
    // the exact way that matters: the camera would stop responding with no visible cause, and
    // "controls are dead" is a far harder thing to diagnose than "controls are not gated yet".
    // The bootstrap logs the reader failure on its side; this logs the fallback on ours.
    private static bool _gateBlindLogged, _gateStateLogged;
    private static bool _lastAllowed = true;
    private static FieldInfo _fLayers, _fLayersReadable;
    private static bool _layerFieldsBound;

    private static bool ControlsAllowed()
    {
        try
        {
            if (!_layerFieldsBound)
            {
                _layerFieldsBound = true;
                _fLayers = _bridge?.GetField("InputLayers");
                _fLayersReadable = _bridge?.GetField("InputLayersReadable");
            }

            // An older bootstrap simply does not have these fields. That is not a gating
            // decision, it is a missing feature, and it must not silently disable the camera.
            if (_fLayers == null || _fLayersReadable == null)
            {
                if (!_gateBlindLogged)
                {
                    _gateBlindLogged = true;
                    RttLog.Line("MANUAL CAMERA: this bootstrap publishes no input layers — seat/freelook " +
                                "gating is INACTIVE and the controls behave as before. Restart the game to " +
                                "adopt the new bootstrap. NOT a gating result.");
                }
                return true;
            }

            if (_fLayersReadable.GetValue(null) is not true)
            {
                if (!_gateBlindLogged)
                {
                    _gateBlindLogged = true;
                    RttLog.Line("MANUAL CAMERA: the engine's active input layers could not be read, so " +
                                "seat/freelook gating cannot be evaluated. Controls stay ACTIVE (the " +
                                "pre-gating behaviour) rather than failing closed. Reader problem, NOT " +
                                "'the player is not seated'.");
                }
                return true;
            }

            var layers = _fLayers.GetValue(null) as string ?? "";

            bool blocked = AnyLayer(layers, FeedConfig.CameraBlockLayers);
            bool required = FeedConfig.CameraRequireLayers.Length == 0
                            || AnyLayer(layers, FeedConfig.CameraRequireLayers);
            bool allowed = required && !blocked;

            if (allowed != _lastAllowed || !_gateStateLogged)
            {
                _gateStateLogged = true;
                _lastAllowed = allowed;
                RttLog.Line($"MANUAL CAMERA: controls {(allowed ? "ENABLED" : "disabled")} — active layers " +
                            $"[{(layers.Length == 0 ? "<none>" : layers)}], " +
                            $"require=[{string.Join(",", FeedConfig.CameraRequireLayers)}] " +
                            $"block=[{string.Join(",", FeedConfig.CameraBlockLayers)}]" +
                            (blocked ? " — BLOCKED (freelook or another blocking layer is active)"
                                     : required ? "" : " — no required layer active (not seated)"));
            }
            return allowed;
        }
        catch { return true; }   // never let a gating fault take the controls away
    }

    private static bool AnyLayer(string layers, string[] wanted)
    {
        if (string.IsNullOrEmpty(layers) || wanted == null) return false;
        foreach (var w in wanted)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            int i = layers.IndexOf(w, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                // Whole-segment match against the '|'-joined list, so "Camera FreeLook" never
                // matches merely because "Camera Controller" shares a prefix.
                bool leftOk = i == 0 || layers[i - 1] == '|';
                int end = i + w.Length;
                bool rightOk = end == layers.Length || layers[end] == '|';
                if (leftOk && rightOk) return true;
                i = layers.IndexOf(w, i + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static void Step()
    {
        var now = Clock.Ms;
        var dt = _lastStepMs == 0 ? 0.0 : Math.Min(0.25, (now - _lastStepMs) / 1000.0);
        _lastStepMs = now;
        if (dt <= 0.0) return;

        // SEAT AND FREELOOK GATING. Nothing below this line runs when the player is not in a
        // controlling seat, or is holding freelook to look around inside it — the whole point
        // being that walking around, or glancing over your shoulder, must not fly the feed.
        if (!ControlsAllowed()) return;

        int count;
        int[] held;
        var bridgeCount = _fHeldCount?.GetValue(null) as int?;
        if (!bridgeCount.HasValue || _fHeld?.GetValue(null) is not int[] hb) return;
        count = bridgeCount.Value; held = hb;

        // FREELOOK BLOCK, on the KEY rather than a layer — see FeedConfig.CameraBlockKeys for
        // why the layer route is a dead end. Checked HERE rather than in ControlsAllowed
        // because it needs the held-key array, which is read just above.
        //
        // Everything below is suppressed while the modifier is down: movement, roll, mouse
        // look and the wheel. That is the whole point — looking around from inside the seat
        // must not also fly a camera hundreds of kilometres away.
        if (BlockedByHeldKey(count, held))
        {
            if (!_keyBlocked)
            {
                _keyBlocked = true;
                RttLog.Line($"MANUAL CAMERA: controls disabled — freelook modifier held " +
                            $"(cameraBlockKeys=[{string.Join(",", FeedConfig.CameraBlockKeys)}]).");
            }
            // The wheel accumulator still has to be RE-BASELINED, or every notch turned while
            // blocked would land in one jump the moment the key is released. Discard, not
            // consume: ConsumeWheel would apply them, which is the opposite of blocking.
            DiscardWheel();
            return;
        }
        if (_keyBlocked)
        {
            _keyBlocked = false;
            RttLog.Line("MANUAL CAMERA: controls re-enabled — freelook modifier released.");
        }

        // MOUSE LOOK AND THE WHEEL RUN WHETHER OR NOT A KEY IS DOWN. The first version sat
        // below an `if (count <= 0) return;`, so looking around only worked while some key
        // happened to be held — reported in game as "aim only works when a keyboard input is
        // being held". Movement needs keys; looking does not.
        MouseLook(dt);
        ConsumeWheel(count, held);
        if (count <= 0) return;

        // Movement is CAMERA-RELATIVE: forward is where you are looking, so flying feels the
        // same whatever attitude the camera is in.
        var move = new Vector3D(0, 0, 0);
        if (Held(count, held, VkW)) move += _fwd;
        if (Held(count, held, VkS)) move -= _fwd;
        if (Held(count, held, VkD)) move += _right;
        if (Held(count, held, VkA)) move -= _right;
        if (Held(count, held, VkSpace)) move += _up;
        if (Held(count, held, VkC)) move -= _up;

        var len = move.Length();
        if (len > 0.0001) _pos += (move / len) * (Speed * dt);

        ConsumeWheel(count, held);

        // ROLL about the view axis. Rotating right and up around forward keeps the basis
        // orthonormal by construction, so no correction pass is needed.
        // Q rolls LEFT, E rolls RIGHT. The first version had these swapped (reported inverted
        // in game); the sign lives here rather than in the knob so that cameraRollRate stays a
        // plain positive RATE. A negative rate still works as a per-user invert.
        double roll = 0;
        if (Held(count, held, VkQ)) roll += 1;
        if (Held(count, held, VkE)) roll -= 1;
        if (roll != 0)
        {
            var a = roll * FeedConfig.CameraRollRate * Math.PI / 180.0 * dt;
            double c = Math.Cos(a), s = Math.Sin(a);
            var r2 = _right * c + _up * s;
            var u2 = _up * c - _right * s;
            _right = Normalize(r2); _up = Normalize(u2);
        }

        if (now - _lastSaveMs > 3000) { _lastSaveMs = now; SaveState(); }
    }

    private static MatrixD BuildMatrix()
    {
        // Rebuilt in the engine's convention: row 2 points BACKWARD along the view. Getting
        // this sign wrong aims the camera behind itself — the same trap the orbit documents.
        var m = MatrixD.Identity;
        m.M11 = _right.X; m.M12 = _right.Y; m.M13 = _right.Z;
        m.M21 = _up.X;    m.M22 = _up.Y;    m.M23 = _up.Z;
        m.M31 = -_fwd.X;  m.M32 = -_fwd.Y;  m.M33 = -_fwd.Z;
        m.Translation = _pos;
        CameraFeed.EyeCache = _pos;
        CameraFeed.LookDirCache = _fwd;
        return m;
    }

    private static Vector3D Normalize(Vector3D v)
    {
        var l = v.Length();
        return l > 1e-9 ? v / l : v;
    }

    // ---- PERSISTENCE: OUR OWN FILE, NEVER THE WORLD SAVE -------------------------------
    //
    // The user asked explicitly (2026-08-03) that nothing of ours enters world saves, and we
    // added a save-time marker despawn to guarantee it. Writing camera state INTO a save would
    // contradict that the same evening. A sidecar keyed by the feed's anchor keeps per-site
    // state without touching the save at all — and it survives save corruption, which a
    // save-embedded value would not.
    private static long _lastSaveMs;
    private static string StatePath()
    {
        var key = FeedConfig.OrbitAnchor;
        if (string.IsNullOrWhiteSpace(key)) key = "default";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
        return System.IO.Path.Combine(RttLog.OutDir, $"camera-state.{key}.txt");
    }

    private static void SaveState()
    {
        try
        {
            System.IO.File.WriteAllText(StatePath(),
                $"pos={_pos.X:R},{_pos.Y:R},{_pos.Z:R}\n" +
                $"fwd={_fwd.X:R},{_fwd.Y:R},{_fwd.Z:R}\n" +
                $"up={_up.X:R},{_up.Y:R},{_up.Z:R}\n" +
                $"right={_right.X:R},{_right.Y:R},{_right.Z:R}\n" +
                $"speed={Speed:R}\n" +
                $"zoom={_zoom:R}\n");
        }
        catch { }
    }

    private static bool LoadState()
    {
        try
        {
            var p = StatePath();
            if (!System.IO.File.Exists(p)) return false;
            Vector3D pos = default, fwd = default, up = default, right = default;
            double speed = Speed;
            foreach (var raw in System.IO.File.ReadAllLines(p))
            {
                var i = raw.IndexOf('=');
                if (i <= 0) continue;
                var k = raw.Substring(0, i).Trim();
                var v = raw.Substring(i + 1).Trim();
                if (k == "speed") { double.TryParse(v, out speed); continue; }
                // A zoom of 0 would collapse the frustum to nothing, so a corrupt value is
                // ignored rather than restored — the same reasoning as the basis check below.
                if (k == "zoom") { if (double.TryParse(v, out var zf) && zf > 0.01 && zf <= 3.0) _zoom = zf; continue; }
                var parts = v.Split(',');
                if (parts.Length != 3) continue;
                if (!double.TryParse(parts[0], out var x) || !double.TryParse(parts[1], out var y)
                 || !double.TryParse(parts[2], out var z)) continue;
                var vec = new Vector3D(x, y, z);
                switch (k) { case "pos": pos = vec; break; case "fwd": fwd = vec; break;
                             case "up": up = vec; break; case "right": right = vec; break; }
            }
            // A basis that did not survive the round trip is worse than none: a degenerate one
            // builds a singular view matrix. Fall back to the orbit seed rather than fly blind.
            if (pos.LengthSquared() < 1.0 || fwd.LengthSquared() < 0.5
             || up.LengthSquared() < 0.5 || right.LengthSquared() < 0.5) return false;
            _pos = pos; _fwd = Normalize(fwd); _up = Normalize(up); _right = Normalize(right);
            Speed = speed > 0 ? speed : Speed;
            RttLog.Line($"MANUAL CAMERA: restored from {System.IO.Path.GetFileName(p)} — " +
                        $"{_pos.X:F0},{_pos.Y:F0},{_pos.Z:F0}, speed {Speed:F1} m/s.");
            return true;
        }
        catch { return false; }
    }

    private static string AvaloniaName(int index)
    {
        if (_avKey == null) return null;
        try
        {
            var name = Enum.GetName(_avKey, index);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }
}
