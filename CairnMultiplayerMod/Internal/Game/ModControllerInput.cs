using System;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Raw controller input for CairnMP-only shortcuts. Holding the View/Share button opens a
/// radial menu aimed with the right stick; the other shortcuts are View/Share + button combos
/// so ordinary Cairn controller actions keep their normal meaning.
/// </summary>
internal static class ModControllerInput
{
    private static readonly ControllerShortcut[] AllShortcuts = Enum.GetValues<ControllerShortcut>();

    private static bool _modifierSeenHeld;
    private static bool _shortcutsEnabled;
    private static ControllerShortcut? _radialChoice;

    internal static bool IsModifierHeld { get; private set; }

    internal static RadialMenuState RadialMenu { get; } = new(ControllerShortcutBindings.RadialItems.Length);

    /// <summary>The shortcut that fired this frame, for on-screen feedback; null if none.</summary>
    internal static ControllerShortcut? PressedThisFrame { get; private set; }

    /// <summary>
    /// The modifier's state right now, not as of the last mod frame. The game can react to the
    /// press earlier in the same frame, before <see cref="Update"/> has seen it.
    /// </summary>
    internal static bool IsModifierPressedNow => Gamepad.current?.selectButton.isPressed == true;

    /// <summary>
    /// Called once near the start of the mod frame. The one-frame arming delay means the
    /// modifier blocks Cairn before any secondary shortcut button is accepted.
    /// </summary>
    internal static void Update(bool radialMenuAvailable, float deltaSeconds)
    {
        var gamepad = Gamepad.current;
        var held = gamepad?.selectButton.isPressed == true;
        _shortcutsEnabled = held && _modifierSeenHeld;
        _modifierSeenHeld = held;
        IsModifierHeld = held;

        // A combo that stays a button shortcut takes over the hold, so the menu steps aside.
        var comboUsed = IsHeld(ControllerShortcut.PushToTalk)
                        || WasPressed(ControllerShortcut.PreviousSharedItem)
                        || WasPressed(ControllerShortcut.NextSharedItem)
                        || WasPressed(ControllerShortcut.Panic);
        var stick = gamepad != null ? gamepad.rightStick.ReadValue() : default;
        var menuHeld = held && radialMenuAvailable && ControllerPreferences.UseRadialMenu
                       && !InputCaptureState.IsKeyboardCaptured;
        var choice = RadialMenu.Update(menuHeld, stick.x, stick.y, deltaSeconds, comboUsed);
        _radialChoice = choice >= 0 ? ControllerShortcutBindings.RadialItems[choice] : null;

        PressedThisFrame = null;
        foreach (var shortcut in AllShortcuts)
        {
            if (!WasPressed(shortcut)) continue;
            PressedThisFrame = shortcut;
            break;
        }
    }

    internal static bool WasPressed(ControllerShortcut shortcut)
    {
        // Radial items fire on the frame the menu is released, when the modifier is already up.
        if (ControllerPreferences.UseRadialMenu && ControllerShortcutBindings.IsRadialItem(shortcut))
            return _radialChoice == shortcut;

        var gamepad = Gamepad.current;
        return _shortcutsEnabled && !InputCaptureState.IsKeyboardCaptured && gamepad != null
               && Resolve(gamepad, ControllerShortcutBindings.ButtonFor(shortcut))?.wasPressedThisFrame == true;
    }

    internal static bool IsHeld(ControllerShortcut shortcut)
    {
        var gamepad = Gamepad.current;
        return _shortcutsEnabled && !InputCaptureState.IsKeyboardCaptured && gamepad != null
               && Resolve(gamepad, ControllerShortcutBindings.ButtonFor(shortcut))?.isPressed == true;
    }

    private static ButtonControl Resolve(Gamepad gamepad, ControllerButton button) => button switch
    {
        ControllerButton.South => gamepad.buttonSouth,
        ControllerButton.East => gamepad.buttonEast,
        ControllerButton.West => gamepad.buttonWest,
        ControllerButton.North => gamepad.buttonNorth,
        ControllerButton.LeftShoulder => gamepad.leftShoulder,
        ControllerButton.RightShoulder => gamepad.rightShoulder,
        ControllerButton.DpadUp => gamepad.dpad.up,
        ControllerButton.DpadLeft => gamepad.dpad.left,
        ControllerButton.DpadRight => gamepad.dpad.right,
        ControllerButton.Menu => gamepad.startButton,
        ControllerButton.RightStick => gamepad.rightStickButton,
        _ => null,
    };
}
