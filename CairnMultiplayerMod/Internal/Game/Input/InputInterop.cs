using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CairnMultiplayerMod.Internal.Game.Input;

/// <summary>Reconciles Cairn's gameplay/menu input blocking (used to freeze gameplay input
/// while the chat overlay is open).</summary>
internal static unsafe class InputInterop
{
    private static MonoBehaviour _inputManagerCached;
    private static int _lastInputManagerSearchFrame;

    /// <summary>Finds the native InputManager, cached (shared with the bivouac diagnostics).</summary>
    internal static Il2Cpp.InputManager FindInputManager()
    {
        var comp = GameInterop.FindMonoBehaviourByName("InputManager", ref _inputManagerCached,
            ref _lastInputManagerSearchFrame);
        return comp?.TryCast<Il2Cpp.InputManager>();
    }

    // Value of InputManager.disableInputs saved before the freeze, restored on unfreeze.
    private static bool _savedDisableInputs;
    // ACTUAL native state applied (not the intent): flips only after a successful call
    // with a non-null InputManager. A frame where the manager can't be found leaves the
    // state unchanged -> retried the next frame.
    private static bool _blockApplied;
    private static bool _mapDiagLogged;

    /// <summary>
    /// Reconciles the gameplay input block with <paramref name="wantBlocked"/> (= chat
    /// open). Called EVERY frame from ChatController.Update: idempotent (acts only on
    /// transitions), so "chat closed" ALWAYS converges to "input restored" — even if one frame
    /// fails to resolve the InputManager, the next one retries. No more one-shot restore.
    ///
    /// Block: <c>disableInputs=true</c> + <c>SetIgnoreInputEvents(true)</c> (the climber doesn't
    /// move while typing). SYMMETRIC unblock: lowering those two flags is NOT enough
    /// to re-enable the action maps (there is no EnableAllMaps on the game side); you need
    /// <c>EnableInputs()</c> + <c>UpdateInputContext()</c> so the game rebuilds the state
    /// of the current context's maps. That was THE cause of the freeze after closing chat.
    ///
    /// The chat's IMGUI overlay keeps receiving keystrokes: it reads Unity's legacy event
    /// system, and the game never disables keyboard devices — so the chat stays closeable.
    /// </summary>
    public static void ReconcileGameplayInput(bool wantBlocked)
    {
        if (wantBlocked == _blockApplied) return; // already in the desired state

        try
        {
            var mgr = FindInputManager();
            if (mgr == null) return; // state unchanged, we retry the next frame

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
                mgr.EnableInputs();        // re-enables the action maps...
                mgr.UpdateInputContext();  // ...recomputed for the current context
                _blockApplied = false;
                LogMapStatusOnce(mgr);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Input] ReconcileGameplayInput failed: {ex.Message}");
        }
    }

    // Block state of the main menu maps (multiplayer panel open).
    private static bool _menuMapsBlocked;
    private static bool _menuBlockDiagLogged;

    /// <summary>
    /// Disables the MAIN MENU action maps (mainMenuUIMap + gameplayUIActionMap) while the
    /// multiplayer panel is open: otherwise the keys (Delete, arrows, back) navigate in the
    /// background. Idempotent, call every frame while the menu is visible (re-asserts the
    /// block if the game re-enables the maps via a context change). Our menu (mouse +
    /// TMP input) goes through the EventSystem (UI module), independent of these maps -> stays interactive.
    /// </summary>
    public static void BlockMainMenuActionMaps()
    {
        try
        {
            // 1) Cut the EventSystem's keyboard/gamepad navigation (move/submit/cancel): that's
            //    how the menu reacts in the background. The mouse (pointer) + the TMP input of
            //    our panel are NOT navigation events -> they stay active.
            var es = EventSystem.current;
            if (es != null) es.sendNavigationEvents = false;

            // 2) Also disable the menu's native action maps (belt and braces).
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

    /// <summary>Restores navigation + the menu maps when the panel closes.</summary>
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

    /// <summary>
    /// Panic failsafe (F10 key): forces a full unblock regardless of the internal state.
    /// Independent of the chat. Call it from a shortcut read on the raw keyboard device (never
    /// affected by the block), placed before any early return in OnUpdate.
    /// </summary>
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

    // Diagnostic once per session: after an unblock, confirms that the gameplay action maps
    // are indeed re-enabled. If they stay OFF, the restore is insufficient (to be
    // escalated to PushInputContext). Defensive: if iterating the il2cpp dict fails, we skip.
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
