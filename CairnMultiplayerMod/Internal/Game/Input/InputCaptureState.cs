namespace CairnMultiplayerMod.Internal.Game.Input;

/// <summary>Tracks whether a mod-owned overlay currently consumes keyboard input.</summary>
internal static class InputCaptureState
{
    internal static bool IsKeyboardCaptured { get; set; }
}
