using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Clock;

/// <summary>
/// Synchronizes the time of day (NightDayCycle.dayTime01) and the sleep state
/// (PlayerStateFeedbacks.IsAsleep) between players.
///
/// IMPORTANT: dayTime01 is a DERIVED value, recomputed every frame by
/// NightDayCycle.UpdateDayTime01() from TimeManager.GameTime. Writing the
/// field directly (ndc.dayTime01 = x) is therefore instantly overwritten -> that's
/// why the old sync never held. We now force the time via the native
/// FREEZE mechanism (isFrozen + lastDayTime01OnFreeze): when isFrozen is
/// true, UpdateDayTime01 keeps lastDayTime01OnFreeze instead of recomputing. It's
/// the day/night equivalent of ForceInfiniteWeatherState on the weather side.
///
/// The visual day/night clock is host-authoritative: the host broadcasts its
/// dayTime01, the clients FREEZE their cycle to that value every frame. When a
/// player sleeps without consensus, the host freezes the cycle at the last normal time; the
/// visual fast-forward therefore only happens when everyone sleeps (host releases the freeze).
///
/// Robustness: missing singletons (loading, menu) or IL2CPP exceptions ->
/// no-op, never a crash. The sync is additive: a failure affects neither
/// native sleep nor gameplay.
/// </summary>
internal static unsafe class TimeApi
{
    private static MonoBehaviour _playerStateFeedbacksCached;
    private static int _lastPlayerStateFeedbacksSearchFrame;
    private static int _isAsleepFieldOffset = -1; // -1 unresolved, -2 failed, >=0 offset
    private static bool _timeApiDumped;
    private static bool _nightDayCycleWarned;

    // True while WE hold the freeze on the day/night cycle. Used to release only
    // our own freeze (UnfreezeDayCycle is a no-op otherwise) -> we don't break a freeze set
    // by the game itself (photo mode, etc.).
    private static bool _dayCycleFrozenByUs;

    // Candidate names for the "is asleep" boolean field on PlayerStateFeedbacks
    // (resolved at runtime: the type isn't accessible as a compile-time type).
    private static readonly string[] IsAsleepFieldCandidates =
    {
        "<IsAsleep>k__BackingField",
        "isAsleep",
        "IsAsleep",
    };

    /// <summary>Reads the normalized time of day (0-1) from NightDayCycle.</summary>
    public static bool TryGetDayTime01(out float dayTime01)
    {
        dayTime01 = 0f;
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;
            dayTime01 = ndc.dayTime01;
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    /// <summary>
    /// Forces and holds the time of day (0-1) via the native freeze of NightDayCycle.
    /// Call every frame: we re-push the value to track the host's time.
    /// isFrozen prevents UpdateDayTime01 from recomputing from TimeManager.GameTime; we
    /// also write dayTime01 for the current frame (anti-flicker, regardless of the execution
    /// order between our tick and NightDayCycle.Update).
    /// </summary>
    public static bool FreezeDayCycle(float dayTime01)
    {
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;

            dayTime01 = Mathf.Repeat(dayTime01, 1f); // cyclic, clamped to 0-1

            // SetFreezeDayTime01 is the dedicated API; we then re-assert the fields to
            // have the last word regardless of its internal implementation.
            ndc.SetFreezeDayTime01(dayTime01);
            ndc.isFrozen = true;
            ndc.lastDayTime01OnFreeze = dayTime01;
            ndc.dayTime01 = dayTime01;
            _dayCycleFrozenByUs = true;
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    /// <summary>
    /// Releases OUR freeze on the day/night cycle to let time resume naturally
    /// (normal time or native fast-forward). No-op if we hadn't frozen it (we don't
    /// touch a freeze set by the game).
    /// </summary>
    public static bool UnfreezeDayCycle()
    {
        if (!_dayCycleFrozenByUs) return true;
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;
            ndc.isFrozen = false;
            _dayCycleFrozenByUs = false;
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    /// <summary>
    /// Indicates whether the local player is asleep. Reads the IsAsleep boolean of
    /// PlayerStateFeedbacks by field offset (resolved at runtime).
    /// </summary>
    public static bool TryIsLocalAsleep(out bool asleep)
    {
        asleep = false;

        var psf = GameInterop.FindMonoBehaviourByName("PlayerStateFeedbacks",
            ref _playerStateFeedbacksCached, ref _lastPlayerStateFeedbacksSearchFrame);
        if (psf == null || psf.Pointer == IntPtr.Zero) return false;

        var offset = EnsureIsAsleepOffset(psf);
        if (offset < 0) return false;

        try
        {
            asleep = *((byte*)psf.Pointer + offset) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int EnsureIsAsleepOffset(MonoBehaviour psf)
    {
        if (_isAsleepFieldOffset != -1) return _isAsleepFieldOffset;

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(psf.Pointer);
            foreach (var candidate in IsAsleepFieldCandidates)
            {
                var field = IL2CPP.GetIl2CppField(klass, candidate);
                if (field == IntPtr.Zero) continue;

                _isAsleepFieldOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
                Mod.LogDebug($"[TimeSync] Resolved PlayerStateFeedbacks IsAsleep field '{candidate}' @ offset {_isAsleepFieldOffset}");
                return _isAsleepFieldOffset;
            }

            _isAsleepFieldOffset = -2;
            Mod.Log.Warning("[TimeSync] PlayerStateFeedbacks IsAsleep field not found — sleep detection disabled.");
        }
        catch (Exception ex)
        {
            _isAsleepFieldOffset = -2;
            Mod.Log.Warning($"[TimeSync] IsAsleep field resolution failed: {ex.GetType().Name}: {ex.Message}");
        }

        return _isAsleepFieldOffset;
    }

    public static void ResetTimeSyncCache()
    {
        _playerStateFeedbacksCached = null;
        _lastPlayerStateFeedbacksSearchFrame = 0;
        // Release our freeze so the cycle isn't left stuck after a disconnect.
        UnfreezeDayCycle();
    }

    /// <summary>Logs the resolution state of the time API once (in-game debug).</summary>
    public static void DumpTimeApi()
    {
        if (_timeApiDumped) return;
        _timeApiDumped = true;

        try
        {
            var hasTime = TryGetDayTime01(out var t);
            var hasAsleep = TryIsLocalAsleep(out var a);
            Mod.LogDebug($"[TimeSync] API dump: dayTime01={(hasTime ? t.ToString("F3") : "n/a")} " +
                        $"playerStateFeedbacks={(hasAsleep ? $"found(asleep={a})" : "n/a")}");
        }
        catch { }
    }

    private static void WarnNightDayCycleOnce(Exception ex)
    {
        if (_nightDayCycleWarned) return;
        _nightDayCycleWarned = true;
        Mod.Log.Warning($"[TimeSync] NightDayCycle access failed: {ex.GetType().Name}: {ex.Message}");
    }
}
