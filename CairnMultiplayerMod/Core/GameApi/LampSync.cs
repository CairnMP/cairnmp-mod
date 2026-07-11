using System;
using System.Reflection;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Synchronisation de la lampe (AavaLightStick) entre joueurs.
///
/// L'API vanille n'est PAS un on/off bool mais un enum Mode pilote par
/// SetMode(Mode, bool instant). On transporte le Mode en int dans le packet
/// et on applique tel quel sur le ghost distant via SetMode.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static AavaLightStick _localLightStickCached;
    private static int _lastLocalLightStickSearchFrame;
    private static bool _localLightStickWarningLogged;

    private static bool _lampReflectionResolved;
    private static bool _lampReflectionFailed;
    private static MethodInfo _lampCurrentModeGetter;   // Mode get_CurrentMode()
    private static MethodInfo _lampSetModeMethod;       // void SetMode(Mode, bool)
    private static Type _lampModeType;

    public static bool TryGetLocalLampState(out int mode)
    {
        mode = 0;
        EnsureLampReflection();
        if (_lampReflectionFailed || _lampCurrentModeGetter == null) return false;

        var lamp = TryGetLocalLightStick();
        if (lamp == null) return false;

        try
        {
            var value = _lampCurrentModeGetter.Invoke(lamp, null);
            if (value != null)
            {
                mode = Convert.ToInt32(value);
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!_localLightStickWarningLogged)
            {
                _localLightStickWarningLogged = true;
                Mod.Log.Warning($"[LampSync] CurrentMode read failed: {ex.GetType().Name}:{ex.Message}");
            }
        }
        return false;
    }

    public static bool TryApplyRemoteLampState(NetplayRemotePlayer remote, int mode)
    {
        if (remote == null || remote.Pointer == IntPtr.Zero) return false;

        EnsureLampReflection();
        if (_lampReflectionFailed || _lampSetModeMethod == null || _lampModeType == null) return false;

        AavaLightStick lamp;
        try
        {
            lamp = remote.gameObject.GetComponentInChildren<AavaLightStick>(true);
        }
        catch
        {
            return false;
        }

        if (lamp == null) return false;

        try
        {
            // Verifie l'etat actuel pour eviter de spammer SetMode.
            if (_lampCurrentModeGetter != null)
            {
                var current = _lampCurrentModeGetter.Invoke(lamp, null);
                if (current != null && Convert.ToInt32(current) == mode)
                    return true;
            }

            // Construit la valeur d'enum a partir de l'int et appelle SetMode(value, instant: true).
            var enumValue = Enum.ToObject(_lampModeType, mode);
            _lampSetModeMethod.Invoke(lamp, new object[] { enumValue, true });
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[LampSync] Apply failed: {ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }

    public static void ResetLocalLampStateCache()
    {
        _localLightStickCached = null;
        _lastLocalLightStickSearchFrame = 0;
        _localLightStickWarningLogged = false;
    }

    private static void EnsureLampReflection()
    {
        if (_lampReflectionResolved || _lampReflectionFailed) return;
        _lampReflectionResolved = true;

        try
        {
            var type = typeof(AavaLightStick);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            // get_CurrentMode() retournant l'enum Mode.
            foreach (var m in type.GetMethods(flags))
            {
                if (m.Name == "get_CurrentMode" && m.GetParameters().Length == 0)
                {
                    _lampCurrentModeGetter = m;
                    _lampModeType = m.ReturnType;
                    break;
                }
            }

            // SetMode(Mode, bool).
            foreach (var m in type.GetMethods(flags))
            {
                if (m.Name != "SetMode") continue;
                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[1].ParameterType == typeof(bool))
                {
                    _lampSetModeMethod = m;
                    if (_lampModeType == null)
                        _lampModeType = ps[0].ParameterType;
                    break;
                }
            }

            Mod.LogDebug(
                $"[LampSync] Reflection getCurrentMode={(_lampCurrentModeGetter?.Name ?? "null")} " +
                $"setMode={(_lampSetModeMethod?.Name ?? "null")} " +
                $"modeType={_lampModeType?.FullName ?? "null"}");

            if (_lampCurrentModeGetter == null || _lampSetModeMethod == null || _lampModeType == null)
            {
                Mod.Log.Warning("[LampSync] Lamp API not fully resolved, sync disabled.");
                _lampReflectionFailed = true;
            }
            else
            {
                LogModeEnumValues(_lampModeType);
            }
        }
        catch (Exception ex)
        {
            _lampReflectionFailed = true;
            Mod.Log.Warning($"[LampSync] Reflection setup failed: {ex.GetType().Name}:{ex.Message}");
        }
    }

    private static void LogModeEnumValues(Type modeType)
    {
        try
        {
            if (!modeType.IsEnum) return;
            var names = Enum.GetNames(modeType);
            var values = Enum.GetValues(modeType);
            var sb = new System.Text.StringBuilder("[LampSync] Mode enum values: ");
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]).Append('=').Append(Convert.ToInt32(values.GetValue(i)));
            }
            Mod.LogDebug(sb.ToString());
        }
        catch { }
    }

    private static AavaLightStick TryGetLocalLightStick()
    {
        if (_localLightStickCached != null && _localLightStickCached.Pointer != IntPtr.Zero)
            return _localLightStickCached;

        var frame = Time.frameCount;
        if (frame == _lastLocalLightStickSearchFrame)
            return null;
        _lastLocalLightStickSearchFrame = frame;

        var mc = TryGetLocalMCGameObject();
        if (mc == null) return null;

        try
        {
            var lamp = mc.GetComponentInChildren<AavaLightStick>(true);
            if (lamp != null)
            {
                _localLightStickCached = lamp;
                return lamp;
            }
        }
        catch (Exception ex)
        {
            if (!_localLightStickWarningLogged)
            {
                _localLightStickWarningLogged = true;
                Mod.Log.Warning($"[LampSync] Local lookup failed: {ex.GetType().Name}:{ex.Message}");
            }
        }

        return null;
    }
}
