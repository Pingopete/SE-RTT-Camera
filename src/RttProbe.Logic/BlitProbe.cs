using System.Reflection;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Render.Lcd;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Render.Contracts;

namespace RttProbe;

// The plumbing spike.
//
// Four stages, each logged before it is attempted so a hard crash names its own
// cause. The last one is the actual unknown: whether IDrawBatch.DrawImage accepts
// a render target's *generated* texture handle, or rejects it inside the render
// thread's command replay — where nothing can catch the throw and the game dies.
//
// Arming is driven from the panel itself so nothing risky happens by merely
// loading the mod:
//   [RTT]   stages 1-3 — create our own target and draw into it. Safe.
//   [RTT!]  also stage 4 — blit that target onto the panel. The risky one.
internal static class BlitProbe
{
    private const string TagSafe = "[RTT]";
    private const string TagArmed = "[RTT!]";

    // 1024 was deployed 2026-08-01 as a FAILED fix for the [RTS] mirror, and stays only
    // because reverting costs a hot reload the VRAM ratchet cannot currently afford. The
    // size is NOT load-bearing; treat it as 512-with-history.
    //
    // The theory it tested: CreateOffscreenTarget registers our texture in the same
    // registry the LCD system borrows panel content targets from, so at 512x512 a
    // size-matched borrow could hand the [RTS] panel OUR texture. DISPROVEN in game —
    // the mirror survived the change to 1024 unchanged.
    //
    // What the mirror evidence actually established (see task #31 for the trail):
    //   * teardown of our ONE bound panel heals BOTH panels; re-bind mirrors both
    //   * the [RTS] ctx's _screenMaterialHandle stays DIFFERENT from ours throughout
    //     (mirror forensics, 15:36) — so it is not wearing our material at ctx level
    //   * therefore the leak is at the level the MESH resolves its material from, and
    //     the one shared object in the bind is the session-wide
    //     LcdContentRendererSessionComponent we pass into SetNewScreenMaterialHandle.
    //
    // The panel samples in UVs, so this size is otherwise behaviour-neutral; the ring
    // and handover size themselves from the target automatically. ~4 MB VRAM per feed
    // over 512, and finer minification for the panel as a side effect.
    private const int RtSize = 1024;

    // Local reflection helpers. Deliberately not shared with PanelBinding's: this file reaches
    // into a different set of engine types, and a single "utility" reflection layer across both
    // would make either one's failure modes harder to attribute.
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        try { return t.GetProperty(name, Any)?.GetValue(o) ?? t.GetField(name, Any)?.GetValue(o); }
        catch { return null; }
    }

    private static readonly string MarkerPath = Path.Combine(RttLog.OutDir, "blit-armed.marker");

    private static RenderContracts _contracts;
    private static UISystem _ui;
    private static bool _resolveTried;

    // The stats panel needs UISystem.GetFont, and this is the only place the UISystem is
    // resolved. Exposed rather than re-resolved so there is exactly one owner of it.
    internal static UISystem Ui => _ui;

    // PER-FEED (phase C1a). The target and the batch that paints it are the most
    // obviously per-feed things in the mod — two feeds means two targets — and the
    // stats panel (A1) already rehearsed exactly this shape on a second surface.
    private static OffscreenRenderTarget? _rt
    { get => Feeds.Cur.Rt; set => Feeds.Cur.Rt = value; }
    private static bool _rtTried
    { get => Feeds.Cur.RtTried; set => Feeds.Cur.RtTried = value; }

    // The camera feed renders into this same target, so the render side needs a
    // handle on it. Boxed, because CameraRender works in reflection terms.
    public static object FeedTarget => _rt.HasValue ? (object)_rt.Value : null;

    // ---- GOAL 11: ONE SQUARE RENDER, ANY PANEL SHAPE, NO DISTORTION ---------------------
    //
    // THE RULE THIS IS BUILT AROUND (user, restated 2026-08-06): the feed must never be
    // RE-RENDERED for a different panel — only RESAMPLED onto it. So the scene render stays
    // exactly one square per feed per cycle, and everything below is a single textured quad.
    // `request = drawOne(ours) = copies` is the regression test: drawOne must never track
    // panel count.
    //
    // WHY A DERIVED TARGET RATHER THAN A MATERIAL SETTING. The panel's own fit controls are
    // the two things we are forbidden to build on:
    //     LcdPanelSurfaceState.PreserveAspectRatio   user-settable — contain vs stretch
    //     LcdPanelSurfaceState.Orientation           user-settable — rotates the panel
    // and LCDMaterialDefinition carries no UV transform at all (_orientation,
    // _screenAspectRatio, _fsrMaskAmount and nothing else). `ScreenAspectRatio` is read by
    // exactly two callers in the whole game — a serialization accessor and AssignToDefinition
    // — so no managed rendering code frames anything with it. Cover is not expressible there.
    //
    // THE TRICK THAT MAKES US IMMUNE INSTEAD OF MERELY COMPATIBLE: give the panel a texture
    // whose aspect ALREADY EQUALS its display aspect. Then both vanilla modes collapse to the
    // same picture — contain has nothing to letterbox, stretch has nothing to stretch. The
    // user can toggle PreserveAspectRatio all day and the feed does not move. We never read
    // that flag, never override it, and never pass a non-native aspect at bind time (which is
    // also what kept us out of SharedRuntimeMaterialKey collisions).
    //
    // KEYED BY ASPECT, NOT BY PANEL. Four identical panels on a block share one derived
    // target and one resample; only genuinely different SHAPES cost anything. That is the
    // property that keeps the fan-out cheap.
    //
    // COVER IS JUST AN OVERSIZED CENTRED DEST BOX. DrawImage takes its destination in target
    // pixel space, so scaling the square by max(tw/src, th/src) and centring it lets the long
    // axis fill and the short axis overrun off the edges, where it clips. That is the
    // "oversample and let the rest run off the display" the user asked for, done with one
    // quad and no second render.
    internal sealed class DerivedTarget
    {
        public OffscreenRenderTarget Rt;
        public int W, H;
        public float Aspect;
        public string Name;              // the CreateOffscreenTarget name — our registry key
        public bool DescLogged;          // resource-description check printed once per shape
    }

    // PER FEED, and that is a correctness requirement rather than tidiness. _rt is per feed
    // (Feeds.Cur.Rt); a single static dictionary keyed on aspect alone would hand feed 1 the
    // target feed 0 built for the same panel shape — two cameras, one texture, the second
    // silently showing the first. It would also mean one feed's Reset released the other
    // feed's targets, since Reset runs per feed.
    //
    // Keyed by a QUANTISED aspect so floating-point noise cannot mint a new target on every
    // bind: 1/64 steps, far finer than any real panel shape and coarse enough to be stable.
    private static Dictionary<int, DerivedTarget> _derived
        => Feeds.Cur.CoverTargets ??= new Dictionary<int, DerivedTarget>();

    private static int AspectKey(float aspect) => (int)System.Math.Round(aspect * 64.0f);

    // The panel's TRUE display aspect, from immutable definition data only, corrected for the
    // user's orientation. Definition.Resolution is the pixel shape the panel actually
    // presents; Definition.AspectRatio is the declared one. They agree on stock blocks (the
    // corner LCD's 2.5x0.5 dummy gives exactly 5.0), and Resolution is preferred because it
    // is what the surface is actually sized in.
    //
    // ORIENTATION IS USER-SETTABLE AND FLIPS THE SHAPE. A 5:1 panel rotated 90 degrees
    // presents as 1:5, so taking AspectRatio at face value would crop a rotated panel against
    // the wrong axis — silently, and only for users who had rotated something.
    // Takes the resolution as plain ints rather than Vector2I: the caller is PanelBinding,
    // which reads every panel property reflectively and has no reason to take a dependency on
    // an engine math type just to pass two numbers across.
    internal static float EffectiveAspect(int resX, int resY, float declaredAspect, int orientation)
    {
        // DECLARED ASPECT IS THE PHYSICAL SHAPE; Resolution is only the texture size.
        //
        // First version preferred Resolution and it was wrong. On the test block those two
        // DISAGREE — Resolution 1024x512 says 2.000, Definition.AspectRatio declares 1.500 —
        // and the panel showed a vertically squashed image with bars, i.e. cropped against a
        // shape it does not have. The corner LCD settles which is authoritative: its LcdPanel
        // dummy measures 2.5 x 0.5, giving exactly the 5.0 it DECLARES. So AspectRatio tracks
        // the geometry and Resolution is just how many texels are painted onto it — non-square
        // texels are perfectly normal and carry no shape information.
        //
        // Resolution stays as the fallback for a definition that declares nothing usable.
        float a = declaredAspect;
        if (a <= 0.0001f || float.IsNaN(a) || float.IsInfinity(a))
            a = (resX > 0 && resY > 0) ? (float)resX / resY : 1.0f;
        if (a <= 0.0001f || float.IsNaN(a) || float.IsInfinity(a)) a = 1.0f;

        // Odd orientations are the quarter turns. Enum values are not documented here, so
        // this treats 1 and 3 as rotated — the same convention the engine's own
        // LcdScreenOrientation uses for portrait — and a wrong guess is visible immediately
        // as a crop against the wrong axis rather than as a subtle error.
        if ((orientation & 1) != 0) a = 1.0f / a;
        return a;
    }

    // Return the texture a panel of this shape should sample, resampling the square into a
    // target of matching aspect if we have not already made one. Returns null on any failure,
    // and null MUST mean "bind the square directly" at the call site — degrading to the old
    // behaviour is always better than binding nothing and blanking the panel.
    internal static object CoverTargetFor(float aspect)
    {
        if (!FeedConfig.PanelCoverFit || _coverMipsDead) return null;
        if (!_rt.HasValue || _contracts == null || _ui == null) return null;
        if (aspect <= 0.0001f || float.IsNaN(aspect) || float.IsInfinity(aspect)) return null;

        // A square panel needs nothing: the render already matches it exactly, and minting a
        // 1024x1024 copy of a 1024x1024 texture would be pure cost for an identity resample.
        if (System.Math.Abs(aspect - 1.0f) < 0.01f) return null;

        try
        {
            int key = AspectKey(aspect);
            if (!_derived.TryGetValue(key, out var d) || d == null)
            {
                // SIZE IT SO THE LONG AXIS KEEPS THE FULL 1024 AND THE SHORT AXIS SHRINKS.
                // Never larger than the source in either axis: the extra pixels would be
                // invented, and a wide panel is a CROP of the square, so its true vertical
                // information content is only RtSize/aspect rows to begin with.
                int w, h;
                if (aspect >= 1.0f) { w = RtSize; h = (int)System.Math.Round(RtSize / aspect); }
                else { h = RtSize; w = (int)System.Math.Round(RtSize * aspect); }
                if (w < 8) w = 8;
                if (h < 8) h = 8;

                string name = $"RttProbeFit{Feeds.Cur.Id}_{key}";
                var rt = _contracts.CreateOffscreenTarget(name, new Vector2I(w, h));
                if (!rt.IsValid)
                {
                    RttLog.Line($"COVER FIT: CreateOffscreenTarget(\"{name}\", {w}x{h}) returned an INVALID " +
                                "target — this aspect falls back to binding the square directly (bars or " +
                                "stretch, per the panel's own setting). Not fatal.");
                    return null;
                }

                d = new DerivedTarget { Rt = rt, W = w, H = h, Aspect = aspect, Name = name };
                _derived[key] = d;
                RttLog.Line($"COVER FIT: built a {w}x{h} target for aspect {aspect:F3} (feed {Feeds.Cur.Id}, " +
                            $"key {key}). One per distinct SHAPE, not per panel — every panel with this " +
                            "aspect shares it, so the fan-out stays one render and one resample.");
            }

            // The RESAMPLE does not happen here. This call only guarantees the target EXISTS
            // and hands it back for binding; FillCoverTargets does the pixels, on the render
            // thread, with a command list. Separating them is what killed the first attempt:
            // it resampled through a UI batch, and IDrawBatch.DrawImage runs the handle through
            // ResourceHandleExtensions.GetMetadata, which only ever resolves a GUID-backed
            // ASSET. A render target's handle has no metadata, so the draw threw
            // "Resourcehandle does not reference to a valid guid" at REPLAY time on the render
            // thread — a world-load CTD, not an error at the call site. That path is closed for
            // good; do not reintroduce it.
            return d.Rt;
        }
        catch (Exception e)
        {
            if (_errLogs++ < 5) RttLog.Error("cover fit resample", e);
            return null;
        }
    }

    // ---- THE COVER RESAMPLE, DONE THE WAY THE ENGINE DOES IT ---------------------------
    //
    // OffscreenUIRenderer.DrawOne never renders into an offscreen target. Read its IL:
    //
    //     BorrowRWRenderTargetTexture(pool, format, resolution, mipLevels)  <- writable, has an RTV
    //     ClearRenderTargetView(...)
    //     IUIBatch.Draw(...)
    //     MipMapJobExtensions.DoWork(...)
    //     component.get_Texture()
    //     CopyCommandList.CopyResource(componentTexture, borrowed)          <- scratch -> target
    //
    // That indirection exists because OffscreenRenderTargetComponent.Texture is an ROTexture:
    // it is an ITexture2DView (so it READS fine) and has no render-target view at all. Nothing
    // can draw straight into one. Borrowing a scratch RW target and copying the whole resource
    // in is the only supported way, and copying the engine's own idiom is the safest thing
    // available when a wrong GPU copy on this project means a removed device.
    //
    // CopyResource IS safe here, despite its history. It failed before because our 1-mip LDR
    // was copied into a 10-mip target — whole-resource copies require identical descriptions.
    // Here BOTH sides are ours and the borrow is taken with the destination's own Format and
    // MipLevels, exactly as DrawOne does, so the descriptions match by construction.
    //
    // RUNS ON OUR SQUARE TARGET'S OWN DrawOne, which is the property that protects multi-feed
    // frame ordering. OffscreenUIRenderer.DoWork services at most FIVE targets per engine
    // frame, from a queue shared with every other feed and every vanilla LCD:
    //
    //     for (i = 0; i < 5; i++) if (!TryDequeueNextRenderRequest(out t)) break; DrawOne(t);
    //
    // Queuing a render request per derived target would eat that budget and starve the square
    // targets — worse with two feeds, which is exactly the ordering constraint. Instead we
    // never call RequestRender: the derived targets are filled as a side effect of the pass
    // that was already scheduled. Zero extra queue entries, cadence untouched, and every shape
    // for a given feed is written from the SAME square in the SAME command list, so no panel
    // can sample this frame while another samples the last.
    private static MethodInfo _miBorrowRw, _miCopySub, _miCopyJobDoWork;
    private static object _copyJobInstance;
    private static int _coverResampleErrs;
    private static bool _coverPathLogged;
    // Latched when a derived target's mip chain could not be generated. Once true the cover
    // path stops handing out targets: a mip-chained target we cannot fill completely is worse
    // than no target at all, because the unwritten levels are live pool memory.
    private static bool _mipFailLogged, _coverMipsDead;
    private static int _lookupFallbackLogs;

    internal static void FillCoverTargets(object[] args, object commandList)
    {
        if (!FeedConfig.PanelCoverFit) return;
        var derived = Feeds.Cur.CoverTargets;
        if (derived == null || derived.Count == 0) return;
        if (commandList == null || !_rt.HasValue) return;

        try
        {
            var core = Type.GetType("Keen.VRage.Render12.Core.CoreSystems, VRage.Render12");
            if (core == null) return;

            // The square we resample FROM, as the engine sees it: the registered component's
            // ROTexture, which IS an ITexture2DView. Going through the registry rather than
            // our OffscreenRenderTarget handle is the whole point — the handle is not an asset.
            // Resolved by the LIVE square's Id, name only as fallback: after a gate cycle the
            // registry holds this name once per generation, and the name-only lookup is what
            // blacked out the panel on 2026-08-06.
            string squareName = Feeds.Count <= 1 ? "RttProbe" : $"RttProbe{Feeds.Cur.Id}";
            var srcComp = LookupComponent(core, FeedTarget, squareName);
            var srcTex = srcComp == null ? null : Prop(srcComp, "Texture");
            if (srcTex == null)
            {
                if (_coverResampleErrs++ < 3)
                    RttLog.Line("COVER FIT: could not resolve our square target's component texture — " +
                                "no resample this pass. Panels keep the square (bars or stretch).");
                return;
            }

            // CoreSystems.SceneDrawSystem is a static, so the copy job needs no threading
            // through the call chain. Borrowing the engine's instance rather than constructing
            // one is deliberate: CopyJob's ctor takes an initialization-task list and builds
            // PSOs asynchronously, and dispatching against a half-built job is a device
            // removal, not an exception — the same reason owning EyeAdaptationJob was
            // impossible and it had to become a state swap.
            if (_copyJobInstance == null)
            {
                var sds = core.GetField("SceneDrawSystem", BindingFlags.Public | BindingFlags.Static)
                              ?.GetValue(null);
                _copyJobInstance = sds?.GetType().GetField("_copyJob", Any)?.GetValue(sds);
            }
            if (_copyJobInstance == null)
            {
                if (_coverResampleErrs++ < 3)
                    RttLog.Line("COVER FIT: SceneDrawSystem._copyJob not found — cannot resample. " +
                                "Borrowing the engine's instance is deliberate: constructing a CopyJob " +
                                "means async PSO init, which is why owning EyeAdaptationJob was impossible.");
                return;
            }

            foreach (var kv in derived)
            {
                var d = kv.Value;
                if (d == null) continue;
                try { ResampleOne(core, args, commandList, srcTex, d); }
                catch (Exception e) { if (_coverResampleErrs++ < 5) RttLog.Error("cover resample one", e); }
            }

            if (!_coverPathLogged)
            {
                _coverPathLogged = true;
                RttLog.Line($"COVER FIT: resample path LIVE — {derived.Count} shape(s) filled from one square, " +
                            "inside our own DrawOne, with NO extra render requests. The engine services at most " +
                            "5 offscreen targets per frame across the whole game, so queuing our own would have " +
                            "starved the feed and broken multi-feed ordering.");
            }
        }
        catch (Exception e) { if (_coverResampleErrs++ < 5) RttLog.Error("cover fill", e); }
    }

    // Resolve an OffscreenRenderTargetComponent from OffscreenTargetManager._registeredTextures.
    //
    // This is what makes the whole approach possible. The component is normally only ever seen
    // as a DrawOne ARGUMENT, which is why the first design looked blocked: you appear to need
    // the engine to service a target before you can write to it. But the manager that hands
    // those components out is a plain global with a registry, one field above the dequeue
    // method — so we can resolve a target we own without it being scheduled at all.
    //
    // MATCH ON THE TARGET'S Id — BY TEXT — AND ONLY FALL BACK TO NAME.
    //
    // This method has now been wrong twice, in opposite directions, and the history is the
    // spec:
    //
    //   * The FIRST version compared OffscreenRenderTarget.TextureHandle against the registry
    //     key with Equals. That can never match: TextureHandle is a ResourceHandle<T>, the key
    //     is a GeneratedResourceHandle — different types, false before any value is looked at.
    //   * The SECOND version matched on Name, under the comment "unique per feed by
    //     construction". Unique per feed — but NOT PER GENERATION. Every gate cycle creates a
    //     fresh "RttProbe0" and "RttProbeFit0_96" while the previous generation's registration
    //     stays in the registry (release clears OUR dict, not the engine's). After one cycle
    //     the name matches TWO components and enumeration order picks one. On 2026-08-06 it
    //     picked the corpse: the resample wrote the DEAD derived target while the panel
    //     sampled the LIVE one, which had been cleared-to-black since creation. A completely
    //     black panel with every counter healthy — and invisible in single-cycle testing,
    //     which is why "worked perfectly" and this bug were both true.
    //
    // The id-as-TEXT comparison is the same idiom FeedHandover has used all along
    // (`handle.Contains(_panelHandleText)`): the component's Handle and the target's Id both
    // print the generated id, so string-contains bridges the type mismatch that killed
    // version one, and the id is per-generation, which kills version two.
    private static object LookupComponent(Type core, object rt, string name)
    {
        try
        {
            var mgr = core.GetField("OffscreenTarget", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (mgr == null) return null;
            var dict = mgr.GetType().GetField("_registeredTextures", Any)?.GetValue(mgr)
                       as System.Collections.IDictionary;
            if (dict == null) return null;

            string idText = rt == null ? null : Prop(rt, "Id")?.ToString();
            if (!string.IsNullOrEmpty(idText))
            {
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    var h = Prop(e.Value, "Handle")?.ToString();
                    if (h != null && h.Contains(idText)) return e.Value;
                }
            }

            // Name fallback, for a registration whose Handle member ever changes shape. A hit
            // here with a LIVE id above means the id read failed, not that the name was right —
            // say so, because a silent fallback is how the generation bug would sneak back.
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var n = Prop(e.Value, "Name") as string;
                if (n != null && n == name)
                {
                    if (_lookupFallbackLogs++ < 3)
                        RttLog.Line($"COVER FIT: resolved \"{name}\" by NAME because the id path failed " +
                                    $"(idText={(idText ?? "<null>")}). Names are not unique across gate " +
                                    "cycles — if this fires after a cycle, the resample may be touching a " +
                                    "DEAD generation and the panel will be black or frozen.");
                    return e.Value;
                }
            }

            if (_coverResampleErrs++ < 3)
            {
                var names = new System.Text.StringBuilder();
                int shown = 0;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    if (shown++ >= 12) break;
                    names.Append(Prop(e.Value, "Name") as string ?? "<null>").Append(' ');
                }
                RttLog.Line($"COVER FIT: no registered offscreen target with id {idText ?? "<null>"} or " +
                            $"name \"{name}\". Registry holds {dict.Count}: {names}— if our names are absent " +
                            "the target was never registered; if they are present under a different string, " +
                            "match on that.");
            }
        }
        catch { }
        return null;
    }

    private static void ResampleOne(Type core, object[] args, object commandList, object srcTex, DerivedTarget d)
    {
        var dstComp = LookupComponent(core, d.Rt, d.Name);
        var dstTex = dstComp == null ? null : Prop(dstComp, "Texture");
        if (dstTex == null) return;

        var pool = core.GetField("BindableTexturePool", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (pool == null) return;

        // BORROW WITH THE DESTINATION'S OWN Format AND MipLevels — the property that makes the
        // CopyResource at the end legal. DrawOne does exactly this, and the 2026-07 device
        // removal came from a CopyResource whose two sides disagreed on mip count.
        _miBorrowRw ??= pool.GetType().GetMethods(Any)
            .FirstOrDefault(m => m.Name == "BorrowRWRenderTargetTexture" && m.GetParameters().Length == 7);
        if (_miBorrowRw == null) return;

        var fmt = Prop(dstComp, "Format");
        int mips = System.Convert.ToInt32(Prop(dstComp, "MipLevels") ?? 1);
        var ps = _miBorrowRw.GetParameters();
        object clearNull = Activator.CreateInstance(ps[5].ParameterType);   // Nullable<Color> = null

        // LIFETIME 128, NOT 0 — matching CameraRender's LDR ring.
        //
        // lifetime is not "when the borrow ends"; it is how many frame-ends a RETURNED
        // texture survives in _available before the pool disposes it. Pool.OnFrameEndDisposal
        // decrements it and calls Dispose() the moment it goes negative, so 0 means allocate
        // and destroy a full-size render target every single frame — the exact per-reload
        // allocator churn the VRAM ratchet was. 128 lets the next frame's resample reuse the
        // same scratch, which is what makes this cheap enough to run per frame.
        var borrowed = _miBorrowRw.Invoke(pool, new object[]
        {
            $"RttCoverFit{Feeds.Cur.Id}_{d.W}x{d.H}", fmt, fmt,
            new Vector2I(d.W, d.H), mips, clearNull, 128
        });
        if (borrowed == null) return;

        try
        {
            // Borrowed<T> — the resource is behind .Resource, same as every engine call site.
            var res = Prop(borrowed, "Resource") ?? borrowed;

            // VERIFY WHAT THE POOL ACTUALLY GAVE US, once per shape. The corruption on
            // 2026-08-06 came from trusting the REQUEST: I asked for the destination's format
            // and mip count and treated the result as matching. Print both sides so a
            // disagreement is a log line rather than a ghosted frame, and so the next reader
            // can see whether the pool honours a request or merely satisfies it.
            if (!d.DescLogged)
            {
                d.DescLogged = true;
                var br = Prop(res, "Resolution");
                RttLog.Line($"COVER FIT: shape {d.W}x{d.H} — borrowed scratch Resolution={br}, " +
                            $"destination Resolution={Prop(dstComp, "Resolution")} " +
                            $"Format={Prop(dstComp, "Format")} MipLevels={mips}. Copying subresources " +
                            "LEVEL BY LEVEL rather than the whole resource, so a mip-count difference is " +
                            "tolerated — and every level gets written, which is what stops the panel " +
                            "sampling recycled pool content at distance.");
            }

            // THE COVER CROP. cropRect selects the SOURCE region, viewport the DESTINATION.
            // For aspect >= 1 this is a pure centred horizontal slice of the square at 1:1 —
            // no scaling at all, which is both sharper and cheaper than resizing. The short
            // axis is what runs off, exactly as asked.
            int srcW = RtSize, srcH = RtSize;
            int cw, ch;
            if (d.Aspect >= 1.0f) { cw = srcW; ch = (int)System.Math.Round(srcW / d.Aspect); }
            else { ch = srcH; cw = (int)System.Math.Round(srcH * d.Aspect); }
            if (cw > srcW) cw = srcW;
            if (ch > srcH) ch = srcH;
            int cx = (srcW - cw) / 2, cy = (srcH - ch) / 2;

            _miCopyJobDoWork ??= _copyJobInstance.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "DoWork" && m.GetParameters().Length == 8);
            if (_miCopyJobDoWork == null) return;

            var cps = _miCopyJobDoWork.GetParameters();
            var rectType = Nullable.GetUnderlyingType(cps[3].ParameterType);
            object viewport = MakeRect(rectType, 0, 0, d.W, d.H);
            object cropRect = MakeRect(rectType, cx, cy, cw, ch);
            object ppNull = Activator.CreateInstance(cps[4].ParameterType);          // Nullable<PostProcess>
            object channelAll = Enum.ToObject(cps[5].ParameterType, 15);             // RGBA

            _miCopyJobDoWork.Invoke(_copyJobInstance, new object[]
            { commandList, res, srcTex, viewport, ppNull, channelAll, null, cropRect });

            // GENERATE THE MIP CHAIN ON THE SCRATCH — THIS IS CORRECTNESS, NOT QUALITY.
            //
            // This block used to resolve MipMapJobExtensions.DoWork and never call it, under
            // the comment "a missing mip pass costs distance quality, never correctness".
            // That was wrong and it is what corrupted the panels on 2026-08-06.
            //
            // CopyTextureSubresource writes ONE subresource. The derived target is created
            // with the same MipLevels as any offscreen target — 10, as its own probe line
            // reported — so writing only level 0 leaves levels 1..9 holding whatever the pool
            // last had in that memory: DrawOne's recycled render targets. The panel samples a
            // mip by distance and angle, so the moment the viewer is not nose-against the
            // screen it reads that stale content, which is precisely the reported symptom —
            // "a semi transparent freeze frame of parts of the game world" that shifted with
            // head movement, because head movement is what selects the mip.
            //
            // The handover diagnosed this identical defect on the square panels months ago and
            // built RegenerateMips for it; its own comment names the cause as "DrawOne's
            // recycled pool content". Reusing that routine rather than writing a second one
            // also means one overload predicate, resolved once, for both sources.
            //
            // Mips must be generated on the SCRATCH, never the destination: the destination is
            // the component's ROTexture and has no writable view at all.
            int levels = (mips > 1 && FeedConfig.PanelMipRegen &&
                          FeedHandover.RegenerateMips(args, res, commandList, mips))
                ? mips
                : 1;

            // NEVER CopyResource HERE. It copies a WHOLE resource and requires IDENTICAL
            // descriptions, and this exact call — with descriptions I had assumed matched
            // because I asked the pool for the destination's Format and MipLevels — produced
            // world-wide ghosting on 2026-08-06: terrain visible through the hull, panels
            // blending foreign content. Undefined behaviour, limping rather than crashing,
            // which is the precise failure this file already documents from the handover's own
            // history ("worked at 2 Hz, died at 15 fps").
            //
            // ASKING FOR A DESCRIPTION IS NOT GETTING ONE. A pooled texture is whatever the
            // pool had; it can differ in mip count, alignment or flags and still satisfy the
            // request. CopyTextureSubresource copies ONE subresource, so mip counts need not
            // agree — the same reason the handover uses it instead of CopyResource.
            _miCopySub ??= commandList.GetType().GetMethods(Any)
                .FirstOrDefault(m => m.Name == "CopyTextureSubresource" && m.GetParameters().Length == 4);
            if (_miCopySub == null)
            {
                if (_coverResampleErrs++ < 3)
                    RttLog.Line("COVER FIT: no CopyTextureSubresource on the command list — refusing to " +
                                "fall back to CopyResource, which is undefined unless the descriptions match " +
                                "exactly and has already corrupted a frame once. Panels keep the square.");
                return;
            }

            // EVERY LEVEL, not just level 0. See the mip block above: a partially written mip
            // chain is not a blurry panel, it is someone else's render target.
            for (int level = 0; level < levels; level++)
                _miCopySub.Invoke(commandList, new object[] { dstTex, level, res, level });

            // If the chain could NOT be generated we have written level 0 into a 10-level
            // destination, which is the corrupting case. Stop minting new derived targets so no
            // further panel is bound into it, and say so in terms the reader can act on —
            // silently degrading is what made this cost two sessions.
            if (levels == 1 && mips > 1 && !_mipFailLogged)
            {
                _mipFailLogged = true;
                _coverMipsDead = true;
                RttLog.Line($"COVER FIT DISABLED: could not generate the mip chain for {d.Name}, but the " +
                            $"target has {mips} levels. Only level 0 is ours; 1..{mips - 1} hold the pool's " +
                            "recycled content, which shows as a semi-transparent freeze-frame of the world " +
                            "that moves with the viewer. No further derived targets will be created. Fix " +
                            "the mip path (panelMipRegen, or a bootstrap too old to pass the " +
                            "OffscreenUIRenderer) or set panelCoverFit = 0.");
            }
        }
        finally
        {
            // RETURN THE BORROW, AND MATCH THE OVERLOAD BY TYPE.
            //
            // BindableTexturePoolManager has SEVEN Return(Borrowed<T>) overloads, one per
            // texture kind, and this used to be FirstOrDefault(m => m.Name == "Return") — an
            // arbitrary pick among seven, invoked with the wrong Borrowed<T> six times out of
            // seven, throwing ArgumentException into an empty catch. The borrow was then never
            // returned, and Pool.OnFrameEndDisposal asserts _allocated.Count == 0 with "Some of
            // the borrowed textures has not been returned" — an assertion on the render thread,
            // which in this engine is a freeze, not an exception. That is the other half of the
            // 2026-08-06 report: the feed camera stopped responding.
            //
            // CameraRender.ReturnBorrowed has always filtered on IsInstanceOfType. This is the
            // same loop; the two should not have differed.
            try
            {
                bool returned = false;
                foreach (var m in pool.GetType().GetMethods(Any))
                {
                    if (m.Name != "Return") continue;
                    var p = m.GetParameters();
                    if (p.Length != 1 || !p[0].ParameterType.IsInstanceOfType(borrowed)) continue;
                    m.Invoke(pool, new[] { borrowed });
                    returned = true;
                    break;
                }
                if (!returned && _coverResampleErrs++ < 3)
                    RttLog.Line("COVER FIT: no Return overload accepts " +
                                $"{borrowed.GetType().Name} — the borrow is LEAKED, and the pool asserts on " +
                                "an unreturned texture at frame end. Set panelCoverFit = 0.");
            }
            catch (Exception e) { if (_coverResampleErrs++ < 3) RttLog.Error("cover return", e); }
        }
    }

    private static object MakeRect(Type rectType, int x, int y, int w, int h)
    {
        if (rectType == null) return null;
        var ctor = rectType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
        return ctor?.Invoke(new object[] { x, y, w, h });
    }

    // Drop the derived targets — from OUR dictionary only. The engine-side registration and
    // the texture behind it are deliberately NOT disposed here: a bound panel keeps sampling
    // the old texture until the next ACTIVE cycle rebinds it, and disposing under a live
    // sampler is the IsValid-assert render-thread freeze this repo has already paid for
    // twice. The cost of not disposing is one small texture per shape per gate cycle and a
    // dead registration in _registeredTextures — which is why every registry lookup matches
    // on the live target's ID, never on the name: after one cycle the name belongs to two
    // components, and writing into the dead one blacked out the panel on 2026-08-06.
    internal static void ReleaseCoverTargets(string why)
    {
        var d = Feeds.Cur.CoverTargets;
        if (d == null || d.Count == 0) return;
        int n = d.Count;
        d.Clear();
        RttLog.Line($"COVER FIT: released {n} derived target(s) for feed {Feeds.Cur.Id} ({why}). " +
                    "Other feeds keep theirs — these are per feed because the square they resample " +
                    "from is per feed. The engine-side registrations stay behind as corpses (safe: " +
                    "lookups match by id, and disposing under a still-bound panel is a freeze).");
    }

    // Once the camera pass is copying real frames in, the 2D test pattern would
    // just overwrite them. The backing field is volatile: written on the tick side,
    // read on the render side, and a property over it keeps those semantics.
    public static bool FeedOwnsTarget
    { get => Feeds.Cur.FeedOwnsTarget; set => Feeds.Cur.FeedOwnsTarget = value; }

    private static PersistentDrawBatch _persistentBatch
    { get => Feeds.Cur.PersistentBatch; set => Feeds.Cur.PersistentBatch = value; }
    private static bool _batchRetired
    { get => Feeds.Cur.BatchRetired; set => Feeds.Cur.BatchRetired = value; }
    private static long _lastPaint;
    private static int _paintCount;

    private static bool _blitRecorded;
    private static int _framesSinceBlit;
    private static bool _blitConfirmed;
    private static bool _disarmed;

    private static int _tickLogs, _renderLogs, _errLogs, _panelHookLogs;
    private static long _tickCount;

    public static void Reset()
    {
        // A marker left behind means the previous session recorded a blit and
        // never came back — i.e. the replay rejected the handle. Refuse to repeat
        // it until a human clears the file.
        if (FileWatch.Exists(MarkerPath))
        {
            _disarmed = true;
            RttLog.Line("!!! PREVIOUS SESSION DIED WITH A BLIT ARMED !!!");
            RttLog.Line($"!!! DrawImage rejected the render-target handle in the replay. Stage 4 is DISABLED.");
            RttLog.Line($"!!! Delete {MarkerPath} to try again.");
        }
        // COVER-FIT TARGETS GO WITH THE MAIN ONE. Each derived target is its own offscreen
        // allocation, and they are keyed by aspect in a static dictionary — so without this
        // they would survive every gate cycle and hot reload while the target they were
        // resampling from was replaced underneath them. That is precisely the shape of the
        // VRAM ratchet (~1.1 GB/reload) and of the descriptor leak that killed own-probes:
        // state that outlives the thing it was derived from, with nothing left able to reach
        // it. Released HERE rather than in a finaliser so the lifetime is the same one the
        // rest of the feed already obeys.
        ReleaseCoverTargets("feed reset");

        _rt = null;
        _rtTried = false;
        _resolveTried = false;
        _contracts = null;
        _ui = null;
        _blitRecorded = false;
        _blitConfirmed = false;
        _framesSinceBlit = 0;
        _paintCount = 0;
        _persistentBatch = null;
        _batchRetired = false;
        FeedOwnsTarget = false;
        _tickLogs = _renderLogs = _errLogs = 0;
    }

    // ------------------------------------------------------------------ tick
    // Per frame, outside panel content recording. Stages 1-3 live here.
    public static void OnTick(object component)
    {
        _tickCount++;

        // PANEL-DRIVEN (phase C1b). The engine hands us ONE LCD component per call, on its
        // own schedule — so the feed is whoever owns that panel, a lookup, NOT a rotation.
        // Rotating here would hand panel A's tick to feed B, which is the single easiest way
        // to make two feeds silently corrupt each other.
        using (Feeds.Enter(Feeds.ForPanel(component)))
            OnTickScoped(component);
    }

    private static void OnTickScoped(object component)
    {
        try
        {
            // Polled here as well as in the whole-scene hook: this is the tick that keeps
            // running when the render-side route is off, so it is what lets a dormant mod
            // notice the panel coming back. Panel DISCOVERY below must stay ungated —
            // that is the signal the gate reads.
            FeedGate.Poll();

            if (_tickLogs < 1) { _tickLogs++; RttLog.Line("Tick hook alive."); }

            // Locate the [RTC] panel this tick belongs to, if any.
            CameraFeed.OnLcdTick(component);

            // And any [RTS] stats panel. Separate call because OnLcdTick returns early for
            // anything not carrying the feed tag, so a stats panel would never be seen.
            StatsPanel.OnLcdTick(component);

            // The DisposePendingProbes drain used to run here, on the premise that this
            // tick is the game thread and therefore outside any frame we record. The second
            // half of that premise is FALSE — the render thread renders concurrently with
            // this tick — and it cost a third device removal to establish. Both the drain
            // and its queue are gone; see the probe-manager comment in WholeSceneRender.Reset.

            // On-demand resource report (drop output/resource-report.marker). Read-only
            // reflection, one File.Exists every 2 s until asked. Deliberately NOT a config
            // knob: a config change can cost a gate cycle, and gate-cycle churn is what
            // took the device three times on 2026-07-30.
            FeedResourceReport.MaybeRun();

            ResolveContracts(component);
            if (_contracts == null || _ui == null) return;

            EnsureRenderTarget();
            if (_rt == null) return;

            // Confirmation of stage 4: the crash we are hunting happens after our
            // postfix returns, during replay. Surviving frames is the evidence.
            if (_blitRecorded && !_blitConfirmed && ++_framesSinceBlit >= 120)
            {
                _blitConfirmed = true;
                try { File.Delete(MarkerPath); } catch { }
                RttLog.Line("=== STAGE 4 CONFIRMED: blit survived 120 frames. DrawImage accepts a render-target handle. ===");
            }

            // The test pattern is only useful until real frames arrive.
            var now = Environment.TickCount64;
            // The test pattern and the camera copy are mutually exclusive writers to
            // the same target. Painting while the handover is armed means DrawOne draws
            // our batch and then our postfix copies over it in the same servicing —
            // two writers, one pass, which killed the game on the first copy.
            // Armed AND actually copying. While the copy is disabled for diagnostics
            // the handover writes nothing, so suppressing the test pattern too would
            // leave a blank panel and no way to tell a broken target from a quiet one.
            bool feedArmed = FeedConfig.CopyEnabled &&
                             FileWatch.Exists(Path.Combine(RttLog.OutDir, "handover-armed.marker"));
            if (!FeedOwnsTarget && !feedArmed)
            {
                if (now - _lastPaint >= 500) { _lastPaint = now; PaintTestPattern(); }
            }
            else if (!_batchRetired && FeedConfig.RetireTestPattern && _paintCount > 0)
            {
                // Suppressing the *repaint* is not enough: a persistent batch keeps
                // being drawn on every servicing until it is replaced, so DrawOne would
                // still paint the last test pattern immediately before our copy lands on
                // top of it. Two writers per servicing, which is the rule we already
                // learned the hard way. Replace it with an empty one — same API, and the
                // only one proven to retire the previous batch.
                _batchRetired = true;
                try
                {
                    _persistentBatch = _ui.CreatePersistentBatchFor(_rt, 0, _persistentBatch, true);
                    _persistentBatch?.Submit();
                    RttLog.Line("Stage 3: test pattern retired — the camera feed is the only writer now.");
                }
                catch (Exception e) { RttLog.Error("retire test pattern", e); }
            }

            // Readback lives here rather than in the camera pass: this hook can make
            // contracts calls (it already creates the offscreen target), whereas the
            // camera pass runs on the render thread where the enqueued command is
            // never pumped.
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("tick", e); }
    }

    // ---------------------------------------------------------------- stage 1
    // RenderContracts is not exposed as a singleton we can reach, but the LCD
    // render component holds one (its rebuild path calls GetUISystem), so it can
    // be fished out of that component's fields.
    private static void ResolveContracts(object component)
    {
        if (_resolveTried || component == null) return;
        _resolveTried = true;
        RttLog.Line("Stage 1: resolving RenderContracts / UISystem...");
        try
        {
            const System.Reflection.BindingFlags All =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

            foreach (var f in component.GetType().GetFields(All))
            {
                object v = null;
                try { v = f.GetValue(f.IsStatic ? null : component); } catch { }
                if (v is RenderContracts rc)
                {
                    _contracts = rc;
                    _ui = rc.GetUISystem();
                    break;
                }
                if (v is UISystem us) _ui = us;
            }
            RttLog.Line($"Stage 1: contracts={(_contracts != null ? "OK" : "NOT FOUND")} uiSystem={(_ui != null ? "OK" : "NOT FOUND")}");
            if (_contracts == null)
                RttLog.Line("Stage 1 FAILED — cannot create a render target without RenderContracts. Fields seen: " +
                    string.Join(", ", component.GetType().GetFields(All).Select(f => f.FieldType.Name).Distinct().Take(25)));
        }
        catch (Exception e) { RttLog.Error("stage1 resolve", e); }
    }

    // ---------------------------------------------------------------- stage 2
    private static void EnsureRenderTarget()
    {
        if (_rtTried) return;
        _rtTried = true;

        // NAMED PER FEED (phase C3). This was the literal "RttProbe" for every caller, which
        // was fine when there was exactly one, and is a collision the moment there are two:
        // both feeds would ask the engine's contracts for a target under one name, and the
        // feed that asked second would either be handed the first feed's target — two panels
        // showing one camera — or fight it for ownership. Neither failure announces itself.
        string name = Feeds.Count <= 1 ? "RttProbe" : $"RttProbe{Feeds.Cur.Id}";

        RttLog.Line($"Stage 2: CreateOffscreenTarget(\"{name}\", {RtSize}x{RtSize})...");
        try
        {
            var rt = _contracts.CreateOffscreenTarget(name, new Vector2I(RtSize, RtSize));
            RttLog.Line($"Stage 2: returned Id={rt.Id} IsValid={rt.IsValid}");
            if (!rt.IsValid) { RttLog.Line("Stage 2 FAILED — target is not valid."); return; }
            _rt = rt;
            RttLog.Line("Stage 2 OK.");
        }
        catch (Exception e) { RttLog.Error("stage2 create", e); }
    }

    // ---------------------------------------------------------------- stage 3
    // Draw a distinctive, animated pattern into our own target. Animation is the
    // point: a frozen image on the panel later would mean the blit is sampling a
    // stale copy rather than the live target.
    private static void PaintTestPattern()
    {
        try
        {
            // Persistent, not immediate. An immediate batch is drawn once and
            // discarded — but our batch is recorded from the tick hook while the target
            // is only serviced later, in DrawOne, which clears it first and then finds
            // nothing to draw. A persistent batch survives until replaced, which is what
            // "content that lives on a render target" actually needs.
            var batch = _persistentBatch = _ui.CreatePersistentBatchFor(_rt, 0, _persistentBatch, true);
            if (batch == null)
            {
                if (_errLogs++ < 5) RttLog.Line("Stage 3: CreateImmediateBatchFor returned null.");
                return;
            }

            const float S = RtSize;
            Fill(batch, 0, 0, S, S, 20, 24, 40);                       // dark blue ground
            Fill(batch, 16, 16, S - 16, S - 16, 220, 60, 60);           // red border block
            Fill(batch, 48, 48, S - 48, S - 48, 30, 34, 55);            // inner panel

            // Corner markers make orientation and any flip readable at a glance.
            Fill(batch, 48, 48, 112, 112, 255, 255, 255);               // top-left white
            Fill(batch, S - 112, 48, S - 48, 112, 80, 255, 80);         // top-right green
            Fill(batch, 48, S - 112, 112, S - 48, 80, 160, 255);        // bottom-left blue
            Fill(batch, S - 112, S - 112, S - 48, S - 48, 255, 220, 60);// bottom-right yellow

            // Sweeping bar: proves the target is being repainted, not cached.
            float t = (_paintCount % 20) / 20f;
            float bx = 64 + t * (S - 192);
            Fill(batch, bx, S * 0.5f - 24, bx + 128, S * 0.5f + 24, 255, 255, 255);

            batch.Submit();
            _paintCount++;
            if (_paintCount == 1) RttLog.Line("Stage 3 OK: first paint submitted into our own render target.");
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("stage3 paint", e); }
    }

    // ---- mirror forensics (2026-08-01) -------------------------------------------
    //
    // Logs the [RTS] surface's screen-material handle whenever it CHANGES, plus the
    // material state. PROCESS-global on purpose: it describes one physical panel that
    // belongs to no feed, and the whole point is a stable identity across gate cycles.
    private static string _mirrorLastHandle;

    private static void MirrorDiag(LcdPanelSurfaceContext ctx)
    {
        try
        {
            // THE RENDER TARGET, not the material. The feed does NOT reach the panel through
            // the material bind — "Feed blit identity: src=1024,1024 dst=512,512" is the
            // handover copying our LDR into the PANEL'S OWN 512 render target. So if this
            // panel is showing our picture, the overwhelmingly simplest explanation is that
            // it is holding THE SAME render target we are copying into: the LCD system pools
            // these and hands them out by need, our feed panel's own content is suppressed
            // (so it can release), and we keep writing into the id we captured.
            //
            // One number settles it. If the two ids below are ever EQUAL, that is the bug and
            // the fix is to re-verify ownership before every copy instead of trusting a
            // captured id. If they are never equal, pooling is exonerated and the leak is
            // upstream in what the mesh samples.
            object handle = null, state = null;
            var t = ctx.GetType();
            var f = t.GetField("_screenMaterialHandle",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f != null) handle = f.GetValue(ctx);
            // ctx.State is a STRUCT — no null-conditional on it.
            try { state = ctx.State.GetType().GetProperty("CurrentMaterialState")?.GetValue(ctx.State); } catch { }

            string rtsRt = RtIdOf(ctx);
            string feedRt = CameraFeed.PanelRtIdText;
            string now = (handle == null ? "<null>" : $"{handle.GetType().Name}#{handle.GetHashCode():x8}")
                       + "|rt=" + rtsRt + "|feedRt=" + feedRt;
            if (now == _mirrorLastHandle) return;
            _mirrorLastHandle = now;

            bool same = rtsRt != "<none>" && rtsRt == feedRt;
            RttLog.Global($"[RTS diag] this panel's RENDER TARGET id={rtsRt}; the feed is copying into id={feedRt}. " +
                          (same
                            ? "*** THEY ARE THE SAME TARGET — the stats panel is being handed the render target we "
                              + "write the feed into. That is the mirror, and it is pooling, not materials. ***"
                            : "Different targets, so the mirror is NOT us writing into this panel's own target.")
                          + $" (materialState={state ?? "?"}, screenMaterial={(handle == null ? "<null>" : handle.GetType().Name)})");
        }
        catch (Exception e) { if (_mirrorDiagErrs++ < 2) RttLog.Error("mirror diag", e); }
    }

    // The OffscreenRenderTarget Id a surface context currently holds, as text.
    // Nullable<OffscreenRenderTarget> — unwrap before reading Id, same shape
    // CameraFeed.CapturePanelRenderTarget deals with.
    private static string RtIdOf(object ctx)
    {
        try
        {
            var rt = ctx.GetType().GetField("RenderTarget",
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                         | System.Reflection.BindingFlags.NonPublic)?.GetValue(ctx);
            if (rt == null) return "<none>";
            var has = rt.GetType().GetProperty("HasValue")?.GetValue(rt);
            if (has is bool b && !b) return "<none>";
            var val = rt.GetType().GetProperty("Value")?.GetValue(rt) ?? rt;
            return val.GetType().GetProperty("Id")?.GetValue(val)?.ToString() ?? "<no id>";
        }
        catch { return "<err>"; }
    }
    private static int _mirrorDiagErrs;

    // Draw the camera feed across the whole tagged surface.
    private static int _feedDrawLogs;

    private static void DrawFeed(IDrawBatch batch, LcdPanelSurfaceContext ctx)
    {
        try
        {
            var res = ctx.Definition.Resolution;
            var dest = new BoundingBox2(new Vector2(0f, 0f), new Vector2(res.X, res.Y));
            ResourceHandle handle = _rt.Value.TextureHandle;

            if (_feedDrawLogs++ == 0)
                RttLog.Line($"=== FEED DRAW: painting camera feed onto [RTC] panel ({res.X}x{res.Y}). ===");

            batch.DrawImage(handle, dest, new ColorSRGB((byte)255, (byte)255, (byte)255, (byte)255), false, null, null);
        }
        catch (Exception e) { if (_errLogs++ < 3) RttLog.Error("feed draw", e); }
    }

    private static void Fill(IDrawBatch batch, float x0, float y0, float x1, float y1, byte r, byte g, byte b)
    {
        var splines = new[]
        {
            new QuadraticBezier2(new Vector2(x0, y0), new Vector2(x1, y0)),
            new QuadraticBezier2(new Vector2(x1, y0), new Vector2(x1, y1)),
            new QuadraticBezier2(new Vector2(x1, y1), new Vector2(x0, y1)),
            new QuadraticBezier2(new Vector2(x0, y1), new Vector2(x0, y0)),
        };
        batch.DrawFill(splines, new ColorSRGB(r, g, b, (byte)255), null, false);
    }

    // ------------------------------------------------------------ panel render
    // Stage 4. The batch handed to us here targets the panel's own offscreen
    // render target, so this is a genuine RT-to-RT blit.
    public static void OnPanelRender(object rendererObj, object batchObj, object ctxObj)
    {
        // PANEL-DRIVEN, keyed on the SURFACE CONTEXT (phase C3). A surface context carries no
        // _lcdBlock and therefore no name, so it cannot be routed the way OnTick's component
        // is — it is registered to its feed during discovery instead, which already runs
        // under that feed's scope. The scope opens before the gate check because
        // FeedGate.Active is per-feed state now: "is this panel's feed live" cannot be
        // answered without first knowing which feed.
        using (Feeds.Enter(Feeds.ForSurface(ctxObj)))
            OnPanelRenderScoped(rendererObj, batchObj, ctxObj);
    }

    private static void OnPanelRenderScoped(object rendererObj, object batchObj, object ctxObj)
    {
        if (batchObj is not IDrawBatch batch || ctxObj is not LcdPanelSurfaceContext ctx) return;
        try
        {
            // The camera feed owns its panel outright: fill the surface with the
            // live render rather than compositing it over the panel's own content.
            string text = null;
            try { text = ctx.State.Text; } catch { }

            // Match the feed panel by its stamped tag, not by reference: surface
            // contexts are re-created, so an identity check silently stops matching.
            if (_panelHookLogs < 1)
            {
                _panelHookLogs++;
                RttLog.Line($"Panel render hook firing (text=\"{text}\", rt={_rt != null}).");
            }

            // THE STATS PANEL FIRST, AND NOT BEHIND THIS SURFACE'S FEED GATE.
            //
            // It used to sit below `if (!FeedGate.Active) return`, and the scope wrapping this
            // method is Feeds.ForSurface(ctx) — which resolves an [RTS] surface to PRIMARY,
            // because nothing ever registers a stats surface to a feed. So the debug panel was
            // gated on FEED 0 specifically. Grind down feed 0's panel and the stats panel goes
            // blank, which is the exact moment it is most worth reading; observed 2026-08-01,
            // reported as "only shows a blank screen with the [RTS] text".
            //
            // Seventh instance of the same family — something keyed to Primary that has no
            // business being keyed to a feed at all. The stats panel is a statement about the
            // MOD: it draws into the panel's own batch, touches no feed machinery, and its most
            // valuable reading is "feed fps 0:off 1:47.4", which by definition happens when a
            // feed is down.
            //
            // The one thing that still silences it is the PAUSE MARKER, and that is deliberate:
            // paused means the game renders exactly as it would without this mod, and a panel
            // we are still drawing on would make that comparison a lie.
            if (text != null && text.Contains(StatsPanel.Tag, StringComparison.OrdinalIgnoreCase))
            {
                // MIRROR FORENSICS (2026-08-01). The [RTS] panel repeatedly turns into a copy
                // of the camera feed: deterministically ON within ~500 ms of a feed's material
                // bind, deterministically OFF at its teardown — we bind ONE panel and both
                // change, which no per-panel path explains. Working theory: the LCD material
                // system caches runtime materials by the SHARED LCDScreen_On definition (its
                // teardown assert is guid-keyed), so this panel's 500 ms content rebuild
                // fetches OUR runtime material — camera texture included — by the shared key.
                //
                // This logs the material handle THIS ctx actually holds, on every change. The
                // theory predicts: handle A while the feed is dormant, handle B (ours) while
                // it is bound, back to A after teardown. Target-content overwrite instead
                // predicts the handle NEVER changes while the picture does.
                MirrorDiag(ctx);
                if (!FeedGate.Paused) StatsPanel.Draw(batch, ctx);
                return;
            }

            // Dormant means the panel draws its own content, exactly as it would without
            // this mod installed. BELOW the stats branch: this gate is about whether THIS
            // SURFACE'S FEED is live, which is a question only feed panels are asking.
            if (!FeedGate.Active) return;

            // DrawImage with a render-target-backed handle is fatal: UISystemComponent
            // .GetTexture asserts IsGuid(), and an OffscreenRenderTarget's handle is a
            // generated RenderId handle. Confirmed by killing the game the instant a
            // tagged panel repainted. The feed reaches the panel by writing into the
            // panel's own render target instead — see CameraFeed.CapturePanelRenderTarget.
            // FeedRouter.IsFeedPanel, not a raw Contains, so [RTC2] is recognised here too.
            // A tag the discovery side accepts and this side rejects would bind the panel's
            // material and then let the panel paint straight over the feed.
            if (FeedRouter.IsFeedPanel(text))
            {
                PanelBinding.OnPanelRender(rendererObj, ctx);
            }
            if (FeedRouter.IsFeedPanel(text))
            {
                // Draw nothing. The feed does not go through this batch at all — it is
                // written straight into the panel's own render target from the UI stage
                // (FeedHandover). Drawing here would only paint over it.
                if (_feedDrawLogs++ == 0)
                    RttLog.Line("[RTC] panel content suppressed — the feed owns its render target.");
                return;
            }

            if (string.IsNullOrEmpty(text)) return;

            bool armed = text.Contains(TagArmed, StringComparison.OrdinalIgnoreCase);
            bool safe = armed || text.Contains(TagSafe, StringComparison.OrdinalIgnoreCase);
            if (!safe) return;

            if (_renderLogs < 1)
            {
                _renderLogs++;
                var res0 = ctx.Definition.Resolution;
                RttLog.Line($"Panel hook alive on a tagged surface ({res0.X}x{res0.Y}), armed={armed}.");
            }

            if (!armed || _rt == null) return;
            if (_disarmed)
            {
                if (_renderLogs < 3) { _renderLogs++; RttLog.Line("Stage 4 skipped — disarmed by a previous crash marker."); }
                return;
            }

            var res = ctx.Definition.Resolution;
            float side = Math.Min(res.X, res.Y) * 0.6f;
            float ox = (res.X - side) * 0.5f, oy = (res.Y - side) * 0.5f;
            var dest = new BoundingBox2(new Vector2(ox, oy), new Vector2(ox + side, oy + side));

            // ResourceHandle<T> -> ResourceHandle via the engine's own implicit
            // conversion. This handle is *generated* (backed by a RenderId), not
            // the file-backed guid handle the UI recorder normally sees — which is
            // precisely what is under test.
            ResourceHandle handle = _rt.Value.TextureHandle;

            if (!_blitRecorded)
            {
                _blitRecorded = true;
                // Invalidate after writing: the existence check now reads a cache refreshed
                // on a background thread every 500 ms, and observing our OWN write half a
                // second late would read as the write having failed.
                try { File.WriteAllText(MarkerPath, $"blit armed {DateTime.Now:O}\n"); FileWatch.Invalidate(MarkerPath); } catch { }
                RttLog.Line($"Stage 4: recording DrawImage with RT handle {handle} into panel batch.");
                RttLog.Line("Stage 4: if the game dies now, the replay rejected it (marker file left behind).");
            }

            batch.DrawImage(handle, dest, new ColorSRGB((byte)255, (byte)255, (byte)255, (byte)255), false, null, null);
        }
        catch (Exception e) { if (_errLogs++ < 5) RttLog.Error("stage4 blit", e); }
    }
}
