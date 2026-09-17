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
}
