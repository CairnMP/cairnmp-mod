using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;
using PlayerStateFeedbacks = Il2CppŢheGameBakers.Cairn.Feedbacks.PlayerStateFeedbacks;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Uses Cairn's freeze lifecycle because direct day-time writes are recomputed each frame;
/// ownership tracking prevents releasing a freeze held by photo mode or a cutscene.
/// </summary>
internal static class TimeInterop
{
    private static bool _timeApiDumped;
    private static bool _nightDayCycleWarned;

    private static bool _dayCycleFrozenByUs;

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

    /// <summary><c>SetFreezeDayTime01</c> only takes effect after native freeze has begun.</summary>
    public static bool FreezeDayCycle(float dayTime01)
    {
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;

            dayTime01 = Mathf.Repeat(dayTime01, 1f);

            // SetFreezeDayTime01 only acts while the cycle is already frozen. Enter the
            // native freeze lifecycle once, then update its target through the dedicated API.
            // If another system owns the freeze (photo mode, cutscene), leave it untouched.
            if (!_dayCycleFrozenByUs)
            {
                if (ndc.isFrozen) return false;
                ndc.Freeze(true);
                _dayCycleFrozenByUs = true;
            }
            else if (!ndc.isFrozen)
            {
                ndc.Freeze(true);
            }

            ndc.SetFreezeDayTime01(dayTime01);
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    public static bool UnfreezeDayCycle()
    {
        if (!_dayCycleFrozenByUs) return true;
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;
            ndc.Freeze(false);
            _dayCycleFrozenByUs = false;
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    public static bool TryIsLocalAsleep(out bool asleep)
    {
        asleep = false;

        try
        {
            var feedbacks = PlayerStateFeedbacks.Instance;
            if (feedbacks == null) return false;
            asleep = feedbacks.IsAsleep;
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("clock.apply-time", exception);
            return false;
        }
    }

    public static void ResetCaches()
    {
        UnfreezeDayCycle();
    }

    /// <summary>
    /// A scene boundary replaces NightDayCycle.Instance, so the freeze we held is gone with
    /// the old one — only the ownership flag needs clearing. Calling Unfreeze here instead
    /// would hand the day/night cycle back to the game for a moment, making the sky jump
    /// until the next host packet re-freezes it.
    /// </summary>
    public static void ForgetSceneBoundFreeze()
    {
        _dayCycleFrozenByUs = false;
    }

    public static void DumpTimeApi()
    {
        if (_timeApiDumped) return;
        _timeApiDumped = true;

        try
        {
            var hasTime = TryGetDayTime01(out var t);
            var hasAsleep = TryIsLocalAsleep(out var a);
            ModLog.Debug($"[TimeSync] API dump: dayTime01={(hasTime ? t.ToString("F3") : "n/a")} " +
                        $"playerStateFeedbacks={(hasAsleep ? $"found(asleep={a})" : "n/a")}");
        }
        catch (Exception exception) { ModLog.SuppressedException("clock.describe-time-target", exception); }
    }

    private static void WarnNightDayCycleOnce(Exception ex)
    {
        if (_nightDayCycleWarned) return;
        _nightDayCycleWarned = true;
        ModLog.Warning($"[TimeSync] NightDayCycle access failed: {ex.GetType().Name}: {ex.Message}");
    }
}
