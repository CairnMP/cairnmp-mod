using System;
using CairnMultiplayerMod.Internal.Game.Bivouac;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Whether this process is in a Rope Team lobby or climb, and whether this camp
/// visit has been rested. The mode enum alone is not enough: it defaults to Rope
/// Team, which is also the answer for solo play. The save run stays marked after
/// a disconnect on the wall, until the player is back at the main menu.
/// </summary>
internal static class BivouacSaveSession
{
    private static Func<bool> _liveSession = () => false;
    private static Func<bool> _backAtMenu = () => false;
    private static bool _runMarked;

    internal static void Bind(Func<bool> liveSession, Func<bool> backAtMenu)
    {
        _liveSession = liveSession ?? throw new ArgumentNullException(nameof(liveSession));
        _backAtMenu = backAtMenu ?? throw new ArgumentNullException(nameof(backAtMenu));
    }

    /// <summary>The lobby, handshake, or story handoff. The bivouac door uses this, not the sticky run.</summary>
    internal static bool RopeTeamSession => _liveSession();

    /// <summary>This climb, including the seconds after disconnect before the main menu.</summary>
    internal static bool SaveRun
    {
        get
        {
            Tick();
            return _runMarked;
        }
    }

    internal static void Tick()
        => _runMarked = BivouacSavePolicy.RunRemainsMarked(_runMarked, _liveSession(), _backAtMenu());

    /// <summary>Set once the visit's rest has finished. Cleared on the way out of camp.</summary>
    internal static bool RestComplete { get; private set; }

    internal static void MarkRestComplete() => RestComplete = true;

    internal static void ClearRest() => RestComplete = false;

    internal static bool MayWrite()
        => BivouacSavePolicy.MayWrite(SaveRun, BivouacSyncGate.AtCamp, RestComplete);

    internal static bool MayWriteQuick()
        => BivouacSavePolicy.MayWriteQuick(SaveRun);

    internal static bool MayWriteDeath()
        => BivouacSavePolicy.MayWriteDeath(SaveRun);

    internal static bool MayWriteAutosave(bool quick)
        => BivouacSavePolicy.MayWriteAutosave(SaveRun, BivouacSyncGate.AtCamp, RestComplete, quick);

    internal static bool MayLoad(bool fileIsInBivouac, float lastBivouacAge, bool isQuickSave = false)
        => BivouacSavePolicy.MayLoad(SaveRun, fileIsInBivouac, lastBivouacAge, isQuickSave);
}
