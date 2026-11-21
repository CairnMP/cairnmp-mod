using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppCairn.UI;
using Il2CppTheGameBakers.Cairn.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Save &amp; Quit / quit: warn that Rope Team wall pitons will not be kept, then let
/// Cairn's quit continue. Gates on <see cref="MainSubmenu.OnQuitToMainMenuClick"/> and
/// <see cref="MainSubmenu.OnQuitGameClick"/>. Dismisses if the pause menu closes.
/// </summary>
internal static class RopeTeamQuitSaveWarningPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeTeamQuitSaveWarning");
    private static bool _installed;
    private static bool _allowNativeQuit;
    private static bool _warningOpen;
    private static MainSubmenu _pendingMenu;
    private static bool _pendingQuitGame;
    private static MainSubmenu _focusRestoreMenu;
    private static bool _focusRestoreQuitGame;
    private static int _focusRestoreFrames;
    private static Func<bool> _inSession = () => false;
    private static Action _showWarning = () => { };
    private static Action _dismissWarningUi = () => { };

    internal static void Bind(Func<bool> inSession, Action showWarning, Action dismissWarningUi)
    {
        _inSession = inSession ?? throw new ArgumentNullException(nameof(inSession));
        _showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
        _dismissWarningUi = dismissWarningUi ?? throw new ArgumentNullException(nameof(dismissWarningUi));
    }

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var toMenu = AccessTools.Method(typeof(MainSubmenu), nameof(MainSubmenu.OnQuitToMainMenuClick))
                         ?? throw new MissingMethodException(nameof(MainSubmenu), nameof(MainSubmenu.OnQuitToMainMenuClick));
            var quitGame = AccessTools.Method(typeof(MainSubmenu), nameof(MainSubmenu.OnQuitGameClick))
                           ?? throw new MissingMethodException(nameof(MainSubmenu), nameof(MainSubmenu.OnQuitGameClick));
            var closing = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OnClosing))
                          ?? throw new MissingMethodException(nameof(PauseMenu), nameof(PauseMenu.OnClosing));
            var openedUpdate = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.OpenedUpdate))
                               ?? throw new MissingMethodException(nameof(PauseMenu), nameof(PauseMenu.OpenedUpdate));
            Harmony.Patch(toMenu, prefix: new HarmonyMethod(typeof(RopeTeamQuitSaveWarningPatch), nameof(BeforeQuitToMainMenu)));
            Harmony.Patch(quitGame, prefix: new HarmonyMethod(typeof(RopeTeamQuitSaveWarningPatch), nameof(BeforeQuitGame)));
            Harmony.Patch(closing, postfix: new HarmonyMethod(typeof(RopeTeamQuitSaveWarningPatch), nameof(AfterPauseClosing)));
            Harmony.Patch(openedUpdate, postfix: new HarmonyMethod(typeof(RopeTeamQuitSaveWarningPatch), nameof(AfterPauseOpenedUpdate)));
            _installed = true;
            ModLog.Info("[RopeTeam] Save & Quit piton warning hook installed (MainSubmenu quit).");
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Save & Quit piton warning unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        ClearPending();
        ClearFocusRestore();
        _allowNativeQuit = false;
        _warningOpen = false;
    }

    internal static void ContinueAfterWarning()
    {
        _warningOpen = false;
        ClearFocusRestore();
        var menu = _pendingMenu;
        var quitGame = _pendingQuitGame;
        ClearPending();
        if ((Object)menu == null) return;

        _allowNativeQuit = true;
        try
        {
            if (quitGame) menu.OnQuitGameClick();
            else menu.OnQuitToMainMenuClick();
        }
        catch (Exception exception)
        {
            _allowNativeQuit = false;
            ModLog.Warning("[RopeTeam] Could not continue quit after warning: " + exception.Message);
        }
    }

    internal static void CancelWarning()
    {
        _warningOpen = false;
        var menu = _pendingMenu;
        var quitGame = _pendingQuitGame;
        ClearPending();
        _allowNativeQuit = false;
        // EventSystem alone is not enough: Cairn's bounce arrow / lastSelectedGO own focus.
        SchedulePauseMenuFocusRestore(menu, quitGame);
        RestorePauseMenuFocus(menu, quitGame);
    }

    private static void AfterPauseClosing()
    {
        if (!_warningOpen) return;
        _warningOpen = false;
        ClearPending();
        ClearFocusRestore();
        _allowNativeQuit = false;
        try { _dismissWarningUi(); }
        catch (Exception exception)
        {
            ModLog.Warning("[RopeTeam] Quit warning dismiss failed: " + exception.Message);
        }
    }

    private static void AfterPauseOpenedUpdate()
    {
        if (_focusRestoreFrames <= 0) return;
        _focusRestoreFrames--;
        var menu = _focusRestoreMenu;
        var quitGame = _focusRestoreQuitGame;
        if (_focusRestoreFrames <= 0)
            ClearFocusRestore();
        RestorePauseMenuFocus(menu, quitGame);
    }

    private static bool BeforeQuitToMainMenu(MainSubmenu __instance)
        => BeforeQuitClick(__instance, quitGame: false);

    private static bool BeforeQuitGame(MainSubmenu __instance)
        => BeforeQuitClick(__instance, quitGame: true);

    private static bool BeforeQuitClick(MainSubmenu __instance, bool quitGame)
    {
        if (_allowNativeQuit)
        {
            _allowNativeQuit = false;
            return true;
        }

        if (_warningOpen) return false;
        if (!_inSession()) return true;
        if (__instance == null) return true;
        if (!RopeInterop.HasTrackedSessionWallPitons()) return true;

        _pendingMenu = __instance;
        _pendingQuitGame = quitGame;
        _warningOpen = true;
        try
        {
            ModLog.Info("[RopeTeam] Save & Quit warning shown.");
            _showWarning();
        }
        catch (Exception exception)
        {
            ClearPending();
            _warningOpen = false;
            ModLog.Warning("[RopeTeam] Save & Quit warning UI failed: " + exception.Message);
            return true;
        }

        return false;
    }

    private static void SchedulePauseMenuFocusRestore(MainSubmenu menu, bool quitGame)
    {
        if ((Object)menu == null)
        {
            ClearFocusRestore();
            return;
        }

        _focusRestoreMenu = menu;
        _focusRestoreQuitGame = quitGame;
        // Overlay Destroy runs end-of-frame; re-assert after that so selection sticks.
        _focusRestoreFrames = 2;
    }

    private static void RestorePauseMenuFocus(MainSubmenu menu, bool quitGame)
    {
        if ((Object)menu == null) return;
        try
        {
            var target = quitGame ? menu.quitButton : menu.backToMainMenuButton;
            if ((Object)target == null) return;

            var bouncing = menu.bouncingButtons;
            if ((Object)bouncing != null)
            {
                bouncing.lastButtonSelected = target;
                bouncing.SetNextSelectionImmediate();
            }

            var pause = menu.GetComponentInParent<PauseMenu>();
            if ((Object)pause != null)
                pause.lastSelectedGO = target.gameObject;

            var eventSystem = EventSystem.current;
            if (eventSystem != null)
                eventSystem.SetSelectedGameObject(target.gameObject);

            // Selectable.Select drives the same path Cairn's bounce zone expects.
            target.Select();

            if ((Object)bouncing != null)
                bouncing.SelectFirstOrSaved();
        }
        catch (Exception exception)
        {
            ModLog.Warning("[RopeTeam] Could not restore pause-menu focus: " + exception.Message);
        }
    }

    private static void ClearPending()
    {
        _pendingMenu = null;
        _pendingQuitGame = false;
    }

    private static void ClearFocusRestore()
    {
        _focusRestoreMenu = null;
        _focusRestoreQuitGame = false;
        _focusRestoreFrames = 0;
    }
}
