namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team may write the leave-camp autosave only, and may load that file.
/// The autosave is the one Cairn writes on the way out: the bivouac flag is
/// already clear. A quick save and a death segment are refused for the whole
/// run, including at camp. Solo and the other modes are not asked. The file
/// itself is never rewritten.
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

    /// <summary>A quick save is a second file the menu prefers over the leave-camp autosave.</summary>
    internal static bool MayWriteQuick(bool ropeTeamSession) => !ropeTeamSession;

    /// <summary>A death segment rewrites the slot from the wall. The leave-camp autosave carries its own path.</summary>
    internal static bool MayWriteDeath(bool ropeTeamSession) => !ropeTeamSession;

    internal static bool MayWriteAutosave(bool ropeTeamSession, bool atCamp, bool restComplete, bool quick)
        => quick ? MayWriteQuick(ropeTeamSession) : MayWrite(ropeTeamSession, atCamp, restComplete);

    internal static bool MayLoad(bool ropeTeamSession, bool fileIsInBivouac, float lastBivouacAge, bool isQuickSave = false)
        => !ropeTeamSession || (!isQuickSave && (fileIsInBivouac || IsCairnExitWrite(lastBivouacAge)));

    /// <summary>
    /// The save-select card for a file this run would refuse. An empty slot is a
    /// new game, so it stays quiet. Solo and the other modes are not asked.
    /// </summary>
    internal const string TentOnlyNotice = "Only saves in tent allowed";

    internal static bool ShowsTentOnlyNotice(
        bool ropeTeamSaveSelect, bool hasSave, float lastBivouacAge,
        bool isQuickSave = false, bool fileIsInBivouac = false)
        => ropeTeamSaveSelect && hasSave
           && !MayLoad(true, fileIsInBivouac, lastBivouacAge, isQuickSave);

    /// <summary>
    /// Disconnect on the wall ends the live session and would otherwise look like solo.
    /// The run stays marked until the main menu, with no lobby, handshake, or story handoff.
    /// </summary>
    internal static bool RunRemainsMarked(bool marked, bool liveSession, bool backAtMenu)
        => liveSession || (marked && !backAtMenu);

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
