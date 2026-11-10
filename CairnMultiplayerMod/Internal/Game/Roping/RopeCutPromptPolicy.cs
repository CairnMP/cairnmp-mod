namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// When the cut-rope control tip is allowed. The row is the same gameplay prompt as clip-in.
/// The follower has to be hanging, and the leader has to be on Cairn's critical-grip clock
/// with at most a second left before the fall. Draining alone, and the start of that
/// clock, are too early. The follower does not get it.
/// </summary>
internal static class RopeCutPromptPolicy
{
    /// <summary>Show the tip when this much time, or less, is left before Cairn drops the leader.</summary>
    internal const float LeadSeconds = 1f;

    /// <summary>
    /// Cutting is the untie press. A captured keyboard would ignore it, so the tip stays hidden.
    /// A window shorter than <see cref="LeadSeconds"/> shows for the whole window.
    /// </summary>
    internal static bool ShouldShow(
        bool gripDraining, bool criticalGrip, float secondsRemaining, bool inputAvailable)
        => gripDraining && criticalGrip && inputAvailable && secondsRemaining <= LeadSeconds;
}
