using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Captures and applies Cairn weather through protocol-safe values.</summary>
internal interface IWeatherApi
{
    bool TryCapture(out WeatherSyncData weather);
    bool IsValid(WeatherSyncData weather);
    void ApplyRemote(WeatherSyncData weather);
    void TickRemote();

    /// <summary>
    /// Crossing a scene boundary: drop what is bound to the old scene but keep the host's
    /// weather, which is reapplied as soon as the new scene can take it.
    /// </summary>
    void OnSceneChanged();

    /// <summary>Leaving the session: hand the weather back to the game for good.</summary>
    void Reset();
}
