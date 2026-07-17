using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Weather;

/// <summary>
/// Synchronizes global weather. On Steam, only the host publishes the state;
/// clients apply the last snapshot received and retry after loading.
/// </summary>
internal sealed class WeatherStateBroadcaster
{
    private readonly NetworkManager _network;
    private readonly SteamLobbyManager _lobby;

    private float _weatherTickTimer = Protocol.WeatherStateUpdateIntervalSeconds;

    internal WeatherStateBroadcaster(NetworkManager network, SteamLobbyManager lobby)
    {
        _network = network;
        _lobby = lobby;
    }

    /// <summary>Rearms the broadcast cadence (on a scene reload / bivouac boundary), so the
    /// next capture is sent promptly.</summary>
    internal void ResetTimer() => _weatherTickTimer = Protocol.WeatherStateUpdateIntervalSeconds;

    internal void Tick()
    {
        if (!_network.IsHandshakeComplete)
            return;

        if (_lobby?.IsHost == true)
        {
            if (Mod.Instance.LocalState != PlayerState.InGame)
                return;

            _weatherTickTimer += Time.unscaledDeltaTime;
            if (_weatherTickTimer < Protocol.WeatherStateUpdateIntervalSeconds)
                return;

            _weatherTickTimer = 0f;
            if (WeatherApi.TryCaptureWeather(out var state))
                _network.SendWeatherState(state, reliable: false);
            return;
        }

        WeatherApi.TickRemoteWeatherSync();
    }
}
