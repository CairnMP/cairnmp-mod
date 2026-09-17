using System;

namespace CairnMultiplayerMod.Internal.Game.World;

/// <summary>
/// Decides whether a pawn may be teleported, from the state name Cairn reports.
///
/// Only a plain grounded walk is safe. While climbing, the pawn holds live references to
/// holds and rope constraints; while falling or dead it runs its own recovery. Writing a new
/// transform under either leaves the native controller bound to geometry that is no longer
/// around it — the instability seen when bringing a player over.
///
/// Kept free of IL2CPP types so the rule itself can be tested.
/// </summary>
internal static class TeleportPolicy
{
    internal const string WalkingState = "Walking";
    internal const string DeadState = "Dead";
    internal const string InvalidState = "Invalid";

    internal static bool AllowsTeleport(string pawnState)
        => string.Equals(pawnState, WalkingState, StringComparison.Ordinal);

    internal static string DescribeRefusal(string pawnState)
    {
        if (string.IsNullOrWhiteSpace(pawnState)) return "your character is not ready";

        return pawnState switch
        {
            DeadState => "you are dead",
            InvalidState => "your character is not ready",
            _ => $"you must be walking on the ground (currently {pawnState})",
        };
    }
}
