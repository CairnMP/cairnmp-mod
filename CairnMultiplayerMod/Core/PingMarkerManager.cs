using System.Collections.Generic;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Gere les marqueurs de ping poses par les joueurs et les dessine en HUD 2D.
///
/// Choix de rendu : un marqueur ecran projete (WorldToScreenPoint) plutot qu'un
/// objet 3D, ce qui garantit la visibilite "a travers tout" (waypoint) sans
/// dependre d'un shader custom fragile en IL2CPP. Quand le point sort du champ,
/// le marqueur est clampe au bord de l'ecran avec une fleche directionnelle.
///
/// Un seul ping actif par joueur : poser un nouveau ping ecrase l'ancien.
/// </summary>
public static class PingMarkerManager
{
    private class PingEntry
    {
        public Vector3 WorldPos;
        public float SpawnTime;
        public Color Color;
    }

    // Indexe par id de joueur -> 1 seul ping actif par joueur.
    private static readonly Dictionary<int, PingEntry> _pings = new();

    private const float MarkerSizePx = 22f;
    private const float ArrowSizePx = 26f;
    private const float EdgeMarginPx = 36f;

    private static Texture2D _dotTexture;
    private static Texture2D _arrowTexture;
    private static GUIStyle _labelStyle;

    /// <summary>
    /// Cree ou remplace le ping du joueur donne. La couleur reprend la palette des
    /// fantomes pour identifier l'auteur. SpawnTime sert a l'expiration auto.
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

    /// <summary>Purge les pings expires. Appele chaque frame depuis OnUpdate.</summary>
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

    /// <summary>Dessine tous les marqueurs actifs. Appele depuis Mod.OnGUI.</summary>
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

        // Derriere la camera : WorldToScreenPoint renvoie des coordonnees
        // inversees, on les retourne pour obtenir une direction coherente.
        if (behind)
        {
            screen.x = Screen.width - screen.x;
            screen.y = Screen.height - screen.y;
        }

        // Passage en coordonnees GUI (origine en haut a gauche, y vers le bas).
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

    /// <summary>Clampe le marqueur au bord de l'ecran avec une fleche vers la cible.</summary>
    private static void DrawEdgeArrow(Vector2 guiPos, int distance, Color color)
    {
        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var dir = guiPos - center;
        if (dir.sqrMagnitude < 0.0001f)
            dir = Vector2.up;
        dir.Normalize();

        var halfW = Screen.width * 0.5f - EdgeMarginPx;
        var halfH = Screen.height * 0.5f - EdgeMarginPx;

        // Intersection de la direction avec le rectangle ecran (clamp aux bords).
        var scaleX = Mathf.Abs(dir.x) > 0.0001f ? halfW / Mathf.Abs(dir.x) : float.MaxValue;
        var scaleY = Mathf.Abs(dir.y) > 0.0001f ? halfH / Mathf.Abs(dir.y) : float.MaxValue;
        var scale = Mathf.Min(scaleX, scaleY);
        var edgePos = center + dir * scale;

        // Rotation de la fleche (texture pointant vers le haut en espace GUI).
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

    /// <summary>Genere une pastille ronde blanche (teintee via GUI.color a l'affichage).</summary>
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
                    c = Color.white;                       // coeur plein
                else if (d <= radius)
                    c = new Color(1f, 1f, 1f, 1f);         // anneau plein
                else if (d <= radius + 1.5f)
                    c = new Color(1f, 1f, 1f, Mathf.Clamp01(radius + 1.5f - d)); // bord adouci
                else
                    c = new Color(1f, 1f, 1f, 0f);         // transparent

                tex.SetPixel(x, y, c);
            }
        }

        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    /// <summary>Genere une fleche triangulaire blanche pointant vers le haut.</summary>
    private static Texture2D BuildArrowTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Triangle : large en bas, pointe en haut (y croit vers le haut en espace texture).
                var t = y / (float)(size - 1);              // 0 en bas, 1 en haut
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
