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
/// Injects a "[N] Hide/Show player names" row into Cairn's native photo-mode
/// legend (PhotoModeUI), by cloning the "Hide UI" row to match the game's style
/// (keycap + label). The keycap becomes "N", the label follows the
/// RemotePlayerManager.ShowNames state.
///
/// IMPORTANT: the rows live under an 'Inputs' container and carry
/// InputAction/script components. Cloning them as-is DUPLICATES those handlers and
/// BLOCKS the game's input. We work around this in two steps:
///   1. instantiate the clone under an INACTIVE parent -> no OnEnable/script runs;
///   2. REMOVE all non-visual components before activating the row.
/// We also inject ONLY when photo mode is actually open (canvas visible), never at
/// boot.
/// </summary>
internal static class PhotoModeNamesRow
{
    private static GameObject _row;
    private static TextMeshProUGUI _label;
    private static int _nextScanFrame;
    private static bool _lastShown;
    private static bool _injectFailedLogged;

    /// <summary>Called every frame from Mod.OnUpdate (before the photo-suspension return).</summary>
    public static void Tick()
    {
        // Row already in place (and UI still alive) -> just refresh the text.
        if (_row != null && _label != null)
        {
            UpdateLabel();
            return;
        }

        // Throttle the search for PhotoModeUI (~2 Hz at 60 fps).
        if (Time.frameCount < _nextScanFrame) return;
        _nextScanFrame = Time.frameCount + 30;

        PhotoModeUI ui = null;
        try
        {
            var found = Object.FindObjectsOfType<PhotoModeUI>();
            if (found != null && found.Length > 0) ui = found[0];
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("photo-mode.find-ui", exception);
            return;
        }

        if (ui == null) { Reset(); return; }

        // Gate: wait for photo mode to actually open (canvas visible) — definitely
        // not at boot, where the persistent PhotoModeUI is present but hidden.
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
            var template = ui.hideMenuTextMesh;          // "Hide UI" label (row F)
            if (template == null) return;
            var rowT = template.transform.parent;         // the row (keycap + label)
            if (rowT == null) return;
            var container = rowT.parent;                  // the rows' container ('Inputs')
            if (container == null) return;

            // Readiness: at boot (Splashscreens) the row exists but is NOT yet styled —
            // the rounded-background sprite (and the keycap letter) are assigned later at
            // runtime by the InputImageAction script. Cloning before that => a white square
            // keycap. So we wait until at least one Image in the row has a sprite, then
            // clone: Instantiate then captures the real rounded background, which survives
            // the strip.
            if (!RowHasStyledKeycap(rowT)) return;

            // Guard: a real row = keycap + label (≈1-2 TMP). Too many TMPs = we grabbed the
            // whole panel -> bail out so we don't duplicate everything.
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

            // 1) Clone under an INACTIVE parent: the row's scripts never run (no
            //    OnEnable), so no input handler is duplicated.
            holder = new GameObject("CairnMP_RowHolder");
            holder.SetActive(false);

            GameObject clone = Object.Instantiate(rowT.gameObject, holder.transform);
            clone.name = "CairnMP_ToggleNamesRow";

            // 2) Remove every non-visual component (scripts/InputAction) BEFORE activation.
            StripNonVisualComponents(clone);

            // 3) The keycap's gray rounded background is a sprite/tint applied AT RUNTIME by
            //    the InputPrompt (which we just removed); without it the Image falls back to
            //    a white square. So we copy the original row's runtime appearance
            //    (sprite + color + material) onto the clone, index by index (identical
            //    hierarchy). Same for the text colors.
            CopyNativeVisuals(rowT.gameObject, clone);

            // Identify the label (same name as the original) + the keycap (the other TMP).
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
            _lastShown = !RemotePlayerManager.ShowNames;   // force the first refresh
            UpdateLabel();

            // Move the clone out of the holder into the real container (becomes active, no scripts).
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

    /// <summary>
    /// Destroys every non-visual component of the clone and its children (scripts,
    /// InputAction, etc.). We keep only what's strictly needed for rendering:
    /// RectTransform, CanvasRenderer, TMP, Image/RawImage, layout components and visual effects.
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
                try { Object.DestroyImmediate(c); }
                catch (Exception exception) { ModLog.SuppressedException("photo-mode.remove-cloned-component", exception); }
            }
        }
    }

    /// <summary>
    /// Copies the runtime appearance (sprite, color, material, type) of the original
    /// row onto the clone, Image by Image and TMP by TMP. Since the hierarchy is
    /// identical (exact clone), index-based pairing is reliable. Restores the keycap's
    /// gray rounded background that the InputPrompt used to provide at runtime.
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
        catch (Exception exception)
        {
            ModLog.SuppressedException("photo-mode.copy-row-appearance", exception);
        }
    }

    /// <summary>
    /// True if the row is styled (at least one Image with a sprite). Used to wait for
    /// the input script to assign the rounded background before cloning.
    /// </summary>
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
            || c.TryCast<Shadow>() != null;              // Outline derives from Shadow
    }

    /// <summary>
    /// Positions the cloned row. With a LayoutGroup, we insert it after "Hide UI"
    /// (the layout places it on its own). Without a layout, we compute the one-step
    /// vertical offset and place the row below the last one ("Quit").
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

        // delta = one step down (consecutive rows "Hide UI" -> "Reset").
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
