using System;
using Il2CppTGBTools.PhotoMode;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Injecte une ligne "[N] Hide/Show player names" dans la legende native du mode
/// photo de Cairn (PhotoModeUI), en clonant la rangee "Hide UI" pour matcher le
/// style du jeu (keycap + label). Le keycap devient "N", le label suit l'etat
/// RemotePlayerManager.ShowNames.
///
/// IMPORTANT : les rangees vivent sous un conteneur 'Inputs' et portent des
/// composants InputAction/script. Les cloner tels quels DUPLIQUE ces handlers et
/// BLOQUE les entrees du jeu. On contourne en deux temps :
///   1. on instancie le clone sous un parent INACTIF -> aucun OnEnable/script ne tourne ;
///   2. on RETIRE tous les composants non-visuels avant d'activer la rangee.
/// On n'injecte par ailleurs QUE lorsque le mode photo est reellement ouvert
/// (canvas visible), jamais au boot.
/// </summary>
public static class PhotoModeNamesRow
{
    private static GameObject _row;
    private static TextMeshProUGUI _label;
    private static int _nextScanFrame;
    private static bool _lastShown;
    private static bool _injectFailedLogged;

    /// <summary>Appele chaque frame depuis Mod.OnUpdate (avant le return de suspension photo).</summary>
    public static void Tick()
    {
        // Rangee deja en place (et UI toujours vivante) -> simple maj du texte.
        if (_row != null && _label != null)
        {
            UpdateLabel();
            return;
        }

        // Throttle la recherche de la PhotoModeUI (~2 Hz a 60 fps).
        if (Time.frameCount < _nextScanFrame) return;
        _nextScanFrame = Time.frameCount + 30;

        PhotoModeUI ui = null;
        try
        {
            var found = Object.FindObjectsOfType<PhotoModeUI>();
            if (found != null && found.Length > 0) ui = found[0];
        }
        catch { return; }

        if (ui == null) { Reset(); return; }

        // Gate : on attend l'ouverture reelle du mode photo (canvas visible) — surtout
        // pas au boot ou la PhotoModeUI persistante est presente mais masquee.
        if (!IsPhotoModeOpen(ui)) return;

        TryInject(ui);
    }

    private static void Reset()
    {
        _row = null;
        _label = null;
    }

    private static bool IsPhotoModeOpen(PhotoModeUI ui)
    {
        try
        {
            var cg = ui.canvasGroup;
            if (cg != null) return cg.alpha > 0.5f;
            var canvas = ui.canvas;
            return canvas != null && canvas.activeInHierarchy;
        }
        catch { return false; }
    }

    private static void TryInject(PhotoModeUI ui)
    {
        GameObject holder = null;
        try
        {
            var template = ui.hideMenuTextMesh;          // label "Hide UI" (rangee F)
            if (template == null) return;
            var rowT = template.transform.parent;         // la rangee (keycap + label)
            if (rowT == null) return;
            var container = rowT.parent;                  // le conteneur des rangees ('Inputs')
            if (container == null) return;

            // Readiness : au boot (Splashscreens) la rangee existe mais n'est PAS encore
            // stylee — le sprite du fond arrondi (et la lettre du keycap) sont assignes plus
            // tard a l'execution par le script InputImageAction. Cloner avant => keycap blanc
            // carre. On attend donc qu'au moins une Image de la rangee ait un sprite, puis on
            // clone : Instantiate capture alors le vrai fond arrondi, qui survit au strip.
            if (!RowHasStyledKeycap(rowT)) return;

            // Garde-fou : une vraie rangee = keycap + label (≈1-2 TMP). Trop de TMP =
            // on a attrape le panneau entier -> on abandonne pour ne pas tout dupliquer.
            var templTmps = rowT.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (templTmps == null || templTmps.Length > 4)
            {
                if (!_injectFailedLogged)
                {
                    _injectFailedLogged = true;
                    Mod.Log.Warning($"[PhotoNames] Unexpected row structure " +
                        $"(tmpCount={(templTmps == null ? -1 : templTmps.Length)}) — skipping injection");
                }
                return;
            }

            // 1) Clone sous un parent INACTIF : les scripts de la rangee ne s'executent
            //    jamais (pas d'OnEnable), donc aucun handler d'input n'est duplique.
            holder = new GameObject("CairnMP_RowHolder");
            holder.SetActive(false);

            GameObject clone = Object.Instantiate(rowT.gameObject, holder.transform);
            clone.name = "CairnMP_ToggleNamesRow";

            // 2) Retire tout composant non-visuel (scripts/InputAction) AVANT activation.
            StripNonVisualComponents(clone);

            // 3) Le fond arrondi gris du keycap est un sprite/teinte appliques A L'EXECUTION
            //    par l'InputPrompt (qu'on vient de retirer) ; sans lui l'Image retombe sur un
            //    carre blanc. On recopie donc l'apparence runtime de la rangee d'origine
            //    (sprite + couleur + materiau) vers le clone, index par index (hierarchie
            //    identique). Idem pour la couleur des textes.
            CopyNativeVisuals(rowT.gameObject, clone);

            // Identifie le label (meme nom que l'original) + le keycap (l'autre TMP).
            var tmps = clone.GetComponentsInChildren<TextMeshProUGUI>(true);
            TextMeshProUGUI label = null, keycap = null;
            string labelName = template.gameObject.name;
            for (int i = 0; i < tmps.Length; i++)
            {
                var t = tmps[i];
                if (t == null) continue;
                if (label == null && t.gameObject.name == labelName) label = t;
                else keycap = t;
            }
            if (label == null && tmps.Length > 0) label = tmps[0];

            if (keycap != null) keycap.text = "N";
            _label = label;
            _row = clone;
            _lastShown = !RemotePlayerManager.ShowNames;   // force la 1ere maj
            UpdateLabel();

            // Sort le clone du holder vers le vrai conteneur (devient actif, sans scripts).
            clone.transform.SetParent(container, false);
            clone.SetActive(true);

            PlaceRow(ui, clone, rowT, container);

            Mod.Log.Msg($"[PhotoNames] Injected legend row under '{container.name}' " +
                $"(tmps={tmps.Length}, label='{(label != null ? label.gameObject.name : "?")}', " +
                $"keycapSet={keycap != null})");
        }
        catch (Exception ex)
        {
            if (!_injectFailedLogged)
            {
                _injectFailedLogged = true;
                Mod.Log.Warning($"[PhotoNames] Injection failed: {ex.GetType().Name}: {ex.Message}");
            }
            Reset();
        }
        finally
        {
            if (holder != null)
            {
                try { Object.Destroy(holder); } catch { }
            }
        }
    }

    /// <summary>
    /// Detruit tous les composants non-visuels du clone et de ses enfants (scripts,
    /// InputAction, etc.). On ne garde que le strict necessaire au rendu : RectTransform,
    /// CanvasRenderer, TMP, Image/RawImage, composants de layout et effets visuels.
    /// </summary>
    private static void StripNonVisualComponents(GameObject root)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            var t = transforms[i];
            if (t == null) continue;
            var comps = t.gameObject.GetComponents<Component>();
            for (int j = 0; j < comps.Length; j++)
            {
                var c = comps[j];
                if (c == null) continue;
                if (IsVisualComponent(c)) continue;
                try { Object.DestroyImmediate(c); } catch { }
            }
        }
    }

    /// <summary>
    /// Recopie l'apparence runtime (sprite, couleur, materiau, type) de la rangee
    /// d'origine vers le clone, Image par Image et TMP par TMP. La hierarchie etant
    /// identique (clone exact), l'appariement par index est fiable. Restaure le fond
    /// arrondi gris du keycap que l'InputPrompt fournissait a l'execution.
    /// </summary>
    private static void CopyNativeVisuals(GameObject template, GameObject clone)
    {
        try
        {
            var srcImgs = template.GetComponentsInChildren<Image>(true);
            var dstImgs = clone.GetComponentsInChildren<Image>(true);
            int n = Math.Min(srcImgs.Length, dstImgs.Length);
            for (int i = 0; i < n; i++)
            {
                var s = srcImgs[i];
                var d = dstImgs[i];
                if (s == null || d == null) continue;
                d.sprite = s.sprite;
                d.color = s.color;
                d.material = s.material;
                d.type = s.type;
                d.preserveAspect = s.preserveAspect;
                d.enabled = s.enabled;
            }

            var srcTmps = template.GetComponentsInChildren<TextMeshProUGUI>(true);
            var dstTmps = clone.GetComponentsInChildren<TextMeshProUGUI>(true);
            int m = Math.Min(srcTmps.Length, dstTmps.Length);
            for (int i = 0; i < m; i++)
            {
                var s = srcTmps[i];
                var d = dstTmps[i];
                if (s == null || d == null) continue;
                d.color = s.color;
                d.fontSharedMaterial = s.fontSharedMaterial;
            }
        }
        catch
        {
            // Apparence non recopiable -> on garde le clone tel quel (au pire un fond uni).
        }
    }

    /// <summary>
    /// Vrai si la rangee est stylee (au moins une Image avec un sprite). Sert a attendre
    /// que le script d'input ait assigne le fond arrondi avant de cloner.
    /// </summary>
    private static bool RowHasStyledKeycap(Transform row)
    {
        try
        {
            var imgs = row.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < imgs.Length; i++)
                if (imgs[i] != null && imgs[i].sprite != null) return true;
        }
        catch { }
        return false;
    }

    private static bool IsVisualComponent(Component c)
    {
        return c.TryCast<Transform>() != null            // RectTransform/Transform
            || c.TryCast<CanvasRenderer>() != null
            || c.TryCast<Canvas>() != null
            || c.TryCast<TextMeshProUGUI>() != null
            || c.TryCast<Image>() != null
            || c.TryCast<RawImage>() != null
            || c.TryCast<LayoutElement>() != null
            || c.TryCast<HorizontalOrVerticalLayoutGroup>() != null  // base H/V layout
            || c.TryCast<GridLayoutGroup>() != null
            || c.TryCast<ContentSizeFitter>() != null
            || c.TryCast<Mask>() != null
            || c.TryCast<RectMask2D>() != null
            || c.TryCast<Shadow>() != null;              // Outline derive de Shadow
    }

    /// <summary>
    /// Positionne la rangee clonee. Avec un LayoutGroup, on l'insere apres "Hide UI"
    /// (le layout place tout seul). Sans layout, on calcule l'offset vertical d'un cran
    /// et on place la rangee sous la derniere ("Quit").
    /// </summary>
    private static void PlaceRow(PhotoModeUI ui, GameObject clone, Transform rowT, Transform container)
    {
        var layout = container.GetComponent<LayoutGroup>();
        if (layout != null)
        {
            clone.transform.SetSiblingIndex(rowT.GetSiblingIndex() + 1);
            return;
        }

        clone.transform.SetAsLastSibling();

        var cloneRect = clone.transform.TryCast<RectTransform>();
        var hideRect = rowT.TryCast<RectTransform>();
        var resetRect = ui.resetSettingsTextMesh != null
            ? ui.resetSettingsTextMesh.transform.parent.TryCast<RectTransform>() : null;
        var quitRect = ui.quitTextMesh != null
            ? ui.quitTextMesh.transform.parent.TryCast<RectTransform>() : null;
        if (cloneRect == null || hideRect == null || resetRect == null || quitRect == null)
            return;

        // delta = un cran vers le bas (rangees consecutives "Hide UI" -> "Reset").
        float delta = resetRect.anchoredPosition.y - hideRect.anchoredPosition.y;
        cloneRect.anchoredPosition = quitRect.anchoredPosition + new Vector2(0f, delta);
    }

    private static void UpdateLabel()
    {
        if (_label == null) return;
        bool shown = RemotePlayerManager.ShowNames;
        if (shown == _lastShown) return;
        _lastShown = shown;
        try { _label.text = shown ? "Hide player names" : "Show player names"; }
        catch { }
    }
}
