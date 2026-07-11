using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI.Sketch;

/// <summary>
/// Fabriques de widgets uGUI habilles avec les sprites « croquis » du jeu (cf. GameUiAssetLibrary).
/// Briques bas niveau reutilisables par les ecrans du menu natif (panneaux, icones, labels, boutons).
/// </summary>
internal static class SketchUiKit
{
    // ── Palette (alignee sur l'art du photo-mode : panneaux bleu nuit, texte creme, accent dore) ──
    public static readonly Color PanelTint  = Color.white;                       // l'art porte deja le bleu nuit
    public static readonly Color FrameTint  = new(1f, 1f, 0.96f, 1f);            // #FFFFF4 (teinte du contour)
    public static readonly Color TextCream  = new(0.91f, 0.89f, 0.83f, 1f);
    public static readonly Color TextDim    = new(0.91f, 0.89f, 0.83f, 0.55f);
    public static readonly Color Accent     = new(0.79f, 0.64f, 0.42f, 1f);      // dore piton
    public static readonly Color IconActive = Color.white;
    public static readonly Color IconIdle   = new(1f, 1f, 1f, 0.45f);
    public static readonly Color FieldBg    = new(0.09f, 0.12f, 0.21f, 0.95f);   // champ encastre bleu nuit
    public static readonly Color RowTint    = new(0.04f, 0.06f, 0.12f, 0.55f);   // bande de rangee (plus sombre)
    public static readonly Color ButtonText = new(0.20f, 0.18f, 0.15f, 1f);      // texte fonce sur bouton clair

    public static GameObject Make(string name, Transform parent) => MultiplayerPanelTheme.MakeGo(name, parent);

    /// <summary>RectTransform ancre par coins (anchorMin/Max) + offsets en pixels.</summary>
    public static RectTransform Rect(GameObject go, Vector2 aMin, Vector2 aMax,
        Vector2 offMin = default, Vector2 offMax = default)
        => MultiplayerPanelTheme.Anchor(go, aMin, aMax, offMin, offMax);

    /// <summary>RectTransform a position/taille fixes (ancre au centre du parent).</summary>
    public static RectTransform Box(GameObject go, Vector2 anchoredPos, Vector2 size)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;
        return rt;
    }

    /// <summary>Bande ancree en haut du parent, pleine largeur, hauteur fixe.</summary>
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

    /// <summary>Remplit le parent en pleine largeur, avec des marges haut/bas en pixels.</summary>
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

    /// <summary>Image 9-slice (cadre/panneau) avec un sprite du jeu.</summary>
    public static Image Sliced(GameObject go, string spriteName, Color color, bool raycast = false)
    {
        var img = go.AddComponent<Image>();
        img.sprite = GameUiAssetLibrary.Get(spriteName);
        img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    /// <summary>Image simple (icone/deco), ratio preserve.</summary>
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

    /// <summary>Aplat de couleur (raycast optionnel) couvrant le GameObject.</summary>
    public static Image FillColor(GameObject go, Color color, bool raycast = false)
    {
        var img = go.AddComponent<Image>();
        img.sprite = GameUiAssetLibrary.Get(GameUiAssetLibrary.White);
        img.type = Image.Type.Simple;
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    public static TextMeshProUGUI Label(Transform parent, string name, string text, float size,
        Color color, TextAlignmentOptions align, bool logo = false)
    {
        var font = logo ? GameUiAssetLibrary.LogoFont : GameUiAssetLibrary.TextFont;
        return MultiplayerPanelTheme.Tmp(parent, name, text, font, size, color, align);
    }

    /// <summary>
    /// Champ texte editable habille avec le fond arrondi natif (RowBg). Construit viewport + masque +
    /// texte + placeholder et renvoie le TMP_InputField pret. Remplit le parent fourni.
    /// </summary>
    public static TMP_InputField NativeField(Transform parent, string placeholder, int characterLimit)
    {
        var root = Make("Field", parent);
        MultiplayerPanelTheme.FullStretch(root);
        // RowBg est un sprite clair : on le teinte en bleu nuit fonce pour un champ encastre lisible.
        Sliced(root, GameUiAssetLibrary.RowBg, FieldBg, raycast: true);

        // Viewport interne (TMP_InputField exige un viewport pour clipper le texte).
        var viewport = Make("Viewport", root.transform);
        Rect(viewport, Vector2.zero, Vector2.one, new Vector2(14f, 6f), new Vector2(-14f, -6f));
        var vpImg = viewport.AddComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0f);
        vpImg.raycastTarget = false;
        viewport.AddComponent<RectMask2D>();

        var textGo = Make("Text", viewport.transform);
        MultiplayerPanelTheme.FullStretch(textGo);
        var textTmp = textGo.AddComponent<TextMeshProUGUI>();
        if (GameUiAssetLibrary.TextFont != null) textTmp.font = GameUiAssetLibrary.TextFont;
        textTmp.fontSize = 16f;
        textTmp.color = TextCream;
        textTmp.alignment = TextAlignmentOptions.Left;
        textTmp.raycastTarget = false;
        textTmp.enableWordWrapping = false;
        textTmp.overflowMode = TextOverflowModes.Masking;

        var phGo = Make("Placeholder", viewport.transform);
        MultiplayerPanelTheme.FullStretch(phGo);
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

    /// <summary>Label dans une cellule positionnee (Box) ; renvoie le TMP pour mise a jour ulterieure.</summary>
    public static TextMeshProUGUI LabelBox(Transform parent, string name, Vector2 pos, Vector2 size,
        string text, float fontSize, Color color, TextAlignmentOptions align, bool logo = false)
    {
        var cell = Make(name, parent);
        Box(cell, pos, size);
        return Label(cell.transform, "Text", text, fontSize, color, align, logo);
    }

    /// <summary>Onglet a icone (icones photo-mode reaffectees) ; teinte selon l'etat actif.</summary>
    public static GameObject Tab(Transform parent, string iconSprite, Vector2 pos, float size,
        bool active, UnityAction onClick)
    {
        var cell = Make("Tab", parent);
        Box(cell, pos, new Vector2(size, size));
        Simple(cell, iconSprite, active ? IconActive : IconIdle, raycast: onClick != null);
        if (onClick != null) MakeButton(cell, onClick);
        return cell;
    }

    /// <summary>Fleche ‹ (sprite natif) ou › (miroir). Cliquable si onClick fourni.</summary>
    public static GameObject ArrowButton(Transform parent, Vector2 pos, Vector2 size, bool left, UnityAction onClick)
    {
        var go = Make(left ? "ArrowLeft" : "ArrowRight", parent);
        var rt = Box(go, pos, size);
        Simple(go, GameUiAssetLibrary.Arrow, TextCream, raycast: onClick != null);
        if (!left) rt.localScale = new Vector3(-1f, 1f, 1f);   // miroir pour pointer a droite
        if (onClick != null) MakeButton(go, onClick);
        return go;
    }

    /// <summary>Rangee en bande pleine largeur : fond + label gauche + cellule de controle droite (largeur fixe).</summary>
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

    /// <summary>Controle ‹ valeur › dans une cellule ; renvoie le TMP de valeur (a mettre a jour).</summary>
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

    /// <summary>Rend le GameObject cliquable (Image raycast invisible si besoin + Button sans transition).</summary>
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
