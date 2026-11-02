namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>One toggle per press/release cycle, including repeated GUI key-down events.</summary>
internal sealed class BelayPanelKeyGate
{
    private bool _down;

    internal bool Observe(bool down)
    {
        var pressed = down && !_down;
        _down = down;
        return pressed;
    }
}
