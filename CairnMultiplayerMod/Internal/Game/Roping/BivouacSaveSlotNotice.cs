using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// On the story save cards, a file this Rope Team run would refuse carries the
/// line "Only saves in tent allowed". The card uses the preview Cairn already
/// built for that slot. An empty slot is left alone. The file is not rewritten.
/// The slot setup method takes that preview by reference, and a patch there
/// faults the menu, so the cards are read from the main-menu tick instead.
/// </summary>
internal static class BivouacSaveSlotNotice
{
    private const string NoticeName = "CairnMpTentOnlyNotice";

    private static Sprite _plateSprite;
    private static bool _reported;

    internal static void Refresh()
    {
        LoadGameSlotUi[] slots;
        try
        {
            slots = UnityEngine.Object.FindObjectsOfType<LoadGameSlotUi>(true);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("save-slot.notice", exception);
            return;
        }

        if (slots == null || slots.Length == 0)
        {
            _reported = false;
            return;
        }

        var refused = 0;
        var firstAge = 0f;
        var sawAge = false;
        for (var index = 0; index < slots.Length; index++)
        {
            if (RefreshSlot(slots[index], out var age, out var show))
            {
                if (!sawAge)
                {
                    firstAge = age;
                    sawAge = true;
                }

                if (show) refused++;
            }
        }

        if (_reported) return;
        _reported = true;
        ModLog.Info(sawAge
            ? $"[Save] Save cards: {slots.Length}, tent line on {refused}. First age {firstAge:0.###}s."
            : $"[Save] Save cards: {slots.Length}, none had a readable preview.");
    }

    private static bool RefreshSlot(LoadGameSlotUi slot, out float age, out bool show)
    {
        age = 0f;
        show = false;
        if (slot == null) return false;

        var readable = false;
        try
        {
            var preview = slot.Preview;
            var hasSave = preview != null && !preview.IsNull();
            if (hasSave)
            {
                age = preview.lastBivouacAge;
                readable = true;
            }

            show = BivouacSavePolicy.ShowsTentOnlyNotice(
                BivouacSaveSession.SaveRun, hasSave, age);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("save-slot.notice", exception);
            show = false;
        }

        try
        {
            Apply(slot.normalState, show);
            Apply(slot.selectedState, show);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("save-slot.notice", exception);
        }

        return readable;
    }

    private static void Apply(LoadGameSlotStateUi state, bool show)
    {
        if (state == null) return;

        var title = state.title;
        StampTitle(title, show);
        var background = state.colorableBackground;
        var host = background != null ? background.transform : title != null ? title.transform.parent : state.transform;
        if (host == null) return;

        var existing = host.Find(NoticeName);
        if (!show)
        {
            if (existing != null) existing.gameObject.SetActive(false);
            return;
        }

        var badge = existing != null ? existing.gameObject : CreateBadge(host, state, title);
        var label = badge.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = BivouacSavePolicy.TentOnlyNotice;
        badge.SetActive(true);
        badge.transform.SetAsLastSibling();
    }

    private static void StampTitle(TextMeshProUGUI title, bool show)
    {
        if (title == null) return;
        var text = title.text ?? "";
        var marker = "\n" + BivouacSavePolicy.TentOnlyNotice;
        var has = text.Contains(BivouacSavePolicy.TentOnlyNotice, StringComparison.Ordinal);
        if (show && !has)
            title.text = text + marker;
        else if (!show && has)
            title.text = text.Replace(marker, "").Replace(BivouacSavePolicy.TentOnlyNotice, "");
    }

    private static GameObject CreateBadge(Transform host, LoadGameSlotStateUi state, TextMeshProUGUI title)
    {
        var badge = new GameObject(NoticeName);
        badge.transform.SetParent(host, false);
        var plate = badge.AddComponent<Image>();
        plate.sprite = PlateSprite(state.colorableBackground);
        plate.color = new Color(0.09f, 0.07f, 0.05f, 0.88f);
        plate.raycastTarget = false;

        var plateRect = badge.GetComponent<RectTransform>();
        plateRect.anchorMin = new Vector2(0.06f, 0.03f);
        plateRect.anchorMax = new Vector2(0.94f, 0.22f);
        plateRect.offsetMin = Vector2.zero;
        plateRect.offsetMax = Vector2.zero;

        var textObject = new GameObject("Text");
        textObject.transform.SetParent(badge.transform, false);
        var label = textObject.AddComponent<TextMeshProUGUI>();
        if (title != null)
        {
            label.font = title.font;
            label.fontSharedMaterial = title.fontSharedMaterial;
            label.fontSize = title.fontSize > 1f ? title.fontSize * 0.62f : 16f;
        }
        else
        {
            label.fontSize = 16f;
        }

        label.alignment = TextAlignmentOptions.Center;
        label.color = new Color(0.96f, 0.82f, 0.55f, 1f);
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.text = BivouacSavePolicy.TentOnlyNotice;

        var textRect = label.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(6f, 2f);
        textRect.offsetMax = new Vector2(-6f, -2f);
        return badge;
    }

    /// <summary>A UI Image draws nothing until it has a sprite.</summary>
    private static Sprite PlateSprite(Image background)
    {
        if (background != null && background.sprite != null) return background.sprite;
        if (_plateSprite != null) return _plateSprite;

        var texture = Texture2D.whiteTexture;
        _plateSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f));
        return _plateSprite;
    }
}
