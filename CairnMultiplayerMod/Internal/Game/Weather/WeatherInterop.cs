using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using CairnMultiplayer.Shared;
using Il2Cpp;
using UnityEngine;
using WeatherStateDefinition = Il2Cpp.WeatherZoneData.WeatherStateDefinition;

namespace CairnMultiplayerMod.Internal.Game.Weather;

/// <summary>
/// Global weather capture/replication against Cairn's native WeatherManager. On Steam only
/// the host captures and publishes; clients apply the last snapshot and retry after loading.
/// </summary>
internal static class WeatherInterop
{
    private static MonoBehaviour _weatherManagerBehaviourCached;
    private static WeatherManager _weatherManagerCached;
    private static int _lastWeatherManagerSearchFrame;
    private static bool _hasPendingRemoteWeather;
    private static WeatherSyncData _pendingRemoteWeather;
    private static int _lastAppliedWeatherKey = int.MinValue;
    private static int _lastAppliedWindOverride = int.MinValue;
    private static float _lastWeatherApplyFailureLogAt;

    /// <summary>Forgets the scene-bound WeatherManager reference and the last-applied keys
    /// (called on scene reload).</summary>
    internal static void ResetCaches()
    {
        _weatherManagerBehaviourCached = null;
        _weatherManagerCached = null;
        _lastWeatherManagerSearchFrame = 0;
        _lastAppliedWeatherKey = int.MinValue;
        _lastAppliedWindOverride = int.MinValue;
    }

    public static bool TryCaptureWeather(out WeatherSyncData state)
    {
        state = default;

        var manager = TryGetWeatherManager();
        if (manager == null)
            return false;

        try
        {
            var weatherDef = WeatherManager.ActualWeatherStateDefinition;
            var weatherType = WeatherManager.WeatherType;
            var windType = WeatherWindModule.ActualWindType;
            var windDir = WeatherWindModule.WindDirNormalized;
            var windModule = manager.WindModule;
            var forcedWind = windModule != null ? windModule.ForcedInfiniteState : WindZoneData.WindOverride.NoOverride;
            var windOverride = forcedWind == WindZoneData.WindOverride.NoOverride
                ? WindOverrideFromType(windType)
                : forcedWind;

            state = new WeatherSyncData
            {
                IsValid = true,
                WeatherType = (int)weatherType,
                RainType = weatherDef != null ? (int)weatherDef.RainType : 0,
                ThunderType = weatherDef != null ? (int)weatherDef.ThunderType : 0,
                FogType = weatherDef != null ? (int)weatherDef.FogType : 0,
                CloudsType = weatherDef != null ? (int)weatherDef.CloudsType : 0,
                WindType = (int)windType,
                WindOverride = (int)windOverride,
                SnowRainForceMode = (int)WeatherManager.ForceSnowInsteadOfRain,
                RemainingDuration = weatherDef != null ? Mathf.Max(0f, weatherDef.RemainingDuration) : 0f,
                UseSnowInsteadOfRain01 = Mathf.Clamp01(WeatherManager.UseSnowInsteadOfRain01),
                WindForce = Mathf.Max(0f, WeatherWindModule.WindForce),
                WindForce01 = Mathf.Max(0f, WeatherWindModule.WindForce01),
                WindDirX = windDir.x,
                WindDirY = windDir.y,
                WindDirZ = windDir.z,
                WindAngle = WeatherWindModule.WindAngle,
            };
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[WeatherSync] Capture failed: {ex.Message}");
            return false;
        }
    }

    public static void ApplyRemoteWeather(WeatherSyncData state)
    {
        if (!state.IsValid)
            return;

        _pendingRemoteWeather = state;
        _hasPendingRemoteWeather = true;
        TickRemote();
    }

    public static void TickRemote()
    {
        if (!_hasPendingRemoteWeather)
            return;

        if (TryApplyRemoteWeather(_pendingRemoteWeather))
            _hasPendingRemoteWeather = false;
    }

    public static void ResetRemoteState()
    {
        _hasPendingRemoteWeather = false;
        _pendingRemoteWeather = default;
        _lastAppliedWeatherKey = int.MinValue;
        _lastAppliedWindOverride = int.MinValue;
        _lastWeatherApplyFailureLogAt = 0f;
    }

    private static bool TryApplyRemoteWeather(WeatherSyncData state)
    {
        var manager = TryGetWeatherManager();
        if (manager == null)
            return false;

        try
        {
            WeatherManager.ForceSnowInsteadOfRain = (WeatherManager.SnowRainForceMode)state.SnowRainForceMode;
            WeatherManager.UseSnowInsteadOfRain01 = Mathf.Clamp01(state.UseSnowInsteadOfRain01);

            var weatherKey = BuildWeatherKey(state);
            if (weatherKey != _lastAppliedWeatherKey)
            {
                var weatherDef = BuildWeatherStateDefinition(manager, state);
                manager.ForceInfiniteWeatherState(
                    weatherDef,
                    WeatherManager.ForcedInifiniteStateOrigin.NetPlay,
                    Protocol.WeatherStateTransitionSeconds);
                _lastAppliedWeatherKey = weatherKey;
            }

            ApplyRemoteWind(manager, state);
            return true;
        }
        catch (Exception ex)
        {
            LogRemoteWeatherApplyFailure(ex.Message);
            return false;
        }
    }

    private static WeatherStateDefinition BuildWeatherStateDefinition(WeatherManager manager, WeatherSyncData state)
    {
        var owner = manager.CurrentWeatherZoneData ?? manager.DefaultWeatherZoneData;
        var weatherType = (WeatherZoneData.WeatherType)state.WeatherType;
        WeatherStateDefinition weatherDef;
        if (owner != null)
        {
            weatherDef = new WeatherStateDefinition(weatherType, owner);
        }
        else
        {
            weatherDef = new WeatherStateDefinition(
                (WeatherZoneData.RainType)state.RainType,
                (WeatherZoneData.ThunderType)state.ThunderType,
                (WeatherZoneData.FogType)state.FogType,
                (WeatherZoneData.CloudsType)state.CloudsType);
        }

        weatherDef.RemainingDuration = Mathf.Max(0f, state.RemainingDuration);
        return weatherDef;
    }

    private static void ApplyRemoteWind(WeatherManager manager, WeatherSyncData state)
    {
        var windModule = manager.WindModule;
        if (windModule == null)
            return;

        var windOverride = (WindZoneData.WindOverride)state.WindOverride;
        if (_lastAppliedWindOverride != state.WindOverride)
        {
            windModule.ForceInfiniteWindState(windOverride);
            _lastAppliedWindOverride = state.WindOverride;
        }

        var dir = new Vector3(state.WindDirX, state.WindDirY, state.WindDirZ);
        if (dir.sqrMagnitude > 0.0001f)
        {
            var normalized = dir.normalized;
            WeatherWindModule.WindDirNormalized = normalized;
            WeatherWindModule.WindDir = normalized * Mathf.Max(0f, state.WindForce);
        }

        WeatherWindModule.WindForce = Mathf.Max(0f, state.WindForce);
        WeatherWindModule.WindAngle = state.WindAngle;
    }

    private static WeatherManager TryGetWeatherManager()
    {
        if (_weatherManagerCached != null)
            return _weatherManagerCached;

        try
        {
            var instance = WeatherManager.Instance;
            if (instance != null)
            {
                _weatherManagerCached = instance;
                return instance;
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("weather.resolve-manager", exception); }

        var behaviour = GameInterop.FindMonoBehaviourByName("WeatherManager", ref _weatherManagerBehaviourCached, ref _lastWeatherManagerSearchFrame);
        var manager = behaviour != null ? behaviour.TryCast<WeatherManager>() : null;
        if (manager != null)
            _weatherManagerCached = manager;
        return manager;
    }

    private static WindZoneData.WindOverride WindOverrideFromType(WindZoneData.WindType windType)
    {
        return windType switch
        {
            WindZoneData.WindType.Wind_Off => WindZoneData.WindOverride.ForceWindOff,
            WindZoneData.WindType.Wind_Small => WindZoneData.WindOverride.ForceWindSmall,
            WindZoneData.WindType.Wind_Heavy => WindZoneData.WindOverride.ForceWindHeavy,
            _ => WindZoneData.WindOverride.NoOverride,
        };
    }

    private static int BuildWeatherKey(WeatherSyncData state)
    {
        unchecked
        {
            var key = 17;
            key = key * 31 + state.WeatherType;
            key = key * 31 + state.RainType;
            key = key * 31 + state.ThunderType;
            key = key * 31 + state.FogType;
            key = key * 31 + state.CloudsType;
            key = key * 31 + state.SnowRainForceMode;
            return key;
        }
    }

    private static void LogRemoteWeatherApplyFailure(string reason)
    {
        var now = Time.unscaledTime;
        if (now - _lastWeatherApplyFailureLogAt < 3f)
            return;

        _lastWeatherApplyFailureLogAt = now;
        ModLog.Warning($"[WeatherSync] Apply failed: {reason}");
    }
}
