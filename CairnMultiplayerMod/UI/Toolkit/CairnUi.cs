using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace CairnMultiplayerMod.UI.Toolkit;

internal static class CairnUi
{
    public static readonly Color Overlay = new(0.00f, 0.00f, 0.00f, 0.64f);
    public static readonly Color CardBg = new(0.080f, 0.072f, 0.060f, 0.98f);
    public static readonly Color Surface = new(0.115f, 0.102f, 0.086f, 1.00f);
    public static readonly Color SurfaceSoft = new(0.155f, 0.137f, 0.115f, 1.00f);
    public static readonly Color Border = new(1.000f, 1.000f, 1.000f, 0.12f);
    public static readonly Color BorderSubtle = new(1.000f, 1.000f, 1.000f, 0.07f);
    public static readonly Color TextPrimary = new(0.910f, 0.871f, 0.788f, 1.00f);
    public static readonly Color TextMuted = new(0.910f, 0.871f, 0.788f, 0.62f);
    public static readonly Color TextDim = new(0.910f, 0.871f, 0.788f, 0.40f);
    public static readonly Color Accent = new(0.788f, 0.639f, 0.420f, 1.00f);
    public static readonly Color AccentSoft = new(0.788f, 0.639f, 0.420f, 0.12f);
    public static readonly Color DarkText = new(0.105f, 0.083f, 0.052f, 1.00f);
    public static readonly Color Ok = new(0.380f, 0.760f, 0.450f, 1.00f);
    public static readonly Color Warn = new(0.860f, 0.730f, 0.290f, 1.00f);
    public static readonly Color Error = new(0.820f, 0.380f, 0.330f, 1.00f);

    private static Font _font;

    public static Font Font => _font ??= LoadFont();

    public static Label Label(string text, int size, Color color, FontStyle style = FontStyle.Normal)
    {
        var label = new Label(text);
        label.style.fontSize = size;
        label.style.color = color;
        if (Font != null) label.style.unityFont = Font;
        label.style.unityFontStyleAndWeight = style;
        label.style.whiteSpace = WhiteSpace.Normal;
        return label;
    }

    public static Label MicroLabel(string text)
    {
        var label = Label(text, 10, TextMuted, FontStyle.Normal);
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        label.style.marginBottom = 4;
        return label;
    }

    public static Button Button(string text, Action clicked, bool primary = false)
    {
        var button = new Button(clicked) { text = text };
        button.style.height = 40;
        button.style.borderTopLeftRadius = 4;
        button.style.borderTopRightRadius = 4;
        button.style.borderBottomLeftRadius = 4;
        button.style.borderBottomRightRadius = 4;
        button.style.borderTopWidth = 1;
        button.style.borderRightWidth = 1;
        button.style.borderBottomWidth = 1;
        button.style.borderLeftWidth = 1;
        button.style.borderTopColor = primary ? Accent : Border;
        button.style.borderRightColor = primary ? Accent : Border;
        button.style.borderBottomColor = primary ? Accent : Border;
        button.style.borderLeftColor = primary ? Accent : Border;
        button.style.backgroundColor = primary ? Accent : Surface;
        button.style.color = primary ? DarkText : TextPrimary;
        if (Font != null) button.style.unityFont = Font;
        button.style.fontSize = 12;
        button.style.unityFontStyleAndWeight = FontStyle.Bold;
        button.style.unityTextAlign = TextAnchor.MiddleCenter;
        button.style.marginTop = 0;
        button.style.marginBottom = 0;
        button.style.paddingLeft = 12;
        button.style.paddingRight = 12;
        return button;
    }

    public static TextField TextField(string placeholder, int maxLength)
    {
        var field = new TextField { maxLength = maxLength };
        field.style.height = 38;
        field.style.borderTopLeftRadius = 4;
        field.style.borderTopRightRadius = 4;
        field.style.borderBottomLeftRadius = 4;
        field.style.borderBottomRightRadius = 4;
        field.style.marginTop = 0;
        field.style.marginBottom = 0;
        field.style.paddingLeft = 0;
        field.style.paddingRight = 0;
        field.style.backgroundColor = Surface;
        field.style.borderTopWidth = 1;
        field.style.borderRightWidth = 1;
        field.style.borderBottomWidth = 1;
        field.style.borderLeftWidth = 1;
        field.style.borderTopColor = Border;
        field.style.borderRightColor = Border;
        field.style.borderBottomColor = Border;
        field.style.borderLeftColor = Border;
        field.style.color = TextPrimary;
        if (Font != null) field.style.unityFont = Font;
        field.style.fontSize = 13;
        field.SetValueWithoutNotify("");
        field.tooltip = placeholder ?? "";
        return field;
    }

    public static VisualElement Card()
    {
        var card = new VisualElement();
        card.style.backgroundColor = Surface;
        card.style.borderTopLeftRadius = 6;
        card.style.borderTopRightRadius = 6;
        card.style.borderBottomLeftRadius = 6;
        card.style.borderBottomRightRadius = 6;
        card.style.borderTopWidth = 1;
        card.style.borderRightWidth = 1;
        card.style.borderBottomWidth = 1;
        card.style.borderLeftWidth = 1;
        card.style.borderTopColor = Border;
        card.style.borderRightColor = Border;
        card.style.borderBottomColor = Border;
        card.style.borderLeftColor = Border;
        card.style.paddingTop = 14;
        card.style.paddingRight = 14;
        card.style.paddingBottom = 14;
        card.style.paddingLeft = 14;
        card.style.marginBottom = 10;
        return card;
    }

    public static VisualElement Row(int gap = 10)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Stretch;
        row.style.marginBottom = gap;
        return row;
    }

    public static void SetBorderColor(VisualElement element, Color color)
    {
        element.style.borderTopColor = color;
        element.style.borderRightColor = color;
        element.style.borderBottomColor = color;
        element.style.borderLeftColor = color;
    }

    public static void Show(VisualElement element, bool visible)
    {
        if (element != null)
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public static void FullScreen(VisualElement element)
    {
        element.style.position = Position.Absolute;
        element.style.left = 0;
        element.style.right = 0;
        element.style.top = 0;
        element.style.bottom = 0;
    }

    private static Font LoadFont()
    {
        try
        {
            var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null) return font;
        }
        catch
        {
            // Some IL2CPP builds don't expose the builtin font.
        }

        try
        {
            return Font.CreateDynamicFontFromOSFont("Arial", 14);
        }
        catch
        {
            return null;
        }
    }
}
