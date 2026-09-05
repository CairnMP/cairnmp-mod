using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Engine-independent logging boundary for internal services. Bootstrap supplies MelonLoader
/// delegates once; lower layers never need to reference the composition root.
/// </summary>
internal static class ModLog
{
    private const int MaxSuppressedFailures = 100;
    private static readonly Action<string> NoOp = _ => { };
    private static readonly object SuppressedFailuresLock = new();
    private static readonly HashSet<string> SuppressedFailures = new(StringComparer.Ordinal);
    private static Action<string> _info = NoOp;
    private static Action<string> _warning = NoOp;
    private static Action<string> _error = NoOp;
    private static Action<string> _debug = NoOp;

    internal static void Initialize(Action<string> info, Action<string> warning,
        Action<string> error, Action<string> debug)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _warning = warning ?? throw new ArgumentNullException(nameof(warning));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _debug = debug ?? throw new ArgumentNullException(nameof(debug));
    }

    internal static void Info(string message) => _info(message);
    internal static void Warning(string message) => _warning(message);
    internal static void Error(string message) => _error(message);
    internal static void Debug(string message) => _debug(message);

    /// <summary>
    /// Records a best-effort interop failure once without changing the caller's fallback behavior.
    /// The cap prevents unstable engine objects from flooding logs every frame.
    /// </summary>
    internal static void SuppressedException(string operation, Exception exception)
    {
        if (exception == null) return;

        var key = $"{operation}\0{exception.GetType().FullName}\0{exception.Message}";
        lock (SuppressedFailuresLock)
        {
            if (SuppressedFailures.Count >= MaxSuppressedFailures || !SuppressedFailures.Add(key))
                return;
        }

        var message = $"[CairnMP] Best-effort operation '{operation}' failed: " +
                      $"{exception.GetType().Name}: {exception.Message}";
        System.Diagnostics.Debug.WriteLine(message);
        try { _debug(message); }
        catch (Exception loggerException)
        {
            System.Diagnostics.Debug.WriteLine($"CairnMP debug logger also failed: {loggerException}");
        }
    }

    internal static void Shutdown()
    {
        _info = NoOp;
        _warning = NoOp;
        _error = NoOp;
        _debug = NoOp;
        lock (SuppressedFailuresLock) SuppressedFailures.Clear();
    }
}
