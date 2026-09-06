using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTMPro;
using Il2CppTheGameBakers.Cairn.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>
/// Fragile bridge to Cairn's native ModeSelect hierarchy. The safe contract lives in
/// GameApi; all scene paths, Unity objects and listener manipulation stay here.
/// </summary>
internal sealed class MainMenuAdapter : IMainMenuApi, IDisposable
{
    private readonly List<ButtonRegistration> _registrations = new();
    private readonly HashSet<string> _registeredIds = new(StringComparer.Ordinal);
    private readonly List<MonoBehaviour> _suspendedMenuBehaviours = new();
    private readonly List<bool> _suspendedMenuStates = new();

    private GameObject _modeSelectContainer;
    private GameObject _suspendedArrow;
    private bool _suspendedArrowWasActive;
    private int _frameCounter;
    private int _notReadyWarnFrames;
    private bool _disposed;

    public IGameRegistration AddButton(string id, string label, Action onClick)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MainMenuAdapter));
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A button id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A button label is required.", nameof(label));
        if (onClick == null) throw new ArgumentNullException(nameof(onClick));

        id = id.Trim();
        if (!_registeredIds.Add(id))
            throw new InvalidOperationException($"Main-menu button '{id}' is already registered.");

        var registration = new ButtonRegistration(this, id, label.Trim(), onClick);
        _registrations.Add(registration);
        _frameCounter = 0;
        return registration;
    }

    /// <summary>Invalidates scene-bound objects while retaining logical registrations.</summary>
    internal void OnSceneLoaded(string sceneName)
    {
        if (!SceneRoles.IsMainMenu(sceneName)) return;

        _modeSelectContainer = null;
        _suspendedArrow = null;
        _frameCounter = 0;
        _notReadyWarnFrames = 0;
        _suspendedMenuBehaviours.Clear();
        _suspendedMenuStates.Clear();
        for (var i = 0; i < _registrations.Count; i++)
            _registrations[i].ForgetNativeButton();
    }

    /// <summary>Retries until Cairn has built the native menu, then maintains every label.</summary>
    internal void Tick()
    {
        if (_disposed || _registrations.Count == 0) return;

        var allAttached = true;
        for (var i = 0; i < _registrations.Count; i++)
        {
            var registration = _registrations[i];
            registration.ReassertLabel();
            allAttached &= registration.HasNativeButton;
        }
        if (allAttached) return;

        _frameCounter++;
        if (_frameCounter < 3) return;

        try
        {
            TryAttachMissingButtons();
        }
        catch (Exception ex)
        {
            ModLog.Error($"[GameApi:MainMenu] Failed to attach buttons: {ex}");
        }
    }

    /// <summary>Hides Cairn's native choices while a mod-owned panel is visible.</summary>
    internal void SuspendNativeMenu()
    {
        if (_modeSelectContainer != null) _modeSelectContainer.SetActive(false);
        SuspendMainMenuInput();
        // The selection arrow is shared by the native menu and lives outside
        // ModeSelect's button container. Hide it with the choices, not behind the panel.
        if (_suspendedArrow == null && _modeSelectContainer != null)
        {
            var mode = _modeSelectContainer.GetComponentInParent<MainMenuModeSelectElement>();
            var arrow = mode?.mainMenu?.BouncingArrow;
            if (arrow != null)
            {
                _suspendedArrow = arrow.gameObject;
                _suspendedArrowWasActive = _suspendedArrow.activeSelf;
                _suspendedArrow.SetActive(false);
            }
        }
        InputInterop.BlockMainMenuActionMaps();
    }

    /// <summary>Restores Cairn's native choices after the mod-owned panel closes.</summary>
    internal void RestoreNativeMenu()
    {
        InputInterop.RestoreMainMenuActionMaps();
        RestoreMainMenuInput();
        if (_modeSelectContainer != null) _modeSelectContainer.SetActive(true);
        if (_suspendedArrow != null) _suspendedArrow.SetActive(_suspendedArrowWasActive);
        _suspendedArrow = null;
    }

    internal void RestoreMainMenuInput()
    {
        for (var i = 0; i < _suspendedMenuBehaviours.Count; i++)
        {
            var behaviour = _suspendedMenuBehaviours[i];
            if (behaviour == null) continue;
            try
            {
                behaviour.enabled = _suspendedMenuStates[i];
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[GameApi:MainMenu] Failed to restore native input: {ex.Message}");
            }
        }

        _suspendedMenuBehaviours.Clear();
        _suspendedMenuStates.Clear();
    }

    private void TryAttachMissingButtons()
    {
        var container = FindModeSelectContainer();
        if (container == null)
        {
            WarnNotReady("Container not found");
            return;
        }

        Transform template = null;
        for (var i = 0; i < container.childCount; i++)
        {
            var child = container.GetChild(i);
            if (child.name != "Story") continue;
            template = child;
            break;
        }
        if (template == null)
        {
            WarnNotReady("Story button template not found");
            return;
        }

        _modeSelectContainer = container.gameObject;
        var siblingIndex = template.GetSiblingIndex() + 1;
        for (var i = 0; i < _registrations.Count; i++)
        {
            var registration = _registrations[i];
            if (!registration.HasNativeButton)
                registration.Attach(template, container, siblingIndex);
            registration.SetSiblingIndex(siblingIndex++);
        }
    }

    private void OnButtonClicked(ButtonRegistration registration)
    {
        if (!registration.IsActive) return;

        ModLog.Info($"[GameApi:MainMenu] '{registration.Id}' clicked.");
        SuspendNativeMenu();
        try
        {
            registration.Invoke();
        }
        catch (Exception ex)
        {
            RestoreNativeMenu();
            ModLog.Error($"[GameApi:MainMenu] '{registration.Id}' callback failed: {ex}");
        }
    }

    private void Remove(ButtonRegistration registration)
    {
        if (!_registeredIds.Remove(registration.Id)) return;
        registration.DestroyNativeButton();
        _registrations.Remove(registration);
    }

    private void WarnNotReady(string reason)
    {
        if (_notReadyWarnFrames > 0)
        {
            _notReadyWarnFrames--;
            return;
        }

        _notReadyWarnFrames = 120;
        ModLog.Warning($"[GameApi:MainMenu] {reason}; retrying next frame.");
    }

    private void SuspendMainMenuInput()
    {
        if (_suspendedMenuBehaviours.Count > 0) return;

        var gameObject = GameObject.Find("MainMenu");
        if (gameObject == null) return;

        try
        {
            var behaviours = gameObject.GetComponents<MonoBehaviour>();
            for (var i = 0; i < behaviours.Count; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null || behaviour.GetIl2CppType().Name != "MainMenu") continue;

                _suspendedMenuBehaviours.Add(behaviour);
                _suspendedMenuStates.Add(behaviour.enabled);
                behaviour.enabled = false;
                ModLog.Info("[GameApi:MainMenu] Native menu input suspended.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[GameApi:MainMenu] Failed to suspend native input: {ex.Message}");
        }
    }

    private static Transform FindModeSelectContainer()
    {
        var gameObject = GameObject.Find("MainMenu");
        var canvas = gameObject?.transform.Find("Canvas");
        var modeSelect = canvas?.Find("MODE SELECT");
        return modeSelect?.Find("Container (1)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        RestoreNativeMenu();
        for (var i = _registrations.Count - 1; i >= 0; i--)
        {
            _registrations[i].DestroyNativeButton();
            _registrations[i].Deactivate();
        }
        _registrations.Clear();
        _registeredIds.Clear();
        _modeSelectContainer = null;
        _disposed = true;
    }

    private sealed class ButtonRegistration : IGameRegistration
    {
        private MainMenuAdapter _owner;
        private readonly Action _onClick;
        private GameObject _nativeButton;
        private TextMeshProUGUI _nativeLabel;
        private MainMenuModeSelectElement _nativeMenu;
        private Button _button;

        internal ButtonRegistration(MainMenuAdapter owner, string id, string label, Action onClick)
        {
            _owner = owner;
            Id = id;
            Label = label;
            _onClick = onClick;
        }

        public string Id { get; }
        internal string Label { get; }
        public bool IsActive => _owner != null;
        internal bool HasNativeButton => _nativeButton != null;

        internal void Attach(Transform template, Transform parent, int siblingIndex)
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            clone.name = $"CairnMP.{Id}";
            clone.transform.SetSiblingIndex(siblingIndex);

            var label = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            var button = clone.GetComponent<Button>();
            if (label == null || button == null)
            {
                UnityEngine.Object.Destroy(clone);
                throw new InvalidOperationException("The cloned Story button has no label or Button component.");
            }

            label.text = Label;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener((UnityAction)(() => _owner?.OnButtonClicked(this)));
            var menu = parent.GetComponentInParent<MainMenuModeSelectElement>();
            if (menu == null)
            {
                UnityEngine.Object.Destroy(clone);
                throw new InvalidOperationException("The native ModeSelect controller was not found.");
            }
            try { MainMenuButtonIntegration.Register(menu, template.GetComponent<Button>(), button); }
            catch
            {
                UnityEngine.Object.Destroy(clone);
                throw;
            }
            _nativeButton = clone;
            _nativeLabel = label;
            _nativeMenu = menu;
            _button = button;
            ModLog.Info($"[GameApi:MainMenu] Added '{Id}' to the main menu.");
        }

        internal void SetSiblingIndex(int index)
        {
            if (_nativeButton != null) _nativeButton.transform.SetSiblingIndex(index);
        }

        internal void ReassertLabel()
        {
            try
            {
                if (_nativeLabel != null && _nativeLabel.text != Label) _nativeLabel.text = Label;
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("main-menu.refresh-button-label", exception);
                ForgetNativeButton();
            }
        }

        internal void Invoke() => _onClick();

        internal void ForgetNativeButton()
        {
            try { MainMenuButtonIntegration.Unregister(_nativeMenu, _button); }
            catch (Exception exception)
            {
                ModLog.SuppressedException("main-menu.remove-native-animation", exception);
            }
            _nativeButton = null;
            _nativeLabel = null;
            _nativeMenu = null;
            _button = null;
        }

        internal void DestroyNativeButton()
        {
            var nativeButton = _nativeButton;
            ForgetNativeButton();
            try
            {
                if (nativeButton != null) UnityEngine.Object.Destroy(nativeButton);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"[GameApi:MainMenu] Failed to remove '{Id}': {ex.Message}");
            }
        }

        internal void Deactivate() => _owner = null;

        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            _owner = null;
            owner.Remove(this);
        }
    }
}
