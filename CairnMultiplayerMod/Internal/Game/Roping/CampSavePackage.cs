namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// What a Rope Team bivouac file is allowed to keep from the simul-climb.
/// The bag is not one of those decisions. Cairn writes the inventory it sees.
/// </summary>
internal static class CampSavePackage
{
    /// <summary>
    /// A camp file has no pitons. A replica is not this climber's gear in any mode.
    /// A planted staff is not gear either. Anything else is written, and a failure stays visible.
    /// </summary>
    internal static bool WritePiton(bool campWrite, bool replica, bool plantedStaff)
        => !campWrite && !replica && !plantedStaff;

    internal static void KeepCampBelay(ref bool wasOrderedToStopSecuring, ref byte pitonsPickUpStreak)
    {
        wasOrderedToStopSecuring = false;
        pitonsPickUpStreak = 0;
    }
}
