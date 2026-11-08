using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// Where a climber goes when the mountain has the last word.
///
/// In a mode with no way back, dying no longer ends the session: the body stays on the face
/// and the player keeps watching from a free camera. The camera is the only thing taken
/// over — nothing here touches the pawn, so a player brought back at a bivouac simply gets
/// their view returned.
/// </summary>
internal sealed class SpectatorFeature : MultiplayerFeature
{
    private const float HintSeconds = 8f;
    private const string CampHint = "Your partner can bring you back at a bivouac";

    private bool _active;
    private string _lastHint;
    private float _nextHintAt;

    public override string Id => "spectator";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        // Always: a spectator is not "in gameplay" as far as the sync gate is concerned, and
        // the camera must keep answering the mouse regardless.
        feature.EveryFrame(Tick, FeaturePhase.Always);
        feature.OnSessionEnded(LeaveSpectating);
        feature.OnSceneReset(LeaveSpectating);
    }

    private void Tick()
    {
        if (!ShouldSpectate())
        {
            Game.Spectator.RememberViewpoint();
            if (_active) LeaveSpectating();
            return;
        }

        if (!_active)
        {
            if (!Game.Spectator.Enter()) return;
            _active = true;
            Game.Spectator.FreeLook();
        }

        Game.Spectator.FreeLook();
        Game.Spectator.Tick(acceptInput: !KeyboardCaptured);
        ShowHint();
    }

    private bool ShouldSpectate()
        => Rules.SpectateAfterDeath && IsConnected && Game.Life.IsLocalPlayerDown
           && Game.Life.IsDeathScreenHeld;

    private void ShowHint()
    {
        if (!Rules.BivouacRevive)
        {
            Game.Hud.HideMessage("hint");
            _lastHint = null;
            return;
        }

        // The HUD message expires on its own, so it is refreshed before it fades.
        var now = Game.Time.UnscaledTime;
        if (CampHint == _lastHint && now < _nextHintAt) return;
        _lastHint = CampHint;
        _nextHintAt = now + HintSeconds * 0.5f;
        Game.Hud.ShowMessage("hint", CampHint, HintSeconds);
    }

    private void LeaveSpectating()
    {
        if (!_active && !Game.Spectator.IsActive) return;
        _active = false;
        _lastHint = null;
        _nextHintAt = 0f;
        Game.Spectator.Leave();
        Game.Hud.HideMessage("hint");
    }
}
