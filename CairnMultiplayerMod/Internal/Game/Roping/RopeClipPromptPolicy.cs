namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// When the clip-in control tip is allowed. The row itself is Cairn's gameplay prompt.
/// A lobby skips Cairn's tutorials; the tip does not wait for them.
/// </summary>
internal static class RopeClipPromptPolicy
{
    /// <summary>
    /// An existing tie, a staff still holding the pitch, nobody in reach, or a captured
    /// keyboard hides it. Untie keeps the same keys and does not get a tip.
    /// </summary>
    internal static bool ShouldShow(
        bool alreadyLinked, bool hasTargetInRange, bool inputAvailable, bool pitchHeldForFollower = false)
        => !alreadyLinked && !pitchHeldForFollower && hasTargetInRange && inputAvailable;
}
