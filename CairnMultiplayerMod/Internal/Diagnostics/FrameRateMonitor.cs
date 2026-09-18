using System;
using System.Globalization;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Frame pacing over a window: average, worst frame, and the 1% low.
///
/// Comparing two engine settings needs more than an average — a change can lift the average
/// while making the stutters worse, which is the opposite of what a player feels. The 1% low
/// is the figure that tracks perceived smoothness.
/// </summary>
internal sealed class FrameRateMonitor
{
    // A window of a few hundred frames is enough for a stable 1% low without holding much.
    private const int Capacity = 2048;

    private readonly double[] _frameMs = new double[Capacity];
    private int _count;
    private bool _wrapped;

    internal double ElapsedSeconds { get; private set; }

    internal void Add(double deltaSeconds)
    {
        // A zero or negative delta is not a frame, and a huge one is a load screen rather than
        // a stutter worth reporting.
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0 || deltaSeconds > 5) return;

        _frameMs[_count % Capacity] = deltaSeconds * 1000;
        _count++;
        if (_count >= Capacity) _wrapped = true;
        ElapsedSeconds += deltaSeconds;
    }

    internal int SampleCount => _wrapped ? Capacity : _count;

    internal void Reset()
    {
        _count = 0;
        _wrapped = false;
        ElapsedSeconds = 0;
    }

    internal double AverageFps => ElapsedSeconds <= 0 || _count == 0 ? 0 : _count / ElapsedSeconds;

    /// <summary>The slowest frame in the window, in milliseconds.</summary>
    internal double WorstFrameMs
    {
        get
        {
            var n = SampleCount;
            var worst = 0d;
            for (var i = 0; i < n; i++) if (_frameMs[i] > worst) worst = _frameMs[i];
            return worst;
        }
    }

    /// <summary>
    /// The 1% low, expressed as fps: the average of the slowest 1% of frames. This is what
    /// stutter feels like, and it moves independently of the average.
    /// </summary>
    internal double OnePercentLowFps
    {
        get
        {
            var n = SampleCount;
            if (n == 0) return 0;

            var sorted = new double[n];
            Array.Copy(_frameMs, sorted, n);
            Array.Sort(sorted);

            var slowest = Math.Max(1, n / 100);
            var total = 0d;
            for (var i = n - slowest; i < n; i++) total += sorted[i];

            var meanMs = total / slowest;
            return meanMs <= 0 ? 0 : 1000 / meanMs;
        }
    }

    internal string Describe() => string.Format(CultureInfo.InvariantCulture,
        "{0} frames over {1:F1}s — avg {2:F1} fps, 1% low {3:F1} fps, worst frame {4:F1} ms",
        _count, ElapsedSeconds, AverageFps, OnePercentLowFps, WorstFrameMs);
}
