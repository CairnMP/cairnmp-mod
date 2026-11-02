using System;

namespace CairnMultiplayerMod.Internal.Game;

internal enum ControllerShortcut
{
    TogglePanel,
    ToggleRope,
    ToggleNames,
    PickupSharedItem,
    PreviousSharedItem,
    NextSharedItem,
    GiveSelectedItem,
    DropSelectedItem,
    PlacePing,
    PushToTalk,
    Panic,
}

internal enum ControllerButton
{
    South,
    East,
    West,
    North,
    LeftShoulder,
    RightShoulder,
    DpadUp,
    DpadLeft,
    DpadRight,
    Menu,
    RightStick,
}

/// <summary>Platform-neutral CairnMP controller layout, shared by runtime input and tests.</summary>
internal static class ControllerShortcutBindings
{
    internal const string ModifierDisplayName = "View/Share";

    internal static ControllerButton ButtonFor(ControllerShortcut shortcut) => shortcut switch
    {
        ControllerShortcut.TogglePanel => ControllerButton.Menu,
        ControllerShortcut.ToggleRope => ControllerButton.North,
        ControllerShortcut.ToggleNames => ControllerButton.DpadUp,
        ControllerShortcut.PickupSharedItem => ControllerButton.South,
        ControllerShortcut.PreviousSharedItem => ControllerButton.DpadLeft,
        ControllerShortcut.NextSharedItem => ControllerButton.DpadRight,
        ControllerShortcut.GiveSelectedItem => ControllerButton.West,
        ControllerShortcut.DropSelectedItem => ControllerButton.East,
        ControllerShortcut.PlacePing => ControllerButton.RightShoulder,
        ControllerShortcut.PushToTalk => ControllerButton.LeftShoulder,
        ControllerShortcut.Panic => ControllerButton.RightStick,
        _ => throw new ArgumentOutOfRangeException(nameof(shortcut), shortcut, null),
    };

    internal static string DisplayNameFor(ControllerButton button) => button switch
    {
        ControllerButton.South => "A/Cross",
        ControllerButton.East => "B/Circle",
        ControllerButton.West => "X/Square",
        ControllerButton.North => "Y/Triangle",
        ControllerButton.LeftShoulder => "LB/L1",
        ControllerButton.RightShoulder => "RB/R1",
        ControllerButton.DpadUp => "D-pad Up",
        ControllerButton.DpadLeft => "D-pad Left",
        ControllerButton.DpadRight => "D-pad Right",
        ControllerButton.Menu => "Menu/Options",
        ControllerButton.RightStick => "Right stick click",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
    };

    internal static string DescriptionFor(ControllerShortcut shortcut) => shortcut switch
    {
        ControllerShortcut.TogglePanel => "Multiplayer panel",
        ControllerShortcut.ToggleRope => "Attach or detach rope",
        ControllerShortcut.ToggleNames => "Show or hide names",
        ControllerShortcut.PickupSharedItem => "Pick up shared item",
        ControllerShortcut.PreviousSharedItem => "Previous shared item",
        ControllerShortcut.NextSharedItem => "Next shared item",
        ControllerShortcut.GiveSelectedItem => "Give item",
        ControllerShortcut.DropSelectedItem => "Drop item",
        ControllerShortcut.PlacePing => "Ping",
        ControllerShortcut.PushToTalk => "Push to talk",
        ControllerShortcut.Panic => "Release input",
        _ => throw new ArgumentOutOfRangeException(nameof(shortcut), shortcut, null),
    };

    /// <summary>
    /// The shortcuts chosen from the radial menu, clockwise from the top. The others stay as
    /// View/Share + button combos: they are held or repeated, which a menu does badly.
    /// </summary>
    internal static readonly ControllerShortcut[] RadialItems =
    {
        ControllerShortcut.TogglePanel,
        ControllerShortcut.ToggleRope,
        ControllerShortcut.PlacePing,
        ControllerShortcut.ToggleNames,
        ControllerShortcut.PickupSharedItem,
        ControllerShortcut.GiveSelectedItem,
        ControllerShortcut.DropSelectedItem,
    };

    internal static bool IsRadialItem(ControllerShortcut shortcut) => Array.IndexOf(RadialItems, shortcut) >= 0;

    /// <summary>The short text shown when a shortcut fires.</summary>
    internal static string FeedbackFor(ControllerShortcut shortcut)
        => IsRadialItem(shortcut) ? DescriptionFor(shortcut) : LabelFor(shortcut);

    /// <summary>On-screen text for a pressed shortcut, e.g. "View/Share + Y/Triangle: Attach or detach rope".</summary>
    internal static string LabelFor(ControllerShortcut shortcut)
        => $"{ModifierDisplayName} + {DisplayNameFor(ButtonFor(shortcut))}: {DescriptionFor(shortcut)}";
}
