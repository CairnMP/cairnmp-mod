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

    /// <summary>Public player lists skip him, so revive, trails, and voice leave him alone.</summary>
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

/// <summary>Where one of the two practice bodies is, and whether that body was on the wall.</summary>
internal readonly struct PracticeSpot
{
    internal PracticeSpot(float x, float y, float z, float yaw, bool climbing)
    {
        X = x;
        Y = y;
        Z = z;
        Yaw = yaw;
        Climbing = climbing;
    }

    internal float X { get; }
    internal float Y { get; }
    internal float Z { get; }
    internal float Yaw { get; }
    internal bool Climbing { get; }
}

/// <summary>
/// Trading places with the practice climber. The other body keeps the spot you leave.
/// The job, when the rope is tied, stays with the body.
/// </summary>
internal static class PracticeSwap
{
    internal enum Block
    {
        None,
        Paused,
        Falling,
        Dead,
        Unready,
    }

    internal const string FailedText = "Could not switch places";
    internal const string UntiedText = "H switches back";
    internal const string LeadingText = "Leading — H switches back";
    internal const string FollowingText = "Following — H switches back";

    internal readonly struct Result
    {
        internal Result(PracticeSpot you, PracticeSpot frozen, bool youLead)
        {
            You = you;
            Frozen = frozen;
            YouLead = youLead;
        }

        internal PracticeSpot You { get; }
        internal PracticeSpot Frozen { get; }
        internal bool YouLead { get; }
    }

    /// <summary>Paused, dead, and falling do not move either body. Anything else on the wall counts as climbing.</summary>
    internal static Block Refusal(bool paused, bool falling, bool dead, bool walkingOrClimbing)
    {
        if (paused) return Block.Paused;
        if (dead) return Block.Dead;
        if (falling) return Block.Falling;
        if (!walkingOrClimbing) return Block.Unready;
        return Block.None;
    }

    /// <summary>You take his spot. He keeps yours. A tied rope gives you the job his body had.</summary>
    internal static Result Exchange(PracticeSpot you, PracticeSpot them, bool tied, bool youLead)
        => new(them, you, tied ? !youLead : youLead);

    /// <summary>Shown while you are in his body. Null when you are back in your own.</summary>
    internal static string StatusText(bool swapped, bool tied, bool youLead)
    {
        if (!swapped) return null;
        if (!tied) return UntiedText;
        return youLead ? LeadingText : FollowingText;
    }

    internal enum Arrival
    {
        /// <summary>Stand where that body was standing.</summary>
        Walk,

        /// <summary>The spot is on the wall and has somewhere to grip.</summary>
        Climb,

        /// <summary>The spot is in the air. Hovering there does not start a fall.</summary>
        Hover,
    }

    internal readonly struct Place
    {
        internal Place(Arrival arrival, bool leaveClimb)
        {
            Arrival = arrival;
            LeaveClimb = leaveClimb;
        }

        internal Arrival Arrival { get; }

        /// <summary>Leaving the wall for the ground. The climb exit has to run, then the walking body is seated.</summary>
        internal bool LeaveClimb { get; }

        /// <summary>The walking capsule has to be moved onto the ground. A mode change alone stays on the wall.</summary>
        internal bool SeatWalkingBody => Arrival == Arrival.Walk;
    }

    /// <summary>
    /// Forcing a walk on the wall drops the grip, and the next step falls.
    /// A standing spot is a walk that seats the body. A wall spot with nothing
    /// under it hovers. A wall spot with a grip stays a climb.
    /// </summary>
    internal static Place ChoosePlace(bool destinationClimbing, bool currentlyClimbing, bool spotNeedsHover)
    {
        if (!destinationClimbing)
            return new Place(Arrival.Walk, leaveClimb: currentlyClimbing);
        if (spotNeedsHover)
            return new Place(Arrival.Hover, leaveClimb: false);
        return new Place(Arrival.Climb, leaveClimb: false);
    }

    /// <summary>A climb is put back only when that body left one behind.</summary>
    internal static bool UsesStoredClimb(bool destinationClimbing, bool hasStoredClimb)
        => destinationClimbing && hasStoredClimb;
}

/// <summary>Moves the practice climber without letting one frame jump far enough to drop the rope.</summary>
internal static class PracticePartnerMotion
{
    internal const float MaxStepMeters = 4f;
    internal const float SpawnSideMeters = 0.5f;

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

    internal static bool Finite(float x, float y, float z)
        => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
}
