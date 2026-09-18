using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CairnMultiplayerMod.Internal.UI.Sketch;

/// <summary>
/// Controller and keyboard navigation for a built screen.
///
/// Unity's automatic navigation picks the nearest neighbour geometrically, which falls apart
/// on this panel: lobby rows span the full width, and a stepper puts two arrows a few pixels
/// apart. So the order is rebuilt from what the screen actually looks like -- rows top to
/// bottom, elements left to right within a row -- and written down explicitly.
/// </summary>
internal static class SketchFocus
{
    /// <summary>Elements within this many pixels of each other count as the same row.</summary>
    private const float RowTolerance = 24f;

    private sealed class Row
    {
        internal float Y;
        internal readonly List<Selectable> Items = new();
    }

    /// <summary>
    /// Chains every selectable under <paramref name="root"/> and returns the one that should
    /// hold focus when the screen opens, or null when the screen has nothing to select.
    /// </summary>
    internal static Selectable Apply(GameObject root)
    {
        if (root == null) return null;

        var rows = BuildRows(root);
        if (rows.Count == 0) return null;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var above = index > 0 ? rows[index - 1] : null;
            var below = index + 1 < rows.Count ? rows[index + 1] : null;

            for (var position = 0; position < row.Items.Count; position++)
            {
                var item = row.Items[position];
                var navigation = item.navigation;
                navigation.mode = Navigation.Mode.Explicit;
                navigation.selectOnLeft = position > 0 ? row.Items[position - 1] : null;
                navigation.selectOnRight = position + 1 < row.Items.Count ? row.Items[position + 1] : null;
                // Leaving a row always lands on its first element: coming back down a column
                // that drifts sideways is how automatic navigation loses people.
                navigation.selectOnUp = above != null ? above.Items[0] : null;
                navigation.selectOnDown = below != null ? below.Items[0] : null;
                item.navigation = navigation;
            }
        }

        return rows[0].Items[0];
    }

    private static List<Row> BuildRows(GameObject root)
    {
        var flat = new List<Selectable>();
        foreach (var selectable in root.GetComponentsInChildren<Selectable>(true))
            if (selectable != null && selectable.IsActive() && selectable.IsInteractable())
                flat.Add(selectable);

        var points = new List<(Selectable Item, Vector2 Point)>(flat.Count);
        foreach (var item in flat)
        {
            var rect = item.GetComponent<RectTransform>();
            if (rect == null) continue;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var centre = (corners[0] + corners[2]) * 0.5f;
            points.Add((item, new Vector2(centre.x, centre.y)));
        }

        // Top to bottom, then left to right.
        points.Sort((left, right) =>
        {
            var vertical = right.Point.y.CompareTo(left.Point.y);
            return vertical != 0 ? vertical : left.Point.x.CompareTo(right.Point.x);
        });

        var rows = new List<Row>();
        foreach (var (item, point) in points)
        {
            var row = rows.Count > 0 && Mathf.Abs(rows[rows.Count - 1].Y - point.y) <= RowTolerance
                ? rows[rows.Count - 1]
                : null;
            if (row == null)
            {
                row = new Row { Y = point.y };
                rows.Add(row);
            }
            row.Items.Add(item);
        }

        // Sorting was global, so a row assembled from several passes keeps its own order.
        foreach (var row in rows)
            row.Items.Sort((left, right) =>
                left.transform.position.x.CompareTo(right.transform.position.x));

        return rows;
    }
}
