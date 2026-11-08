using System;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal interface IRopeBinding : IDisposable
{
    bool IsReady { get; }
    bool Attach();
    bool Maintain();
}

internal enum RopeBindingState { Waiting, Attached, Released }

/// <summary>Owns partial native initialization as well as successful attachments.</summary>
internal sealed class RopeBindingSession : IDisposable
{
    private IRopeBinding _binding;
    private readonly double _deadline;
    internal RopeBindingState State { get; private set; } = RopeBindingState.Waiting;

    /// <summary>
    /// Maintain failed after the rope existed. The drawing is dropped. The tie stays.
    /// A refused attach and a missed deadline leave this false, and those still end the tie.
    /// </summary>
    internal bool HarnessLost { get; private set; }
    internal RopeBindingSession(IRopeBinding binding, double now)
    {
        _binding = binding;
        _deadline = now + 5;
    }
    internal bool Tick(double now)
    {
        if (_binding == null) return false;
        try
        {
            if (State == RopeBindingState.Waiting)
            {
                if (!_binding.IsReady)
                {
                    if (now < _deadline) return true;
                    Dispose();
                    return false;
                }
                if (!_binding.Attach()) { Dispose(); return false; }
                State = RopeBindingState.Attached;
            }
            if (_binding.Maintain()) return true;
            HarnessLost = true;
            Dispose();
            return false;
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        var binding = _binding;
        _binding = null;
        State = RopeBindingState.Released;
        binding?.Dispose();
    }
}
