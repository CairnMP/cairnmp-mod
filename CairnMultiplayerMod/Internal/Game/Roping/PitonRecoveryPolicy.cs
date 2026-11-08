using System.Collections.Generic;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Which placed piton a climber may take. The nearest one inside reach decides, so a piece
/// that is holding the climber is not skipped in favour of one farther along the wall.
/// </summary>
internal static class PitonRecoveryPolicy
{
    /// <summary>Inside <see cref="PitonSync.RecoverReachMeters"/>, so a take is attributed
    /// to this climber and not reported as a piton that vanished out of sight.</summary>
    internal const float ReachMeters = 0.75f;

    /// <summary>
    /// A climber on the wall or on the ground can take a piton the role allows.
    /// A climber hanging on the rope cannot take the piton that is holding them.
    /// </summary>
    internal static bool RefusesHeldPiton(PartnerPose pose, bool heldByRope)
        => pose == PartnerPose.Hanging && heldByRope;

    internal readonly struct Candidate
    {
        internal Candidate(Vector3 position, bool critical, bool bagCanTake)
        {
            Position = position;
            Critical = critical;
            BagCanTake = bagCanTake;
        }

        internal Vector3 Position { get; }
        internal bool Critical { get; }
        internal bool BagCanTake { get; }
    }

    internal enum Decision { None, Take, Critical, BagFull }

    internal readonly struct Result
    {
        internal Result(Decision decision, int index)
        {
            Decision = decision;
            Index = index;
        }

        internal Decision Decision { get; }
        internal int Index { get; }
    }

    internal static Result Choose(IReadOnlyList<Candidate> candidates, Vector3 player)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        var reach = ReachMeters * ReachMeters;
        for (var i = 0; i < candidates.Count; i++)
        {
            var distance = Vector3.DistanceSquared(candidates[i].Position, player);
            if (distance > reach || distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }

        if (best < 0) return new Result(Decision.None, -1);
        var chosen = candidates[best];
        if (chosen.Critical) return new Result(Decision.Critical, best);
        if (!chosen.BagCanTake) return new Result(Decision.BagFull, best);
        return new Result(Decision.Take, best);
    }
}
