namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// When the cut-rope control tip is allowed. The row is the same gameplay prompt as clip-in.
/// The leader's grip is emptying because the follower hangs, so the tip stays up for that
/// whole drain. The follower does not get it.
/// </summary>
internal static class RopeCutPromptPolicy
{
    /// <summary>
    /// Cutting is the untie press. A captured keyboard would ignore it, so the tip stays hidden.
    /// </summary>
    internal static bool ShouldShow(bool gripDraining, bool inputAvailable)
        => gripDraining && inputAvailable;
}
