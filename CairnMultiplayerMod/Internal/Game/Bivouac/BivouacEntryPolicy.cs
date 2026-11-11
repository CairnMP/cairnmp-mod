using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Game.Bivouac;

/// <summary>
/// Rope Team may start a bivouac visit only when every other climber still outside
/// is at this same camp. Solo and the other modes are not asked. A partner who has
/// already gone in, or who has left the session, is not waiting outside.
/// </summary>
internal static class BivouacEntryPolicy
{
    /// <summary>A pose older than the location and voice windows is not where they are now.</summary>
    internal const double PoseMaxAgeSeconds = 2;

    internal static bool MayEnter(bool ropeTeamSession, int othersStillOut, int othersAtThisCamp)
        => !ropeTeamSession || othersAtThisCamp >= othersStillOut;

    /// <summary>Already inside does not hold the door. Everyone else in the session does.</summary>
    internal static bool IsStillOut(PlayerState state) => state != PlayerState.Bivouac;

    internal static bool IsAtThisCamp(PlayerState state, bool poseFresh, bool sameScene, bool inReach)
        => state == PlayerState.InGame && poseFresh && sameScene && inReach;

    internal static bool PoseIsFresh(double lastUpdateSeconds, double nowSeconds)
        => double.IsFinite(lastUpdateSeconds) && double.IsFinite(nowSeconds)
           && lastUpdateSeconds > 0 && nowSeconds >= lastUpdateSeconds
           && nowSeconds - lastUpdateSeconds <= PoseMaxAgeSeconds;
}
