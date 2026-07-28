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
            FeedGate.Reset();
            BlitProbe.Reset();
            ScenePassHook.Reset();
            CameraRender.Reset();
            CameraFeed.Reset();
            WholeSceneRender.Reset();
            CameraCbSwap.Reset();
            FeedHandover.Reset();
            PanelBinding.Reset();

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

            // The whole-scene route. Null on an older bootstrap, which is the expected
            // state until the game is restarted to adopt the new one — the field is
            // looked up rather than assumed so a stale bootstrap degrades to "this
            // route is off" instead of throwing and taking the other hooks with it.
            var wholeScene = bridge.GetField("WholeSceneHook");
            if (wholeScene != null)
            {
                wholeScene.SetValue(null, (Action<object, object>)WholeSceneRender.OnWholeScene);
                RttLog.Line("Whole-scene hook registered (SceneDrawSystem.Draw postfix).");

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
