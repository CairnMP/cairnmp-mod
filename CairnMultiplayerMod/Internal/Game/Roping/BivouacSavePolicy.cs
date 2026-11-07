namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team may write a save only while the climber is at camp and the rest has
/// finished, and may load the file Cairn writes for that visit. That file is the
/// one written on the way out: the bivouac flag is already clear. Solo and the
/// other modes are not asked. The file itself is never rewritten, and Cairn's
/// own write is not refused.
/// </summary>
internal static class BivouacSavePolicy
{
    /// <summary>
    /// ExitBivouac stamps LastUsedBivouacTime, then writes in that same exit.
    /// The three files from that write were 0.024, 0.096, and 0.120 seconds old.
    /// One second still belongs to the exit. A save made after the climber has
    /// been out is older.
    /// </summary>
    internal const float ExitWriteAgeSeconds = 1f;

    internal static bool MayWrite(bool ropeTeamSession, bool atCamp, bool restComplete)
        => !ropeTeamSession || (atCamp && restComplete);

    internal static bool MayLoad(bool ropeTeamSession, bool fileIsInBivouac, float lastBivouacAge)
        => !ropeTeamSession || fileIsInBivouac || IsCairnExitWrite(lastBivouacAge);

    internal static bool IsCairnExitWrite(float lastBivouacAge)
        => lastBivouacAge >= 0f && lastBivouacAge < ExitWriteAgeSeconds;

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
