using System;
using CairnMultiplayerMod.Internal.Game.Bivouac;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Whether this process is in a Rope Team lobby or climb, and whether this camp
/// visit has been rested. The mode enum alone is not enough: it defaults to Rope
/// Team, which is also the answer for solo play.
/// </summary>
internal static class BivouacSaveSession
{
    private static Func<bool> _liveSession = () => false;

    internal static void Bind(Func<bool> liveSession)
    {
        _liveSession = liveSession ?? throw new ArgumentNullException(nameof(liveSession));
    }

    /// <summary>The lobby, handshake, or story handoff. The bivouac door uses this.</summary>
    internal static bool RopeTeamSession => _liveSession();

    /// <summary>Set once the visit's rest has finished. Cleared on the way out of camp.</summary>
    internal static bool RestComplete { get; private set; }

    internal static void MarkRestComplete() => RestComplete = true;

    internal static void ClearRest() => RestComplete = false;

    /// <summary>The world Cairn is about to write is already this climber's own.</summary>
    internal static bool AlreadyClean => BivouacSyncGate.AtCamp && RestComplete;
}
