using System;
using System.Text;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CairnMultiplayerMod.Internal.Game;

internal static class InputInterop
{
    internal static Il2Cpp.InputManager FindInputManager()
        => Il2Cpp.InputManager.Instance;

    private static bool _savedDisableInputs;
    // Track the applied state, rather than the request, so a missing singleton is retried.
    private static bool _blockApplied;
    private static bool _mapDiagLogged;

    /// <summary>
    /// Unblocking must rebuild Cairn's action-map context; clearing its two input flags alone
    /// leaves the current maps disabled.
    /// </summary>
    public static void ReconcileGameplayInput(bool wantBlocked)
    {
        if (wantBlocked == _blockApplied) return;

        try
        {
            var mgr = FindInputManager();
            if (mgr == null) return;

            if (wantBlocked)
            {
                _savedDisableInputs = mgr.disableInputs;
                mgr.disableInputs = true;
                mgr.SetIgnoreInputEvents(true);
                _blockApplied = true;
            }
            else
            {
                mgr.SetIgnoreInputEvents(false);
                mgr.disableInputs = _savedDisableInputs;
                mgr.EnableInputs();
                mgr.UpdateInputContext();
                _blockApplied = false;
                LogMapStatusOnce(mgr);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Input] ReconcileGameplayInput failed: {ex.Message}");
        }
    }

    private static bool _menuMapsBlocked;
    private static bool _menuBlockDiagLogged;

    /// <summary>
    /// Both navigation layers must be disabled: either EventSystem or Cairn's action maps can
    /// otherwise move the native menu behind the multiplayer panel.
    /// </summary>
    public static void BlockMainMenuActionMaps()
    {
        try
        {
            var es = EventSystem.current;
            if (es != null) es.sendNavigationEvents = false;

            var mgr = FindInputManager();
            if (mgr != null)
            {
                mgr.mainMenuUIMap?.Disable();
                mgr.gameplayUIActionMap?.Disable();
            }

            _menuMapsBlocked = true;

            if (!_menuBlockDiagLogged)
            {
                _menuBlockDiagLogged = true;
                ModLog.Info($"[Input] menu input block: eventSystem={(es != null)} inputManager={(mgr != null)}");
                if (mgr != null)
                {
                    try
                    {
                        var maps = mgr.GetMapsEnabledAndDisabled();
                        if (maps != null)
                        {
                            var sb = new StringBuilder();
                            foreach (var kv in maps) sb.Append(kv.Key).Append('=').Append(kv.Value ? "ON" : "off").Append("  ");
                            ModLog.Info($"[Input] action maps: {sb}");
                        }
                    }
                    catch (Exception ex2) { ModLog.Info($"[Input] map enum failed: {ex2.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Input] BlockMainMenuActionMaps failed: {ex.Message}");
        }
    }

    public static void RestoreMainMenuActionMaps()
    {
        if (!_menuMapsBlocked) return;
        try
        {
            var es = EventSystem.current;
            if (es != null) es.sendNavigationEvents = true;

            var mgr = FindInputManager();
            if (mgr != null)
            {
                mgr.EnableInputs();
                mgr.UpdateInputContext();
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Input] RestoreMainMenuActionMaps failed: {ex.Message}");
        }
        _menuMapsBlocked = false;
        _menuBlockDiagLogged = false;
    }

    /// <summary>The raw-keyboard panic path must remain independent of Cairn's blocked maps.</summary>
    public static void ForceClearBlock()
    {
        try
        {
            var mgr = FindInputManager();
            if (mgr != null)
            {
                mgr.SetIgnoreInputEvents(false);
                mgr.disableInputs = false;
                mgr.EnableInputs();
                mgr.UpdateInputContext();
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Input] ForceClearBlock failed: {ex.Message}");
        }
        _blockApplied = false;
    }

    private static void LogMapStatusOnce(Il2Cpp.InputManager mgr)
    {
        if (_mapDiagLogged) return;
        _mapDiagLogged = true;
        try
        {
            var maps = mgr.GetMapsEnabledAndDisabled();
            if (maps == null) return;
            int enabled = 0, total = 0;
            var on = new StringBuilder();
            foreach (var kv in maps)
            {
                total++;
                if (kv.Value)
                {
                    enabled++;
                    if (on.Length < 220) on.Append(kv.Key).Append(' ');
                }
            }
            ModLog.Debug($"[Input] after-unblock action maps enabled {enabled}/{total}: {on}");
        }
        catch (Exception ex)
        {
            ModLog.Debug($"[Input] map-status diag skipped: {ex.Message}");
        }
    }
}
