using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Players;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.World;

/// <summary>
/// Manages the ping markers placed by players and draws them as a 2D HUD.
///
/// Rendering choice: a projected screen marker (WorldToScreenPoint) rather than a
/// 3D object, which guarantees "see-through-everything" visibility (waypoint)
/// without relying on a custom shader that's fragile under IL2CPP. When the point
/// goes off-screen, the marker is clamped to the screen edge with a directional
/// arrow.
///
/// One active ping per player: placing a new ping overwrites the old one.
/// </summary>
internal static class PingMarkerManager
{
    private class PingEntry
    {
        public Vector3 WorldPos;
        public float SpawnTime;
        public Color Color;
    }

    // Indexed by player id -> one active ping per player.
    private static readonly Dictionary<int, PingEntry> _pings = new();

    private const float MarkerSizePx = 22f;
    private const float ArrowSizePx = 26f;
    private const float EdgeMarginPx = 36f;

    private static Texture2D _dotTexture;
    private static Texture2D _arrowTexture;
    private static GUIStyle _labelStyle;

    /// <summary>
    /// Creates or replaces the given player's ping. The color reuses the ghost
    /// palette to identify the author. SpawnTime drives auto-expiration.
    /// </summary>
    public static void Spawn(int ownerId, Vector3 worldPos)
    {
        _pings[ownerId] = new PingEntry
        {
            WorldPos = worldPos,
            SpawnTime = Time.unscaledTime,
            Color = RemotePlayerManager.ColorForPlayer(ownerId),
        };
    }

    /// <summary>Purges expired pings. Called every frame from OnUpdate.</summary>
    public static void Update()
    {
        if (_pings.Count == 0) return;

        var now = Time.unscaledTime;
        List<int> expired = null;
        foreach (var kv in _pings)
        {
            if (now - kv.Value.SpawnTime >= Protocol.PingLifetimeSeconds)
                (expired ??= new List<int>()).Add(kv.Key);
        }

        if (expired != null)
            foreach (var id in expired)
                _pings.Remove(id);
    }

    public static void ClearAll() => _pings.Clear();

    /// <summary>Draws all active markers. Called from Mod.OnGUI.</summary>
    public static void OnGUI()
    {
        if (_pings.Count == 0) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        var cam = Camera.main;
        if (cam == null) return;

        EnsureResources();

        var camPos = cam.transform.position;
        var prevColor = GUI.color;

        foreach (var kv in _pings)
        {
            var ping = kv.Value;
            DrawPing(cam, camPos, ping);
        }

        GUI.color = prevColor;
    }

    private static void DrawPing(Camera cam, Vector3 camPos, PingEntry ping)
    {
        var screen = cam.WorldToScreenPoint(ping.WorldPos);
        var behind = screen.z < 0f;

        // Behind the camera: WorldToScreenPoint returns inverted coordinates, so we
        // flip them back to get a consistent direction.
        if (behind)
        {
            screen.x = Screen.width - screen.x;
            screen.y = Screen.height - screen.y;
        }

        // Convert to GUI coordinates (origin top-left, y going down).
        var guiPos = new Vector2(screen.x, Screen.height - screen.y);

        var distance = Mathf.RoundToInt(Vector3.Distance(camPos, ping.WorldPos));

        var onScreen = !behind
            && guiPos.x >= 0f && guiPos.x <= Screen.width
            && guiPos.y >= 0f && guiPos.y <= Screen.height;

        GUI.color = ping.Color;

        if (onScreen)
        {
            var rect = new Rect(guiPos.x - MarkerSizePx * 0.5f, guiPos.y - MarkerSizePx * 0.5f, MarkerSizePx, MarkerSizePx);
            GUI.DrawTexture(rect, _dotTexture);
            DrawLabel(guiPos.x, guiPos.y + MarkerSizePx * 0.5f + 2f, $"{distance}m");
        }
        else
        {
            DrawEdgeArrow(guiPos, distance, ping.Color);
        }
    }

    /// <summary>Clamps the marker to the screen edge with an arrow pointing at the target.</summary>
    private static void DrawEdgeArrow(Vector2 guiPos, int distance, Color color)
    {
        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var dir = guiPos - center;
        if (dir.sqrMagnitude < 0.0001f)
            dir = Vector2.up;
        dir.Normalize();

        var halfW = Screen.width * 0.5f - EdgeMarginPx;
        var halfH = Screen.height * 0.5f - EdgeMarginPx;

        // Intersection of the direction with the screen rectangle (clamped to the edges).
        var scaleX = Mathf.Abs(dir.x) > 0.0001f ? halfW / Mathf.Abs(dir.x) : float.MaxValue;
        var scaleY = Mathf.Abs(dir.y) > 0.0001f ? halfH / Mathf.Abs(dir.y) : float.MaxValue;
        var scale = Mathf.Min(scaleX, scaleY);
        var edgePos = center + dir * scale;

        // Arrow rotation (texture points up in GUI space).
        var angle = Mathf.Atan2(dir.x, -dir.y) * Mathf.Rad2Deg;
        var rect = new Rect(edgePos.x - ArrowSizePx * 0.5f, edgePos.y - ArrowSizePx * 0.5f, ArrowSizePx, ArrowSizePx);

        var prevMatrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, edgePos);
        GUI.color = color;
        GUI.DrawTexture(rect, _arrowTexture);
        GUI.matrix = prevMatrix;

        DrawLabel(edgePos.x, edgePos.y + ArrowSizePx * 0.5f + 2f, $"{distance}m");
    }

    private static void DrawLabel(float centerX, float top, string text)
    {
        const float w = 80f;
        var rect = new Rect(centerX - w * 0.5f, top, w, 18f);
        GUI.color = Color.white;
        GUI.Label(rect, text, _labelStyle);
    }

    private static void EnsureResources()
    {
        if (_dotTexture == null)
            _dotTexture = BuildCircleTexture(32);
        if (_arrowTexture == null)
            _arrowTexture = BuildArrowTexture(32);
        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
            };
            _labelStyle.normal.textColor = Color.white;
        }
    }

    /// <summary>Generates a white round dot (tinted via GUI.color when drawn).</summary>
    private static Texture2D BuildCircleTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var center = (size - 1) * 0.5f;
        var radius = size * 0.42f;
        var ringInner = size * 0.30f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var d = Mathf.Sqrt(dx * dx + dy * dy);

                Color c;
                if (d <= ringInner)
                    c = Color.white;                       // solid core
                else if (d <= radius)
                    c = new Color(1f, 1f, 1f, 1f);         // solid ring
                else if (d <= radius + 1.5f)
                    c = new Color(1f, 1f, 1f, Mathf.Clamp01(radius + 1.5f - d)); // softened edge
                else
                    c = new Color(1f, 1f, 1f, 0f);         // transparent

                tex.SetPixel(x, y, c);
            }
        }

        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    /// <summary>Generates a white triangular arrow pointing up.</summary>
    private static Texture2D BuildArrowTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Triangle: wide at the bottom, pointed at the top (y increases upward in texture space).
                var t = y / (float)(size - 1);              // 0 at bottom, 1 at top
                var halfWidth = (1f - t) * 0.5f * size;
                var dxFromCenter = Mathf.Abs(x - (size - 1) * 0.5f);
                var inside = dxFromCenter <= halfWidth && t >= 0.15f;
                tex.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }

        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }
}
