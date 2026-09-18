using System.Collections.Generic;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// Where a climber goes when the mountain has the last word.
///
/// In a mode with no way back, dying no longer ends the session: the body stays on the face
/// and the player keeps watching the climb, either flying free or over a teammate's shoulder.
/// The camera is the only thing taken over — nothing here touches the pawn, so a player
/// brought back at a bivouac simply gets their view returned.
/// </summary>
internal sealed class SpectatorFeature : MultiplayerFeature
{
    private const GameKey ToggleViewKey = GameKey.F;
    private const GameKey NextClimberKey = GameKey.C;
    private const float HintSeconds = 8f;

    private readonly List<int> _targets = new();
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
        feature.OnPlayerLeft((playerId, _) =>
        {
            if (Game.Spectator.FollowedPlayerId == playerId) FollowNext();
        });
    }

    private void Tick()
    {
        if (!ShouldSpectate())
        {
            if (_active) LeaveSpectating();
            return;
        }

        if (!_active)
        {
            if (!Game.Spectator.Enter()) return;
            _active = true;
            // Free flight first: the player has just died and wants to see where, not to be
            // yanked onto somebody else's back.
            Game.Spectator.FreeLook();
            ShowHint();
        }

        if (!KeyboardCaptured) HandleInput();
        Game.Spectator.Tick(acceptInput: !KeyboardCaptured);
        ShowHint();
    }

    private bool ShouldSpectate()
        => Rules.SpectateAfterDeath && IsConnected && Game.Life.IsLocalPlayerDown;

    private void HandleInput()
    {
        if (Game.Input.WasKeyPressed(ToggleViewKey))
        {
            if (Game.Spectator.IsFollowing) Game.Spectator.FreeLook();
            else FollowNext();
        }
        else if (Game.Input.WasKeyPressed(NextClimberKey))
        {
            FollowNext();
        }
    }

    /// <summary>
    /// Moves to the next climber with a visible body, wrapping around. Falls back to free
    /// flight when nobody can be watched — everyone else may be down too.
    /// </summary>
    private void FollowNext()
    {
        _targets.Clear();
        foreach (var playerId in Game.Players.RemotePlayersInGame) _targets.Add(playerId);
        if (_targets.Count == 0)
        {
            Game.Spectator.FreeLook();
            return;
        }

        var current = Game.Spectator.FollowedPlayerId;
        var start = _targets.IndexOf(current) + 1;
        for (var offset = 0; offset < _targets.Count; offset++)
        {
            var candidate = _targets[(start + offset) % _targets.Count];
            if (candidate == current) continue;
            if (Game.Spectator.Follow(candidate)) return;
        }

        // Only one candidate and it is the one we already watch: keep it rather than drop to
        // free flight, otherwise pressing the key looks broken.
        if (current >= 0 && Game.Spectator.Follow(current)) return;
        Game.Spectator.FreeLook();
    }

    private void ShowHint()
    {
        var followed = Game.Spectator.FollowedPlayerId;
        var hint = followed >= 0
            ? $"Watching {GetPlayerName(followed)} — {ToggleViewKey}: free camera, {NextClimberKey}: next climber"
            : $"Spectating — WASD/Space to fly, {ToggleViewKey}: follow a climber";

        // The HUD message expires on its own, so it is refreshed before it fades as well as
        // whenever the view changes: a spectator keeps the controls in front of them.
        var now = Game.Time.UnscaledTime;
        if (hint == _lastHint && now < _nextHintAt) return;
        _lastHint = hint;
        _nextHintAt = now + HintSeconds * 0.5f;
        Game.Hud.ShowMessage("hint", hint, HintSeconds);
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
