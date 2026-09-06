using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>
/// Extends ModeSelect's native button list so Cairn owns hover, text animation,
/// selection arrow and controller navigation for injected buttons too.
/// </summary>
internal static class MainMenuButtonIntegration
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.MainMenuButtons");
    private static readonly Dictionary<IntPtr, Binding> Bindings = new();

    private sealed class Binding
    {
        internal readonly MainMenuModeSelectElement Menu;
        internal readonly List<Button> Added = new();
        internal Binding(MainMenuModeSelectElement menu) => Menu = menu;
    }

    public static void Install()
    {
        Harmony.Patch(AccessTools.Method(typeof(MainMenuModeSelectElement), nameof(MainMenuModeSelectElement.GetButtons)),
            postfix: new HarmonyMethod(typeof(MainMenuButtonIntegration), nameof(IncludeRegisteredButtons)));
    }

    public static void Uninstall()
    {
        Bindings.Clear();
        Harmony.UnpatchSelf();
    }

    internal static void Register(MainMenuModeSelectElement menu, Button template, Button button)
    {
        if (!Bindings.TryGetValue(menu.Pointer, out var binding))
        {
            binding = new Binding(menu);
            Bindings.Add(menu.Pointer, binding);
        }
        if (binding.Added.Contains(button)) return;

        var previous = menu.GetButtons();
        RestoreBaseMargins(menu, previous);
        // A clone can inherit the selected Story label's offset. Start from its
        // unselected margin before Cairn takes the new baseline.
        var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        var templateLabel = template.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null && templateLabel != null) label.margin = templateLabel.margin;

        binding.Added.Add(button);
        try { Refresh(menu); }
        catch
        {
            binding.Added.Remove(button);
            if (binding.Added.Count == 0) Bindings.Remove(menu.Pointer);
            Refresh(menu);
            throw;
        }
    }

    internal static void Unregister(MainMenuModeSelectElement menu, Button button)
    {
        if (ReferenceEquals(menu, null) || !Bindings.TryGetValue(menu.Pointer, out var binding)) return;
        if (menu == null)
        {
            Bindings.Remove(menu.Pointer);
            return;
        }

        RestoreBaseMargins(menu, menu.GetButtons());
        binding.Added.Remove(button);
        if (binding.Added.Count == 0) Bindings.Remove(menu.Pointer);
        if (button != null)
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == button.gameObject)
                menu.storyModeButton.Select();
            button.gameObject.SetActive(false);
        }
        Refresh(menu);
    }

    private static void RestoreBaseMargins(MainMenuModeSelectElement menu, Il2CppReferenceArray<Button> buttons)
    {
        var offsets = menu.BaseOffsets;
        if (offsets == null || buttons == null) return;
        for (int i = 0; i < Math.Min(offsets.Length, buttons.Length); i++)
        {
            var label = buttons[i]?.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null) continue;
            var margin = label.margin;
            margin.x = offsets[i];
            label.margin = margin;
        }
    }

    private static void Refresh(MainMenuModeSelectElement menu)
    {
        Canvas.ForceUpdateCanvases();
        // OnInitialized rebuilds the parallel text/margin/position/badge arrays.
        // Native disassembly confirms it removes then adds MmButton_OnPointerEntered,
        // so refreshing does not duplicate handlers. Initialize also subscribes to
        // global menu/input events and must not be called a second time.
        menu.OnInitialized();
        menu.RefreshNavigation(menu.GetButtons());
        if (menu.IsShown) menu.MainMenu_OnSelectionChanged();
    }

    private static void IncludeRegisteredButtons(MainMenuModeSelectElement __instance,
        ref Il2CppReferenceArray<Button> __result)
    {
        if (__result == null || !Bindings.TryGetValue(__instance.Pointer, out var binding)) return;
        try
        {
            var buttons = new List<Button>(__result.Length + binding.Added.Count);
            foreach (var original in __result)
            {
                buttons.Add(original);
                if (original != __instance.storyModeButton) continue;
                foreach (var added in binding.Added)
                    if (added != null) buttons.Add(added);
            }
            __result = new Il2CppReferenceArray<Button>(buttons.ToArray());
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("main-menu.include-registered-buttons", exception);
        }
    }
}
