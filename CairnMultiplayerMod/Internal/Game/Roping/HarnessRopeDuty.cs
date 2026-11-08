namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Whether the harness-to-harness rope is still allowed to pull.
/// A piton on the chain is the catch, so that rope stops simulating.
/// The tie and the coil length stay. The rope does not start pulling again
/// if the last piton later leaves, because a short rope between two climbers
/// who have moved apart would yank them.
/// </summary>
internal readonly struct HarnessRopeDuty
{
    internal HarnessRopeDuty(bool simulate, bool tied, float coil)
    {
        Simulate = simulate;
        Tied = tied;
        Coil = coil;
    }

    /// <summary>The physics rope between the harnesses is still stepping.</summary>
    internal bool Simulate { get; }

    /// <summary>The pair stays clipped.</summary>
    internal bool Tied { get; }

    /// <summary>The coil the paid-out stop measures against.</summary>
    internal float Coil { get; }

    internal static HarnessRopeDuty ForChain(bool tied, bool alreadyParked, int pitonCount, float coil)
    {
        if (!tied) return new HarnessRopeDuty(false, false, 0f);
        var simulate = !alreadyParked && pitonCount <= 0;
        return new HarnessRopeDuty(simulate, true, coil);
    }
}
