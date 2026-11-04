using System;
using System.Collections.Generic;
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
                _savedDisableInputs = mgr.disabledInputs;
                mgr.disabledInputs = true;
                // While the game ignores input events, the gamepad's own state can stop updating,
                // and the radial menu reads that state directly. Only the chat needs the flag.
                if (InputCaptureState.IsKeyboardCaptured) mgr.SetIgnoreInputEvents(true);
                _blockApplied = true;
            }
            else
            {
                mgr.SetIgnoreInputEvents(false);
                mgr.disabledInputs = _savedDisableInputs;
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

    private static readonly List<UnityEngine.InputSystem.InputAction> ViewShareActions = new();
    private static readonly List<UnityEngine.InputSystem.InputAction> CameraLookActions = new();
    // Back (B) and the other face buttons: with View/Share held in the backpack they belong to give/drop.
    private static readonly List<UnityEngine.InputSystem.InputAction> FaceButtonActions = new();
    private static bool _faceButtonsBlocked;
    private static int _nextActionScan;
    private static bool _nativeSuppressed;
    private static string _lastSuppressionLog = "";

    /// <summary>
    /// Keeps native actions from reacting to input the mod has claimed. The game handles a
    /// press before the mod's update sees it, so these actions are disabled ahead of time and
    /// every frame while wanted: View/Share on the backpack screen (it would close it), and the
    /// right stick's camera look while the radial menu is aimed with that stick.
    /// </summary>
    internal static void SuppressNativeInput(bool blockViewShare, bool blockCameraLook, bool blockFaceButtons = false)
    {
        try
        {
            if (_faceButtonsBlocked && !blockFaceButtons)
            {
                // Combo released with the backpack still open: restore the buttons' native actions.
                _faceButtonsBlocked = false;
                if (!_blockApplied) FindInputManager()?.UpdateInputContext();
                RestoreDisabled(FaceButtonActions);
            }

            if (!blockViewShare && !blockCameraLook && !blockFaceButtons)
            {
                if (!_nativeSuppressed) return;
                _nativeSuppressed = false;
                if (!_blockApplied) FindInputManager()?.UpdateInputContext();
                RestoreDisabled(ViewShareActions);
                RestoreDisabled(CameraLookActions);
                RestoreDisabled(FaceButtonActions);
                ViewShareActions.Clear();
                CameraLookActions.Clear();
                FaceButtonActions.Clear();
                _nextActionScan = 0;
                // Rebuild the maps so everything disabled here is restored exactly as the game wants it.
                if (!_blockApplied) FindInputManager()?.UpdateInputContext();
                return;
            }

            _nativeSuppressed = true;
            if (Time.frameCount >= _nextActionScan)
            {
                _nextActionScan = Time.frameCount + 30;
                ScanNativeActions();
            }

            if (blockViewShare) DisableAll(ViewShareActions);
            if (blockCameraLook) DisableAll(CameraLookActions);
            if (blockFaceButtons) { _faceButtonsBlocked = true; DisableAll(FaceButtonActions); }
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("input.native-suppression", ex);
        }
    }

    private static void DisableAll(List<UnityEngine.InputSystem.InputAction> actions)
    {
        foreach (var action in actions)
        {
            if (action == null || !action.enabled) continue;
            action.Disable();
            DisabledByUs.Add(action);
        }
    }

    // Rebuilding the game's input context leaves an action that was disabled one at a time
    // disabled, so every action this class turned off is switched back on explicitly.
    private static readonly HashSet<UnityEngine.InputSystem.InputAction> DisabledByUs = new();

    private static void RestoreDisabled(List<UnityEngine.InputSystem.InputAction> actions)
    {
        foreach (var action in actions)
            if (action != null && DisabledByUs.Remove(action) && !action.enabled) action.Enable();
    }

    private static void ScanNativeActions()
    {
        var enabled = new Il2CppSystem.Collections.Generic.List<UnityEngine.InputSystem.InputAction>();
        UnityEngine.InputSystem.InputSystem.ListEnabledActions(enabled);
        for (var i = 0; i < enabled.Count; i++)
        {
            var action = enabled[i];
            if (action == null) continue;
            var map = action.actionMap?.name ?? "";
            if (map.Contains("Camera") && action.name.StartsWith("rs", StringComparison.OrdinalIgnoreCase)
                && !CameraLookActions.Contains(action))
                CameraLookActions.Add(action);
            if (BindsGamepad(action, "/select") && !ViewShareActions.Contains(action))
                ViewShareActions.Add(action);
            if ((BindsGamepad(action, "/buttonEast") || BindsGamepad(action, "/buttonWest") || BindsGamepad(action, "/circle") || BindsGamepad(action, "/square"))
                && !FaceButtonActions.Contains(action))
                FaceButtonActions.Add(action);
        }

        var summary = $"{ViewShareActions.Count}/{CameraLookActions.Count}/{FaceButtonActions.Count}";
        if (summary == _lastSuppressionLog) return;
        _lastSuppressionLog = summary;
        ModLog.Info("[Input] suppressing View/Share actions: "
                    + string.Join(", ", ViewShareActions.ConvertAll(a => $"{a.actionMap?.name}/{a.name}"))
                    + " | camera look: "
                    + string.Join(", ", CameraLookActions.ConvertAll(a => $"{a.actionMap?.name}/{a.name}"))
                    + " | face buttons: "
                    + string.Join(", ", FaceButtonActions.ConvertAll(a => $"{a.actionMap?.name}/{a.name}")));
    }

    private static bool BindsGamepad(UnityEngine.InputSystem.InputAction action, string suffix)
    {
        var bindings = action.bindings;
        for (var i = 0; i < bindings.Count; i++)
        {
            var path = bindings[i].effectivePath;
            if (path != null && path.Contains("Gamepad") && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool _menuMapsBlocked;
    private static bool _menuBlockDiagLogged;

    /// <summary>
    /// Both navigation layers must be disabled: either EventSystem or Cairn's action maps can
    /// otherwise move the native menu behind the multiplayer panel.
    /// </summary>
    public static void BlockMainMenuActionMaps(bool allowOverlayNavigation = false)
    {
        try
        {
            var es = EventSystem.current;
            if (es != null) es.sendNavigationEvents = allowOverlayNavigation;

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
                mgr.disabledInputs = false;
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
