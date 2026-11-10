using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The team rope's answer for Cairn's own "can the rope still pay out this way" check.
/// It does not move a climber. While the span between the harnesses is known, that span
/// is the only length limit, including when Cairn's own rope is already spent.
/// </summary>
internal static class RopeLengthPolicy
{
    /// <summary>Stop this far inside the coil so the answer does not flicker on the boundary.</summary>
    internal const float SkinMeters = 0.05f;

    /// <summary>
    /// A vector longer than this is a place in the world. The climb sits hundreds of metres
    /// from the origin, so that place is not a direction arrow.
    /// </summary>
    internal const float WorldPointMeters = 50f;

    /// <summary>
    /// The path from the follower's harness through each clipped point to the leader's harness
    /// has used the coil, within the skin.
    /// </summary>
    internal static bool IsPaidOut(float pathLength, float coilLength)
        => float.IsFinite(pathLength) && float.IsFinite(coilLength) && coilLength > 0f
           && pathLength >= coilLength - SkinMeters;

    /// <summary>
    /// The rope warning is worth showing. The distance is the one stored on Cairn's own warning.
    /// </summary>
    internal static bool ShowsAlmostOut(float remaining, float showWithin)
        => float.IsFinite(remaining) && remaining >= 0f
           && float.IsFinite(showWithin) && showWithin > 0f
           && remaining <= showWithin;

    /// <summary>
    /// The metres Cairn prints, in its own format, with whatever it puts after the number.
    /// Null when the length cannot be shown. A format the runtime rejects falls back to one decimal.
    /// </summary>
    internal static string WarningText(float remaining, string format, string suffix)
    {
        if (!float.IsFinite(remaining) || remaining < 0f) return null;
        string number;
        try
        {
            number = string.IsNullOrEmpty(format)
                ? remaining.ToString("0.0", CultureInfo.InvariantCulture)
                : format.Contains("{0", StringComparison.Ordinal)
                    ? string.Format(CultureInfo.InvariantCulture, format, remaining)
                    : remaining.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            number = null;
        }

        // A format with no digit slot is copied through as words, which is not a length.
        if (string.IsNullOrEmpty(number) || !HasDigit(number))
            number = remaining.ToString("0.0", CultureInfo.InvariantCulture);
        return number + (suffix ?? "");
    }

    private static bool HasDigit(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsDigit(text[i])) return true;
        return false;
    }

    /// <summary>
    /// How far the number has moved toward the end color. Zero at the outline distance and
    /// beyond, one when nothing is left. A missing outline distance uses the show distance.
    /// </summary>
    internal static float EndColorBlend(float remaining, float showWithin, float outlineWithin)
    {
        if (!float.IsFinite(remaining)) return 0f;
        if (remaining <= 0f) return 1f;
        var span = float.IsFinite(outlineWithin) && outlineWithin > 0f ? outlineWithin : showWithin;
        if (!float.IsFinite(span) || span <= 0f || remaining >= span) return 0f;
        return 1f - remaining / span;
    }

    /// <summary>The number picks up its outline once the leftover is inside that distance.</summary>
    internal static bool UsesOutline(float remaining, float outlineWithin)
        => float.IsFinite(remaining) && remaining >= 0f
           && float.IsFinite(outlineWithin) && outlineWithin > 0f
           && remaining <= outlineWithin;

    /// <summary>
    /// Which rope picture to use. Each entry is the least leftover that picture is for.
    /// The picture is the one with the highest such distance that the leftover still meets.
    /// Below every distance, the picture with the lowest distance is the short one.
    /// </summary>
    internal static int RopePictureIndex(float remaining, IReadOnlyList<float> minimums)
    {
        if (!float.IsFinite(remaining) || minimums == null || minimums.Count == 0) return -1;
        var best = -1;
        var bestMin = float.NegativeInfinity;
        var lowest = -1;
        var lowestMin = float.PositiveInfinity;
        for (var i = 0; i < minimums.Count; i++)
        {
            var min = minimums[i];
            if (!float.IsFinite(min)) continue;
            if (min < lowestMin)
            {
                lowestMin = min;
                lowest = i;
            }

            if (remaining >= min && min >= bestMin)
            {
                bestMin = min;
                best = i;
            }
        }

        return best >= 0 ? best : lowest;
    }

    private static bool IsFiniteCoil(float coilLength)
        => float.IsFinite(coilLength) && coilLength > 0f;

    /// <summary>
    /// The step, once a team span is known. Null leaves Cairn's answer alone.
    /// A known span allows the step unless that span refuses it.
    /// </summary>
    internal static bool? StepAllowed(bool teamSpanKnown, bool teamRefuses)
        => teamSpanKnown ? !teamRefuses : null;

    /// <summary>
    /// The step while this climber is still on the team rope. A measured span decides.
    /// A gap uses the last answer. No answer yet allows the step. Null leaves Cairn's
    /// answer alone, which is a fall, a death, or a leader who has already left.
    /// </summary>
    internal static bool? AnswerForStep(bool stillOnRope, bool? measuredAllows, bool? lastAllows)
    {
        if (!stillOnRope) return null;
        if (measuredAllows.HasValue) return measuredAllows;
        return lastAllows ?? true;
    }

    /// <summary>
    /// Metres still paying out while this climber is still on the team rope.
    /// Null leaves Cairn's remaining distance alone. No answer yet reports a metre
    /// of slack so a spent climbot coil cannot stop the step.
    /// </summary>
    internal static float? AnswerForRemaining(bool stillOnRope, float measured, float last)
    {
        if (!stillOnRope) return null;
        if (float.IsFinite(measured) && measured >= 0f) return measured;
        if (float.IsFinite(last) && last >= 0f) return last;
        return 1f;
    }

    /// <summary>
    /// Metres still paying out. The team remainder wins whenever it can be measured,
    /// including when Cairn's own rope is already at zero.
    /// </summary>
    internal static float ReportedRemaining(float nativeRemaining, float teamRemaining)
        => float.IsFinite(teamRemaining) ? teamRemaining : nativeRemaining;

    /// <summary>
    /// Flat unit direction from the previous rope point toward this climber, for the wall
    /// Cairn places in front of a walk. Null when the coil still has slack, or when this
    /// climber is stacked above the previous point and there is no flat direction to face.
    /// </summary>
    internal static Vector3? WalkDirection(bool localIsLeader, IReadOnlyList<Vector3> path, float coilLength)
    {
        if (!TryMeasure(localIsLeader, path, out var anchor, out var end, out var length)) return null;
        if (!IsPaidOut(length, coilLength)) return null;

        var flat = new Vector3(end.X - anchor.X, 0f, end.Z - anchor.Z);
        var sqr = flat.LengthSquared();
        // A centimetre of flat separation is still "directly above". Cairn multiplies
        // this direction by its own wall distance, so it has to be one unit long.
        if (!(sqr > 0.01f * 0.01f)) return null;
        var scale = 1f / MathF.Sqrt(sqr);
        var unit = flat * scale;
        return IsFinite(unit) ? unit : null;
    }

    /// <summary>Metres of coil still unused. Zero once paid out, including a path past the coil.</summary>
    internal static float RemainingMeters(float pathLength, float coilLength)
    {
        if (!float.IsFinite(pathLength) || pathLength < 0f || !IsFiniteCoil(coilLength))
            return float.NaN;
        if (IsPaidOut(pathLength, coilLength)) return 0f;
        var left = coilLength - pathLength;
        return float.IsFinite(left) && left > 0f ? left : 0f;
    }

    /// <summary>
    /// The step Cairn is asking about. A short vector is already a direction. A far one is
    /// a world point, and the step is from this harness to that point.
    /// </summary>
    internal static Vector3 StepFromToward(Vector3 toward, Vector3 here)
    {
        if (!IsFinite(toward)) return toward;
        if (toward.LengthSquared() > WorldPointMeters * WorldPointMeters && IsFinite(here))
            return toward - here;
        return toward;
    }

    /// <summary>Length of a follower-to-leader path. False when a point is missing or not finite.</summary>
    internal static bool TryMeasureLength(IReadOnlyList<Vector3> path, out float length)
        => TryMeasure(localIsLeader: true, path, out _, out _, out length);

    /// <summary>
    /// True when this direction would stretch a paid-out rope. The path is the follower harness,
    /// then each point from oldest to newest, then the leader harness. A step back toward the
    /// previous point, a step while there is still slack, and a non-finite value do not refuse.
    /// </summary>
    internal static bool RefusesDirection(
        bool localIsLeader, IReadOnlyList<Vector3> path, float coilLength, Vector3 direction)
    {
        if (!TryMeasure(localIsLeader, path, out var anchor, out var end, out var length)) return false;
        if (!IsFiniteCoil(coilLength) || !IsPaidOut(length, coilLength)) return false;
        if (!IsFinite(direction) || direction.LengthSquared() <= 1e-8f) return false;

        var fromAnchor = end - anchor;
        // The climber is already on the previous point and the rest of the rope is spent.
        // Every real step pays out more.
        if (fromAnchor.LengthSquared() <= 1e-8f) return true;
        return Vector3.Dot(direction, fromAnchor) > 0f;
    }

    private static bool TryMeasure(
        bool localIsLeader, IReadOnlyList<Vector3> path, out Vector3 anchor, out Vector3 end, out float length)
    {
        anchor = default;
        end = default;
        length = 0f;
        if (path == null || path.Count < 2) return false;

        for (var i = 0; i < path.Count; i++)
            if (!IsFinite(path[i])) return false;

        for (var i = 1; i < path.Count; i++)
            length += Vector3.Distance(path[i - 1], path[i]);
        if (!float.IsFinite(length)) return false;

        if (localIsLeader)
        {
            end = path[path.Count - 1];
            anchor = path[path.Count - 2];
        }
        else
        {
            end = path[0];
            anchor = path[1];
        }

        return true;
    }

    private static bool IsFinite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
