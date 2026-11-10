using System;
using System.Collections.Generic;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>What the local climb module was doing. Wall modules are still on the rock.</summary>
internal enum TopAnchorModule
{
    Other = 0,
    Mantling,
    LedgeWalking,
    GetUpFromClimb,
    Falling,
    SecuredFalling,
    GetDownFromWall,
    JumpDownFromWall,
    Wall,
}

/// <summary>
/// A free staff piton at the lip. The leader plants it by mantling. It comes out when the
/// follower is standing in the ledge volume: 8 m across, from 2 m below the stick upward.
/// </summary>
internal static class TopAnchorPolicy
{
    internal const uint FirstId = 0xF0000001u;
    internal const float HorizontalMeters = 8f;
    internal const float BelowMeters = 2f;

    /// <summary>The stick reaches about this far along its shaft. The rope ties at the top.</summary>
    internal const float TieHeightMeters = 0.62f;

    /// <summary>How far the tip sits in the rock once the shaft is seated.</summary>
    internal const float SeatEmbedMeters = 0.08f;

    /// <summary>
    /// Further than the stick can hang below its bone. The 2.05 m glow seat was past the
    /// shaft and left it in the air. No lift leaves the shaft inside the ledge.
    /// </summary>
    internal const float MaxSeatMeters = 2.2f;

    internal const int MaxLeaveIds = 16;

    internal static bool IsStickId(uint id) => id >= FirstId;

    internal static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    /// <summary>True when the climber is on this stick's ledge. The edge is included.</summary>
    internal static bool InVolume(Vector3 stick, Vector3 climber)
    {
        if (!Finite(stick) || !Finite(climber)) return false;
        var dx = climber.X - stick.X;
        var dz = climber.Z - stick.Z;
        if (dx * dx + dz * dz > HorizontalMeters * HorizontalMeters) return false;
        return climber.Y >= stick.Y - BelowMeters;
    }

    internal static bool TryNearest(
        IReadOnlyList<uint> ids, IReadOnlyDictionary<uint, Vector3> positions, Vector3 climber, out uint id)
        => TryNearest(ids, positions, climber, out id, skip: null);

    /// <summary>
    /// The nearest stick in the ledge volume. <paramref name="skip"/> drops sticks that
    /// are not pulled by standing there, such as one left by a story scene.
    /// </summary>
    internal static bool TryNearest(
        IReadOnlyList<uint> ids,
        IReadOnlyDictionary<uint, Vector3> positions,
        Vector3 climber,
        out uint id,
        System.Func<uint, bool> skip)
    {
        id = 0;
        var best = float.MaxValue;
        var found = false;
        for (var i = 0; i < ids.Count; i++)
        {
            var candidate = ids[i];
            if (skip != null && skip(candidate)) continue;
            if (!positions.TryGetValue(candidate, out var stick) || !InVolume(stick, climber)) continue;
            var dx = climber.X - stick.X;
            var dz = climber.Z - stick.Z;
            var distance = dx * dx + dz * dz;
            if (found && distance >= best) continue;
            best = distance;
            id = candidate;
            found = true;
        }

        return found;
    }

    internal static Vector3 TiePoint(Vector3 lip) => TiePoint(lip, new Vector3(0f, 1f, 0f));

    /// <summary>The rope meets the head of the stick, out along the direction it leaves the rock.</summary>
    internal static Vector3 TiePoint(Vector3 lip, Vector3 outward)
    {
        var dir = OutwardOrUp(outward);
        return lip + dir * TieHeightMeters;
    }

    /// <summary>A missing or flat direction stands the stick upright.</summary>
    internal static Vector3 OutwardOrUp(Vector3 outward)
    {
        if (!Finite(outward)) return new Vector3(0f, 1f, 0f);
        var length = outward.Length();
        if (length < 0.001f) return new Vector3(0f, 1f, 0f);
        return outward / length;
    }

    /// <summary>
    /// How far to raise the bone so the shaft tip sits in the lip. False when the measured
    /// tip is too far below the bone to be the stick.
    /// </summary>
    internal static bool TrySeatLift(float lipY, float tipY, out float lift)
    {
        lift = 0f;
        var raw = (lipY - SeatEmbedMeters) - tipY;
        if (!float.IsFinite(raw)) return false;
        if (raw < 0f) return true;
        if (raw > MaxSeatMeters) return false;
        lift = raw;
        return true;
    }

    /// <summary>
    /// The mantle finished on the ledge. Falling, or going back down the wall, does not.
    /// Standing is enough when the next module is not itself a wall module.
    /// </summary>
    internal static bool CommitsTopOut(TopAnchorModule previous, TopAnchorModule next, bool walking)
    {
        if (previous != TopAnchorModule.Mantling) return false;
        if (next is TopAnchorModule.Falling
            or TopAnchorModule.SecuredFalling
            or TopAnchorModule.GetDownFromWall
            or TopAnchorModule.JumpDownFromWall
            or TopAnchorModule.Wall)
            return false;
        return next is TopAnchorModule.LedgeWalking or TopAnchorModule.GetUpFromClimb || walking;
    }

    internal static TopAnchorModule Classify(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return TopAnchorModule.Other;
        if (typeName.Contains("Mantling", StringComparison.Ordinal)) return TopAnchorModule.Mantling;
        if (typeName.Contains("LedgeWalking", StringComparison.Ordinal)) return TopAnchorModule.LedgeWalking;
        if (typeName.Contains("GetUpFromClimb", StringComparison.Ordinal)) return TopAnchorModule.GetUpFromClimb;
        if (typeName.Contains("SecuredFalling", StringComparison.Ordinal)) return TopAnchorModule.SecuredFalling;
        if (typeName.Contains("JumpDownFromWall", StringComparison.Ordinal)) return TopAnchorModule.JumpDownFromWall;
        if (typeName.Contains("GetDownFromWall", StringComparison.Ordinal)) return TopAnchorModule.GetDownFromWall;
        if (typeName.Contains("Falling", StringComparison.Ordinal)) return TopAnchorModule.Falling;
        // A finished mantle enters Idle, then the pawn walks. Idle must not cancel the lip.
        // Reaching and the other wall modules mean the climber is back on the rock.
        if (typeName.Contains("Reaching", StringComparison.Ordinal)
            || typeName.Contains("Hang", StringComparison.Ordinal)
            || typeName.Contains("Dyno", StringComparison.Ordinal)
            || typeName.Contains("Piton", StringComparison.Ordinal)
            || typeName.Contains("Rest", StringComparison.Ordinal)
            || typeName.Contains("Rappel", StringComparison.Ordinal)
            || typeName.Contains("Abseil", StringComparison.Ordinal))
            return TopAnchorModule.Wall;
        return TopAnchorModule.Other;
    }
}

/// <summary>Remembers the lip while a mantle is in progress, then accepts or drops it.</summary>
internal struct TopAnchorMantle
{
    public bool Armed;
    public Vector3 Lip;
    public Vector3 ArmedAt;

    public bool Observe(TopAnchorModule module, bool walking, Vector3 harness, out Vector3 lip)
    {
        lip = default;
        if (module == TopAnchorModule.Mantling)
        {
            if (TopAnchorPolicy.Finite(harness))
            {
                if (!Armed) ArmedAt = harness;
                Armed = true;
                Lip = harness;
            }

            return false;
        }

        if (!Armed) return false;
        if (TopAnchorPolicy.CommitsTopOut(TopAnchorModule.Mantling, module, walking))
        {
            Armed = false;
            lip = TopAnchorPolicy.Finite(harness) ? harness : Lip;
            return TopAnchorPolicy.Finite(lip);
        }

        if (module is TopAnchorModule.Falling
            or TopAnchorModule.SecuredFalling
            or TopAnchorModule.GetDownFromWall
            or TopAnchorModule.JumpDownFromWall
            or TopAnchorModule.Wall
            || !TopAnchorPolicy.Finite(harness)
            || Vector3.Distance(harness, ArmedAt) > TopAnchorPolicy.HorizontalMeters)
            Armed = false;
        return false;
    }
}
