namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team wall pitons are session gear: they stay on the climb but are omitted
/// from Cairn's save file.
/// </summary>
internal static class RopeTeamSavePolicy
{
    /// <summary>
    /// Skip <c>Piton.WriteToSavegame</c> when a Rope Team session is active and the
    /// piton is tracked session wall gear (not a top-anchor or untracked world piton).
    /// </summary>
    internal static bool OmitFromSave(bool ropeTeamSessionActive, bool isTrackedSessionWallPiton)
        => ropeTeamSessionActive && isTrackedSessionWallPiton;
}
