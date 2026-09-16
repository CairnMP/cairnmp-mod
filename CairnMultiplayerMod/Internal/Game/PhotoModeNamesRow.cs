using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using Il2CppTGBTools.PhotoMode;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Native legend rows carry input handlers, so clones stay inactive until those handlers are
/// stripped; otherwise they contend with Cairn's photo controls.
/// </summary>
internal static class PhotoModeNamesRow
{
    private const int MissingUiRetryFrames = 300;
    private static PhotoModeUI _ui;
    private static GameObject _row;
    private static TextMeshProUGUI _label;
    private static int _nextScanFrame;
    private static bool _lastShown;
    private static bool _injectFailedLogged;

    public static void Tick()
    {
        if (_row != null && _label != null)
        {
            UpdateLabel();
            return;
        }

        if (_ui == null)
        {
            // PhotoModeUI is persistent and normally exists while hidden. Keep that instance
            // instead of repeating a global scene scan until the player opens photo mode.
            if (Time.frameCount < _nextScanFrame) return;
            _nextScanFrame = Time.frameCount + MissingUiRetryFrames;

            try
            {
                var found = Object.FindObjectsOfType<PhotoModeUI>();
                if (found != null && found.Length > 0) _ui = found[0];
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("photo-mode.find-ui", exception);
                return;
            }
        }

        var ui = _ui;
        if (ui == null) return;

        // PhotoModeUI also exists at boot while hidden and is not ready for injection then.
        if (!IsPhotoModeOpen(ui)) return;

        TryInject(ui);
    }

    private static void Reset()
    {
        _row = null;
        _label = null;
    }

    internal static void InvalidateNativeUiCache()
    {
        _ui = null;
        _nextScanFrame = 0;
        if (_row == null) _label = null;
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
        catch (Exception exception)
        {
            ModLog.SuppressedException("photo-mode.read-visibility", exception);
            return false;
        }
    }

    private static void TryInject(PhotoModeUI ui)
    {
        GameObject holder = null;
        try
        {
            var template = ui.hideMenuTextMesh;
            if (template == null) return;
            var rowT = template.transform.parent;
            if (rowT == null) return;
            var container = rowT.parent;
            if (container == null) return;

            // InputImageAction assigns the keycap sprite after boot; cloning sooner leaves a
            // white square after the script is stripped from the copy.
            if (!RowHasStyledKeycap(rowT)) return;

            // An unexpected TMP count means the binding resolved the panel instead of one row.
            var templTmps = rowT.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (templTmps == null || templTmps.Length > 4)
            {
                if (!_injectFailedLogged)
                {
                    _injectFailedLogged = true;
                    ModLog.Warning($"[PhotoNames] Unexpected row structure " +
                        $"(tmpCount={(templTmps == null ? -1 : templTmps.Length)}) — skipping injection");
                }
                return;
            }

            // Clone under an inactive parent so native input scripts never receive OnEnable.
            holder = new GameObject("CairnMP_RowHolder");
            holder.SetActive(false);

            GameObject clone = Object.Instantiate(rowT.gameObject, holder.transform);
            clone.name = "CairnMP_ToggleNamesRow";

            // Duplicate native input handlers would contend with the original photo controls.
            StripNonVisualComponents(clone);

            // The stripped InputPrompt owns runtime styling, so copy its resolved appearance
            // before activating the clone.
            CopyNativeVisuals(rowT.gameObject, clone);

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
            _lastShown = !RemotePlayerManager.ShowNames;
            UpdateLabel();

            clone.transform.SetParent(container, false);
            clone.SetActive(true);

            PlaceRow(ui, clone, rowT, container);

            ModLog.Info($"[PhotoNames] Injected legend row under '{container.name}' " +
                $"(tmps={tmps.Length}, label='{(label != null ? label.gameObject.name : "?")}', " +
                $"keycapSet={keycap != null})");
        }
        catch (Exception ex)
        {
            if (!_injectFailedLogged)
            {
                _injectFailedLogged = true;
                ModLog.Warning($"[PhotoNames] Injection failed: {ex.GetType().Name}: {ex.Message}");
            }
            Reset();
        }
        finally
        {
            if (holder != null)
            {
                try { Object.Destroy(holder); }
                catch (Exception exception) { ModLog.SuppressedException("photo-mode.destroy-row-holder", exception); }
            }
        }
    }

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
                try { Object.DestroyImmediate(c); }
                catch (Exception exception) { ModLog.SuppressedException("photo-mode.remove-cloned-component", exception); }
            }
        }
    }

    /// <summary>The stripped InputPrompt owns runtime styling, so its resolved visuals must be copied.</summary>
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
        catch (Exception exception)
        {
            ModLog.SuppressedException("photo-mode.copy-row-appearance", exception);
        }
    }

    /// <summary>Cloning before InputPrompt assigns its sprite leaves a permanent white keycap.</summary>
    private static bool RowHasStyledKeycap(Transform row)
    {
        try
        {
            var imgs = row.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < imgs.Length; i++)
                if (imgs[i] != null && imgs[i].sprite != null) return true;
        }
        catch (Exception exception) { ModLog.SuppressedException("photo-mode.inspect-row-visuals", exception); }
        return false;
    }

    private static bool IsVisualComponent(Component c)
    {
        return c.TryCast<Transform>() != null
            || c.TryCast<CanvasRenderer>() != null
            || c.TryCast<Canvas>() != null
            || c.TryCast<TextMeshProUGUI>() != null
            || c.TryCast<Image>() != null
            || c.TryCast<RawImage>() != null
            || c.TryCast<LayoutElement>() != null
            || c.TryCast<HorizontalOrVerticalLayoutGroup>() != null
            || c.TryCast<GridLayoutGroup>() != null
            || c.TryCast<ContentSizeFitter>() != null
            || c.TryCast<Mask>() != null
            || c.TryCast<RectMask2D>() != null
            || c.TryCast<Shadow>() != null;
    }

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
        catch (Exception exception) { ModLog.SuppressedException("photo-mode.update-toggle-label", exception); }
    }
}
