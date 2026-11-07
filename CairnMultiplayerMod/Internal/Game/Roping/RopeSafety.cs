namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>What the rope does on this frame when the session may no longer be safe to climb.</summary>
internal enum RopeSafetyAction
{
    /// <summary>Alive and in the world. The rope tick runs.</summary>
    Continue = 0,

    /// <summary>Keep the logical link, and drop only the native anchors. The grace before a
    /// death is decided, a load, and a teardown that already ran.</summary>
    ReleaseAnchors = 1,

    /// <summary>A held death. Cut the link once, the way the climber pressed the untie.</summary>
    UnclipOnce = 2,

    /// <summary>The death played out, or the session is gone. The hard teardown runs once.</summary>
    TearDown = 3,
}

/// <summary>
/// Decides the rope's reaction to a death without touching the pawn.
///
/// A held death is an unclip. The two seconds before the game has said so leave the link
/// alone. A death that plays out, a disconnect, the game-over menu, and the main menu use
/// the hard teardown.
/// </summary>
internal static class RopeSafety
{
    internal static RopeSafetyAction Decide(
        bool connected,
        bool inGame,
        bool dead,
        bool holdingDeath,
        bool withinGrace,
        bool gameOver,
        bool atMainMenu,
        bool alreadyUnclipped,
        bool alreadyTornDown)
    {
        var downed = dead && (holdingDeath || withinGrace);
        var hardUnsafe = !connected || (dead && !downed) || gameOver || atMainMenu;
        if (hardUnsafe)
            return alreadyTornDown ? RopeSafetyAction.ReleaseAnchors : RopeSafetyAction.TearDown;

        // The hold is the decision. It can arrive inside the grace window.
        if (dead && holdingDeath && !alreadyUnclipped)
            return RopeSafetyAction.UnclipOnce;

        if (downed || !inGame)
            return RopeSafetyAction.ReleaseAnchors;

        return RopeSafetyAction.Continue;
    }
}
