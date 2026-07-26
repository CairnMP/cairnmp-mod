using System;

namespace CairnMultiplayerMod.Framework;

/// <summary>
/// Where the framework writes. Goes to the MelonLoader console in game; tests redirect it so
/// the framework can be exercised without Unity, which is the whole point of putting feature
/// logic here rather than in the networking layer.
/// </summary>
internal static class FeatureLog
{
    private static Action<string> _info = message => Mod.Log.Msg(message);
    private static Action<string> _warn = message => Mod.Log.Warning(message);
    private static Action<string> _error = message => Mod.Log.Error(message);

    internal static void Info(string message) => _info(message);
    internal static void Warn(string message) => _warn(message);
    internal static void Error(string message) => _error(message);

    /// <summary>Redirects the output (tests). Pass null to restore the game console.</summary>
    internal static void SetSink(Action<string> info, Action<string> warn, Action<string> error)
    {
        _info = info ?? (message => Mod.Log.Msg(message));
        _warn = warn ?? (message => Mod.Log.Warning(message));
        _error = error ?? (message => Mod.Log.Error(message));
    }
}
