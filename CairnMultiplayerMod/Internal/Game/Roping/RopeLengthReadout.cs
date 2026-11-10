using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// While the pair is on the team rope, Cairn's own rope warning shows the metres left
/// on that rope, and only once the rope is almost out. The trial line in the corner is gone.
/// A camp and the quick inventory keep the hide Cairn already applied.
/// </summary>
internal static class RopeLengthReadout
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeLengthWarning");
    private static readonly List<GameObject> ForcedOff = new();
    private static readonly List<float> PictureDistances = new();
    private static bool _installed;
    private static RopeLengthUI _widget;
    private static bool _restCaptured;
    private static Color _rest;

    internal static void Bind(HudAdapter hud)
    {
        // The warning is Cairn's widget. The mod HUD no longer prints the coil.
    }

    public static void Install()
    {
        if (_installed) return;
        try
        {
            var update = AccessTools.Method(typeof(RopeLengthUI), nameof(RopeLengthUI.Update))
                         ?? throw new MissingMethodException(nameof(RopeLengthUI), nameof(RopeLengthUI.Update));
            Harmony.Patch(update, postfix: new HarmonyMethod(typeof(RopeLengthReadout), nameof(AfterWarning)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Rope warning hook unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        Hide();
    }

    internal static void Tick()
    {
        var warning = Widget();
        if (warning == null) return;
        try
        {
            // The game's own update writes the warning, and the hook after it plays the shake.
            // This pass covers a frame where that update did not run.
            Present(warning, shake: false);
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.length-warning", ex);
        }
    }

    internal static void Hide()
    {
        try { Release(); }
        catch (Exception ex) { ModLog.SuppressedException("rope.length-warning", ex); }
        _widget = null;
        _restCaptured = false;
    }

    /// <summary>Runs after Cairn has written the warning, so the team rope replaces that write.</summary>
    private static void AfterWarning(RopeLengthUI __instance)
    {
        var warning = Widget();
        if (warning == null || __instance == null || warning.Pointer != __instance.Pointer) return;
        try { Present(warning, shake: true); }
        catch (Exception ex) { ModLog.SuppressedException("rope.length-warning", ex); }
    }

    private static void Present(RopeLengthUI warning, bool shake)
    {
        if (warning == null) return;
        if (!RopeLengthPatch.TryReadRemaining(out var remaining))
        {
            CaptureRest(warning);
            Release();
            if (shake) RopeLengthPatch.ConsumeRefusedStep(out _);
            return;
        }

        if (warning.HasHiddenReasons())
        {
            if (shake) RopeLengthPatch.ConsumeRefusedStep(out _);
            return;
        }

        var show = RopeLengthPolicy.ShowsAlmostOut(remaining, warning.maxRopeDistToDisplayUI);
        if (!show)
        {
            CaptureRest(warning);
            SetShown(warning, false);
            if (shake) RopeLengthPatch.ConsumeRefusedStep(out _);
            return;
        }

        if (!warning.gameObject.activeSelf) warning.gameObject.SetActive(true);
        if (!warning.enabled) warning.enabled = true;
        SetShown(warning, true);
        Write(warning, remaining);
        if (!shake || !RopeLengthPatch.ConsumeRefusedStep(out var direction)) return;
        warning.OnRopeForbidGoingToward(direction);
    }

    private static void Write(RopeLengthUI warning, float remaining)
    {
        var text = warning.textDisplay;
        if (text != null)
        {
            var written = RopeLengthPolicy.WarningText(
                remaining, warning.lengthFloatFormatDisplay, warning.lengthPostNumbersDisplay);
            if (written != null) text.text = written;
            var blend = RopeLengthPolicy.EndColorBlend(
                remaining, warning.maxRopeDistToDisplayUI, warning.maxRopeDistToAddTextOutline);
            var from = _restCaptured ? _rest : Color.white;
            text.color = Color.Lerp(from, warning.textColorWhenReachingZero, blend);
            text.outlineWidth = RopeLengthPolicy.UsesOutline(remaining, warning.maxRopeDistToAddTextOutline)
                ? warning.textOutlineSize
                : 0f;
        }

        var pictures = warning.orderedRopeUIData;
        if (pictures == null || warning.ropeImage == null) return;
        PictureDistances.Clear();
        for (var i = 0; i < pictures.Count; i++)
        {
            var row = pictures[i];
            PictureDistances.Add(row != null ? row.minDistToDisplaySprite : float.NaN);
        }

        var index = RopeLengthPolicy.RopePictureIndex(remaining, PictureDistances);
        if (index < 0) return;
        var chosen = pictures[index];
        if (chosen != null && chosen.sprite != null) warning.ropeImage.sprite = chosen.sprite;
    }

    /// <summary>
    /// The ordinary number color, taken while the warning is not yet at its end color.
    /// The first look can already be the end color when Cairn's own rope is spent.
    /// </summary>
    private static void CaptureRest(RopeLengthUI warning)
    {
        if (_restCaptured) return;
        var text = warning.textDisplay;
        if (text == null) return;
        var current = text.color;
        if (SameColor(current, warning.textColorWhenReachingZero)) return;
        _rest = current;
        _restCaptured = true;
    }

    private static bool SameColor(Color a, Color b)
        => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f
           && Mathf.Abs(a.b - b.b) < 0.02f && Mathf.Abs(a.a - b.a) < 0.02f;

    private static void SetShown(RopeLengthUI warning, bool shown)
    {
        var parts = warning.hideableUIParts;
        if (parts == null) return;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part == null) continue;
            // Switching off the warning itself, or a parent of it, stops this update.
            var ancestor = part == warning.gameObject
                || (warning.transform != null && part.transform != null
                    && warning.transform.IsChildOf(part.transform));
            if (!shown)
            {
                if (ancestor || !part.activeSelf) continue;
                part.SetActive(false);
                if (!AlreadyForced(part)) ForcedOff.Add(part);
                continue;
            }

            if (!part.activeSelf) part.SetActive(true);
        }

        if (shown) ForcedOff.Clear();
    }

    private static bool AlreadyForced(GameObject part)
    {
        for (var i = 0; i < ForcedOff.Count; i++)
            if (ForcedOff[i] == part) return true;
        return false;
    }

    /// <summary>Parts this warning switched off go back on, so a later solo rope can show them.</summary>
    private static void Release()
    {
        for (var i = 0; i < ForcedOff.Count; i++)
        {
            var part = ForcedOff[i];
            if (part != null) part.SetActive(true);
        }

        ForcedOff.Clear();
    }

    private static RopeLengthUI Widget()
    {
        if (_widget != null) return _widget;
        try
        {
            var found = Resources.FindObjectsOfTypeAll<GameHud>();
            if (found == null) return null;
            for (var i = 0; i < found.Length; i++)
            {
                var hud = found[i];
                if (hud == null || hud.gameObject == null || !hud.gameObject.scene.IsValid()) continue;
                var warning = hud.ropeLengthUI;
                if (warning == null) continue;
                _widget = warning;
                return _widget;
            }
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.length-warning", ex);
        }

        return null;
    }
}
