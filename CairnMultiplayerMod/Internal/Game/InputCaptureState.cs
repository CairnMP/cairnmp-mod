namespace CairnMultiplayerMod.Internal.Game;

internal static class InputCaptureState
{
    internal static bool IsKeyboardCaptured { get; set; }
    internal static bool IsControllerShortcutCaptured { get; set; }
    internal static bool WantsGameplayBlocked
        => IsKeyboardCaptured || IsControllerShortcutCaptured;
}
