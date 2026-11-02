using System;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Identity and lifetime of a captured native station, never a replacement piton.</summary>
internal sealed class BelayStationAnchor
{
    internal const float MaximumMovement = .05f;
    private readonly long _lifeline, _rope, _piton;
    private readonly Vector3 _position;

    internal BelayStationAnchor(long lifeline, long rope, long piton, Vector3 position)
    {
        if (lifeline == 0 || rope == 0 || piton == 0 || !Finite(position))
            throw new ArgumentException("A station needs live native identities and a finite position.");
        _lifeline = lifeline; _rope = rope; _piton = piton; _position = position;
    }

    internal bool Matches(long lifeline, long rope, long piton, float health, Vector3 position)
        => lifeline == _lifeline && rope == _rope && piton == _piton
            && float.IsFinite(health) && health > 0 && Finite(position)
            && Vector3.DistanceSquared(_position, position) <= MaximumMovement * MaximumMovement;

    private static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
