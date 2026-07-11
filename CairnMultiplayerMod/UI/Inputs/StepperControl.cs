using System;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI.Inputs;

/// <summary>
/// Stepper -/+ with a central value, clamped to [min, max]. Used for the number
/// of lobby slots (2-8).
/// </summary>
internal sealed class StepperControl
{
    private readonly int _min;
    private readonly int _max;
    private readonly TextMeshProUGUI _valueTmp;

    private int _value;

    public GameObject Root { get; }
    public int Value => _value;
    public event Action<int> Changed;

    public StepperControl(Transform parent, TMP_FontAsset font, int min, int max, int initial)
    {
        _min = min;
        _max = max;
        _value = Mathf.Clamp(initial, min, max);

        Root = MultiplayerPanelTheme.MakeGo("Stepper", parent);
        MultiplayerPanelTheme.Fill(Root, MultiplayerPanelTheme.Border);

        var inner = MultiplayerPanelTheme.MakeGo("Inner", Root.transform);
        MultiplayerPanelTheme.Anchor(inner, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        MultiplayerPanelTheme.Fill(inner, MultiplayerPanelTheme.SubBg, raycast: true);

        BuildArrowButton(inner.transform, font, "−",
            new Vector2(0f, 0f), new Vector2(0.22f, 1f),
            (UnityAction)(() => Adjust(-1)));

        _valueTmp = MultiplayerPanelTheme.Tmp(inner.transform, "Value", _value.ToString(),
            font, 16, MultiplayerPanelTheme.Accent, TextAlignmentOptions.Center, FontStyles.Bold);
        MultiplayerPanelTheme.Anchor(_valueTmp.gameObject, new Vector2(0.22f, 0f), new Vector2(0.78f, 1f));

        BuildArrowButton(inner.transform, font, "+",
            new Vector2(0.78f, 0f), new Vector2(1f, 1f),
            (UnityAction)(() => Adjust(+1)));
    }

    public void SetInteractable(bool value)
    {
        // Simply disables the child Buttons; the visuals remain.
        foreach (var btn in Root.GetComponentsInChildren<Button>(true))
            btn.interactable = value;
    }

    private void Adjust(int delta)
    {
        int next = Mathf.Clamp(_value + delta, _min, _max);
        if (next == _value) return;
        _value = next;
        _valueTmp.text = _value.ToString();
        Changed?.Invoke(_value);
    }

    private static void BuildArrowButton(Transform parent, TMP_FontAsset font, string glyph,
        Vector2 aMin, Vector2 aMax, UnityAction onClick)
    {
        var go = MultiplayerPanelTheme.MakeGo("Arrow", parent);
        MultiplayerPanelTheme.Anchor(go, aMin, aMax);
        var img = go.AddComponent<Image>();
        img.color         = new Color(0, 0, 0, 0);
        img.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        // Arrow tint: muted normally, accent on hover (via Button colors).
        var colors = btn.colors;
        colors.normalColor      = Color.white;
        colors.highlightedColor = new Color(1f, 1f, 1f, 0.85f);
        colors.pressedColor     = new Color(1f, 1f, 1f, 0.65f);
        btn.colors = colors;

        MultiplayerPanelTheme.Tmp(go.transform, "Glyph", glyph, font, 16,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.Center, FontStyles.Bold);
    }
}
