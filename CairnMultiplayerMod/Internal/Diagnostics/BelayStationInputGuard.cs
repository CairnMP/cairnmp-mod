using System;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>Owns one disabled climb action, including restoration after a modal panel closes.</summary>
internal sealed class BelayStationInputGuard<T> where T : class
{
    private readonly Func<T, long> _identity;
    private readonly Func<T, bool> _enabled;
    private readonly Action<T> _disable, _enable, _reset;
    private T _owned;

    internal BelayStationInputGuard(Func<T, long> identity, Func<T, bool> enabled,
        Action<T> disable, Action<T> enable, Action<T> reset)
    {
        _identity = identity;
        _enabled = enabled;
        _disable = disable;
        _enable = enable;
        _reset = reset;
    }

    internal bool HasOwnership => _owned != null;

    // Scene/shutdown teardown must never retain an action from a destroyed input asset.
    internal void DiscardOwnership() => _owned = null;

    internal bool TrySuppress(T action)
    {
        if (action == null || _identity(action) == 0) return false;
        if (_owned != null && _identity(_owned) != _identity(action)) return false;
        if (_enabled(action))
        {
            // Record the attempt first: Disable can change native state before it throws.
            _owned ??= action;
            _disable(action);
        }
        // Disable alone preserves WasPressedThisFrame. Clear the already sampled
        // press as well, including when a modal disabled this action before us.
        _reset(action);
        return !_enabled(action);
    }

    internal bool TryRelease(bool canRestore)
    {
        if (_owned == null) return true;
        if (!canRestore) return false;
        // Keep ownership on an exception or unsuccessful enable so Tick can retry.
        if (!_enabled(_owned)) _enable(_owned);
        if (!_enabled(_owned)) return false;
        _owned = null;
        return true;
    }
}
