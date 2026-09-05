using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Bivouac;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.Roping;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Central dispatcher that forgets the IL2CPP references bound to the current scene.
/// During a reload after death, Cairn destroys then recreates these objects; keeping the
/// old pointers can cause a native crash on the first CaptureFrame. Each feature owns its
/// own ResetCaches(); this simply fans out to them.
/// </summary>
internal static class SceneCache
{
    public static void Reset()
    {
        LocalPlayerInterop.ResetCaches();
        PawnCaptureInterop.ResetCaches();
        RopeInterop.ResetCaches();
        NetplayAnimationInterop.ResetCaches();
        WeatherInterop.ResetCaches();
        BivouacDiagnostics.ResetCaches();
        GameLifecycleService.ResetCaches();

        ModLog.Debug("[SceneCache] Scene-bound IL2CPP caches reset");
    }
}
