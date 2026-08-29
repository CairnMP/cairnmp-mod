using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Weather;

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
            WeatherApi.ResetRemoteState();
            _publishTimer = Protocol.WeatherStateUpdateIntervalSeconds;
        });
        feature.OnSessionEnded(WeatherApi.ResetRemoteState);
    }

    private void TickHost()
    {
        if (!IsHost || Mod.Instance.LocalState != PlayerState.InGame) return;

        _publishTimer += Time.unscaledDeltaTime;
        if (_publishTimer < Protocol.WeatherStateUpdateIntervalSeconds) return;

        _publishTimer = 0f;
        // Validate before publishing: a garbled capture reaching the other players is worse
        // than a missed tick — the next one is a second away.
        if (WeatherApi.TryCaptureWeather(out var captured) && NetworkManager.IsValidWeatherState(captured))
            _weather.Set(new WeatherState { Data = captured });
    }

    /// <summary>Retries applying the last received weather: the manager may not exist yet
    /// right after a load, so a single apply on arrival is not enough.</summary>
    private void TickClient()
    {
        if (IsHost) return;
        WeatherApi.TickRemote();
    }

    private void ApplyRemote(WeatherState state)
    {
        // The host already runs this weather — applying our own publication back would
        // fight the game's own weather manager.
        if (IsHost) return;
        WeatherApi.ApplyRemoteWeather(state.Data);
    }
}
