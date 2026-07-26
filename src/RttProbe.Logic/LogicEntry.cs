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
            BlitProbe.Reset();
            SceneDrawRecon.Reset();
            CameraRender.Reset();
            CameraFeed.Reset();
            OwnContexts.Reset();
            DynamicExposure.Reset();
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
            bridge.GetField("SceneDrawHook")?.SetValue(null, (Action<object, object, int>)SceneDrawRecon.OnSceneDraw);
            bridge.GetField("OffscreenUiDrawHook")?.SetValue(null, (Action<object[]>)FeedHandover.OnOffscreenUiDraw);
            RttLog.Line("Hooks registered. Scene-draw recon is read-only and runs on its own.");
            RttLog.Line("Tag a panel [RTT] for blit stages 1-3, [RTT!] to also arm the blit.");
        }
        catch (Exception e) { RttLog.Error("bridge hookup", e); }
    }
}
