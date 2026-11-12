namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// One hold for the save Cairn is taking, including a save that calls another save.
/// The outer call sets the pitch aside. The matching end puts it back. A nested
/// call does neither.
/// </summary>
internal static class BivouacSaveSnapshot
{
    private static int _depth;
    private static BivouacSavePolicy.SaveSnapshotPlan _plan;

    internal static bool Enter(bool ropeTeam, bool pitchLive, bool alreadyClean, bool leaving)
    {
        if (_depth++ > 0) return false;
        _plan = BivouacSavePolicy.ForWrite(ropeTeam, pitchLive, alreadyClean, leaving);
        return _plan.SetAside;
    }

    internal static bool Exit(bool failed)
    {
        if (_depth <= 0) return false;
        if (--_depth > 0) return false;
        var restore = BivouacSavePolicy.Restores(_plan, failed);
        _plan = default;
        return restore;
    }

    internal static void Reset()
    {
        _depth = 0;
        _plan = default;
    }
}
