using System;
using CairnMultiplayerMod.Internal.Game;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>Rope Team sessions offer nearby piton pickup and stop Climbot collecting them.</summary>
internal static class PitonRecoverySession
{
    internal const string MessageId = "rope.piton-recover";
    internal const string TakeKeyName = "P";
    internal const string TakeText = "Tap D-pad up — Take piton";
    internal const string BagFullText = "Backpack is full";

    private static Func<bool> _suppressesClimbot;
    private static HudAdapter _hud;

    internal static bool SuppressesClimbot => _suppressesClimbot?.Invoke() == true;

    internal static void Bind(Func<bool> suppressesClimbot, HudAdapter hud)
    {
        _suppressesClimbot = suppressesClimbot ?? throw new ArgumentNullException(nameof(suppressesClimbot));
        _hud = hud ?? throw new ArgumentNullException(nameof(hud));
    }

    internal static void Show(string text) => _hud?.ShowMessage(MessageId, text, 0.35f);

    internal static void Hide() => _hud?.HideMessage(MessageId);
}
