using CairnMultiplayerMod.Internal.Game;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Temporary test line for the coil. Shows the path the rope check measures, and the coil.
/// Taken out with the other rope-team trial readouts.
/// </summary>
internal static class RopeLengthReadout
{
    internal const string MessageId = "rope.length";

    private static HudAdapter _hud;

    internal static void Bind(HudAdapter hud) => _hud = hud;

    internal static void Tick()
    {
        if (!RopeLengthPatch.TryRead(out var pathLength, out var coilLength))
        {
            Hide();
            return;
        }

        var text = RopeLengthPolicy.Readout(pathLength, coilLength);
        if (text == null) Hide();
        else _hud?.ShowMessage(MessageId, text, 0.35f);
    }

    internal static void Hide() => _hud?.HideMessage(MessageId);
}
