using System;
using CairnMultiplayerMod.Internal.Game.Bivouac;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Whether this process is in a Rope Team lobby or climb, and whether this camp
/// visit has been rested. The mode enum alone is not enough: it defaults to Rope
/// Team, which is also the answer for solo play.
/// </summary>
internal static class BivouacSaveSession
{
    private static Func<bool> _ropeTeamSession = () => false;

    internal static void Bind(Func<bool> ropeTeamSession)
        => _ropeTeamSession = ropeTeamSession ?? throw new ArgumentNullException(nameof(ropeTeamSession));

    internal static bool RopeTeamSession => _ropeTeamSession();

    /// <summary>Set once the visit's rest has finished. Cleared on the way out of camp.</summary>
    internal static bool RestComplete { get; private set; }

    internal static void MarkRestComplete() => RestComplete = true;

    internal static void ClearRest() => RestComplete = false;

    internal static bool MayWrite()
        => BivouacSavePolicy.MayWrite(RopeTeamSession, BivouacSyncGate.AtCamp, RestComplete);

    internal static bool MayLoad(bool fileIsInBivouac, float lastBivouacAge)
        => BivouacSavePolicy.MayLoad(RopeTeamSession, fileIsInBivouac, lastBivouacAge);
}
