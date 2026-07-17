namespace CairnMultiplayerMod.GameApi;

/// <summary>
/// Central dispatcher that forgets the IL2CPP references bound to the current scene.
/// During a reload after death, Cairn destroys then recreates these objects; keeping the
/// old pointers can cause a native crash on the first CaptureFrame. Each feature owns its
/// own ResetCaches(); this simply fans out to them.
/// </summary>
internal static class SceneCache
{
    public static void ResetSceneCaches()
    {
        LocalPlayerApi.ResetCaches();
        PawnCaptureApi.ResetCaches();
        RopeApi.ResetCaches();
        NetplayAnimationApi.ResetCaches();
        WeatherApi.ResetCaches();
        BivouacDiagnostics.ResetCaches();
        GameLifecycleService.ResetCaches();

        Mod.LogDebug("[SceneCache] Scene-bound IL2CPP caches reset");
    }
}
