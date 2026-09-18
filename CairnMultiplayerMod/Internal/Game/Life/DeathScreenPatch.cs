using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>
/// Keeps the death screen from opening while a downed climber can still be picked up.
///
/// Cairn does this itself: <c>GameOverMenu.GameEventManager_OnDeath</c> opens with
/// <c>if (NetplayManager.Instance.IsInRoom) return;</c>. Our session is not one of its rooms,
/// so we answer that question for it. Everything else about dying is left alone — the body
/// still falls, the stats still say dead, and letting the hold go replays the very same
/// method to end the run for real.
/// </summary>
internal static class DeathScreenPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.DeathScreen");
    private const string OnDeathMethod = "GameEventManager_OnDeath";

    private static MethodInfo _onDeath;
    private static Func<bool> _keepHolding;
    private static Action _onWentDown;
    private static bool _installed;
    private static bool _replaying;

    internal static bool IsInstalled => _installed;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            _onDeath = AccessTools.Method(typeof(GameOverMenu), OnDeathMethod)
                       ?? throw new MissingMethodException(nameof(GameOverMenu), OnDeathMethod);
            Harmony.Patch(_onDeath, prefix: new HarmonyMethod(typeof(DeathScreenPatch), nameof(BeforeDeathScreen)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            _onDeath = null;
            ModLog.Warning("[Life] Death screen hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Release();
        Harmony.UnpatchSelf();
        _onDeath = null;
        _installed = false;
    }

    /// <summary>Starts holding the death screen back. Replaces any previous hold.</summary>
    internal static void Hold(Func<bool> keepHolding, Action onWentDown)
    {
        _keepHolding = keepHolding ?? throw new ArgumentNullException(nameof(keepHolding));
        _onWentDown = onWentDown;
    }

    internal static void Release()
    {
        _keepHolding = null;
        _onWentDown = null;
    }

    internal static bool IsHolding => _keepHolding != null;

    /// <summary>
    /// Replays the death the game already decided on. The hold is bypassed for this one call
    /// only, so the menu opens exactly as it would have at the moment of death.
    /// </summary>
    internal static void OpenDeathScreenNow()
    {
        if (_onDeath == null)
        {
            ModLog.Warning("[Life] Cannot end the run: the death screen hook is not installed.");
            return;
        }

        try
        {
            var menu = GlobalUIs.Instance?.gameOverMenu;
            if (menu == null)
            {
                ModLog.Warning("[Life] Cannot end the run: the game over menu is not loaded.");
                return;
            }

            _replaying = true;
            try { _onDeath.Invoke(menu, null); }
            finally { _replaying = false; }
        }
        catch (Exception exception)
        {
            _replaying = false;
            ModLog.Warning($"[Life] Could not open the death screen: {exception.Message}");
        }
    }

    private static bool BeforeDeathScreen()
    {
        if (_replaying) return true;

        var keepHolding = _keepHolding;
        if (keepHolding == null) return true;

        bool hold;
        try { hold = keepHolding(); }
        catch (Exception exception)
        {
            // A feature that cannot answer must never trap the player in a dead body.
            ModLog.SuppressedException("life.death-screen-hold", exception);
            return true;
        }

        if (!hold) return true;

        try { _onWentDown?.Invoke(); }
        catch (Exception exception) { ModLog.SuppressedException("life.went-down", exception); }
        return false;
    }
}
