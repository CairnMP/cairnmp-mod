using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>
/// A local climber the host can tie to while testing the rope team alone.
/// He is not a Steam member and is never broadcast.
/// </summary>
internal static class PracticePartner
{
    /// <summary>Kept clear of the small ids a lobby uses. Spawn refuses this id if it is already taken.</summary>
    internal const int PlayerId = 1_999_999_937;

    internal const string Name = "Practice";

    internal static bool Is(int playerId) => playerId == PlayerId;

    /// <summary>Public player lists skip him, so rations, revive, trails, and voice leave him alone.</summary>
    internal static bool IsPublicRemote(int playerId) => !Is(playerId);

    internal readonly struct LinkCandidate
    {
        internal LinkCandidate(int id, float distance, bool practice)
        {
            Id = id;
            Distance = distance;
            Practice = practice;
        }

        internal int Id { get; }
        internal float Distance { get; }
        internal bool Practice { get; }
    }

    /// <summary>
    /// A real climber inside the link range wins, even when the practice climber is closer.
    /// The practice climber is used only when no real climber is in range.
    /// </summary>
    internal static int ChooseLinkTarget(IReadOnlyList<LinkCandidate> candidates, float rangeMeters)
    {
        if (candidates == null || !float.IsFinite(rangeMeters) || rangeMeters < 0f) return -1;
        var limit = rangeMeters * rangeMeters;
        var bestReal = -1;
        var bestRealDistance = float.MaxValue;
        var bestPractice = -1;
        var bestPracticeDistance = float.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.Id == 0 || !float.IsFinite(candidate.Distance) || candidate.Distance < 0f) continue;
            var squared = candidate.Distance * candidate.Distance;
            if (!float.IsFinite(squared) || squared > limit) continue;
            if (candidate.Practice)
            {
                if (squared >= bestPracticeDistance) continue;
                bestPracticeDistance = squared;
                bestPractice = candidate.Id;
            }
            else if (squared < bestRealDistance)
            {
                bestRealDistance = squared;
                bestReal = candidate.Id;
            }
        }

        return bestReal != -1 ? bestReal : bestPractice;
    }
}

/// <summary>Moves the practice climber without letting one frame jump far enough to drop the rope.</summary>
internal static class PracticePartnerMotion
{
    internal const float MaxStepMeters = 4f;
    internal const float SpawnSideMeters = 0.5f;
    internal const float FlySpeedMetersPerSecond = 8f;
    internal const float FlyBoost = 3f;

    internal static bool TryStep(
        float fromX, float fromY, float fromZ,
        float toX, float toY, float toZ,
        float maxStep,
        out float x, out float y, out float z)
    {
        x = fromX;
        y = fromY;
        z = fromZ;
        if (!Finite(fromX, fromY, fromZ) || !Finite(toX, toY, toZ) || !float.IsFinite(maxStep) || maxStep <= 0f)
            return false;
        var dx = toX - fromX;
        var dy = toY - fromY;
        var dz = toZ - fromZ;
        var squared = dx * dx + dy * dy + dz * dz;
        if (!float.IsFinite(squared)) return false;
        if (squared <= maxStep * maxStep)
        {
            x = toX;
            y = toY;
            z = toZ;
            return true;
        }

        var scale = maxStep / MathF.Sqrt(squared);
        x = fromX + dx * scale;
        y = fromY + dy * scale;
        z = fromZ + dz * scale;
        return Finite(x, y, z);
    }

    /// <summary>Unity yaw 0 faces +Z, so his right side is +X.</summary>
    internal static bool TryBeside(
        float x, float y, float z, float yawDegrees, float sideMeters,
        out float outX, out float outY, out float outZ)
    {
        outX = x;
        outY = y;
        outZ = z;
        if (!Finite(x, y, z) || !float.IsFinite(yawDegrees) || !float.IsFinite(sideMeters)) return false;
        var yaw = yawDegrees * (MathF.PI / 180f);
        outX = x + MathF.Cos(yaw) * sideMeters;
        outY = y;
        outZ = z - MathF.Sin(yaw) * sideMeters;
        return Finite(outX, outY, outZ);
    }

    /// <summary>
    /// Flies from the current point. ahead and strafe are in the look frame.
    /// vertical is world up. A frame that would jump past maxStep is shortened.
    /// </summary>
    internal static bool TryFly(
        float fromX, float fromY, float fromZ,
        float forwardX, float forwardY, float forwardZ,
        float rightX, float rightY, float rightZ,
        float strafe, float ahead, float vertical,
        float speed, float deltaSeconds, float maxStep,
        out float x, out float y, out float z)
    {
        x = fromX;
        y = fromY;
        z = fromZ;
        if (!Finite(fromX, fromY, fromZ) || !Finite(forwardX, forwardY, forwardZ) || !Finite(rightX, rightY, rightZ))
            return false;
        if (!float.IsFinite(strafe) || !float.IsFinite(ahead) || !float.IsFinite(vertical)
            || !float.IsFinite(speed) || speed < 0f
            || !float.IsFinite(deltaSeconds) || deltaSeconds < 0f)
            return false;

        var wishX = rightX * strafe + forwardX * ahead;
        var wishY = rightY * strafe + forwardY * ahead + vertical;
        var wishZ = rightZ * strafe + forwardZ * ahead;
        var squared = wishX * wishX + wishY * wishY + wishZ * wishZ;
        if (!float.IsFinite(squared)) return false;
        if (squared > 1f)
        {
            var scale = 1f / MathF.Sqrt(squared);
            wishX *= scale;
            wishY *= scale;
            wishZ *= scale;
        }

        var distance = speed * deltaSeconds;
        return TryStep(fromX, fromY, fromZ,
            fromX + wishX * distance, fromY + wishY * distance, fromZ + wishZ * distance,
            maxStep, out x, out y, out z);
    }

    internal static bool Finite(float x, float y, float z)
        => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
}
