using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal readonly record struct BelayFallRopePart(long Id, long Begin, long End, float RestLength, bool Loaded);

internal enum BelayFallRouteFailure
{
    None, InvalidBounds, InvalidCollider, MissingPart, AmbiguousPart, ReusedPart, UnloadedPart, InvalidLength
}

/// <summary>Measures only the requested collider walk, independent of native holder-list padding.</summary>
internal static class BelayFallRopeRoute
{
    internal static bool TryMeasure(ReadOnlySpan<long> colliders, ReadOnlySpan<BelayFallRopePart> parts,
        Span<int> selectedParts, out float length, out BelayFallRouteFailure failure, out int failedLeg)
    {
        length = float.NaN;
        failure = BelayFallRouteFailure.InvalidBounds;
        failedLeg = -1;
        if (colliders.Length < 2 || colliders.Length > 65 || parts.Length < 1 || parts.Length > 64
            || selectedParts.Length < colliders.Length - 1) return false;

        var total = 0f;
        for (var leg = 0; leg < colliders.Length - 1; leg++)
        {
            failedLeg = leg;
            var begin = colliders[leg];
            var end = colliders[leg + 1];
            if (begin == 0 || end == 0 || begin == end)
            { failure = BelayFallRouteFailure.InvalidCollider; return false; }

            var selected = -1;
            for (var i = 0; i < parts.Length; i++)
            {
                var candidate = parts[i];
                if (!(candidate.Begin == begin && candidate.End == end
                    || candidate.Begin == end && candidate.End == begin)) continue;
                if (selected >= 0)
                { failure = BelayFallRouteFailure.AmbiguousPart; return false; }
                selected = i;
            }
            // A matching endpoint pair with no native part identity cannot be used.
            if (selected < 0 || parts[selected].Id == 0)
            { failure = BelayFallRouteFailure.MissingPart; return false; }
            var part = parts[selected];
            for (var previous = 0; previous < leg; previous++)
            {
                var previousIndex = selectedParts[previous];
                if (previousIndex == selected || parts[previousIndex].Id == part.Id)
                { failure = BelayFallRouteFailure.ReusedPart; return false; }
            }
            if (!part.Loaded)
            { failure = BelayFallRouteFailure.UnloadedPart; return false; }
            if (!float.IsFinite(part.RestLength) || part.RestLength <= 0)
            { failure = BelayFallRouteFailure.InvalidLength; return false; }
            total += part.RestLength;
            if (!float.IsFinite(total) || total > 200)
            { failure = BelayFallRouteFailure.InvalidLength; return false; }
            selectedParts[leg] = selected;
        }

        length = total;
        failure = BelayFallRouteFailure.None;
        failedLeg = -1;
        return true;
    }
}
