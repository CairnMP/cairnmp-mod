using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.World;

/// <summary>
/// The line a climber leaves behind them.
///
/// In Cairn the route you pick is the whole decision, so seeing where somebody actually went
/// is worth more than knowing where they are. Nothing is sent for this: every client already
/// receives everyone's position, so each one draws the others' trails from what it has.
/// </summary>
internal static class ClimbTrailManager
{
    /// <summary>A point is kept only after this much movement, so a climber resting on a
    /// ledge does not fill the buffer with the same spot.</summary>
    private const float MinPointSpacing = 2f;

    private const int MaxPoints = 400;
    private const float LineWidth = 0.08f;

    private sealed class Trail
    {
        internal readonly List<Vector3> Points = new();
        internal GameObject Root;
        internal LineRenderer Line;
    }

    private static readonly Dictionary<int, Trail> Trails = new();
    private static Material _material;
    private static bool _materialMissingLogged;
    private static bool _visible;

    internal static bool IsVisible => _visible;

    internal static void Record(int playerId, Vector3 point)
    {
        if (!Trails.TryGetValue(playerId, out var trail))
            Trails[playerId] = trail = new Trail();

        var points = trail.Points;
        if (points.Count > 0 && (points[points.Count - 1] - point).sqrMagnitude
            < MinPointSpacing * MinPointSpacing) return;

        points.Add(point);
        if (points.Count > MaxPoints) points.RemoveAt(0);
        if (_visible) Redraw(playerId, trail);
    }

    internal static void SetVisible(bool visible)
    {
        _visible = visible;
        foreach (var pair in Trails)
        {
            if (visible) Redraw(pair.Key, pair.Value);
            else DestroyLine(pair.Value);
        }
    }

    internal static void Forget(int playerId)
    {
        if (!Trails.Remove(playerId, out var trail)) return;
        DestroyLine(trail);
    }

    internal static void Clear()
    {
        foreach (var pair in Trails) DestroyLine(pair.Value);
        Trails.Clear();
    }

    /// <summary>Scene changes destroy the line objects under us; the points are kept so a
    /// trail survives streaming, and the renderer is rebuilt on the next redraw.</summary>
    internal static void OnSceneChanged()
    {
        foreach (var pair in Trails)
        {
            pair.Value.Root = null;
            pair.Value.Line = null;
        }
    }

    private static void Redraw(int playerId, Trail trail)
    {
        try
        {
            if (trail.Points.Count < 2) return;
            if (!EnsureLine(playerId, trail)) return;

            trail.Line.positionCount = trail.Points.Count;
            for (var index = 0; index < trail.Points.Count; index++)
                trail.Line.SetPosition(index, trail.Points[index]);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("trails.redraw", exception);
        }
    }

    private static bool EnsureLine(int playerId, Trail trail)
    {
        if (trail.Line != null) return true;

        var material = EnsureMaterial();
        if (material == null) return false;

        trail.Root = new GameObject($"CairnMP_Trail_{playerId}");
        UnityEngine.Object.DontDestroyOnLoad(trail.Root);
        var line = trail.Root.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.widthMultiplier = LineWidth;
        line.numCapVertices = 2;
        line.positionCount = 0;

        var colour = RemotePlayerManager.ColorForPlayer(playerId);
        line.startColor = colour;
        line.endColor = new Color(colour.r, colour.g, colour.b, 0.25f);
        trail.Line = line;
        return true;
    }

    private static Material EnsureMaterial()
    {
        if (_material != null) return _material;
        // No shader is guaranteed present in an IL2CPP build, so the usual suspects are tried
        // in turn rather than assuming one.
        foreach (var name in new[] { "Sprites/Default", "Universal Render Pipeline/Unlit", "Unlit/Color" })
        {
            var shader = Shader.Find(name);
            if (shader == null) continue;
            _material = new Material(shader);
            UnityEngine.Object.DontDestroyOnLoad(_material);
            return _material;
        }

        if (_materialMissingLogged) return null;
        _materialMissingLogged = true;
        ModLog.Warning("[Trails] No usable shader found; climb trails stay hidden.");
        return null;
    }

    private static void DestroyLine(Trail trail)
    {
        var root = trail.Root;
        trail.Root = null;
        trail.Line = null;
        if (root == null) return;
        try { UnityEngine.Object.Destroy(root); }
        catch (Exception exception) { ModLog.SuppressedException("trails.destroy", exception); }
    }
}
