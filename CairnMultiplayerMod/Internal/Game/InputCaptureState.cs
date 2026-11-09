namespace CairnMultiplayerMod.Internal.Game;

internal static class InputCaptureState
{
    private static bool _chatCaptured;
    internal static bool IsKeyboardCaptured { get => _chatCaptured || IsBelayPanelCaptured; set => _chatCaptured = value; }
    internal static bool IsBelayPanelCaptured { get; set; }
    internal static bool IsControllerShortcutCaptured { get; set; }
    internal static bool WantsGameplayBlocked
        => IsKeyboardCaptured || IsControllerShortcutCaptured;
}
