namespace RttProbe;

public static class LogicEntry
{
    // Called by the bootstrap on every (re)load. The bridge is reached by
    // reflection rather than a compile-time reference: the logic assembly lives
    // in a collectible load context, and a hard reference would pin it.
    public static void Install()
    {
        RttLog.Line("=== logic installed ===");
        try
        {
            // Prove the unscoped-access detector can actually fire, BEFORE the scope below
            // makes every later access legitimate. This runs on the load thread with no pump
            // having claimed it, so it is genuinely unscoped — see Feeds.SelfTest for why a
            // silent detector is worthless evidence at Count == 1.
            Feeds.SelfTest();

            // SCOPED (phase C1b). Every Reset below writes per-feed state, and on plugin
            // load no pump has claimed this thread — so without a scope the very first
            // thing a reload does is trip the unscoped-access diagnostic, dozens of times,
            // before anything has gone wrong.
            //
            // Scoped to Primary rather than swept across the registry with ForEach, because
            // these methods are a MIX: FeedGate.Reset is purely per-feed, while
            // BlitProbe.Reset also clears the arm markers and CameraRender.Reset drops
            // reflection caches — process-global work that running once per feed would
            // repeat pointlessly and, for the marker files, incorrectly. C3 has to split
            // each Reset into its global and per-feed halves before this becomes ForEach.
            // At Count == 1 the two are identical, which is exactly why this is safe now
            // and is a documented C3 prerequisite rather than a hidden one.
            // Panel->feed claims belong to the previous assembly's instances. Cleared here
            // so a reload re-derives ownership from what is actually in the world, rather
            // than inheriting a map pointing at objects that no longer exist.
            FeedRouter.Reset();

            // BEFORE anything can route a panel. Feeds.Count is read by the router on the very
            // first tick, and until the config has been read it answers 1 — at which point
            // every [RTCn] panel claims feed 0. See FeedConfig.PrimeFeedCount.
            FeedConfig.PrimeFeedCount();

            using (Feeds.Enter(Feeds.Primary))
            {
                FeedGate.Reset();
                BlitProbe.Reset();
                ScenePassHook.Reset();
                CameraRender.Reset();
                CameraFeed.Reset();
                StatsPanel.Reset();
                WholeSceneRender.Reset();
                CameraCbSwap.Reset();
                FeedHandover.Reset();
                PanelBinding.Reset();
            }

            var bridge = Type.GetType("RttProbe.RttBridge, RttProbe");
            if (bridge == null)
            {
                RttLog.Line("RttBridge not found — bootstrap too old? Restart the game to adopt the new bootstrap.");
                return;
            }
            bridge.GetField("TickHook")?.SetValue(null, (Action<object>)BlitProbe.OnTick);
            bridge.GetField("PanelRenderHook")?.SetValue(null, (Action<object, object, object>)BlitProbe.OnPanelRender);
            bridge.GetField("SceneDrawHook")?.SetValue(null, (Action<object, object, int>)ScenePassHook.OnSceneDraw);
            bridge.GetField("OffscreenUiDrawHook")?.SetValue(null, (Action<object[]>)FeedHandover.OnOffscreenUiDraw);

            // For the bootstrap's log-only probes: whether an engine call fired inside our
            // nested Draw. Null-safe on an older bootstrap.
            bridge.GetField("InOurRenderHook")?.SetValue(null, (Func<bool>)(() => WholeSceneRender.InOurRender));

            // Per-body clipmap camera. Absent on an older bootstrap, which degrades to
            // "terrain always follows the player" — the behaviour before this existed.
            bridge.GetField("ClipmapCameraHook")?.SetValue(null,
                (Func<object, object, object>)WorldGrids.ChooseClipmapCamera);

            // The whole-scene route. Null on an older bootstrap, which is the expected
            // state until the game is restarted to adopt the new one — the field is
            // looked up rather than assumed so a stale bootstrap degrades to "this
            // route is off" instead of throwing and taking the other hooks with it.
            var wholeScene = bridge.GetField("WholeSceneHook");
            if (wholeScene != null)
            {
                wholeScene.SetValue(null, (Action<object, object>)WholeSceneRender.OnWholeScene);
                RttLog.Line("Whole-scene hook registered (SceneDrawSystem.Draw postfix).");

                // The start-of-frame submission position. Null on an older bootstrap, in
                // which case the postfix keeps doing the render and wholeSceneSubmitEarly
                // is simply inert — worth saying out loud, because a silently ignored flag
                // is how an A/B gets misread.
                var early = bridge.GetField("WholeSceneEarlyHook");
                if (early != null)
                {
                    early.SetValue(null, (Action<object, object>)WholeSceneRender.OnWholeSceneEarly);
                    RttLog.Line("Start-of-frame hook registered (SceneDrawSystem.Draw PREFIX) — " +
                                "set wholeSceneSubmitEarly=1 to move the render there and overlap our " +
                                "GPU work with the player's frame recording.");
                }
                else
                {
                    RttLog.Line("WholeSceneEarlyHook not on this bootstrap — restart to adopt it. " +
                                "wholeSceneSubmitEarly will have NO EFFECT until then.");
                }

                var skip = bridge.GetField("SkipStageHook");
                if (skip != null)
                {
                    skip.SetValue(null, (Func<int, bool>)WholeSceneRender.ShouldSkipStage);
                    RttLog.Line("Stage-skip hook registered — Draw sub-stages can now be suppressed " +
                                "inside our render only. This is the only lever that reaches stages the " +
                                "settings do not gate, such as acceleration-structure building.");
                }
            }
            else
            {
                RttLog.Line("WholeSceneHook not on this bootstrap — restart the game to adopt it. " +
                            "The probe-based feed is unaffected.");
            }
            RttLog.Line("Hooks registered. Scene-draw recon is read-only and runs on its own.");
            RttLog.Line("Tag a panel [RTT] for blit stages 1-3, [RTT!] to also arm the blit.");
        }
        catch (Exception e) { RttLog.Error("bridge hookup", e); }
    }
}
