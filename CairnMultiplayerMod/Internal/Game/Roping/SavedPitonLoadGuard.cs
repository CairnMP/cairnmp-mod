namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Decides which reloaded pitons are hollow, and when a world load has just finished.
///
/// Builds with the piton save guard wrote every piton on the Lifeline as an empty record. Cairn loads
/// such a record at (NaN, NaN, 0), and the physics scene fails around it until the engine
/// dies. Those pitons leave the Lifeline once after each load. Nothing here patches the
/// game: a Harmony patch on a method that takes a <c>Savegame.*</c> record by reference is
/// what emptied the records in the first place.
/// </summary>
internal static class SavedPitonLoadGuard
{
    /// <summary>A pass that could not remove every hollow piton is tried again this many times.</summary>
    internal const int MaxPasses = 5;

    /// <summary>
    /// True for a piton Cairn could not place. A real piton always has three finite
    /// coordinates, and any of them may be zero.
    /// </summary>
    internal static bool IsHollow(float x, float y, float z)
        => !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z);

    /// <summary>True on the first frame Cairn reports the loaded world as started.</summary>
    internal static bool LoadJustFinished(bool wasStarted, bool isStarted)
        => isStarted && !wasStarted;

    /// <summary>
    /// True while a pass is still owed: after a pass that left a hollow piton behind,
    /// until <see cref="MaxPasses"/> have run.
    /// </summary>
    internal static bool ShouldRetry(int failed, int passes)
        => failed > 0 && passes < MaxPasses;
}
