using System;
using CairnMultiplayerMod.Internal.Game;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>Rope Team sessions offer nearby piton pickup and stop Climbot collecting them.</summary>
internal static class PitonRecoverySession
{
    internal const string MessageId = "rope.piton-recover";
    internal const string TakeKeyName = "P";
    internal const string TakeCue = "Tap";
    internal const string TakeText = "Take piton";
    internal const string PartnerTakeText = "Partner takes piton";
    internal const string BagFullText = "Backpack is full";
    internal const string TakeId = "piton";
    private const string TakeGamepad = "<Gamepad>/dpad/up";

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

    /// <summary>The take offer is Cairn's control tip. A full bag stays a plain message.</summary>
    internal static void ShowTip(string label)
    {
        Hide();
        RopeClipPrompt.Tick(TakeId, true, label, TakeCue,
            "<Keyboard>/" + TakeKeyName.ToLowerInvariant(), TakeGamepad);
    }

    internal static void HideTip()
    {
        RopeClipPrompt.Hide(TakeId);
        Hide();
    }
}
