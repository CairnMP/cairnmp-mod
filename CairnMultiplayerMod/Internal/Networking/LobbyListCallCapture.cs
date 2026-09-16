using System;

namespace CairnMultiplayerMod.Internal.Networking;

internal interface ILobbyListResultReader
{
    void Advance();
    bool IsCompleted(ulong call, out bool failed);
    bool Read(ulong call, out uint count, out bool failed);
}

/// <summary>Copies a single-use Steam result before the game's dispatcher drains it.
/// Only managed data survives until the mod's next update.</summary>
internal sealed class LobbyListCallCapture
{
    private ulong _call;
    private bool _ready;
    private uint _count;
    private string _error;

    internal void Begin(ulong call)
    {
        if (call == 0) throw new ArgumentOutOfRangeException(nameof(call));
        Cancel();
        _call = call;
    }

    internal void Capture(ILobbyListResultReader reader)
    {
        if (_call == 0 || _ready) return;
        try
        {
            reader.Advance();
            if (!reader.IsCompleted(_call, out var failed)) return;
            if (failed) _error = $"Steam lobby call {_call} completed with an I/O failure.";
            else if (!reader.Read(_call, out _count, out failed))
                _error = $"Steam manual dispatch could not read lobby call {_call} (callback 510, size 4, failed={failed}).";
            else if (failed) _error = $"Steam lobby call {_call} returned an I/O failure.";
            _ready = true;
        }
        catch (Exception exception)
        {
            // Never let a mod failure escape into the game's callback dispatcher.
            _error = exception.Message;
            _ready = true;
        }
    }

    internal bool TryTake(out uint count, out string error)
    {
        count = _count;
        error = _error;
        if (!_ready) return false;
        Cancel();
        return true;
    }

    internal void Cancel() { _call = 0; _ready = false; _count = 0; _error = null; }
}
