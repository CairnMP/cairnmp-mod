using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using CairnMultiplayerMod.Core;

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
        if (sceneName == "MainMenu")
        {
            _intercepted = false;
            _frameCounter = 0;
            _modeSelectContainer = null;
            _suspendedMenuBehaviours.Clear();
            _suspendedMenuStates.Clear();
        }
    }

    public static void OnUpdate()
    {
        if (_intercepted) return;
        _frameCounter++;
        if (_frameCounter < 3) return;

        try { InterceptStoryButton(); }
        catch (Exception ex) { Mod.Log.Error($"[MainMenuBtn] Failed: {ex}"); }
        _intercepted = true;
    }

    /// <summary>Hides the 4 Cairn menu buttons (Story/Settings/Credits/Quit).</summary>
    public static void HideModeSelect()
    {
        if (_modeSelectContainer != null) _modeSelectContainer.SetActive(false);
        SuspendMainMenuInput();
        CairnGameApi.BlockMainMenuActionMaps();   // blocks Delete/arrows/back in the background
    }

    /// <summary>Re-shows the 4 Cairn menu buttons after the panel is closed.</summary>
    public static void RestoreModeSelect()
    {
        CairnGameApi.RestoreMainMenuActionMaps();
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

    private static void InterceptStoryButton()
    {
        var container = FindModeSelectContainer();
        if (container == null) { Mod.Log.Warning("[MainMenuBtn] Container not found"); return; }

        _modeSelectContainer = container.gameObject;

        Transform storyTransform = null;
        for (int i = 0; i < container.childCount; i++)
        {
            var child = container.GetChild(i);
            if (child.name == "Story") { storyTransform = child; break; }
        }
        if (storyTransform == null) { Mod.Log.Warning("[MainMenuBtn] Story button not found"); return; }

        // Capture the game font once for use by the panel.
        var storyTMP = storyTransform.GetComponentInChildren<TextMeshProUGUI>(true);
        if (storyTMP != null && CapturedFont == null)
            CapturedFont = storyTMP.font;

        // Rename the button
        if (storyTMP != null)
            storyTMP.text = "Multiplayer";

        // Replace the click handler
        var btn = storyTransform.GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener((UnityAction)OnMultiplayerClicked);
        }

        Mod.Log.Msg("[MainMenuBtn] Story intercepted → 'Multiplayer'; opens MultiplayerPanel directly");
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
