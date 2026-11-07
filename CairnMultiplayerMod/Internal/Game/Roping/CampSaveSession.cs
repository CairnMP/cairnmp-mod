using System;
using CairnMultiplayerMod.Internal.Game.Bivouac;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Whether this process is in a Rope Team lobby or climb, and whether the climber
/// is at camp. The mode enum alone is not enough: it defaults to Rope Team, which
/// is also the answer for solo play.
/// </summary>
internal static class CampSaveSession
{
    private static Func<bool> _ropeTeamSession = () => false;

    internal static void Bind(Func<bool> ropeTeamSession)
        => _ropeTeamSession = ropeTeamSession ?? throw new ArgumentNullException(nameof(ropeTeamSession));

    internal static bool RopeTeamSession => _ropeTeamSession();

    internal static bool MayWrite()
        => CampSavePolicy.MayWrite(RopeTeamSession, BivouacSyncGate.AtCamp);

    internal static bool MayLoad(bool fileIsInBivouac)
        => CampSavePolicy.MayLoad(RopeTeamSession, fileIsInBivouac);

    /// <summary>
    /// True from the start of a Rope Team bivouac <c>WriteToSavegame</c> until its
    /// callback. The fill can finish after the method returns.
    /// </summary>
    internal static bool WritingCampFile { get; private set; }

    internal static void BeginCampFile()
        => WritingCampFile = CampSavePolicy.IsCampFile(RopeTeamSession, BivouacSyncGate.AtCamp);

    internal static void EndCampFile() => WritingCampFile = false;
}
