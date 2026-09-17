using System;
using System.Globalization;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Call count and time for one instrumented native method. Plain counters, no allocation on
/// the sampling path: these are accumulated from inside per-frame game methods.
/// </summary>
internal sealed class NativeProbeStats
{
    private readonly long _frequency;

    internal NativeProbeStats(string name, long frequency)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = frequency;
    }

    internal string Name { get; }
    internal long Calls { get; private set; }
    internal long TotalTicks { get; private set; }
    internal long MaxTicks { get; private set; }

    internal void Add(long ticks)
    {
        // A negative delta means the timestamp wrapped or the pair was mismatched; counting it
        // would quietly corrupt the total we are trying to trust.
        if (ticks < 0) return;

        Calls++;
        TotalTicks += ticks;
        if (ticks > MaxTicks) MaxTicks = ticks;
    }

    internal void Reset()
    {
        Calls = 0;
        TotalTicks = 0;
        MaxTicks = 0;
    }

    internal double TotalMilliseconds => TotalTicks * 1000d / _frequency;
    internal double MaxMilliseconds => MaxTicks * 1000d / _frequency;
    internal double AverageMilliseconds => Calls == 0 ? 0 : TotalMilliseconds / Calls;

    /// <summary>Milliseconds spent per second of wall clock — the figure that says whether
    /// this method is worth optimising at all.</summary>
    internal double MillisecondsPerSecond(double elapsedSeconds)
        => elapsedSeconds <= 0 ? 0 : TotalMilliseconds / elapsedSeconds;

    internal string Describe(double elapsedSeconds)
    {
        var culture = CultureInfo.InvariantCulture;
        return string.Format(culture,
            "{0}: {1} calls, {2:F2} ms total ({3:F3} ms/s), avg {4:F4} ms, worst {5:F2} ms",
            Name, Calls, TotalMilliseconds, MillisecondsPerSecond(elapsedSeconds),
            AverageMilliseconds, MaxMilliseconds);
    }
}
