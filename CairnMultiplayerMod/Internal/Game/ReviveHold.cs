namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Bringing a partner back from camp. R and Square both count, and Square does not count
/// while View/Share is held, because that chord gives an item.
/// </summary>
internal static class ReviveHold
{
    internal const float Seconds = 0.5f;

    internal static bool IsDown(bool keyHeld, bool squareHeld, bool viewShareHeld)
        => keyHeld || (squareHeld && !viewShareHeld);
}
