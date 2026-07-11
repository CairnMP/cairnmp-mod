namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    /// <summary>
    /// Forgets the IL2CPP references bound to the current scene. During a reload
    /// after death, Cairn destroys then recreates these objects; keeping the old
    /// pointers can cause a native crash on the first CaptureFrame.
    /// </summary>
    public static void ResetSceneCaches()
    {
        _pawnManagerCached = null;
        _lastPawnManagerSearchFrame = 0;
        _mcResolvedOnce = false;
        _nullReadCount = 0;

        _pawnCaptureCached = null;
        _typedPawnCaptureCached = null;
        _typedClimbotCaptureCached = null;
        _lastPawnCaptureSearchFrame = 0;
        _lastTypedCaptureSearchFrame = 0;
        _nextPlayerCaptureRetryAt = 0f;
        _nextClimbotCaptureRetryAt = 0f;
        _lastPlayerCaptureFailureLogAt = 0f;
        _lastClimbotCaptureFailureLogAt = 0f;
        _localPlayerFallbackCaptureLogged = false;

        _lifelineCached = null;
        _lastLifelineSearchFrame = 0;
        _lastKnownPitonCount = 0;
        _remotePitonsAdded = 0;
        _localPitonIdsByPointer.Clear();
        _remotePitonsByNetId.Clear();

        _netplayManagerCached = null;
        _lastNetplayManagerSearchFrame = 0;
        _climberPrefabCached = null;
        _lastPrefabSearchFrame = 0;

        _weatherManagerBehaviourCached = null;
        _weatherManagerCached = null;
        _lastWeatherManagerSearchFrame = 0;
        _lastAppliedWeatherKey = int.MinValue;
        _lastAppliedWindOverride = int.MinValue;

        _globalGameManagerCached = null;
        _bivouacManagerCached = null;
        _tapingFingersManagerCached = null;
        _lastGlobalGameManagerSearchFrame = 0;
        _lastBivouacManagerSearchFrame = 0;
        _lastTapingFingersManagerSearchFrame = 0;

        Mod.LogDebug("[CairnGameApi] Scene-bound IL2CPP caches reset");
    }
}
