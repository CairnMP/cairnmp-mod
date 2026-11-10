using System;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Restricts native movement permission at a braked, paid-out rope end.</summary>
internal static class BelayClimbLimit
{
    internal const float EndClearance = BelayRopeControl.Clearance;

    internal static bool TryRestrict(bool nativeAllowed, bool braked, float released,
        Vector3 pitonToHarness, Vector3 direction, out bool allowed)
    {
        allowed = nativeAllowed;
        if (!nativeAllowed || !braked) return true;
        if (!float.IsFinite(released) || released <= 0 || released > 200
            || !Finite(pitonToHarness) || !Finite(direction)) return false;
        var distance = pitonToHarness.Length();
        var movement = direction.LengthSquared();
        if (!float.IsFinite(distance) || !float.IsFinite(movement)) return false;
        if (distance == 0 || movement == 0 || released - distance > EndClearance) return true;
        // Match the native rope-end direction rule. Inward movement remains
        // possible even if a previous frame has slightly exceeded the budget.
        // A tangent at the taut boundary would also require additional rope.
        allowed = Vector3.Dot(pitonToHarness, direction) < 0;
        return true;
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
