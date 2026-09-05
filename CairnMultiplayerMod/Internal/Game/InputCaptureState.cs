namespace CairnMultiplayerMod.Internal.Game;

/// <summary>Tracks whether a mod-owned overlay currently consumes keyboard input.</summary>
internal static class InputCaptureState
{
    internal static bool IsKeyboardCaptured { get; set; }
}
