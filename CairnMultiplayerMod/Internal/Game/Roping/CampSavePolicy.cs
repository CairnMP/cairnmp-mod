namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team may write a save only while the climber is at camp, and may load
/// only a file that resumes there. Solo and the other modes are not asked.
/// </summary>
internal static class CampSavePolicy
{
    internal static bool MayWrite(bool ropeTeamSession, bool atCamp)
        => !ropeTeamSession || atCamp;

    internal static bool MayLoad(bool ropeTeamSession, bool fileIsInBivouac)
        => !ropeTeamSession || fileIsInBivouac;

    /// <summary>
    /// The indoor scene counts. A lifecycle flag that has been judged stuck does not,
    /// because that flag can stay on after the climber is back on the wall.
    /// </summary>
    internal static bool AtCamp(bool sceneIsBivouac, bool stuckFlagOverridden, bool lifecycleSaysBivouac)
    {
        if (sceneIsBivouac) return true;
        if (stuckFlagOverridden) return false;
        return lifecycleSaysBivouac;
    }
}
