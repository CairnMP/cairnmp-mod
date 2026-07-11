using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public partial class Mod
{
    private float _weatherTickTimer = Protocol.WeatherStateUpdateIntervalSeconds;

    /// <summary>
    /// Synchronizes global weather. On Steam, only the host publishes the state;
    /// clients apply the last snapshot received and retry after loading.
    /// </summary>
    private void TickWeatherSync()
    {
        if (!_network.IsHandshakeComplete)
            return;

        if (_lobby?.IsHost == true)
        {
            if (LocalState != PlayerState.InGame)
                return;

            _weatherTickTimer += Time.unscaledDeltaTime;
            if (_weatherTickTimer < Protocol.WeatherStateUpdateIntervalSeconds)
                return;

            _weatherTickTimer = 0f;
            if (CairnGameApi.TryCaptureWeather(out var state))
                _network.SendWeatherState(state, reliable: false);
            return;
        }

        CairnGameApi.TickRemoteWeatherSync();
    }
}
