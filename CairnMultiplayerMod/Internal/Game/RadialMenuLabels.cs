namespace CairnMultiplayerMod.Internal.Game;

/// <summary>What the radial menu needs to know about the game to word its entries.</summary>
internal readonly struct RadialContext
{
    internal RadialContext(bool ropeAttached, bool namesShown)
    {
        RopeAttached = ropeAttached;
        NamesShown = namesShown;
    }

    internal bool RopeAttached { get; }
    internal bool NamesShown { get; }
}

internal static class RadialMenuLabels
{
    internal static string For(ControllerShortcut shortcut, RadialContext context) => shortcut switch
    {
        ControllerShortcut.ToggleRope => context.RopeAttached ? "Detach rope" : "Attach rope",
        ControllerShortcut.ToggleNames => context.NamesShown ? "Hide names" : "Show names",
        _ => ControllerShortcutBindings.DescriptionFor(shortcut),
    };
}
