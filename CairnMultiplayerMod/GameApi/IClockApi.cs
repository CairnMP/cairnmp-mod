namespace CairnMultiplayerMod.GameApi;

/// <summary>Cairn's day/night cycle and local bivouac sleep state.</summary>
internal interface IClockApi
{
    bool TryGetDayTime(out float dayTime);
    bool TryGetLocalSleep(out bool asleep);
    bool Freeze(float dayTime);
    bool Unfreeze();
    void Reset();
    void LogDiagnosticsOnce();
}
