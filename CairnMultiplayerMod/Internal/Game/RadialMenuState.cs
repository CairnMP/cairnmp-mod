using System;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// The logic of the controller radial menu, free of Unity types so it can be tested. Held, the
/// menu appears after a short delay and the stick aims it; letting go picks the aimed item.
/// </summary>
internal sealed class RadialMenuState
{
    internal const float ShowDelaySeconds = 0.15f;
    internal const float Deadzone = 0.5f;

    private readonly int _count;
    private float _heldSeconds;
    private bool _armed;
    private bool _cancelled;

    internal RadialMenuState(int itemCount)
    {
        if (itemCount <= 0) throw new ArgumentOutOfRangeException(nameof(itemCount));
        _count = itemCount;
    }

    internal bool IsVisible { get; private set; }

    /// <summary>The aimed item, or -1 while the stick is centred.</summary>
    internal int SelectedIndex { get; private set; } = -1;

    /// <summary>
    /// Advances one frame. Returns the chosen item on the frame the button is released, else -1.
    /// An item only counts once the stick has been seen centred since the button went down, so
    /// a stick already pushed for the camera cannot pick something by accident.
    /// </summary>
    internal int Update(bool held, float stickX, float stickY, float deltaSeconds, bool cancel)
    {
        if (!held)
        {
            var chosen = IsVisible && SelectedIndex >= 0 ? SelectedIndex : -1;
            Reset();
            return chosen;
        }

        _heldSeconds += deltaSeconds;
        if (cancel) _cancelled = true;

        if (MathF.Sqrt(stickX * stickX + stickY * stickY) < Deadzone)
        {
            _armed = true;
            SelectedIndex = -1;
        }
        else
        {
            SelectedIndex = _armed ? IndexFor(stickX, stickY) : -1;
        }

        IsVisible = !_cancelled && _heldSeconds >= ShowDelaySeconds;
        return -1;
    }

    /// <summary>Items run clockwise from straight up.</summary>
    internal int IndexFor(float stickX, float stickY)
    {
        var angle = MathF.Atan2(stickX, stickY);
        var sector = 2f * MathF.PI / _count;
        var index = (int)MathF.Round(angle / sector);
        return ((index % _count) + _count) % _count;
    }

    private void Reset()
    {
        _heldSeconds = 0f;
        _armed = false;
        _cancelled = false;
        IsVisible = false;
        SelectedIndex = -1;
    }
}
