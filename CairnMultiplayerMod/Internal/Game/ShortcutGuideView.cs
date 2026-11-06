using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Draws the shortcut list over the CairnMP options page with IMGUI. The game's own settings
/// rows are tall, so a list that long would need scrolling there.
/// </summary>
internal static class ShortcutGuideView
{
    private static readonly Color Backdrop = new(0.05f, 0.05f, 0.07f, 0.88f);
    private static readonly Color TitleColor = new(0.95f, 0.75f, 0.25f, 1f);
    private static readonly Color TextColor = new(0.92f, 0.92f, 0.92f, 1f);

    private static GUIStyle _style;

    /// <summary>Fits the columns inside <paramref name="area"/>, shrinking the text if it must.</summary>
    internal static void Draw(Rect area, GuideColumn[] columns)
    {
        if (Event.current?.type != EventType.Repaint) return;

        var padding = Mathf.Min(10f, area.height * 0.03f);
        var columnWidth = (area.width - padding * 2f) / columns.Length;

        var rows = 0;
        foreach (var column in columns) rows = Mathf.Max(rows, column.Lines.Length);
        var lineHeight = Mathf.Clamp((area.height - padding * 2f) / (rows + 1.3f), 9f, 26f);
        var panel = area;
        var scale = lineHeight / 17f;

        _style ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Clip,
            padding = new RectOffset(0, 0, 0, 0),
        };

        var previous = GUI.color;
        GUI.color = Backdrop;
        GUI.DrawTexture(panel, Texture2D.whiteTexture);

        _style.fontSize = Mathf.RoundToInt(13f * scale);
        for (var c = 0; c < columns.Length; c++)
        {
            var x = panel.x + padding + c * columnWidth;
            var y = panel.y + padding;

            _style.fontStyle = FontStyle.Bold;
            GUI.color = TitleColor;
            GUI.Label(new Rect(x, y, columnWidth - 8f * scale, lineHeight), columns[c].Title, _style);

            _style.fontStyle = FontStyle.Normal;
            GUI.color = TextColor;
            for (var i = 0; i < columns[c].Lines.Length; i++)
                GUI.Label(new Rect(x, y + (i + 1) * lineHeight + 4f * scale, columnWidth - 8f * scale, lineHeight),
                    columns[c].Lines[i], _style);
        }

        GUI.color = previous;
    }
}
