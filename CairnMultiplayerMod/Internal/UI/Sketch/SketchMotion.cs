using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CairnMultiplayerMod.Internal.UI.Sketch;

/// <summary>
/// The panel's motion: what grows under the cursor, what fades in, what settles.
///
/// Everything is driven from the panel's own per-frame tick rather than from components on
/// the objects. Adding a MonoBehaviour under IL2CPP means registering a type with the
/// runtime, and hover state would still have to be tracked somewhere; a dozen rectangle
/// tests per frame costs less and keeps the whole behaviour readable in one file.
/// </summary>
internal static class SketchMotion
{
    private const float HoverScale = 1.05f;
    private const float PressScale = 0.975f;
    private const float ScaleSpeed = 14f;
    private const float FadeSpeed = 6f;

    /// <summary>How far a wide element slides towards the reader when it lights up.</summary>
    private const float HoverSlide = 10f;

    private sealed class Tracked
    {
        internal RectTransform Target;
        internal float Current = 1f;

        /// <summary>
        /// The scale the element was built with, mirroring included: the kit makes a right
        /// arrow by flipping a left one on X, and scaling from Vector3.one would quietly turn
        /// it back around.
        /// </summary>
        internal Vector3 BaseScale = Vector3.one;
        internal List<Accented> Texts;

        /// <summary>Set for wide elements, which slide and grow an edge rather than only
        /// scaling: a card 660 pixels across barely reads a few percent of scale.</summary>
        internal bool IsCard;
        internal Vector2 BasePosition;
        internal Image Edge;
    }

    /// <summary>
    /// A label that lights up with its element. The colour tint of a Selectable only reaches
    /// its target graphic, so a card's own title stays dim however bright the card gets --
    /// which is exactly the half-lit look a hover is supposed to remove.
    /// </summary>
    private sealed class Accented
    {
        internal TextMeshProUGUI Text;
        internal Color Idle, Active;
    }

    private sealed class Fade
    {
        internal CanvasGroup Group;
        internal RectTransform Scaled;
        internal float Progress;
        internal float From;
    }

    private static readonly List<Tracked> Tracked_ = new();
    private static readonly List<Fade> Fades = new();

    /// <summary>Follows an element with the cursor and the controller focus.</summary>
    internal static void Track(GameObject go)
    {
        var rect = go != null ? go.GetComponent<RectTransform>() : null;
        if (rect == null) return;
        Tracked_.Add(new Tracked { Target = rect, BaseScale = rect.localScale });
    }

    /// <summary>
    /// Promotes a tracked element to a card: it slides towards the reader and grows a golden
    /// edge while it is hovered or focused. The edge is built here so a caller only has to
    /// say which elements are cards.
    /// </summary>
    internal static void Card(GameObject owner)
    {
        var rect = owner != null ? owner.GetComponent<RectTransform>() : null;
        if (rect == null) return;

        foreach (var tracked in Tracked_)
        {
            if (tracked.Target != rect) continue;
            tracked.IsCard = true;
            tracked.BasePosition = rect.anchoredPosition;
            tracked.Edge = BuildEdge(owner);
            return;
        }
    }

    private static Image BuildEdge(GameObject owner)
    {
        var edge = SketchUiKit.Make("HoverEdge", owner.transform);
        var rect = edge.GetComponent<RectTransform>() ?? edge.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.offsetMin = new Vector2(0f, 8f);
        rect.offsetMax = new Vector2(5f, -8f);
        var image = SketchUiKit.FillColor(edge, SketchUiKit.Accent);
        var colour = image.color;
        colour.a = 0f;
        image.color = colour;
        return image;
    }

    /// <summary>
    /// Lights <paramref name="text"/> up while <paramref name="owner"/> is hovered or
    /// focused. The element must already be tracked, which every button is.
    /// </summary>
    internal static void Accent(GameObject owner, TextMeshProUGUI text, Color active)
    {
        if (owner == null || text == null) return;
        var rect = owner.GetComponent<RectTransform>();
        if (rect == null) return;

        foreach (var tracked in Tracked_)
        {
            if (tracked.Target != rect) continue;
            (tracked.Texts ??= new List<Accented>()).Add(
                new Accented { Text = text, Idle = text.color, Active = active });
            return;
        }
    }

    /// <summary>
    /// Fades a group in, optionally lifting it from a slightly smaller scale. Used for the
    /// panel opening and for every screen change, so a new screen arrives instead of
    /// replacing the old one between two frames.
    /// </summary>
    internal static void FadeIn(CanvasGroup group, RectTransform scaled = null, float fromScale = 1f)
    {
        if (group == null) return;
        group.alpha = 0f;
        if (scaled != null) scaled.localScale = Vector3.one * fromScale;
        Fades.Add(new Fade { Group = group, Scaled = scaled, Progress = 0f, From = fromScale });
    }

    /// <summary>Drops every tracked element. Called whenever the panel rebuilds a screen:
    /// the objects behind these handles are destroyed a frame later.</summary>
    internal static void Clear()
    {
        Tracked_.Clear();
        Fades.Clear();
    }

    /// <summary>
    /// Decoration must never cost the panel. The host wrapper drops the whole native panel
    /// for its fallback on any exception raised from a tick, so anything that goes wrong in
    /// here is swallowed and reported once instead.
    /// </summary>
    internal static void Tick(float dt)
    {
        if (dt <= 0f) dt = Time.unscaledDeltaTime;
        try
        {
            TickFades(dt);
            TickHover(dt);
        }
        catch (Exception exception)
        {
            if (_failureLogged) return;
            _failureLogged = true;
            ModLog.Warning($"[Panel] Motion disabled after an error: {exception.Message}");
            Clear();
        }
    }

    private static bool _failureLogged;
    private static bool _hoverLogged;

    private static void TickFades(float dt)
    {
        for (var index = Fades.Count - 1; index >= 0; index--)
        {
            var fade = Fades[index];
            if (fade.Group == null) { Fades.RemoveAt(index); continue; }

            fade.Progress = Mathf.Min(1f, fade.Progress + dt * FadeSpeed);
            // Ease out: the last frames of a fade are the ones the eye reads as motion.
            var eased = 1f - (1f - fade.Progress) * (1f - fade.Progress);
            fade.Group.alpha = eased;
            if (fade.Scaled != null)
                fade.Scaled.localScale = Vector3.one * Mathf.Lerp(fade.From, 1f, eased);

            if (fade.Progress >= 1f) Fades.RemoveAt(index);
        }
    }

    private static void TickCard(Tracked tracked, bool active, float dt)
    {
        if (!tracked.IsCard) return;

        var step = Mathf.Min(1f, dt * ScaleSpeed);
        var wanted = tracked.BasePosition + new Vector2(active ? HoverSlide : 0f, 0f);
        tracked.Target.anchoredPosition =
            Vector2.Lerp(tracked.Target.anchoredPosition, wanted, step);

        if (tracked.Edge == null) return;
        var colour = tracked.Edge.color;
        colour.a = Mathf.Lerp(colour.a, active ? 1f : 0f, step);
        tracked.Edge.color = colour;
    }

    /// <summary>
    /// Proof the pointer is reaching the panel at all. Without it, a hover that never fires
    /// is indistinguishable from one that is simply too subtle to notice.
    /// </summary>
    private static void LogFirstHover()
    {
        if (_hoverLogged) return;
        _hoverLogged = true;
        ModLog.Debug("[Panel] Pointer hover detected on a panel element");
    }

    private static void TickTexts(Tracked tracked, bool active, float dt)
    {
        if (tracked.Texts == null) return;
        for (var index = tracked.Texts.Count - 1; index >= 0; index--)
        {
            var accented = tracked.Texts[index];
            if (accented.Text == null) { tracked.Texts.RemoveAt(index); continue; }
            accented.Text.color = Color.Lerp(accented.Text.color,
                active ? accented.Active : accented.Idle, Mathf.Min(1f, dt * ScaleSpeed));
        }
    }

    private static void TickHover(float dt)
    {
        if (Tracked_.Count == 0) return;

        var mouse = Mouse.current;
        var pointer = mouse != null ? (Vector2?)mouse.position.ReadValue() : null;
        var pressed = mouse != null && mouse.leftButton.isPressed;
        // A still mouse must not steal focus back from the controller, so the pointer only
        // claims it on the frames it actually moves.
        var pointerMoved = mouse != null && mouse.delta.ReadValue().sqrMagnitude > 0.01f;
        var eventSystem = UnityEngine.EventSystems.EventSystem.current;
        var focused = eventSystem != null ? eventSystem.currentSelectedGameObject : null;

        for (var index = Tracked_.Count - 1; index >= 0; index--)
        {
            var tracked = Tracked_[index];
            if (tracked.Target == null) { Tracked_.RemoveAt(index); continue; }

            var under = pointer.HasValue && RectTransformUtility.RectangleContainsScreenPoint(
                tracked.Target, pointer.Value, null);
            // The controller gets the same feedback as the mouse: whatever has focus is what
            // the player is about to activate. Hovering also moves that focus, so the two
            // never light up two different elements at once.
            var go = tracked.Target.gameObject;
            if (under && pointerMoved && eventSystem != null && focused != go)
            {
                eventSystem.SetSelectedGameObject(go);
                focused = go;
            }

            var active = under || (focused != null && focused == go);
            if (under) LogFirstHover();

            var wanted = !active ? 1f : pressed && under ? PressScale : HoverScale;
            tracked.Current = Mathf.Lerp(tracked.Current, wanted, Mathf.Min(1f, dt * ScaleSpeed));
            tracked.Target.localScale = tracked.BaseScale * tracked.Current;
            TickCard(tracked, active, dt);
            TickTexts(tracked, active, dt);
        }
    }
}
