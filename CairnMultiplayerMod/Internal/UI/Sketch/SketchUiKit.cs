using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.Internal.UI.Sketch;

/// <summary>
/// uGUI widget factories dressed with the game's "sketch" sprites (see GameUiAssetLibrary).
/// Low-level building blocks reused by the native menu's screens (panels, icons, labels, buttons).
/// </summary>
internal static class SketchUiKit
{
    // ── Palette (aligned with the photo-mode art: midnight-blue panels, cream text, gold accent) ──
    public static readonly Color PanelTint = Color.white;                       // the art already carries the midnight blue
    public static readonly Color FrameTint = new(1f, 1f, 0.96f, 1f);            // #FFFFF4 (outline tint)
    public static readonly Color TextCream = new(0.91f, 0.89f, 0.83f, 1f);
    public static readonly Color TextDim = new(0.91f, 0.89f, 0.83f, 0.55f);
    public static readonly Color Accent = new(0.79f, 0.64f, 0.42f, 1f);      // piton gold
    public static readonly Color IconActive = Color.white;
    public static readonly Color IconIdle = new(1f, 1f, 1f, 0.45f);
    public static readonly Color FieldBg = new(0.09f, 0.12f, 0.21f, 0.95f);   // inset midnight-blue field
    public static readonly Color RowTint = new(0.04f, 0.06f, 0.12f, 0.55f);   // row strip (darker)
    public static readonly Color ButtonText = new(0.20f, 0.18f, 0.15f, 1f);      // dark text on light button

    /// <summary>Creates an empty GameObject parented to <paramref name="parent"/> (local transform kept).</summary>
    public static GameObject Make(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    /// <summary>RectTransform anchored by corners (anchorMin/Max) + offsets in pixels.</summary>
    public static RectTransform Rect(GameObject go, Vector2 aMin, Vector2 aMax,
        Vector2 offMin = default, Vector2 offMax = default)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = offMin;
        rt.offsetMax = offMax;
        return rt;
    }

    /// <summary>RectTransform covering the whole parent, with no margin.</summary>
    public static RectTransform Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    /// <summary>RectTransform at fixed position/size (anchored to the parent's center).</summary>
    public static RectTransform Box(GameObject go, Vector2 anchoredPos, Vector2 size)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;
        return rt;
    }

    /// <summary>Strip anchored to the top of the parent, full width, fixed height.</summary>
    public static RectTransform StretchTop(GameObject go, float height)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(0f, height);
        rt.anchoredPosition = Vector2.zero;
        return rt;
    }

    /// <summary>Fills the parent at full width, with top/bottom margins in pixels.</summary>
    public static RectTransform StretchFill(GameObject go, float topInset, float bottomInset)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(0f, bottomInset);
        rt.offsetMax = new Vector2(0f, -topInset);
        return rt;
    }

    /// <summary>9-slice image (frame/panel) with a game sprite.</summary>
    public static Image Sliced(GameObject go, string spriteName, Color color, bool raycast = false)
    {
        var img = go.AddComponent<Image>();
        img.sprite = GameUiAssetLibrary.Get(spriteName);
        img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    /// <summary>Simple image (icon/decoration), aspect ratio preserved.</summary>
    public static Image Simple(GameObject go, string spriteName, Color color, bool raycast = false)
    {
        var img = go.AddComponent<Image>();
        img.sprite = GameUiAssetLibrary.Get(spriteName);
        img.type = Image.Type.Simple;
        img.preserveAspect = true;
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    /// <summary>Flat color fill (optional raycast) covering the GameObject.</summary>
    public static Image FillColor(GameObject go, Color color, bool raycast = false)
    {
        var img = go.AddComponent<Image>();
        img.sprite = GameUiAssetLibrary.Get(GameUiAssetLibrary.White);
        img.type = Image.Type.Simple;
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    /// <summary>Non-interactive TextMeshProUGUI covering its parent, using a game font.</summary>
    public static TextMeshProUGUI Label(Transform parent, string name, string text, float size,
        Color color, TextAlignmentOptions align, bool logo = false)
    {
        var go = Make(name, parent);
        Stretch(go);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        var font = logo ? GameUiAssetLibrary.LogoFont : GameUiAssetLibrary.TextFont;
        if (font != null) tmp.font = font;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.text = text;
        tmp.raycastTarget = false;
        return tmp;
    }

    /// <summary>
    /// Editable text field dressed with the native rounded background (RowBg). Builds viewport + mask +
    /// text + placeholder and returns the ready TMP_InputField. Fills the supplied parent.
    /// </summary>
    public static TMP_InputField NativeField(Transform parent, string placeholder, int characterLimit)
    {
        var root = Make("Field", parent);
        Stretch(root);
        // RowBg is a light sprite: tint it dark midnight blue for a legible inset field.
        Sliced(root, GameUiAssetLibrary.RowBg, FieldBg, raycast: true);

        // Internal viewport (TMP_InputField requires a viewport to clip the text).
        var viewport = Make("Viewport", root.transform);
        Rect(viewport, Vector2.zero, Vector2.one, new Vector2(14f, 6f), new Vector2(-14f, -6f));
        var vpImg = viewport.AddComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0f);
        vpImg.raycastTarget = false;
        viewport.AddComponent<RectMask2D>();

        var textGo = Make("Text", viewport.transform);
        Stretch(textGo);
        var textTmp = textGo.AddComponent<TextMeshProUGUI>();
        if (GameUiAssetLibrary.TextFont != null) textTmp.font = GameUiAssetLibrary.TextFont;
        textTmp.fontSize = 16f;
        textTmp.color = TextCream;
        textTmp.alignment = TextAlignmentOptions.Left;
        textTmp.raycastTarget = false;
        textTmp.enableWordWrapping = false;
        textTmp.overflowMode = TextOverflowModes.Masking;

        var phGo = Make("Placeholder", viewport.transform);
        Stretch(phGo);
        var phTmp = phGo.AddComponent<TextMeshProUGUI>();
        if (GameUiAssetLibrary.TextFont != null) phTmp.font = GameUiAssetLibrary.TextFont;
        phTmp.fontSize = 16f;
        phTmp.color = TextDim;
        phTmp.alignment = TextAlignmentOptions.Left;
        phTmp.text = placeholder ?? "";
        phTmp.raycastTarget = false;
        phTmp.fontStyle = FontStyles.Italic;

        var input = root.AddComponent<TMP_InputField>();
        input.textViewport = viewport.GetComponent<RectTransform>();
        input.textComponent = textTmp;
        input.placeholder = phTmp;
        input.characterLimit = characterLimit;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.caretWidth = 1;
        input.caretColor = Accent;
        input.selectionColor = new Color(0.79f, 0.64f, 0.42f, 0.35f);
        input.restoreOriginalTextOnEscape = false;
        return input;
    }

    /// <summary>Label in a positioned cell (Box); returns the TMP for later updates.</summary>
    public static TextMeshProUGUI LabelBox(Transform parent, string name, Vector2 pos, Vector2 size,
        string text, float fontSize, Color color, TextAlignmentOptions align, bool logo = false)
    {
        var cell = Make(name, parent);
        Box(cell, pos, size);
        return Label(cell.transform, "Text", text, fontSize, color, align, logo);
    }

    /// <summary>Icon tab (repurposed photo-mode icons); tinted according to the active state.</summary>
    public static GameObject Tab(Transform parent, string iconSprite, Vector2 pos, float size,
        bool active, UnityAction onClick)
    {
        var cell = Make("Tab", parent);
        Box(cell, pos, new Vector2(size, size));
        Simple(cell, iconSprite, active ? IconActive : IconIdle, raycast: onClick != null);
        if (onClick != null) MakeButton(cell, onClick);
        return cell;
    }

    /// <summary>Arrow ‹ (native sprite) or › (mirrored). Clickable if onClick is provided.</summary>
    public static GameObject ArrowButton(Transform parent, Vector2 pos, Vector2 size, bool left, UnityAction onClick)
    {
        var go = Make(left ? "ArrowLeft" : "ArrowRight", parent);
        var rt = Box(go, pos, size);
        Simple(go, GameUiAssetLibrary.Arrow, TextCream, raycast: onClick != null);
        if (!left) rt.localScale = new Vector3(-1f, 1f, 1f);   // mirror to point right
        if (onClick != null) MakeButton(go, onClick);
        return go;
    }

    /// <summary>Full-width strip row: background + left label + right control cell (fixed width).</summary>
    public static Transform RowStrip(Transform body, string label, float y, float cellWidth = 250f)
    {
        var row = Make($"Row_{label}", body);
        var rt = row.GetComponent<RectTransform>() ?? row.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(-40f, 52f);
        rt.anchoredPosition = new Vector2(0f, y);
        Sliced(row, GameUiAssetLibrary.RowBg, RowTint);

        var labelGo = Make("Label", row.transform);
        var lrt = labelGo.GetComponent<RectTransform>() ?? labelGo.AddComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0f, 0f);
        lrt.anchorMax = new Vector2(0.5f, 1f);
        lrt.offsetMin = new Vector2(22f, 0f);
        lrt.offsetMax = Vector2.zero;
        Label(labelGo.transform, "Text", label, 19f, TextCream, TextAlignmentOptions.Left);

        var cell = Make("Cell", row.transform);
        var crt = cell.GetComponent<RectTransform>() ?? cell.AddComponent<RectTransform>();
        crt.anchorMin = new Vector2(1f, 0.5f);
        crt.anchorMax = new Vector2(1f, 0.5f);
        crt.pivot = new Vector2(1f, 0.5f);
        crt.sizeDelta = new Vector2(cellWidth, 42f);
        crt.anchoredPosition = new Vector2(-16f, 0f);
        return cell.transform;
    }

    /// <summary>‹ value › control in a cell; returns the value TMP (to be updated).</summary>
    public static TextMeshProUGUI Stepper(Transform cell, string value, UnityAction onLeft, UnityAction onRight)
    {
        var leftGo = Make("Left", cell);
        Box(leftGo, new Vector2(-100f, 0f), new Vector2(26f, 34f));
        Simple(leftGo, GameUiAssetLibrary.Arrow, TextCream, raycast: true);
        MakeButton(leftGo, onLeft);

        var valTmp = LabelBox(cell, "Value", Vector2.zero, new Vector2(130f, 40f),
            value, 20f, TextCream, TextAlignmentOptions.Center);

        var rightGo = Make("Right", cell);
        var rightRt = Box(rightGo, new Vector2(100f, 0f), new Vector2(26f, 34f));
        Simple(rightGo, GameUiAssetLibrary.Arrow, TextCream, raycast: true);
        rightRt.localScale = new Vector3(-1f, 1f, 1f);
        MakeButton(rightGo, onRight);

        return valTmp;
    }

    /// <summary>Makes the GameObject clickable (invisible raycast Image if needed + Button with no transition).</summary>
    public static Button MakeButton(GameObject go, UnityAction onClick)
    {
        var hit = go.GetComponent<Image>();
        if (hit == null)
        {
            hit = go.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
        }
        hit.raycastTarget = true;
        var btn = go.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(onClick);
        return btn;
    }
}
