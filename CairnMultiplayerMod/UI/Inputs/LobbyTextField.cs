using System;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI.Inputs;

/// <summary>
/// Lightweight wrapper around TMP_InputField. Builds a styled text field
/// (border + background) with native handling of focus, caret, paste and
/// selection. Replaces the old custom IMGUI input.
/// </summary>
internal sealed class LobbyTextField
{
    private readonly TMP_InputField _input;
    private readonly Image _border;

    public GameObject Root { get; }
    public TMP_InputField Input => _input;

    public string Value
    {
        get => _input.text;
        set => _input.text = value ?? string.Empty;
    }

    /// <summary>Fires on every keystroke; receives the current value.</summary>
    public event Action<string> Changed;

    /// <summary>Fires on focus loss (useful for persisting a value).</summary>
    public event Action<string> Blurred;

    public LobbyTextField(Transform parent, TMP_FontAsset font, string placeholder, int characterLimit)
    {
        Root = MultiplayerPanelTheme.MakeGo("Field", parent);

        _border = MultiplayerPanelTheme.Fill(Root, MultiplayerPanelTheme.Border);

        var inner = MultiplayerPanelTheme.MakeGo("Inner", Root.transform);
        MultiplayerPanelTheme.Anchor(inner, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        MultiplayerPanelTheme.Fill(inner, MultiplayerPanelTheme.SubBg, raycast: true);

        // Inner viewport — TMP_InputField requires a viewport to clip the text.
        var viewport = MultiplayerPanelTheme.MakeGo("Viewport", inner.transform);
        MultiplayerPanelTheme.Anchor(viewport, Vector2.zero, Vector2.one, new Vector2(10f, 4f), new Vector2(-10f, -4f));
        var vpImg = viewport.AddComponent<Image>();
        vpImg.color = new Color(0, 0, 0, 0);
        vpImg.raycastTarget = false;
        viewport.AddComponent<RectMask2D>();

        // Displayed text.
        var text = MultiplayerPanelTheme.MakeGo("Text", viewport.transform);
        MultiplayerPanelTheme.FullStretch(text);
        var textTmp = text.AddComponent<TextMeshProUGUI>();
        if (font != null) textTmp.font = font;
        textTmp.fontSize      = 14;
        textTmp.color         = MultiplayerPanelTheme.TextPrimary;
        textTmp.alignment     = TextAlignmentOptions.Left;
        textTmp.raycastTarget = false;
        textTmp.enableWordWrapping = false;
        textTmp.overflowMode  = TextOverflowModes.Masking;

        // Placeholder.
        var ph = MultiplayerPanelTheme.MakeGo("Placeholder", viewport.transform);
        MultiplayerPanelTheme.FullStretch(ph);
        var phTmp = ph.AddComponent<TextMeshProUGUI>();
        if (font != null) phTmp.font = font;
        phTmp.fontSize      = 14;
        phTmp.color         = MultiplayerPanelTheme.TextDim;
        phTmp.alignment     = TextAlignmentOptions.Left;
        phTmp.text          = placeholder ?? string.Empty;
        phTmp.raycastTarget = false;
        phTmp.fontStyle     = FontStyles.Italic;

        // The InputField component must be on the Inner to receive clicks.
        _input = inner.AddComponent<TMP_InputField>();
        _input.textViewport      = viewport.GetComponent<RectTransform>();
        _input.textComponent     = textTmp;
        _input.placeholder       = phTmp;
        _input.characterLimit    = characterLimit;
        _input.lineType          = TMP_InputField.LineType.SingleLine;
        _input.caretWidth        = 1;
        _input.caretColor        = MultiplayerPanelTheme.Accent;
        _input.selectionColor    = new Color(0.788f, 0.639f, 0.420f, 0.35f);
        _input.restoreOriginalTextOnEscape = false;

        _input.onValueChanged.AddListener((UnityAction<string>)OnValueChanged);
        _input.onSelect.AddListener((UnityAction<string>)OnSelected);
        _input.onDeselect.AddListener((UnityAction<string>)OnDeselected);
    }

    public void SetInteractable(bool value) => _input.interactable = value;

    public void SetCharacterValidation(TMP_InputField.CharacterValidation v) => _input.characterValidation = v;

    public void SetContentType(TMP_InputField.ContentType v) => _input.contentType = v;

    public void Focus() => _input.Select();

    private void OnValueChanged(string value)   => Changed?.Invoke(value);
    private void OnSelected(string _)           => _border.color = MultiplayerPanelTheme.BorderFocus;
    private void OnDeselected(string value)
    {
        _border.color = MultiplayerPanelTheme.Border;
        Blurred?.Invoke(value);
    }
}
