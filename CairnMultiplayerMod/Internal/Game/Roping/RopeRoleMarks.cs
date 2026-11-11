using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using UnityEngine;
using UnityEngine.Rendering;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The two marks above a tied pair. The leader's is a little cairn, the follower's
/// one flat stone, both in the held rope's brown. They face the camera and they
/// are not labels, piton colors, or player colors.
/// </summary>
internal static class RopeRoleMarks
{
    /// <summary>The stand-in name sits 2.25 m up a body whose capsule is 1.8 m.</summary>
    private const float AboveBodyMeters = 2.2f;

    /// <summary>Clears a line of name text without leaving the head.</summary>
    private const float AboveNameMeters = 0.18f;

    private const float StoneThickMeters = 0.02f;
    private const float StoneGapMeters = 0.016f;

    private static readonly float[] CairnWidths = { 0.14f, 0.10f, 0.064f };
    private static readonly float[] StoneWidths = { 0.09f };

    /// <summary>The brown used for a held line before it borrows Cairn's rope material.</summary>
    private static readonly Color StoneColor = new(0.55f, 0.38f, 0.22f, 0.75f);

    private static readonly string[] ShaderNames =
    {
        "Sprites/Default",
        "Universal Render Pipeline/Unlit",
        "Unlit/Color",
    };

    private sealed class Mark
    {
        internal GameObject Root;
        internal LineRenderer[] Stones;
    }

    private static Mark _cairn;
    private static Mark _stone;
    private static Material _material;
    private static bool _shaderMissingLogged;

    internal static void Present(int localId, int leaderId, int followerId, bool namesShown)
    {
        if (!RopeRoleMarkPolicy.Visible(leaderId, followerId, namesShown))
        {
            Hide();
            return;
        }

        try
        {
            Place(ref _cairn, localId, leaderId, CairnWidths, "CairnMP Leader Mark");
            Place(ref _stone, localId, followerId, StoneWidths, "CairnMP Follower Mark");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.role-mark", exception);
            Hide();
        }
    }

    internal static void Hide()
    {
        Drop(ref _cairn);
        Drop(ref _stone);
    }

    private static void Place(ref Mark mark, int localId, int playerId, float[] widths, string name)
    {
        if (!TryAnchor(localId, playerId, out var position, out var up, out var atName))
        {
            SetShown(mark, false);
            return;
        }

        var drawn = Ensure(ref mark, widths.Length, name);
        if (drawn == null) return;

        var right = StoneRight(up);
        var cursor = position + up * (atName ? AboveNameMeters : AboveBodyMeters);
        var step = up * (StoneThickMeters + StoneGapMeters);
        for (var i = 0; i < widths.Length; i++)
        {
            var half = right * (widths[i] * 0.5f);
            var line = drawn.Stones[i];
            line.SetPosition(0, cursor - half);
            line.SetPosition(1, cursor + half);
            line.enabled = true;
            cursor += step;
        }
    }

    private static bool TryAnchor(int localId, int playerId, out Vector3 position, out Vector3 up, out bool atName)
    {
        position = default;
        up = Vector3.up;
        atName = false;
        if (playerId <= 0) return false;
        if (playerId == localId) return TryLocal(out position, out up);
        return RemotePlayerManager.TryGetRoleAnchor(playerId, out position, out up, out atName);
    }

    private static bool TryLocal(out Vector3 position, out Vector3 up)
    {
        position = default;
        up = Vector3.up;
        var body = LocalPlayerInterop.TryGetMCGameObject();
        if (body == null) return false;
        try
        {
            var root = body.transform;
            if (root == null) return false;
            position = root.position;
            if (!RemotePlayerManager.IsClimbablePoint(position)) return false;
            var bodyUp = root.up;
            if (bodyUp.sqrMagnitude > 1e-4f) up = bodyUp.normalized;
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.role-mark-local", exception);
            return false;
        }
    }

    /// <summary>A line across the stones, in the plane that faces the camera.</summary>
    private static Vector3 StoneRight(Vector3 up)
    {
        var camera = Camera.main;
        var hint = camera != null ? camera.transform.right : Vector3.right;
        var right = hint - up * Vector3.Dot(hint, up);
        if (right.sqrMagnitude < 1e-4f)
        {
            var forward = camera != null ? camera.transform.forward : Vector3.forward;
            right = Vector3.Cross(up, forward);
        }

        if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
        return right.normalized;
    }

    private static Mark Ensure(ref Mark mark, int stones, string name)
    {
        if (Intact(mark, stones)) return mark;
        Drop(ref mark);

        var material = Material();
        if (material == null) return null;

        var root = new GameObject(name);
        UnityEngine.Object.DontDestroyOnLoad(root);
        var lines = new LineRenderer[stones];
        for (var i = 0; i < stones; i++)
        {
            var child = new GameObject("Stone");
            child.transform.SetParent(root.transform, false);
            var line = child.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.widthMultiplier = StoneThickMeters;
            line.numCapVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.alignment = LineAlignment.View;
            line.startColor = StoneColor;
            line.endColor = StoneColor;
            line.enabled = false;
            lines[i] = line;
        }

        mark = new Mark { Root = root, Stones = lines };
        return mark;
    }

    private static bool Intact(Mark mark, int stones)
    {
        if (mark == null || mark.Root == null || mark.Stones == null || mark.Stones.Length != stones)
            return false;
        for (var i = 0; i < stones; i++)
            if (mark.Stones[i] == null) return false;
        return true;
    }

    private static void SetShown(Mark mark, bool shown)
    {
        if (mark == null || mark.Stones == null) return;
        for (var i = 0; i < mark.Stones.Length; i++)
        {
            var line = mark.Stones[i];
            if (line != null) line.enabled = shown;
        }
    }

    private static void Drop(ref Mark mark)
    {
        var root = mark?.Root;
        mark = null;
        if (root == null) return;
        try { UnityEngine.Object.Destroy(root); }
        catch (Exception exception) { ModLog.SuppressedException("rope.role-mark-drop", exception); }
    }

    private static Material Material()
    {
        if (_material != null) return _material;
        foreach (var shaderName in ShaderNames)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) continue;
            _material = new Material(shader);
            UnityEngine.Object.DontDestroyOnLoad(_material);
            return _material;
        }

        if (_shaderMissingLogged) return null;
        _shaderMissingLogged = true;
        ModLog.Warning("[RopeTeam] No usable shader found; the leader and follower marks stay hidden.");
        return null;
    }
}
