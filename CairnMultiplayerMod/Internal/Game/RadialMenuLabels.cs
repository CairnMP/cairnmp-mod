namespace CairnMultiplayerMod.Internal.Game;

/// <summary>What the radial menu needs to know about the game to word its entries.</summary>
internal readonly struct RadialContext
{
    internal RadialContext(bool ropeAttached, bool namesShown, string itemName, int itemCount)
    {
        RopeAttached = ropeAttached;
        NamesShown = namesShown;
        ItemName = itemName;
        ItemCount = itemCount;
    }

    internal bool RopeAttached { get; }
    internal bool NamesShown { get; }

    /// <summary>The backpack item Give and Drop would act on; null when there is none.</summary>
    internal string ItemName { get; }
    internal int ItemCount { get; }
}

/// <summary>One radial menu entry: what it does, and a smaller line saying what it acts on.</summary>
internal readonly struct RadialLabel
{
    internal RadialLabel(string title, string detail = "", bool dimmed = false)
    {
        Title = title;
        Detail = detail;
        Dimmed = dimmed;
    }

    internal string Title { get; }
    internal string Detail { get; }

    /// <summary>The entry would do nothing right now.</summary>
    internal bool Dimmed { get; }
}

internal static class RadialMenuLabels
{
    internal static RadialLabel For(ControllerShortcut shortcut, RadialContext context) => shortcut switch
    {
        ControllerShortcut.ToggleRope => new RadialLabel(context.RopeAttached ? "Detach rope" : "Attach rope"),
        ControllerShortcut.ToggleNames => new RadialLabel(context.NamesShown ? "Hide names" : "Show names"),
        ControllerShortcut.GiveSelectedItem => ForItem("Give item", context),
        ControllerShortcut.DropSelectedItem => ForItem("Drop item", context),
        _ => new RadialLabel(ControllerShortcutBindings.DescriptionFor(shortcut)),
    };

    private static RadialLabel ForItem(string title, RadialContext context)
    {
        if (string.IsNullOrEmpty(context.ItemName)) return new RadialLabel(title, "No shareable item", dimmed: true);
        var detail = context.ItemCount > 1 ? $"{context.ItemName} x{context.ItemCount}" : context.ItemName;
        return new RadialLabel(title, detail);
    }
}
