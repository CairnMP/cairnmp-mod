using System;
using System.Diagnostics;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal enum PerformanceArea { ModUpdate, Network, Players, Features, Ropes, Ui, Count }

/// <summary>Main-thread recorder. No Unity dependencies, allocations or I/O during frame sampling.</summary>
internal sealed class PerformanceCapture
{
    internal const int DefaultCapacity = 131072;
    internal const int EventCapacity = 256;
    internal const int AreaCount = (int)PerformanceArea.Count;
    private readonly long _frequency;
    private readonly long _warmupEnd;
    private readonly long _duration;
    private readonly long[] _pending = new long[AreaCount];
    private long _previous;
    private long _measurementStart;

    internal readonly double[] FrameMilliseconds;
    internal readonly double[] FrameEndSeconds;
    internal readonly long[] AreaTicks;
    internal readonly PerformanceEvent[] Events = new PerformanceEvent[EventCapacity];
    internal long StartedTimestamp { get; }
    internal long Frequency => _frequency;
    internal int Count { get; private set; }
    internal int EventCount { get; private set; }
    internal int DroppedEvents { get; private set; }
    internal bool IsRecording { get; private set; }
    internal bool IsComplete { get; private set; }
    internal string StopReason { get; private set; }
    internal double WarmupSeconds { get; }
    internal double RequestedSeconds { get; }
    internal double? MeasurementStartSeconds { get; private set; }
    internal double StoppedSeconds { get; private set; }

    internal PerformanceCapture(long now, long frequency, double warmupSeconds = 30,
        double durationSeconds = 180, int capacity = DefaultCapacity)
    {
        if (frequency <= 0 || capacity <= 0 || capacity > DefaultCapacity ||
            !double.IsFinite(warmupSeconds) || warmupSeconds < 0 || warmupSeconds > 300 ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0 || durationSeconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        StartedTimestamp = now;
        _frequency = frequency;
        WarmupSeconds = warmupSeconds;
        RequestedSeconds = durationSeconds;
        _warmupEnd = checked(now + (long)(warmupSeconds * frequency));
        _duration = checked((long)(durationSeconds * frequency));
        FrameMilliseconds = new double[capacity];
        FrameEndSeconds = new double[capacity];
        AreaTicks = new long[capacity * AreaCount];
    }

    // The interval ending at this Update belongs to work collected AFTER the previous Update.
    // Keep the complete last interval, even when a stall carries it beyond the time limit.
    internal void Advance(long now)
    {
        if (IsComplete) return;
        if (!IsRecording)
        {
            if (now < _warmupEnd) return;
            IsRecording = true;
            _previous = _measurementStart = now;
            MeasurementStartSeconds = Seconds(now);
            return;
        }
        if (now <= _previous) return;
        FrameMilliseconds[Count] = (now - _previous) * 1000.0 / _frequency;
        FrameEndSeconds[Count] = Seconds(now);
        Array.Copy(_pending, 0, AreaTicks, Count * AreaCount, AreaCount);
        Array.Clear(_pending, 0, AreaCount);
        Count++;
        _previous = now;
        if (now - _measurementStart >= _duration) Stop(now, "duration-complete");
        else if (Count == FrameMilliseconds.Length) Stop(now, "buffer-capacity");
    }

    internal void AddTicks(PerformanceArea area, long ticks)
    {
        if (IsRecording && !IsComplete && ticks >= 0 && area >= 0 && area < PerformanceArea.Count)
            _pending[(int)area] += ticks;
    }

    internal Measurement Measure(PerformanceArea area)
        => IsRecording && !IsComplete ? new Measurement(this, area) : default;

    internal void AddEvent(long now, string kind, string value)
    {
        if (IsComplete) return;
        if (EventCount == Events.Length) { DroppedEvents++; return; }
        Events[EventCount++] = new PerformanceEvent(Seconds(now), Limit(kind), Limit(value));
    }

    // Stop never invents a partial frame. Advance at an Update boundary before a manual stop.
    internal void Stop(long now, string reason)
    {
        if (IsComplete) return;
        IsComplete = true;
        IsRecording = false;
        StopReason = reason;
        StoppedSeconds = Seconds(now);
    }

    private double Seconds(long now) => (now - StartedTimestamp) / (double)_frequency;
    private static string Limit(string value) => value == null ? "" : value.Length <= 512 ? value : value.Substring(0, 512);

    internal readonly struct Measurement : IDisposable
    {
        private readonly PerformanceCapture _owner;
        private readonly PerformanceArea _area;
        private readonly long _start;
        internal Measurement(PerformanceCapture owner, PerformanceArea area)
        {
            _owner = owner;
            _area = area;
            _start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_owner != null) _owner.AddTicks(_area, Stopwatch.GetTimestamp() - _start);
        }
    }
}

internal sealed record PerformanceEvent(double SecondsSinceTrigger, string Kind, string Value);
