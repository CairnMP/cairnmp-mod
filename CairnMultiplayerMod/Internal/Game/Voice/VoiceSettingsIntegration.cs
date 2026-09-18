using System;
using System.Collections.Generic;
using System.Linq;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTGBTools.UI;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Registers a real SettingsPage: native fields, scrolling, focus, back and reset.</summary>
internal sealed class VoiceSettingsIntegration : IDisposable
{
    private static readonly Dictionary<IntPtr, VoiceSettingsIntegration> Pages = new();
    private readonly HarmonyLib.Harmony _harmony = new("CairnMultiplayerMod.VoiceSettings");
    private readonly VoiceAdapter _voice;
    private readonly List<Binding> _bindings = new();
    private const double DeviceCheckIntervalSeconds = .5;
    private double _nextDeviceCheck;
    private const double MinSearchIntervalSeconds = 2;
    private const double MaxSearchIntervalSeconds = 30;
    private double _nextSearch;
    private double _searchInterval = MinSearchIntervalSeconds;
    private double _nextMeter;
    private FieldInfo _status;
    private FieldListDropdown _microphoneField;
    private List<string> _deviceOptions;
    private string _deviceFingerprint;
    // Called several times per Tick and from VoiceAdapter; a LINQ predicate here allocates a
    // closure and an enumerator on every call, every frame.
    internal bool IsOpen
    {
        get
        {
            for (var i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                if (b.Menu != null && b.Menu.gameObject.activeInHierarchy && b.Menu.currentSettingsPageButton == b.Button)
                    return true;
            }
            return false;
        }
    }
    private sealed class Binding
    {
        internal IntPtr PagePointer;
        internal SettingsMenu Menu;
        internal SettingsPage Page;
        internal SettingsPageButton Button;
        internal TextMeshProUGUI Label;
        internal Button Previous;
        internal Navigation PreviousNavigation;
    }

    internal VoiceSettingsIntegration(VoiceAdapter voice)
    {
        _voice = voice;
        Patch(nameof(SettingsPage.CreateFields), nameof(CreateFields), true);
        Patch(nameof(SettingsPage.OnClosed), nameof(PageClosed), false);
        Patch(nameof(SettingsPage.SaveData), nameof(SaveData), true);
        _harmony.Patch(AccessTools.Method(typeof(AudioSettingsPage), nameof(AudioSettingsPage.CreateFields)),
            prefix: new HarmonyMethod(typeof(VoiceSettingsIntegration), nameof(CreateFields)));
        _harmony.Patch(AccessTools.Method(typeof(AudioSettingsPage), nameof(AudioSettingsPage.SaveData)),
            prefix: new HarmonyMethod(typeof(VoiceSettingsIntegration), nameof(SaveData)));
        _harmony.Patch(AccessTools.Method(typeof(AudioSettingsPage), nameof(AudioSettingsPage.OnDeviceChanged), Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(VoiceSettingsIntegration), nameof(SkipAudioDeviceChange)));
        _harmony.Patch(AccessTools.Method(typeof(SettingsMenu), nameof(SettingsMenu.OpenSettingsPage)),
            prefix: new HarmonyMethod(typeof(VoiceSettingsIntegration), nameof(PageOpening)));
    }
    private void Patch(string method, string callback, bool prefix)
    {
        var hook = new HarmonyMethod(typeof(VoiceSettingsIntegration), callback);
        _harmony.Patch(AccessTools.Method(typeof(SettingsPage), method), prefix: prefix ? hook : null, postfix: prefix ? null : hook);
    }

    internal void Tick()
    {
        if (GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out _)
            && lifecycle == CairnGameLifecycleState.Loading)
        {
            _voice.TestMicrophone = false;
            return;
        }
        for (var i = _bindings.Count - 1; i >= 0; i--)
        {
            var binding = _bindings[i];
            if (binding.Menu == null || binding.Button == null)
            {
                Pages.Remove(binding.PagePointer);
                if (binding.Page != null) UnityEngine.Object.Destroy(binding.Page);
                _bindings.RemoveAt(i);
                continue;
            }
            if (binding.Label != null && binding.Label.text != "CairnMP") binding.Label.text = "CairnMP";
        }
        if (!IsOpen) _voice.TestMicrophone = false;
        else if (_status != null && Time.realtimeSinceStartupAsDouble >= _nextMeter)
        {
            _nextMeter = Time.realtimeSinceStartupAsDouble + .2;
            _status.Info = new FieldLabel(_voice.TestMicrophone
                ? $"{_voice.Status} | Raw: {_voice.InputLevelDb:F0} dB | Processed: {_voice.ProcessedLevelDb:F0} dB | Auto gain: {_voice.ProcessingGainDb:+0;-0;0} dB | {(_voice.LevelDb >= VoicePreferences.SafeThreshold ? "Voice detected" : "Below threshold")}" : _voice.Status);
            foreach (var binding in _bindings)
                if (binding.Menu != null && binding.Menu.currentSettingsPageButton == binding.Button)
                {
                    binding.Menu.FieldsUI.GetFieldUI(_status)?.Repaint();
                    // Endpoint names can be much longer than the native dropdown caption.
                    var microphoneUi = binding.Menu.FieldsUI.GetFieldUI(_microphoneField)?.TryCast<FieldUIListDropdown>();
                    var caption = microphoneUi?.dropdown?.captionText;
                    if (caption != null)
                    {
                        caption.enableWordWrapping = false;
                        caption.enableAutoSizing = true;
                        caption.fontSizeMin = 14;
                        caption.fontSizeMax = 32;
                        caption.overflowMode = TextOverflowModes.Overflow;
                    }
                }
        }
        if (IsOpen && _microphoneField != null && Time.realtimeSinceStartupAsDouble >= _nextDeviceCheck)
        {
            // Audio endpoints do not change at frame rate, and this fingerprint allocates a
            // string per device plus the joined result every time it is built.
            _nextDeviceCheck = Time.realtimeSinceStartupAsDouble + DeviceCheckIntervalSeconds;
            var fingerprint = string.Join("|", _voice.Devices.Select(d => d.Id + d.Name));
            if (fingerprint != _deviceFingerprint)
            {
                _deviceFingerprint = fingerprint;
                _microphoneField.values = DeviceLabels(_deviceOptions);
                foreach (var binding in _bindings)
                    if (binding.Menu != null && binding.Menu.currentSettingsPageButton == binding.Button)
                        binding.Menu.FieldsUI.GetFieldUI(_microphoneField)?.Refresh();
            }
        }
        // A settings menu is scene-owned. Once attached, repeated global Resources
        // scans only add frame spikes; destroyed bindings above re-enable discovery.
        if (_bindings.Count > 0) return;
        // Attach() requires an ACTIVE menu, so scanning while no settings menu can be open
        // finds nothing, leaves _bindings empty, and comes back two seconds later forever.
        // Measured at ~20 ms per scan: a dropped frame every two seconds, all game long.
        if (!CanASettingsMenuBeOpen(lifecycle)) return;
        if (Time.realtimeSinceStartupAsDouble < _nextSearch) return;

        var found = false;
        foreach (var menu in Resources.FindObjectsOfTypeAll<SettingsMenu>())
        {
            if (menu == null || !menu.gameObject.scene.IsValid() || _bindings.Any(b => b.Menu == menu)) continue;
            found = true;
            try { Attach(menu); }
            catch (Exception ex) { ModLog.Warning("[VoiceSettings] Could not attach page: " + ex.Message); }
        }

        // Back off while the search keeps coming up empty, so an unexpected state cannot
        // reinstate a spike every two seconds. Any success returns to the responsive rate.
        _searchInterval = found || _bindings.Count > 0
            ? MinSearchIntervalSeconds
            : Math.Min(_searchInterval * 2, MaxSearchIntervalSeconds);
        _nextSearch = Time.realtimeSinceStartupAsDouble + _searchInterval;
    }

    /// <summary>
    /// Whether the game can currently be showing a settings menu at all. The menu lives in the
    /// main menu and behind the in-game pause menu; anywhere else the scan is pure waste.
    /// </summary>
    private static bool CanASettingsMenuBeOpen(CairnGameLifecycleState lifecycle)
        // Unknown is allowed on purpose: if the lifecycle service is not answering we must not
        // silently stop attaching the page. The backoff above bounds the cost of that case.
        => lifecycle == CairnGameLifecycleState.Menu
           || lifecycle == CairnGameLifecycleState.Unknown
           || MultiplayerPausePatch.IsPauseMenuActive;

    private void Attach(SettingsMenu menu)
    {
        var template = menu.GetComponentsInChildren<SettingsPageButton>(true).FirstOrDefault();
        if (template == null || !menu.gameObject.activeInHierarchy || template.button == null) return;
        // Keep the clone inactive until its page reference has been replaced: Awake must
        // never initialize the original Audio/Video ScriptableObject on behalf of our clone.
        var staging = new GameObject("CairnMP.SettingsStaging");
        staging.SetActive(false);
        var clone = UnityEngine.Object.Instantiate(template.gameObject, staging.transform);
        // SettingsPage is abstract in native metadata (interop does not preserve that flag).
        // Use a concrete instance and intercept its virtual audio-specific methods only for ours.
        var page = ScriptableObject.CreateInstance<AudioSettingsPage>();
        try
        {
            clone.name = "CairnMP.Settings";
            var button = clone.GetComponent<SettingsPageButton>();
            button.settingsMenu = menu;
            button.settingsPage = page;
            page.infos = new Il2CppReferenceArray<SettingsPage.Info>(0);
            Pages.Add(page.Pointer, this);
            var label = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            label.text = "CairnMP";
            clone.SetActive(false);
            clone.transform.SetParent(template.transform.parent, false);
            clone.transform.SetAsLastSibling();
            var originalRect = template.GetComponent<RectTransform>();
            var rect = clone.GetComponent<RectTransform>();
            // Most menus use a layout group; retain a fallback for the fixed-position variant.
            if (template.transform.parent.GetComponent<LayoutGroup>() == null)
            {
                var siblings = menu.GetComponentsInChildren<SettingsPageButton>(true).Where(b => b.gameObject != clone).ToArray();
                var lastY = siblings.Min(b => b.GetComponent<RectTransform>().anchoredPosition.y);
                rect.anchoredPosition = new Vector2(originalRect.anchoredPosition.x, lastY - originalRect.rect.height - 8);
            }
            clone.SetActive(true);
            button.button.onClick = new Button.ButtonClickedEvent();
            button.button.onClick.AddListener((UnityEngine.Events.UnityAction)(() => menu.OpenSettingsPage(button)));
            // Register in the same animation/focus owner as the other categories.
            var bouncing = menu.bouncingButtons;
            var more = button.button.TryCast<ButtonWithMoreEvents>();
            if (bouncing != null && more != null)
            {
                var source = template.button.TryCast<ButtonWithMoreEvents>();
                if (source != null && bouncing.ButtonsData != null && bouncing.ButtonsData.ContainsKey(source))
                {
                    var margin = label.margin;
                    margin.x = bouncing.ButtonsData[source].baseOffset;
                    label.margin = margin;
                }
                if (bouncing.ButtonsData != null && !bouncing.ButtonsData.ContainsKey(more)) bouncing.ButtonsData.Add(more, new BouncingButtons.ButtonData(label));
                // Selection belongs to the arrow zone: it moves the arrow AND notifies
                // BouncingButtons. Its Awake-time child cache does not include this clone.
                // Do not bypass that owner with separate select/deselect callbacks.
                var zone = bouncing.GetComponent<BouncingArrowZone>();
                if (zone != null)
                {
                    var children = zone.childs?.ToArray() ?? Array.Empty<BouncingArrowZone.SelectableChild>();
                    if (!children.Any(child => child.selectable == more))
                        zone.childs = new Il2CppReferenceArray<BouncingArrowZone.SelectableChild>(
                            children.Append(new BouncingArrowZone.SelectableChild(more)).ToArray());
                }
                more.OnPointerEntered = (Il2CppSystem.Action<ButtonWithMoreEvents>)(new Action<ButtonWithMoreEvents>(b => bouncing.SelectOnPointerEnter(b)));
            }
            var navigation = button.button.navigation;
            navigation.mode = Navigation.Mode.Automatic;
            button.button.navigation = navigation;
            var previous = menu.GetComponentsInChildren<SettingsPageButton>(true).LastOrDefault(b => b != button)?.button;
            var oldNavigation = previous != null ? previous.navigation : default;
            if (previous != null && oldNavigation.mode == Navigation.Mode.Explicit)
            {
                var updated = oldNavigation;
                updated.selectOnDown = button.button;
                previous.navigation = updated;
                navigation.mode = Navigation.Mode.Explicit;
                navigation.selectOnUp = previous;
                navigation.selectOnDown = oldNavigation.selectOnDown;
                button.button.navigation = navigation;
            }
            menu.ToggleSettingPageButton(button, false);
            _bindings.Add(new Binding { PagePointer = page.Pointer, Menu = menu, Page = page, Button = button, Label = label, Previous = previous, PreviousNavigation = oldNavigation });
            ModLog.Info("[VoiceSettings] CairnMP page attached to " + menu.name);
        }
        catch
        {
            Pages.Remove(page.Pointer);
            UnityEngine.Object.Destroy(clone);
            UnityEngine.Object.Destroy(page);
            throw;
        }
        finally { UnityEngine.Object.Destroy(staging); }
    }

    private static bool CreateFields(SettingsPage __instance, ref Il2CppReferenceArray<Field> __result)
    {
        if (!Pages.TryGetValue(__instance.Pointer, out var owner)) return true;
        __result = owner.BuildFields();
        return false;
    }
    private static bool SkipAudioDeviceChange(AudioSettingsPage __instance) => !Pages.ContainsKey(__instance.Pointer);
    private static void PageOpening(SettingsPageButton __0)
    {
        if (__0 != null && __0.settingsPage != null && Pages.TryGetValue(__0.settingsPage.Pointer, out var owner))
            __0.settingsPage.fields = owner.BuildFields();
    }
    private Il2CppReferenceArray<Field> BuildFields()
    {
        var options = new List<string> { "" };
        options.AddRange(_voice.Devices.Select(d => d.Id));
        var selected = VoicePreferences.Microphone.Value ?? "";
        if (!options.Contains(selected)) options.Add(selected);
        _deviceOptions = options;
        _deviceFingerprint = string.Join("|", _voice.Devices.Select(d => d.Id + d.Name));
        var keys = Enum.GetValues<Key>().Where(k => k != Key.None).ToArray();
        var currentKey = Enum.TryParse<Key>(VoicePreferences.PushToTalkKey.Value, true, out var parsed) ? parsed : Key.V;
        var fields = new List<Field>
        {
            new FieldInfo("PROXIMITY VOICE — 40 m outdoors, farther in shared rooms"),
            new FieldListDropdown(new FieldLabel("Voice mode"), Labels(new[] { "Open mic (voice detection)", "Push to talk", "Microphone muted" }),
                (int)VoicePreferences.CurrentMode, (FieldList.OnValueChangedDelegate)(new Action<int,int>((_, value) => { VoicePreferences.Mode.Value = value; VoicePreferences.Save(); })), new Il2CppSystem.Nullable<int>(0)),
            (_microphoneField = new FieldListDropdown(new FieldLabel("Microphone"), DeviceLabels(options),
                options.IndexOf(selected), (FieldList.OnValueChangedDelegate)(new Action<int,int>((_, value) => { if (value >= 0 && value < options.Count) { VoicePreferences.Microphone.Value = options[value]; VoicePreferences.Save(); } })), new Il2CppSystem.Nullable<int>(0))),
            new FieldSlider(new FieldLabel("Detection threshold (dB)"), -60, -10, true, VoicePreferences.SafeThreshold,
                (FieldSlider.OnValueChangedDelegate)(new Action<float,float>((_, value) => { VoicePreferences.ThresholdDb.Value = value; VoicePreferences.Save(); })), new Il2CppSystem.Nullable<float>(-40)),
            new FieldInfo("Lower threshold = more sensitive. Quiet sounds may also activate the microphone."),
            new FieldToggle(new FieldLabel("Microphone enhancement"), VoicePreferences.EnhanceMicrophone.Value,
                (Il2CppSystem.Action<bool>)(new Action<bool>(value => { VoicePreferences.EnhanceMicrophone.Value = value; VoicePreferences.Save(); })), new Il2CppSystem.Nullable<bool>(true)),
            new FieldListDropdown(new FieldLabel("Push-to-talk key"), Labels(keys.Select(k => k.ToString()).ToArray()), Math.Max(0, Array.IndexOf(keys, currentKey)),
                (FieldList.OnValueChangedDelegate)(new Action<int,int>((_, value) => { if (value >= 0 && value < keys.Length) { VoicePreferences.PushToTalkKey.Value = keys[value].ToString(); VoicePreferences.Save(); } })), new Il2CppSystem.Nullable<int>(Array.IndexOf(keys, Key.V))),
            new FieldInfo("Controller push-to-talk: hold View/Share + LB/L1."),
            new FieldSlider(new FieldLabel("Voice volume (%)"), 0, 300, true, VoicePreferences.SafeVolume * 100,
                (FieldSlider.OnValueChangedDelegate)(new Action<float,float>((_, value) => { VoicePreferences.Volume.Value = value / 100; VoicePreferences.Save(); })), new Il2CppSystem.Nullable<float>(100)),
            new FieldToggle(new FieldLabel("Test microphone (local playback)"), false,
                (Il2CppSystem.Action<bool>)(new Action<bool>(value => _voice.TestMicrophone = value)), new Il2CppSystem.Nullable<bool>(false)),
            (_status = new FieldInfo(_voice.Status)),
            new FieldInfo("The microphone test stays local. Close this page to resume proximity voice."),
        };
        foreach (var player in _voice.RemotePlayers)
        {
            var id = player.Id;
            var name = (player.Name ?? "Player").Replace("<", "").Replace(">", "");
            if (name.Length > 40) name = name.Substring(0, 40) + "…";
            fields.Add(new FieldToggle(new FieldLabel("Mute " + name), _voice.IsMuted(id),
                (Il2CppSystem.Action<bool>)(new Action<bool>(muted => _voice.SetMuted(id, muted))), new Il2CppSystem.Nullable<bool>(false)));
        }
        return new Il2CppReferenceArray<Field>(fields.ToArray());
    }
    private static Il2CppReferenceArray<FieldLabel> Labels(string[] labels)
        => new(labels.Select(label => new FieldLabel(label)).ToArray());
    private Il2CppReferenceArray<FieldLabel> DeviceLabels(List<string> options)
    {
        // Keep indices stable while the dropdown is open, including unplugged entries.
        foreach (var device in _voice.Devices) if (!options.Contains(device.Id)) options.Add(device.Id);
        return Labels(options.Select(id => id.Length == 0 ? "System default (communications)" : _voice.Devices.FirstOrDefault(d => d.Id == id)?.Name ?? "Disconnected microphone").ToArray());
    }
    private static void PageClosed(SettingsPage __instance)
    {
        if (Pages.TryGetValue(__instance.Pointer, out var owner)) { owner._voice.TestMicrophone = false; VoicePreferences.Save(); }
    }
    private static bool SaveData(SettingsPage __instance)
    {
        if (!Pages.ContainsKey(__instance.Pointer)) return true;
        VoicePreferences.Save();
        return false;
    }
    /// <summary>Detaches while session-owned UI is still expected to be alive.</summary>
    internal void ResetSession()
    {
        var canTouchNativeOwners = GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out _)
                                   && lifecycle != CairnGameLifecycleState.Loading;
        ClearBindings(canTouchNativeOwners);
    }

    /// <summary>
    /// A scene callback may run after Unity has already freed native menu objects. Only
    /// forget their managed wrappers here; the scene owns and destroys the cloned button.
    /// </summary>
    internal void ResetScene() => ClearBindings(cleanNativeOwners: false);

    private void ClearBindings(bool cleanNativeOwners)
    {
        foreach (var binding in _bindings)
        {
            Pages.Remove(binding.PagePointer);
            if (cleanNativeOwners)
            {
                try
                {
                    if (binding.Menu != null && binding.Menu.currentSettingsPageButton == binding.Button) binding.Menu.CloseSettingsPage(true);
                    if (binding.Previous != null) binding.Previous.navigation = binding.PreviousNavigation;
                    if (binding.Menu != null && binding.Button != null)
                    {
                        var bouncing = binding.Menu.bouncingButtons;
                        var more = binding.Button.button?.TryCast<ButtonWithMoreEvents>();
                        if (bouncing != null && more != null && bouncing.ButtonsData != null) bouncing.ButtonsData.Remove(more);
                        var zone = bouncing?.GetComponent<BouncingArrowZone>();
                        if (zone != null && zone.childs != null)
                        {
                            zone.childs = new Il2CppReferenceArray<BouncingArrowZone.SelectableChild>(
                                zone.childs.Where(child => child.selectable != more).ToArray());
                            if (zone.lastSelectedGameObject == binding.Button.gameObject)
                            {
                                zone.lastSelectedGameObject = null;
                                zone.lastSelectedChild = new Il2CppSystem.Nullable<BouncingArrowZone.SelectableChild>();
                            }
                        }
                        if (binding.Menu.lastSettingsPageButton == binding.Button) binding.Menu.lastSettingsPageButton = null;
                    }
                    if (binding.Button != null) UnityEngine.Object.Destroy(binding.Button.gameObject);
                }
                catch (Exception ex) { ModLog.SuppressedException("voice.settings-reset", ex); }
            }
            // The ScriptableObject is ours rather than scene-owned. Destroy it only when
            // its wrapper is still known-good; otherwise dropping Pages makes it inert.
            if (cleanNativeOwners)
            {
                try { if (binding.Page != null) UnityEngine.Object.Destroy(binding.Page); }
                catch (Exception ex) { ModLog.SuppressedException("voice.settings-page-reset", ex); }
            }
        }
        _bindings.Clear();
        _status = null;
        _microphoneField = null;
        _deviceOptions = null;
        _deviceFingerprint = null;
        _nextSearch = 0;
        _searchInterval = MinSearchIntervalSeconds;
        _voice.TestMicrophone = false;
    }

    public void Dispose()
    {
        ClearBindings(cleanNativeOwners: true);
        _harmony.UnpatchSelf();
    }
}
