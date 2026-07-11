using System;
using System.Reflection;
using HarmonyLib;
using Il2CppCairn.UI;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Pause « amicale en multijoueur » : en solo, ouvrir le menu pause (ESC) gele tout
/// (Time.timeScale = 0). En multi, ce gel local ferait decrocher le joueur du monde
/// partage — l'horloge jour/nuit (host-autoritaire) se fige pour tous, les avatars
/// distants ne sont plus mis a jour, etc. On neutralise donc UNIQUEMENT le gel du menu
/// pause pendant une session multijoueur : le monde continue de tourner pour tout le
/// monde, tandis que le grimpeur local reste fige sur place (le contexte d'input du menu
/// coupe deja le gameplay -> aucune action ne lui parvient).
///
/// On cible precisement <see cref="PauseMenu"/> (ESC), distinct des autres pauses
/// legitimes (cutscene, dialogue, chargement) qui, elles, doivent continuer a geler.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static readonly HarmonyLib.Harmony MpPauseHarmony = new("CairnMultiplayerMod.MultiplayerPausePatch");
    private static bool _mpPausePatchInstalled;
    private static bool _mpPausePatchFailed;

    // Vrai tant que le menu pause natif (ESC) est ouvert. Mis a jour par les postfixes
    // PauseMenu.OnOpened / OnClosed.
    private static bool _pauseMenuOpen;

    // Trace one-shot : confirme la 1re fois que l'override de timeScale s'applique reellement.
    private static bool _pauseOverrideLoggedOnce;

    /// <summary>
    /// Faut-il maintenir le monde en marche malgre la pause ? Vrai si le menu pause ESC est
    /// ouvert ET qu'on est connecte en multijoueur. NB : on ne teste PAS LocalState==InGame,
    /// car a l'ouverture du menu pause GlobalGameManager passe en Menu -> LocalState devient
    /// InMenu. Le flag _pauseMenuOpen (mis par PauseMenu.OnOpened) garantit deja qu'il s'agit
    /// du menu pause en jeu, pas du menu titre. Hors-ligne, on ne touche a rien.
    /// </summary>
    private static bool ShouldKeepWorldRunningWhilePaused()
    {
        if (!_pauseMenuOpen) return false;
        var mod = Mod.Instance;
        return mod?.Network != null && mod.Network.IsConnected;
    }

    public static void InstallMultiplayerPausePatch()
    {
        if (_mpPausePatchInstalled || _mpPausePatchFailed) return;

        try
        {
            var onOpened   = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OnOpened));
            var onClosed   = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OnClosed));
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
                Mod.Log.Warning("[CairnGameApi] Multiplayer pause patch methods were not found");
                return;
            }

            MpPauseHarmony.Patch(onOpened, postfix: new HarmonyMethod(openedPostfix));
            MpPauseHarmony.Patch(onClosed, postfix: new HarmonyMethod(closedPostfix));
            MpPauseHarmony.Patch(timeUpdate, postfix: new HarmonyMethod(timePostfix));

            _mpPausePatchInstalled = true;
            Mod.Log.Msg("[CairnGameApi] Multiplayer pause patch installed (pause menu no longer freezes the shared world)");
        }
        catch (Exception ex)
        {
            _mpPausePatchFailed = true;
            Mod.Log.Warning($"[CairnGameApi] Failed to install multiplayer pause patch: {ex.Message}");
        }
    }

    private static class MultiplayerPausePatches
    {
        // Le menu pause vient de s'ouvrir.
        internal static void PauseMenuOnOpenedPostfix()
        {
            _pauseMenuOpen = true;
            _pauseOverrideLoggedOnce = false;
            Mod.Log.Msg("[Pause] Pause menu opened" +
                (ShouldKeepWorldRunningWhilePaused() ? " — keeping shared world running (MP session)" : " (solo: normal pause)"));
        }

        // Le menu pause vient de se fermer : le jeu reprend son timeScale normal tout seul
        // (plus aucune requete de pause), rien a restaurer de notre cote.
        internal static void PauseMenuOnClosedPostfix()
        {
            _pauseMenuOpen = false;
            Mod.Log.Msg("[Pause] Pause menu closed");
        }

        // Postfix sur TimeManager.Update : apres que le jeu a applique son timeScale (0 a
        // cause de la pause-menu), on le force a 1 tant qu'on est en session MP. Idempotent
        // et borne au seul cas pause-menu + MP -> aucun effet en solo ni sur les cutscenes.
        internal static void TimeManagerUpdatePostfix()
        {
            if (!ShouldKeepWorldRunningWhilePaused()) return;
            if (Time.timeScale != 1f)
            {
                Time.timeScale = 1f;
                if (!_pauseOverrideLoggedOnce)
                {
                    _pauseOverrideLoggedOnce = true;
                    Mod.Log.Msg("[Pause] Forced timeScale back to 1 (shared world keeps running while paused)");
                }
            }
        }
    }
}
