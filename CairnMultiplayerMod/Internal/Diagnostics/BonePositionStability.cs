using System;
using System.Globalization;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Answers one question: do the bone local positions in a NetFrame actually change?
///
/// A NetFrame carries a position AND an euler triplet per bone, at 30 Hz. For every bone but
/// the root those positions are Transform.localPosition — in a skeletal rig that is the bone's
/// length, fixed by the bind pose, while animation only turns joints. If that holds here, half
/// of every frame is a constant resent thirty times a second, and could be sent once.
///
/// "Probably constant" is not good enough to build on, hence this sampler. Pure arithmetic so
/// the rule can be tested; the caller feeds it captured frames.
/// </summary>
internal sealed class BonePositionStability
{
    /// <summary>
    /// A bone counts as moving past this, in metres. Well under a millimetre: small enough that
    /// real motion is caught, large enough to ignore float noise in a round trip.
    /// </summary>
    internal const float MovementEpsilon = 1e-4f;

    private float[] _reference;

    internal long FramesCompared { get; private set; }
    internal int MovingComponents { get; private set; }
    internal float LargestDelta { get; private set; }
    internal int LastVectorCount { get; private set; }
    internal bool SawDifferentLength { get; private set; }

    /// <summary>
    /// Compares one captured position array against the first one seen. The root (index 0) is
    /// excluded: it is a world position and is expected to move.
    /// </summary>
    internal void Sample(float[] positions)
    {
        if (positions == null || positions.Length < 3) return;

        LastVectorCount = positions.Length / 3;

        if (_reference == null || _reference.Length != positions.Length)
        {
            if (_reference != null) SawDifferentLength = true;
            _reference = (float[])positions.Clone();
            return;
        }

        FramesCompared++;

        // Skip the first vector: the root carries a world position and legitimately moves.
        for (var i = 3; i < positions.Length; i++)
        {
            var delta = Math.Abs(positions[i] - _reference[i]);
            if (delta <= MovementEpsilon) continue;

            MovingComponents++;
            if (delta > LargestDelta) LargestDelta = delta;
        }
    }

    internal void Reset()
    {
        _reference = null;
        FramesCompared = 0;
        MovingComponents = 0;
        LargestDelta = 0;
        SawDifferentLength = false;
    }

    /// <summary>True when nothing but the root ever moved — the case that makes the
    /// optimisation safe.</summary>
    internal bool PositionsLookConstant => FramesCompared > 0 && MovingComponents == 0 && !SawDifferentLength;

    internal string Describe()
    {
        var culture = CultureInfo.InvariantCulture;
        if (FramesCompared == 0)
            return string.Format(culture, "bones={0}: not enough frames yet", LastVectorCount);

        return string.Format(culture,
            "bones={0} frames={1} moving-components={2} largest-delta={3:F6} m length-changed={4} -> {5}",
            LastVectorCount, FramesCompared, MovingComponents, LargestDelta, SawDifferentLength,
            PositionsLookConstant
                ? "positions are CONSTANT (they could be sent once)"
                : "positions DO move (they must keep being sent)");
    }
}
