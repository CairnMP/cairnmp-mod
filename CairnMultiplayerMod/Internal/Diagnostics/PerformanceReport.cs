using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CairnMultiplayerMod.Internal.Diagnostics;

internal sealed record TimingSummary(int Samples, double TotalMs, double? MeanMs, double? MedianMs,
    double? P95Ms, double? P99Ms, double? MaximumMs, double? AverageFps,
    int Over33Point3Ms, int Over50Ms, int Over100Ms)
{
    internal static TimingSummary Calculate(double[] values)
    {
        if (values.Any(v => !double.IsFinite(v) || v < 0))
            throw new ArgumentException("Timing samples must be finite and nonnegative.", nameof(values));
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        if (sorted.Length == 0) return new(0, 0, null, null, null, null, null, null, 0, 0, 0);
        double Percentile(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
        var total = sorted.Sum();
        return new(sorted.Length, total, total / sorted.Length, Percentile(.5), Percentile(.95),
            Percentile(.99), sorted[^1], total > 0 ? sorted.Length * 1000.0 / total : null,
            sorted.Count(v => v > 33.3), sorted.Count(v => v > 50), sorted.Count(v => v > 100));
    }
}

internal static class PerformanceReport
{
    internal const string Limitations = "Frame intervals are between CairnMP OnUpdate callbacks, not display presents or CPU/GPU frame times. " +
        "Scopes measure main-thread elapsed wall time, including synchronous native calls, waits and preemption; background voice/network work and uninstrumented native work are excluded. " +
        "ModUpdate includes nested subsystem scopes; Ui also includes OnGUI calls between Updates. Do not add ModUpdate to subsystem totals. " +
        "Percentiles use nearest rank. FPS is sample count / total interval duration. Scene changes are retained and marked, not discarded. " +
        "External display/native/GPU measurements and causality are unavailable in this report. No performance gain has been established.";

    internal static string Write(string directory, PerformanceCapture capture, Dictionary<string, string> metadata)
    {
        if (!capture.IsComplete) throw new InvalidOperationException("Freeze the capture before exporting it.");
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, $"CairnMP-performance-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var frames = capture.FrameMilliseconds.Take(capture.Count).ToArray();
        var timing = TimingSummary.Calculate(frames);
        var areas = new Dictionary<string, TimingSummary>();
        var rawAreas = new Dictionary<string, double[]>();
        for (var a = 0; a < PerformanceCapture.AreaCount; a++)
        {
            var values = new double[capture.Count];
            for (var i = 0; i < values.Length; i++)
                values[i] = capture.AreaTicks[i * PerformanceCapture.AreaCount + a] * 1000.0 / capture.Frequency;
            var name = ((PerformanceArea)a).ToString();
            areas[name] = TimingSummary.Calculate(values) with { AverageFps = null };
            rawAreas[name] = values;
        }
        var report = new
        {
            SchemaVersion = 1, Metadata = metadata, Limitations,
            capture.StopReason, capture.WarmupSeconds, capture.RequestedSeconds,
            capture.StartedTimestamp, StopwatchFrequency = capture.Frequency,
            capture.MeasurementStartSeconds, capture.StoppedSeconds,
            BufferCapacity = capture.FrameMilliseconds.Length,
            capture.DroppedEvents, Frames = timing, Areas = areas,
            Events = capture.Events.Take(capture.EventCount).ToArray(),
            FrameEndSecondsSinceTrigger = capture.FrameEndSeconds.Take(capture.Count).ToArray(),
            FrameIntervalsMs = frames, AreaMillisecondsByFrame = rawAreas
        };
        using (var stream = new FileStream(stem + ".json", FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });

        var md = new StringBuilder("# CairnMP performance capture\n\n");
        md.AppendLine(Limitations).AppendLine();
        md.AppendLine($"Stop reason: **{capture.StopReason}**. Samples: **{capture.Count}**. Dropped scene/session events: **{capture.DroppedEvents}**.");
        md.AppendLine("A buffer-capacity or early stop is a partial run; do not compare it as a complete three-minute run.\n");
        foreach (var item in metadata) md.AppendLine($"- {Escape(item.Key)}: {Escape(item.Value)}");
        md.AppendLine("\n## Timings\n\n| Measurement | Mean ms | Median ms | p95 ms | p99 ms | Max ms |\n| --- | ---: | ---: | ---: | ---: | ---: |");
        AppendRow(md, "Update interval", timing);
        foreach (var area in areas) AppendRow(md, area.Key, area.Value);
        md.AppendLine($"\nAverage update FPS: {Number(timing.AverageFps)}. Intervals >33.3 / >50 / >100 ms: {timing.Over33Point3Ms} / {timing.Over50Ms} / {timing.Over100Ms}.\n");
        md.AppendLine("## Events\n\n| Seconds since trigger | Event | Value |\n| ---: | --- | --- |");
        foreach (var ev in capture.Events.Take(capture.EventCount))
            md.AppendLine($"| {Number(ev.SecondsSinceTrigger)} | {Escape(ev.Kind)} | {Escape(ev.Value)} |");
        md.AppendLine("\n## Interpretation\n\nMeasured: callback intervals and instrumented scope elapsed times.\n\nConfirmed bottlenecks: none inferred automatically. Compare repeated external baseline captures before attributing a slowdown to the game, loader or mod.");
        File.WriteAllText(stem + ".md", md.ToString());
        return stem;
    }

    private static void AppendRow(StringBuilder md, string name, TimingSummary s)
        => md.AppendLine($"| {name} | {Number(s.MeanMs)} | {Number(s.MedianMs)} | {Number(s.P95Ms)} | {Number(s.P99Ms)} | {Number(s.MaximumMs)} |");
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Escape(string value) => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;");
}
