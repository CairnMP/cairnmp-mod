using System.Collections.Generic;
using System.Linq;

namespace CairnMultiplayerMod.Internal.Game;

internal readonly struct GuideColumn
{
    internal GuideColumn(string title, string[] lines)
    {
        Title = title;
        Lines = lines;
    }

    internal string Title { get; }
    internal string[] Lines { get; }
}

/// <summary>Everything the CairnMP options page lists: both controller styles and the keyboard.</summary>
internal static class ShortcutGuide
{
    internal static GuideColumn[] Columns(bool radialActive, string connectKey, string disconnectKey, string talkKey)
    {
        var modifier = ControllerShortcutBindings.ModifierDisplayName;
        var alwaysOn = new[]
        {
            ControllerShortcut.GiveSelectedItem, ControllerShortcut.DropSelectedItem,
            ControllerShortcut.PreviousSharedItem, ControllerShortcut.NextSharedItem,
            ControllerShortcut.PushToTalk, ControllerShortcut.Panic,
        };

        var radial = new List<string> { $"Hold {modifier}, aim with the", "right stick, release to choose" };
        radial.AddRange(ControllerShortcutBindings.RadialItems.Select(ControllerShortcutBindings.DescriptionFor));

        var combos = new List<string> { $"Hold {modifier} and press" };
        combos.AddRange(ControllerShortcutBindings.RadialItems.Select(Combo));
        combos.Add($"Either style, with {modifier}:");
        combos.AddRange(alwaysOn.Select(Combo));

        var keyboard = new[]
        {
            $"{connectKey}: Multiplayer panel",
            $"{disconnectKey}: Disconnect",
            "L: Attach or detach rope",
            "N: Show or hide names",
            "G: Give item",
            "X: Drop item",
            "E: Pick up shared item",
            "Left click: Ping (eagle view)",
            "T: Climb trails",
            "B: Trail mark",
            "R: Bivouac recall",
            "F: Spectator view",
            "C: Next climber",
            $"{talkKey}: Push to talk",
            "Enter: Chat",
            "F10: Release input",
        };

        return new[]
        {
            new GuideColumn("Controller: radial menu" + (radialActive ? " (on)" : ""), radial.ToArray()),
            new GuideColumn("Controller: button combos" + (radialActive ? "" : " (on)"), combos.ToArray()),
            new GuideColumn("Keyboard", keyboard),
        };
    }

    private static string Combo(ControllerShortcut shortcut)
        => $"{ControllerShortcutBindings.DisplayNameFor(ControllerShortcutBindings.ButtonFor(shortcut))}: {ControllerShortcutBindings.DescriptionFor(shortcut)}";
}
