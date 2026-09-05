using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using System.Reflection;
using HarmonyLib;
using Il2CppCairn.UI;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Pause;

/// <summary>
/// "Multiplayer-friendly" pause: in solo, opening the pause menu (ESC) freezes everything
/// (Time.timeScale = 0). In multiplayer, that local freeze would disconnect the player from the
/// shared world — the day/night clock (host-authoritative) freezes for everyone, remote
/// avatars stop updating, etc. So we neutralize ONLY the pause menu's freeze
/// during a multiplayer session: the world keeps running for everyone,
/// while the local climber stays frozen in place (the menu's input context
/// already cuts gameplay -> no action reaches it).
///
/// We target precisely <see cref="PauseMenu"/> (ESC), distinct from the other legitimate
/// pauses (cutscene, dialogue, loading) which must keep freezing.
/// </summary>
internal static unsafe class MultiplayerPausePatch
{
    private static readonly HarmonyLib.Harmony MpPauseHarmony = new("CairnMultiplayerMod.MultiplayerPausePatch");
    private static bool _mpPausePatchInstalled;
    private static bool _mpPausePatchFailed;

    // True while the native pause menu (ESC) is open. Updated by the PauseMenu.OnOpened /
    // OnClosed postfixes.
    private static bool _pauseMenuOpen;

    // One-shot trace: confirms the first time the timeScale override actually applies.
    private static bool _pauseOverrideLoggedOnce;
    private static Func<bool> _isMultiplayerConnected = () => false;

    internal static void Configure(Func<bool> isMultiplayerConnected)
        => _isMultiplayerConnected = isMultiplayerConnected ?? (() => false);

    /// <summary>
    /// Should we keep the world running despite the pause? True if the ESC pause menu is
    /// open AND we're connected in multiplayer. NB: we do NOT test LocalState==InGame,
    /// because when the pause menu opens GlobalGameManager switches to Menu -> LocalState becomes
    /// InMenu. The _pauseMenuOpen flag (set by PauseMenu.OnOpened) already guarantees this is
    /// the in-game pause menu, not the title menu. Offline, we touch nothing.
    /// </summary>
    private static bool ShouldKeepWorldRunningWhilePaused()
    {
        if (!_pauseMenuOpen) return false;
        return _isMultiplayerConnected();
    }

    public static void Install()
    {
        if (_mpPausePatchInstalled || _mpPausePatchFailed) return;

        try
        {
            var onOpened = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OnOpened));
            var onClosed = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OnClosed));
            var timeUpdate = AccessTools.Method(typeof(Il2Cpp.TimeManager), nameof(Il2Cpp.TimeManager.Update));

            var openedPostfix = typeof(MultiplayerPausePatches).GetMethod(
                nameof(MultiplayerPausePatches.PauseMenuOnOpenedPostfix), BindingFlags.NonPublic | BindingFlags.Static);
            var closedPostfix = typeof(MultiplayerPausePatches).GetMethod(
                nameof(MultiplayerPausePatches.PauseMenuOnClosedPostfix), BindingFlags.NonPublic | BindingFlags.Static);
            var timePostfix = typeof(MultiplayerPausePatches).GetMethod(
                nameof(MultiplayerPausePatches.TimeManagerUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static);

            if (onOpened == null || onClosed == null || timeUpdate == null ||
                openedPostfix == null || closedPostfix == null || timePostfix == null)
            {
                _mpPausePatchFailed = true;
                ModLog.Warning("[Pause] Multiplayer pause patch methods were not found");
                return;
            }

            MpPauseHarmony.Patch(onOpened, postfix: new HarmonyMethod(openedPostfix));
            MpPauseHarmony.Patch(onClosed, postfix: new HarmonyMethod(closedPostfix));
            MpPauseHarmony.Patch(timeUpdate, postfix: new HarmonyMethod(timePostfix));

            _mpPausePatchInstalled = true;
            ModLog.Info("[Pause] Multiplayer pause patch installed (pause menu no longer freezes the shared world)");
        }
        catch (Exception ex)
        {
            _mpPausePatchFailed = true;
            ModLog.Warning($"[Pause] Failed to install multiplayer pause patch: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        try { MpPauseHarmony.UnpatchSelf(); }
        finally
        {
            _mpPausePatchInstalled = false;
            _mpPausePatchFailed = false;
            _pauseMenuOpen = false;
            _pauseOverrideLoggedOnce = false;
            _isMultiplayerConnected = () => false;
        }
    }

    private static class MultiplayerPausePatches
    {
        // The pause menu has just opened.
        internal static void PauseMenuOnOpenedPostfix()
        {
            _pauseMenuOpen = true;
            _pauseOverrideLoggedOnce = false;
            ModLog.Info("[Pause] Pause menu opened" +
                (ShouldKeepWorldRunningWhilePaused() ? " — keeping shared world running (MP session)" : " (solo: normal pause)"));
        }

        // The pause menu has just closed: the game restores its normal timeScale on its own
        // (no more pause requests), nothing for us to restore.
        internal static void PauseMenuOnClosedPostfix()
        {
            _pauseMenuOpen = false;
            ModLog.Info("[Pause] Pause menu closed");
        }

        // Postfix on TimeManager.Update: after the game applies its timeScale (0 because
        // of the pause menu), we force it back to 1 while in an MP session. Idempotent
        // and scoped to the pause-menu + MP case only -> no effect in solo or on cutscenes.
        internal static void TimeManagerUpdatePostfix()
        {
            if (!ShouldKeepWorldRunningWhilePaused()) return;
            if (Time.timeScale != 1f)
            {
                Time.timeScale = 1f;
                if (!_pauseOverrideLoggedOnce)
                {
                    _pauseOverrideLoggedOnce = true;
                    ModLog.Info("[Pause] Forced timeScale back to 1 (shared world keeps running while paused)");
                }
            }
        }
    }
}
