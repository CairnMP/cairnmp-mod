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
        var seen = new Dictionary<uint, Vector3>();
        foreach (var point in protections)
        {
            if (point.Id == 0 || !Finite(point.Position)) return false;
            // A native holder walk can visit the same protection more than once.
            // Retain its occurrences, but never give one identity two positions.
            if (seen.TryGetValue(point.Id, out var known) && known != point.Position) return false;
            seen[point.Id] = point.Position;
            fixedLength += Vector3.Distance(previous, point.Position);
            previous = point.Position;
        }
        var lastDistance = Vector3.Distance(previous, climber);
        if (!float.IsFinite(fixedLength) || !float.IsFinite(lastDistance) || fixedLength > capacity) return false;
        plan = new BelayRoutePlan(fixedLength, lastDistance, capacity - fixedLength, protections.Count);
        return true;
    }

    internal static bool TryPlanFromStation(uint stationId, IReadOnlyList<BelayProtectionPoint> protections,
        Vector3 climber, float capacity, out BelayRoutePlan plan)
    {
        plan = default;
        if (stationId == 0 || protections == null || protections.Count > 63) return false;
        var stationIndex = -1;
        for (var i = 0; i < protections.Count; i++)
            if (protections[i].Id == stationId)
            {
                // Picking a different occurrence silently changes the route.
                if (stationIndex >= 0) return false;
                stationIndex = i;
            }
        if (stationIndex < 0) return false;
        var afterStation = new BelayProtectionPoint[protections.Count - stationIndex - 1];
        for (var i = 0; i < afterStation.Length; i++) afterStation[i] = protections[stationIndex + i + 1];
        return TryPlan(protections[stationIndex].Position, afterStation, climber, capacity, out plan);
    }

    internal static bool TryLimitFinalCapacity(BelayRoutePlan plan, float nativeAvailable, out float available)
    {
        available = 0;
        if (!float.IsFinite(plan.AvailableToClimber) || plan.AvailableToClimber <= 0 || plan.AvailableToClimber > 200
            || !float.IsFinite(nativeAvailable) || nativeAvailable <= 0 || nativeAvailable > 200) return false;
        available = Math.Min(plan.AvailableToClimber, nativeAvailable);
        return available > 0 && available <= 200;
    }

    internal static bool IsSingleProtectionAppend(IReadOnlyList<BelayProtectionPoint> previous,
        IReadOnlyList<BelayProtectionPoint> next)
    {
        if (previous == null || next == null || previous.Count < 1 || next.Count > 63
            || next.Count != previous.Count + 1 || next[next.Count - 1].Id == 0) return false;
        var added = next[next.Count - 1].Id;
        for (var i = 0; i < previous.Count; i++)
            if (previous[i].Id == 0 || previous[i].Id != next[i].Id || previous[i].Id == added) return false;
        return true;
    }

    internal static bool TryFindStationPiton(Vector3 position, IReadOnlyList<BelayProtectionPoint> pitons, out uint id)
    {
        id = 0;
        if (!Finite(position) || pitons == null || pitons.Count > 63) return false;
        uint candidate = 0;
        foreach (var point in pitons)
        {
            if (point.Id == 0 || !Finite(point.Position)) return false;
            if (Vector3.DistanceSquared(position, point.Position) > BelayStationAnchor.MaximumMovement * BelayStationAnchor.MaximumMovement)
                continue;
            // A station must name one occurrence, even if other protections repeat.
            if (candidate != 0) return false;
            candidate = point.Id;
        }
        id = candidate;
        return id != 0;
    }

    private static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
