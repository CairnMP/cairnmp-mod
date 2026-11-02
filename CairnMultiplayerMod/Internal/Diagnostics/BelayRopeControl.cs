using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Local released-length experiment for one fixed final protection segment.
/// Capacity, released rope and geometric distance are separate measurements.
/// </summary>
internal sealed class BelayRopeControl
{
    internal const float Clearance = .03f;
    internal const float MetersPerSecond = 1f;
    internal const float MaximumStepSeconds = .1f;
    private double _released;
    private float _swingOffset;

    internal float Capacity { get; private set; }
    internal float Released => (float)_released;
    internal float Distance { get; private set; }
    internal float Slack => Math.Max(0, Released - Distance);
    internal float Reserve => Math.Max(0, Capacity - Released);
    internal bool Braked { get; private set; }
    internal bool CaptureBrakeDistance { get; private set; }
    internal int Motion { get; private set; }
    internal bool Suspended { get; private set; }
    internal float SwingOffset => _swingOffset;

    internal BelayRopeControl(float capacity, float released, float distance)
    {
        if (!float.IsFinite(capacity) || capacity <= 0 || capacity > 200)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (!ValidDistance(distance) || distance > capacity)
            throw new ArgumentOutOfRangeException(nameof(distance));
        if (!ValidDistance(released) || released > capacity)
            throw new ArgumentOutOfRangeException(nameof(released));
        Capacity = capacity;
        Distance = distance;
        _released = Math.Max(released, Math.Min(capacity, distance + Clearance));
    }

    internal void SetBrake(bool braked)
    {
        if (Braked == braked) return;
        Braked = braked;
        // Input can arrive before the next native physics step. Catch a falling
        // body at that step's loaded distance, not at an older rope measurement.
        CaptureBrakeDistance = braked && Suspended;
    }

    internal bool TrySetMotion(int motion)
    {
        if (motion < -1 || motion > 1) return false;
        Motion = motion;
        return true;
    }

    internal bool TryObserveCapacity(float capacity)
    {
        if (!float.IsFinite(capacity) || capacity <= 0 || capacity > 200) return false;
        // GetRemainingSlackMeters(false) excludes the final segment but still
        // depends on upstream holder positions. That geometric remainder can
        // move without a topology change; it must not reset the paid-out length.
        if (Suspended) return true;
        if (capacity < Released) return false;
        Capacity = capacity;
        return true;
    }

    internal bool TryStep(float distance, float minimumForTakeIn, float elapsed)
    {
        if (!ValidDistance(distance) || !ValidDistance(minimumForTakeIn)
            || !float.IsFinite(elapsed) || elapsed < 0) return false;
        Distance = distance;
        var floor = Math.Min(Capacity, Math.Max(distance, minimumForTakeIn) + Clearance);
        var step = MetersPerSecond * Math.Min(elapsed, MaximumStepSeconds);

        // Free payout must cover BOTH the rope geometry and native suspension.
        // Their origins can diverge during a fall. Capturing a brake only once
        // prevents a shorter stale target from winching the body upwards, while
        // subsequent braked steps keep the catch fixed rather than following it.
        if (!Braked || CaptureBrakeDistance)
        {
            _released = Math.Max(_released, floor);
            CaptureBrakeDistance = false;
        }
        if (Motion > 0 && !Braked) _released = Math.Min(Capacity, _released + step);
        // One-way take-in removes slack even with the brake engaged. It never
        // shortens an already loaded segment or increases a stretched target.
        if (Motion < 0 && _released > floor) _released = Math.Max(floor, _released - step);
        return true;
    }

    internal bool TryAttachSuspension(float realDistance, out float target)
    {
        target = realDistance;
        if (Suspended || !ValidDistance(realDistance)) return false;
        var offset = Distance - realDistance;
        if (!TryMapTarget(offset, out target)) return false;
        _swingOffset = offset;
        Suspended = true;
        return true;
    }

    internal void EndSuspension()
    {
        Suspended = false;
        CaptureBrakeDistance = false;
        _swingOffset = 0;
    }

    internal bool TryGetSuspensionTarget(out float target)
    {
        target = 0;
        return Suspended && TryMapTarget(_swingOffset, out target);
    }

    private bool TryMapTarget(float offset, out float target)
    {
        // A fixed translation is an experimental adapter contract, not a claim
        // that rope geometry and the native suspension share the same origin.
        target = (Braked ? Released : Capacity) - offset;
        return float.IsFinite(target) && target > 0 && target <= 200;
    }

    private static bool ValidDistance(float value) => float.IsFinite(value) && value > 0;
}
