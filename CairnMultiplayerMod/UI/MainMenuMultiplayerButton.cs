using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using CairnMultiplayerMod.Core;

namespace CairnMultiplayerMod.UI;

/// <summary>
/// Intercepte le bouton Story dans le ModeSelect du MainMenu :
///   - Change son texte en "Multiplayer"
///   - Remplace son onClick par l'ouverture directe du panneau multijoueur
///   - Cache le Container (1) entier (Story/Settings/Credits/Quit) pendant l'affichage du panel
/// </summary>
public static class MainMenuMultiplayerButton
{
    private static bool _intercepted;
    private static int _frameCounter;

    // Parent commun des 4 boutons Cairn ; masqué pendant l'ouverture du panel.
    private static GameObject _modeSelectContainer;
    private static readonly List<MonoBehaviour> _suspendedMenuBehaviours = new();
    private static readonly List<bool> _suspendedMenuStates = new();

    // Panneau à ouvrir au clic.
    private static IMultiplayerPanel _panel;

    /// <summary>Police du jeu capturée depuis le TMP du bouton Story.</summary>
    public static TMP_FontAsset CapturedFont { get; private set; }

    /// <summary>Enregistre le panneau à ouvrir au clic. Appelé une fois depuis Mod.OnInitializeMelon.</summary>
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

    /// <summary>Cache les 4 boutons du menu Cairn (Story/Settings/Credits/Quit).</summary>
    public static void HideModeSelect()
    {
        if (_modeSelectContainer != null) _modeSelectContainer.SetActive(false);
        SuspendMainMenuInput();
        CairnGameApi.BlockMainMenuActionMaps();   // bloque Suppr/fleches/retour en arriere-plan
    }

    /// <summary>Réaffiche les 4 boutons du menu Cairn après fermeture du panel.</summary>
    public static void RestoreModeSelect()
    {
        CairnGameApi.RestoreMainMenuActionMaps();
        RestoreMainMenuInput();
        if (_modeSelectContainer != null) _modeSelectContainer.SetActive(true);
    }

    /// <summary>Réactive uniquement le contrôleur menu, sans réafficher les boutons du menu.</summary>
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

        // Capture la police du jeu une seule fois pour utilisation par le panel.
        var storyTMP = storyTransform.GetComponentInChildren<TextMeshProUGUI>(true);
        if (storyTMP != null && CapturedFont == null)
            CapturedFont = storyTMP.font;

        // Renomme le bouton
        if (storyTMP != null)
            storyTMP.text = "Multiplayer";

        // Remplace le handler de clic
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
