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

    /// <summary>True when the gamepad was the last device used, so on-screen hints show its buttons.</summary>
    internal static bool UsingController { get; private set; }

    /// <summary>True while the radial menu owns the hold, so the game must not also act on the right stick.</summary>
    internal static bool IsRadialHeld { get; private set; }

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
        TrackActiveDevice(gamepad);
        var held = gamepad?.selectButton.isPressed == true;
        _shortcutsEnabled = held && _modifierSeenHeld;
        _modifierSeenHeld = held;
        IsModifierHeld = held;

        // A combo that stays a button shortcut takes over the hold, so the menu steps aside.
        var comboUsed = IsHeld(ControllerShortcut.PushToTalk)
                        || WasPressed(ControllerShortcut.PreviousSharedItem)
                        || WasPressed(ControllerShortcut.NextSharedItem)
                        || WasPressed(ControllerShortcut.GiveSelectedItem)
                        || WasPressed(ControllerShortcut.DropSelectedItem)
                        || WasPressed(ControllerShortcut.Panic);
        var stick = gamepad != null ? gamepad.rightStick.ReadValue() : default;
        var menuHeld = held && radialMenuAvailable && ControllerPreferences.UseRadialMenu
                       && !InputCaptureState.IsKeyboardCaptured;
        IsRadialHeld = menuHeld;
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

    private static void TrackActiveDevice(Gamepad gamepad)
    {
        if (gamepad != null && (gamepad.leftStick.ReadValue().sqrMagnitude > .09f
                                || gamepad.rightStick.ReadValue().sqrMagnitude > .09f
                                || gamepad.allControls.Count > 0 && AnyGamepadButton(gamepad)))
            UsingController = true;
        else if (Keyboard.current?.anyKey.isPressed == true
                 || Mouse.current != null && (Mouse.current.leftButton.isPressed
                                               || Mouse.current.delta.ReadValue().sqrMagnitude > 4f
                                               || Mouse.current.scroll.ReadValue().sqrMagnitude > 0f))
            UsingController = false;
    }

    private static bool AnyGamepadButton(Gamepad g)
        => g.buttonSouth.isPressed || g.buttonEast.isPressed || g.buttonWest.isPressed || g.buttonNorth.isPressed
           || g.leftShoulder.isPressed || g.rightShoulder.isPressed || g.selectButton.isPressed
           || g.startButton.isPressed || g.dpad.up.isPressed || g.dpad.down.isPressed
           || g.dpad.left.isPressed || g.dpad.right.isPressed
           || g.leftTrigger.isPressed || g.rightTrigger.isPressed;

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
