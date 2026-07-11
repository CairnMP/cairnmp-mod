using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI;

/// <summary>
/// Color palette and UI object construction helpers for the multiplayer panel.
/// Visual direction "Minimal warm": dark with a gold accent (piton color)
/// reserved for active states and primary buttons.
/// </summary>
internal static class MultiplayerPanelTheme
{
    // ── Palette ───────────────────────────────────────────────────────────────

    public static readonly Color Overlay      = new(0.00f, 0.00f, 0.00f, 0.62f);
    public static readonly Color CardBg       = new(0.086f, 0.075f, 0.060f, 0.97f);  // #16130f
    public static readonly Color SubBg        = new(0.000f, 0.000f, 0.000f, 0.30f);  // input/section
    public static readonly Color Border       = new(1.000f, 1.000f, 1.000f, 0.12f);
    public static readonly Color BorderSubtle = new(1.000f, 1.000f, 1.000f, 0.07f);
    public static readonly Color BorderFocus  = new(0.788f, 0.639f, 0.420f, 1.00f);  // accent

    public static readonly Color TextPrimary  = new(0.910f, 0.871f, 0.788f, 1.00f);  // #e8dec9
    public static readonly Color TextMuted    = new(0.910f, 0.871f, 0.788f, 0.55f);
    public static readonly Color TextDim      = new(0.910f, 0.871f, 0.788f, 0.38f);

    public static readonly Color Accent       = new(0.788f, 0.639f, 0.420f, 1.00f);  // #c9a36b — piton
    public static readonly Color AccentBg     = new(0.788f, 0.639f, 0.420f, 0.06f);
    public static readonly Color AccentSolid  = new(0.788f, 0.639f, 0.420f, 1.00f);

    public static readonly Color StatusOk     = new(0.380f, 0.760f, 0.450f, 1.00f);
    public static readonly Color StatusError  = new(0.820f, 0.380f, 0.330f, 1.00f);
    public static readonly Color StatusWarn   = new(0.860f, 0.730f, 0.290f, 1.00f);

    public static readonly Color DangerText   = new(0.820f, 0.380f, 0.330f, 0.85f);

    // ── GameObject helpers ────────────────────────────────────────────────────

    public static GameObject MakeGo(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    public static RectTransform Anchor(GameObject go, Vector2 min, Vector2 max,
        Vector2 offsetMin = default, Vector2 offsetMax = default)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return rt;
    }

    public static void FullStretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    /// <summary>Creates a configured TextMeshProUGUI. Anchors cover the whole parent by default.</summary>
    public static TextMeshProUGUI Tmp(Transform parent, string name, string text,
        TMP_FontAsset font, float size, Color color, TextAlignmentOptions align,
        FontStyles style = FontStyles.Normal)
    {
        var go  = MakeGo(name, parent);
        FullStretch(go);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.fontSize      = size;
        tmp.fontStyle     = style;
        tmp.color         = color;
        tmp.alignment     = align;
        tmp.text          = text;
        tmp.raycastTarget = false;
        return tmp;
    }

    /// <summary>Creates a solid Image filling its parent.</summary>
    public static Image Fill(GameObject go, Color color, bool raycast = false)
    {
        var img = go.AddComponent<Image>();
        img.color         = color;
        img.raycastTarget = raycast;
        return img;
    }

    /// <summary>Rectangular border simulated with 4 thin lines.</summary>
    public static void DrawBorder(Transform parent, Color color, float thickness = 1f)
    {
        Edge(parent, color, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -thickness), Vector2.zero);
        Edge(parent, color, new Vector2(0, 0), new Vector2(1, 0), Vector2.zero, new Vector2(0, thickness));
        Edge(parent, color, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(thickness, 0));
        Edge(parent, color, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-thickness, 0), Vector2.zero);
    }

    private static void Edge(Transform parent, Color color, Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax)
    {
        var go = MakeGo("Edge", parent);
        Anchor(go, aMin, aMax, oMin, oMax);
        Fill(go, color);
    }
}
