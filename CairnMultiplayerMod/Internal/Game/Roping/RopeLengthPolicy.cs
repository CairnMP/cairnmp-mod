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

    /// <summary>Temporary test line. Null when the lengths cannot be shown.</summary>
    internal static string Readout(float pathLength, float coilLength)
    {
        if (!float.IsFinite(pathLength) || pathLength < 0f || !IsFiniteCoil(coilLength)) return null;
        var text = string.Format(CultureInfo.InvariantCulture, "Rope {0:0.00} / {1:0.00} m", pathLength, coilLength);
        return IsPaidOut(pathLength, coilLength) ? text + " — paid out" : text;
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
