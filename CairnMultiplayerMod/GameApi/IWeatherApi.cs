using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Captures and applies Cairn weather through protocol-safe values.</summary>
internal interface IWeatherApi
{
    bool TryCapture(out WeatherSyncData weather);
    bool IsValid(WeatherSyncData weather);
    void ApplyRemote(WeatherSyncData weather);
    void TickRemote();
    void Reset();
}
