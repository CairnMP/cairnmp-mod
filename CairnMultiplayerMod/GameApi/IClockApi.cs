namespace CairnMultiplayerMod.GameApi;

/// <summary>Cairn's day/night cycle and local bivouac sleep state.</summary>
internal interface IClockApi
{
    bool TryGetDayTime(out float dayTime);
    bool TryGetLocalSleep(out bool asleep);
    bool Freeze(float dayTime);
    bool Unfreeze();

    /// <summary>
    /// Crossing a scene boundary: forget the freeze bound to the old cycle without releasing
    /// it, so the next frame re-freezes onto the host's time instead of showing the local one.
    /// </summary>
    void OnSceneChanged();

    /// <summary>Leaving the session: hand the day/night cycle back to the game for good.</summary>
    void Reset();
    void LogDiagnosticsOnce();
}
