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
    private double _nextSearch;
    private double _nextMeter;
    private FieldInfo _status;
    private FieldListDropdown _microphoneField;
    private List<string> _deviceOptions;
    private string _deviceFingerprint;
    internal bool IsOpen => _bindings.Any(b => b.Menu != null && b.Menu.gameObject.activeInHierarchy && b.Menu.currentSettingsPageButton == b.Button);
    private sealed class Binding
    {
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
        for (var i = _bindings.Count - 1; i >= 0; i--)
        {
            var binding = _bindings[i];
            if (binding.Menu == null || binding.Button == null)
            {
                Pages.Remove(binding.Page.Pointer);
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
                ? $"{_voice.Status} | Level: {_voice.LevelDb:F0} dB | {(_voice.LevelDb >= VoicePreferences.SafeThreshold ? "Voice detected" : "Below threshold")}" : _voice.Status);
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
        if (IsOpen && _microphoneField != null)
        {
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
        if (Time.realtimeSinceStartupAsDouble < _nextSearch) return;
        _nextSearch = Time.realtimeSinceStartupAsDouble + 2;
        foreach (var menu in Resources.FindObjectsOfTypeAll<SettingsMenu>())
        {
            if (menu == null || !menu.gameObject.scene.IsValid() || _bindings.Any(b => b.Menu == menu)) continue;
            try { Attach(menu); }
            catch (Exception ex) { ModLog.Warning("[VoiceSettings] Could not attach page: " + ex.Message); }
        }
    }

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
            _bindings.Add(new Binding { Menu = menu, Page = page, Button = button, Label = label, Previous = previous, PreviousNavigation = oldNavigation });
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
            new FieldInfo("PROXIMITY VOICE — voices fade out beyond 30 m"),
            new FieldListDropdown(new FieldLabel("Voice mode"), Labels(new[] { "Open mic (voice detection)", "Push to talk", "Microphone muted" }),
                (int)VoicePreferences.CurrentMode, (FieldList.OnValueChangedDelegate)(new Action<int,int>((_, value) => { VoicePreferences.Mode.Value = value; VoicePreferences.Save(); })), new Il2CppSystem.Nullable<int>(0)),
            (_microphoneField = new FieldListDropdown(new FieldLabel("Microphone"), DeviceLabels(options),
                options.IndexOf(selected), (FieldList.OnValueChangedDelegate)(new Action<int,int>((_, value) => { if (value >= 0 && value < options.Count) { VoicePreferences.Microphone.Value = options[value]; VoicePreferences.Save(); } })), new Il2CppSystem.Nullable<int>(0))),
            new FieldSlider(new FieldLabel("Detection threshold (dB)"), -60, -10, true, VoicePreferences.SafeThreshold,
                (FieldSlider.OnValueChangedDelegate)(new Action<float,float>((_, value) => { VoicePreferences.ThresholdDb.Value = value; VoicePreferences.Save(); })), new Il2CppSystem.Nullable<float>(-40)),
            new FieldInfo("Lower threshold = more sensitive. Quiet sounds may also activate the microphone."),
            new FieldListDropdown(new FieldLabel("Push-to-talk key"), Labels(keys.Select(k => k.ToString()).ToArray()), Math.Max(0, Array.IndexOf(keys, currentKey)),
                (FieldList.OnValueChangedDelegate)(new Action<int,int>((_, value) => { if (value >= 0 && value < keys.Length) { VoicePreferences.PushToTalkKey.Value = keys[value].ToString(); VoicePreferences.Save(); } })), new Il2CppSystem.Nullable<int>(Array.IndexOf(keys, Key.V))),
            new FieldSlider(new FieldLabel("Voice volume (%)"), 0, 200, true, VoicePreferences.SafeVolume * 100,
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
    public void Dispose()
    {
        foreach (var binding in _bindings)
        {
            if (binding.Menu != null && binding.Menu.currentSettingsPageButton == binding.Button) binding.Menu.CloseSettingsPage(true);
            Pages.Remove(binding.Page.Pointer);
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
            if (binding.Page != null) UnityEngine.Object.Destroy(binding.Page);
        }
        _bindings.Clear();
        _harmony.UnpatchSelf();
    }
}
