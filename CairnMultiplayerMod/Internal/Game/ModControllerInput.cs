using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Raw controller input for CairnMP-only shortcuts. Every shortcut requires the
/// View/Share button so ordinary Cairn controller actions keep their normal meaning.
/// </summary>
internal static class ModControllerInput
{
    private static bool _modifierSeenHeld;
    private static bool _shortcutsEnabled;

    internal static bool IsModifierHeld { get; private set; }

    /// <summary>
    /// Called once near the start of the mod frame. The one-frame arming delay means the
    /// modifier blocks Cairn before any secondary shortcut button is accepted.
    /// </summary>
    internal static void Update()
    {
        var held = Gamepad.current?.selectButton.isPressed == true;
        _shortcutsEnabled = held && _modifierSeenHeld;
        _modifierSeenHeld = held;
        IsModifierHeld = held;
    }

    internal static bool WasPressed(ControllerShortcut shortcut)
    {
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
        ControllerButton.DpadUp => gamepad.dpad.up,
        ControllerButton.DpadLeft => gamepad.dpad.left,
        ControllerButton.DpadRight => gamepad.dpad.right,
        ControllerButton.Menu => gamepad.startButton,
        ControllerButton.RightStick => gamepad.rightStickButton,
        _ => null,
    };
}
