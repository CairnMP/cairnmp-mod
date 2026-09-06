using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CairnMultiplayerMod.Framework;

/// <summary>
/// Where the framework writes. Goes to the MelonLoader console in game; tests redirect it so
/// the framework can be exercised without Unity, which is the whole point of putting feature
/// logic here rather than in the networking layer.
/// </summary>
internal static class FeatureLog
{
    private static readonly Action<string> NoOp = _ => { };
    private static Action<string> _info = NoOp;
    private static Action<string> _warn = NoOp;
    private static Action<string> _error = NoOp;
    private static readonly Dictionary<string, long> LastErrors = new();

    internal static void ErrorThrottled(string key, string message)
    {
        var now = Stopwatch.GetTimestamp();
        if (LastErrors.TryGetValue(key, out var last) && (now - last) / (double)Stopwatch.Frequency < 5) return;
        if (!LastErrors.ContainsKey(key) && LastErrors.Count >= 128) return;
        LastErrors[key] = now;
        _error(message);
    }

    internal static void Info(string message) => _info(message);
    internal static void Warn(string message) => _warn(message);
    internal static void Error(string message) => _error(message);

    /// <summary>Redirects the output. Pass null delegates to disable output.</summary>
    internal static void SetSink(Action<string> info, Action<string> warn, Action<string> error)
    {
        LastErrors.Clear();
        _info = info ?? NoOp;
        _warn = warn ?? NoOp;
        _error = error ?? NoOp;
    }
}
