namespace CairnMultiplayerMod.Framework;

/// <summary>
/// Shared input state between features. When a mod UI takes over the keyboard — the chat
/// while typing — every other feature must stop reacting to key presses, or typing a
/// message would fire their shortcuts.
///
/// Features read <see cref="KeyboardCaptured"/>; the one owning the UI sets it. It lives
/// here rather than on the chat itself so nothing has to reach across to another feature
/// to ask.
/// </summary>
internal static class FeatureInput
{
    /// <summary>True while a mod UI is consuming key presses.</summary>
    internal static bool KeyboardCaptured { get; set; }
}
