using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;

namespace Sts2McpBridge;

[ModInitializer(nameof(Initialize))]
public static class BridgeInitializer
{
    private static bool _attached;

    public static void Initialize()
    {
        try { AttachOrRetry(BridgeConfig.FromEnvironment()); }
        catch (Exception exception) { GD.PrintErr($"[Sts2McpBridge] disabled: {exception.Message}"); }
    }

    private static void AttachOrRetry(BridgeConfig config)
    {
        if (_attached) return;
        NGame? game = NGame.Instance;
        if (game is null || !game.IsInsideTree() || !game.IsNodeReady())
        {
            if (Engine.GetMainLoop() is SceneTree) Callable.From(() => AttachOrRetry(config)).CallDeferred();
            return;
        }
        if (!NGame.IsMainThread()) { Callable.From(() => AttachOrRetry(config)).CallDeferred(); return; }
        _attached = true;
        BridgeController controller = new(config);
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(game) || game.GetTree() is null)
            {
                _attached = false;
                AttachOrRetry(config);
                return;
            }
            game.GetTree().Root.AddChild(controller);
            controller.Start();
            GD.Print("[Sts2McpBridge] main-thread controller started");
        }).CallDeferred();
    }
}
