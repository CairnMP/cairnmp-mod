using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI;

/// <summary>
/// Intercepts the Story button in the MainMenu's ModeSelect:
///   - Changes its text to "Multiplayer"
///   - Replaces its onClick with directly opening the multiplayer panel
///   - Hides the entire Container (1) (Story/Settings/Credits/Quit) while the panel is shown
/// </summary>
public static class MainMenuMultiplayerButton
{
    private static bool _intercepted;
    private static int _frameCounter;
    private static int _notReadyWarnFrames;

    // Cached so we can re-assert the label each frame without re-scanning.
    private static TextMeshProUGUI _storyTMP;

    // Common parent of the 4 Cairn buttons; hidden while the panel is open.
    private static GameObject _modeSelectContainer;
    private static readonly List<MonoBehaviour> _suspendedMenuBehaviours = new();
    private static readonly List<bool> _suspendedMenuStates = new();

    // Panel to open on click.
    private static IMultiplayerPanel _panel;

    /// <summary>Game font captured from the Story button's TMP.</summary>
    public static TMP_FontAsset CapturedFont { get; private set; }

    /// <summary>Registers the panel to open on click. Called once from Mod.OnInitializeMelon.</summary>
    public static void Bind(IMultiplayerPanel panel)
    {
        _panel = panel;
    }

    public static void OnSceneLoaded(string sceneName)
    {
        if (SceneRoles.IsMainMenu(sceneName))
        {
            _intercepted = false;
            _frameCounter = 0;
            _notReadyWarnFrames = 0;
            _storyTMP = null;
            _modeSelectContainer = null;
            _suspendedMenuBehaviours.Clear();
            _suspendedMenuStates.Clear();
        }
    }

    public static void OnUpdate()
    {
        // The game re-localizes the button to "Story" a few frames after we swap it,
        // so keep re-asserting the label instead of intercepting just once.
        if (_intercepted)
        {
            ReassertLabel();
            return;
        }

        _frameCounter++;
        if (_frameCounter < 3) return;

        try
        {
            // Retry next frame if the menu isn't built yet, rather than giving up.
            if (InterceptStoryButton())
                _intercepted = true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[MainMenuBtn] Failed: {ex}");
            _intercepted = true;
        }
    }

    /// <summary>Re-applies the "Multiplayer" label if the game reverted it.</summary>
    private static void ReassertLabel()
    {
        try
        {
            if (_storyTMP != null && _storyTMP.text != "Multiplayer")
                _storyTMP.text = "Multiplayer";
        }
        catch { /* object destroyed mid-frame */ }
    }

    /// <summary>Hides the 4 Cairn menu buttons (Story/Settings/Credits/Quit).</summary>
    public static void HideModeSelect()
    {
        if (_modeSelectContainer != null) _modeSelectContainer.SetActive(false);
        SuspendMainMenuInput();
        InputApi.BlockMainMenuActionMaps();   // blocks Delete/arrows/back in the background
    }

    /// <summary>Re-shows the 4 Cairn menu buttons after the panel is closed.</summary>
    public static void RestoreModeSelect()
    {
        InputApi.RestoreMainMenuActionMaps();
        RestoreMainMenuInput();
        if (_modeSelectContainer != null) _modeSelectContainer.SetActive(true);
    }

    /// <summary>Re-enables only the menu controller, without re-showing the menu buttons.</summary>
    public static void RestoreMainMenuInput()
    {
        for (int i = 0; i < _suspendedMenuBehaviours.Count; i++)
        {
            var behaviour = _suspendedMenuBehaviours[i];
            if (behaviour == null) continue;
            try { behaviour.enabled = _suspendedMenuStates[i]; }
            catch (Exception ex) { Mod.Log.Warning($"[CairnMP] Failed to restore MainMenu input: {ex.Message}"); }
        }

        _suspendedMenuBehaviours.Clear();
        _suspendedMenuStates.Clear();
    }

    // ── Interception ─────────────────────────────────────────────────────

    /// <summary>Intercepts the Story button. Returns false if the menu isn't ready yet.</summary>
    private static bool InterceptStoryButton()
    {
        var container = FindModeSelectContainer();
        if (container == null) { WarnNotReady("Container not found"); return false; }

        _modeSelectContainer = container.gameObject;

        Transform storyTransform = null;
        for (int i = 0; i < container.childCount; i++)
        {
            var child = container.GetChild(i);
            if (child.name == "Story") { storyTransform = child; break; }
        }
        if (storyTransform == null) { WarnNotReady("Story button not found"); return false; }

        // Capture the game font once for use by the panel.
        var storyTMP = storyTransform.GetComponentInChildren<TextMeshProUGUI>(true);
        if (storyTMP == null) { WarnNotReady("Story TMP not found"); return false; }

        if (CapturedFont == null)
            CapturedFont = storyTMP.font;

        _storyTMP = storyTMP;
        storyTMP.text = "Multiplayer";

        // Replace the click handler
        var btn = storyTransform.GetComponent<Button>();
        if (btn == null) { WarnNotReady("Story Button component not found"); return false; }

        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener((UnityAction)OnMultiplayerClicked);

        Mod.Log.Msg("[MainMenuBtn] Story intercepted → 'Multiplayer'; opens MultiplayerPanel directly");
        return true;
    }

    /// <summary>Warns at most once every ~2s so retries don't spam the log.</summary>
    private static void WarnNotReady(string reason)
    {
        if (_notReadyWarnFrames > 0) { _notReadyWarnFrames--; return; }
        _notReadyWarnFrames = 120;
        Mod.Log.Warning($"[MainMenuBtn] {reason}; retrying next frame");
    }

    private static void OnMultiplayerClicked()
    {
        Mod.Log.Msg("[MainMenuBtn] Multiplayer clicked → opening panel, hiding menu");
        HideModeSelect();
        _panel?.Show();
    }

    private static void SuspendMainMenuInput()
    {
        if (_suspendedMenuBehaviours.Count > 0) return;

        var go = GameObject.Find("MainMenu");
        if (go == null) return;

        try
        {
            var behaviours = go.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Count; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue;
                if (behaviour.GetIl2CppType().Name != "MainMenu") continue;

                _suspendedMenuBehaviours.Add(behaviour);
                _suspendedMenuStates.Add(behaviour.enabled);
                behaviour.enabled = false;
                Mod.Log.Msg("[CairnMP] MainMenu input suspended while multiplayer panel is open.");
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnMP] Failed to suspend MainMenu input: {ex.Message}");
        }
    }

    private static Transform FindModeSelectContainer()
    {
        var go = GameObject.Find("MainMenu");
        if (go == null) return null;
        var canvas = go.transform.Find("Canvas");
        if (canvas == null) return null;
        var ms = canvas.Find("MODE SELECT");
        if (ms == null) return null;
        return ms.Find("Container (1)");
    }
}
