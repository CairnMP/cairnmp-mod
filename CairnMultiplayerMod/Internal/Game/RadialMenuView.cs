using System;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>Draws the controller radial menu with IMGUI, like the rest of the mod's HUD.</summary>
internal static class RadialMenuView
{
    private static readonly Color Backdrop = new(0f, 0f, 0f, 0.35f);
    private static readonly Color Idle = new(0.08f, 0.08f, 0.1f, 0.85f);
    private static readonly Color Chosen = new(0.95f, 0.75f, 0.25f, 0.95f);
    private static readonly Color IdleText = new(0.92f, 0.92f, 0.92f, 1f);
    private static readonly Color ChosenText = new(0.08f, 0.08f, 0.1f, 1f);

    private static GUIStyle _style;

    internal static void Draw(RadialMenuState state, RadialContext context)
    {
        if (!state.IsVisible) return;
        if (Event.current?.type != EventType.Repaint) return;

        var items = ControllerShortcutBindings.RadialItems;
        var scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
        var center = new Vector2(Screen.width / 2f, Screen.height / 2f);

        // Neighbours on the ring are about (radius x 1.18) apart, so the boxes have to stay
        // narrower than that or they overlap.
        var radius = 240f * scale;
        var size = new Vector2(215f * scale, 48f * scale);

        _style ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            wordWrap = false,
            clipping = TextClipping.Clip,
        };

        var previous = GUI.color;
        Fill(new Rect(0f, 0f, Screen.width, Screen.height), Backdrop);

        for (var i = 0; i < items.Length; i++)
        {
            var angle = 2f * MathF.PI * i / items.Length;
            var middle = center + new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * radius;
            var rect = new Rect(middle.x - size.x / 2f, middle.y - size.y / 2f, size.x, size.y);
            var selected = i == state.SelectedIndex;
            var label = RadialMenuLabels.For(items[i], context);

            Fill(rect, selected ? Chosen : Idle);
            GUI.color = selected ? ChosenText : IdleText;
            _style.fontSize = Mathf.RoundToInt(18f * scale);
            GUI.Label(rect, label, _style);
        }

        GUI.color = IdleText;
        _style.fontSize = Mathf.RoundToInt(18f * scale);
        var hint = new Rect(center.x - 160f * scale, center.y - 20f * scale, 320f * scale, 40f * scale);
        GUI.Label(hint, state.SelectedIndex < 0 ? "Aim with the right stick" : "Release to choose", _style);
        GUI.color = previous;
    }

    private static void Fill(Rect rect, Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
    }
}
