using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// The weather the host is currently running. Wraps the shared struct in a class: the
/// framework builds the message with new() and fills it in place, which is unambiguous
/// for a reference type.
/// </summary>
internal sealed class WeatherState : IPacket
{
    public WeatherSyncData Data;

    public void Serialize(BinaryWriter writer) => Data.Serialize(writer);
    public void Deserialize(BinaryReader reader) => Data.Deserialize(reader);
}

/// <summary>
/// Global weather sync. The host publishes what its own game is running; everyone else
/// applies it. Being host state rather than a broadcast, a player joining mid-session now
/// gets the current weather on arrival instead of waiting for the next tick.
/// </summary>
internal sealed class WeatherFeature : MultiplayerFeature
{
    public override string Id => "weather";

    private HostState<WeatherState> _weather;
    private float _publishTimer = Protocol.WeatherStateUpdateIntervalSeconds;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _weather = feature.HostState<WeatherState>("current", ApplyRemote);

        feature.EveryFrame(TickHost, FeaturePhase.Gameplay);
        feature.EveryFrame(TickClient, FeaturePhase.Gameplay);

        // The scene reload destroys the weather manager we cached, and rearms the cadence
        // so the next capture goes out promptly instead of waiting a full interval.
        feature.OnSceneReset(() =>
        {
            Game.Weather.Reset();
            _publishTimer = Protocol.WeatherStateUpdateIntervalSeconds;
        });
        feature.OnSessionEnded(Game.Weather.Reset);
    }

    private void TickHost()
    {
        if (!IsHost || !Game.State.IsLocalPlayerInGame) return;

        _publishTimer += Game.Time.UnscaledDeltaTime;
        if (_publishTimer < Protocol.WeatherStateUpdateIntervalSeconds) return;

        _publishTimer = 0f;
        // Validate before publishing: a garbled capture reaching the other players is worse
        // than a missed tick — the next one is a second away.
        if (Game.Weather.TryCapture(out var captured) && Game.Weather.IsValid(captured))
            _weather.Set(new WeatherState { Data = captured });
    }

    /// <summary>Retries applying the last received weather: the manager may not exist yet
    /// right after a load, so a single apply on arrival is not enough.</summary>
    private void TickClient()
    {
        if (IsHost) return;
        Game.Weather.TickRemote();
    }

    private void ApplyRemote(WeatherState state)
    {
        // The host already runs this weather — applying our own publication back would
        // fight the game's own weather manager.
        if (IsHost) return;
        Game.Weather.ApplyRemote(state.Data);
    }
}
