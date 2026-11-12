namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Cairn decides when a save or a load may run. Rope Team does not add a second
/// answer. While a pitch is live, the snapshot of that save sets the pitch aside
/// and, when the climb continues, puts it back. A camp rest has already made the
/// world this climber's own, so that write is left alone. The partner is never told.
/// </summary>
internal static class BivouacSavePolicy
{
    internal readonly struct SaveSnapshotPlan
    {
        internal bool SetAside { get; }
        internal bool Restore { get; }

        /// <summary>A snapshot never tells the partner the rope was dropped.</summary>
        internal bool TellsPartner => false;

        internal SaveSnapshotPlan(bool setAside, bool restore)
        {
            SetAside = setAside;
            Restore = restore;
        }
    }

    /// <summary>
    /// A live Rope Team pitch is set aside for the write. The camp rest already
    /// cleared that pitch, so it is not set aside again. Leaving the game does not
    /// need the pitch put back. A failed write still does.
    /// </summary>
    internal static SaveSnapshotPlan ForWrite(bool ropeTeam, bool pitchLive, bool alreadyClean, bool leaving)
    {
        var setAside = ropeTeam && pitchLive && !alreadyClean;
        return new SaveSnapshotPlan(setAside, setAside && !leaving);
    }

    internal static bool Restores(SaveSnapshotPlan plan, bool failed)
        => plan.SetAside && (plan.Restore || failed);

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
