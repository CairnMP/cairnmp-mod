using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal sealed class BelayBrakeControl
{
    internal const float CatchClearance = .03f;
    internal bool Braked { get; private set; }
    internal bool Attached { get; private set; }
    internal bool CaptureBrakeDistance { get; private set; }
    internal float Available { get; }
    internal float Target { get; private set; }

    internal BelayBrakeControl(float available)
    {
        if (!float.IsFinite(available) || available <= 0 || available > 200) throw new ArgumentOutOfRangeException(nameof(available));
        Available = available;
        Target = available;
    }

    internal void ToggleBrake()
    {
        // Manual input stays latched until another press.
        Braked = !Braked;
        CaptureBrakeDistance = Braked;
        if (!Braked) Target = Available;
    }

    internal bool TryAttach(float nativeDistance, float realDistance, out float target)
    {
        target = nativeDistance;
        if (Attached || !ValidDistance(nativeDistance) || !ValidDistance(realDistance)
            || nativeDistance > Available + CatchClearance) return false;
        Attached = true;
        if (Braked) CaptureBrakeDistance = true;
        return TryStep(realDistance, out target);
    }

    internal bool TryStep(float realDistance, out float target)
    {
        target = Target;
        if (!Attached || !ValidDistance(realDistance)) return false;
        if (CaptureBrakeDistance)
        {
            // Take up unused allowance only, not the loaded geometric distance.
            // At the rope end the knot limit remains the cap, even under stretch.
            Target = Math.Min(Available, realDistance + CatchClearance);
            CaptureBrakeDistance = false;
        }
        target = Target;
        return true;
    }

    private static bool ValidDistance(float value) => float.IsFinite(value) && value > 0;
}
