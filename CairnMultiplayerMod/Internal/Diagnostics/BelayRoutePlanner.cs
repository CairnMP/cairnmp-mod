using System;
using System.Collections.Generic;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal readonly record struct BelayProtectionPoint(uint Id, Vector3 Position);

internal readonly record struct BelayRoutePlan(float FixedLength, float ClimberDistance,
    float AvailableToClimber, int ProtectionCount)
{
    internal bool ReachesClimber => ClimberDistance <= AvailableToClimber;
}

/// <summary>
/// Geometry for the intended station -> ordered protection -> climber route.
/// This planner neither creates native attachments nor substitutes for fall physics.
/// </summary>
internal static class BelayRoutePlanner
{
    internal static bool TryPlan(Vector3 station, IReadOnlyList<BelayProtectionPoint> protections,
        Vector3 climber, float capacity, out BelayRoutePlan plan)
    {
        plan = default;
        if (!Finite(station) || !Finite(climber) || protections == null || protections.Count > 63
            || !float.IsFinite(capacity) || capacity <= 0 || capacity > 200) return false;
        var previous = station;
        var fixedLength = 0f;
        var seen = new HashSet<uint>();
        foreach (var point in protections)
        {
            if (point.Id == 0 || !seen.Add(point.Id) || !Finite(point.Position)) return false;
            fixedLength += Vector3.Distance(previous, point.Position);
            previous = point.Position;
        }
        var lastDistance = Vector3.Distance(previous, climber);
        if (!float.IsFinite(fixedLength) || !float.IsFinite(lastDistance) || fixedLength > capacity) return false;
        plan = new BelayRoutePlan(fixedLength, lastDistance, capacity - fixedLength, protections.Count);
        return true;
    }

    private static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
