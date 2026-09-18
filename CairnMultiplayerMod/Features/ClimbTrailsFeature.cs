using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// The route each climber actually took, drawn on the mountain.
///
/// In Cairn the line you choose is the decision, so where somebody went matters more than
/// where they are. This costs nothing on the wire: every client already receives everyone's
/// position, so each one draws the others' trails from what it already has.
///
/// Hidden by default -- a face criss-crossed with lines is the opposite of what the game is
/// about. It is turned on when the team wants to compare routes, or find the way somebody
/// else already solved.
/// </summary>
internal sealed class ClimbTrailsFeature : MultiplayerFeature
{
    private const GameKey ToggleKey = GameKey.T;
    private const float SampleSeconds = 0.5f;
    private const float NoticeSeconds = 4f;

    private float _nextSampleAt;

    public override string Id => "trails";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        feature.Game.Chat.AddCommand("trails", "/trails",
            "Show or hide the routes everyone has climbed", _ => Toggle());

        feature.EveryFrame(Tick, FeaturePhase.Always);
        feature.OnPlayerLeft((playerId, _) => Game.World.ForgetTrail(playerId));
        feature.OnSceneReset(() => Game.World.ClearTrails());
        feature.OnSessionEnded(() =>
        {
            Game.World.SetTrailsVisible(false);
            Game.World.ClearTrails();
        });
    }

    private void Tick()
    {
        if (!IsConnected) return;

        if (!KeyboardCaptured && Game.Input.WasKeyPressed(ToggleKey)) Toggle();

        // Sampling runs whether or not the trails are shown: turning them on should reveal
        // where everyone has been, not start a recording from that moment.
        var now = Game.Time.UnscaledTime;
        if (now < _nextSampleAt) return;
        _nextSampleAt = now + SampleSeconds;

        Sample(LocalPlayerId);
        foreach (var playerId in Game.Players.RemotePlayersInGame) Sample(playerId);
    }

    private void Sample(int playerId)
    {
        if (!Game.Players.TryGetLocation(playerId, out var location)) return;
        Game.World.RecordTrailPoint(playerId,
            new WorldPosition(location.X, location.Y, location.Z));
    }

    private void Toggle()
    {
        var visible = !Game.World.AreTrailsVisible;
        Game.World.SetTrailsVisible(visible);
        Game.Hud.ShowMessage("trails",
            visible ? "Climb trails shown." : "Climb trails hidden.", NoticeSeconds);
    }
}
