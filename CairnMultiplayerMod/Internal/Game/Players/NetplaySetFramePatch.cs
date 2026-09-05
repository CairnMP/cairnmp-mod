using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnMultiplayerMod.Internal.Game.Players;

internal static unsafe class NetplaySetFramePatch
{
    private const int NetFrameIsValidFallbackOffset = 0x00;
    private const int NetFrameFlagsFallbackOffset = 0x01;
    private const int NetFramePositionsFallbackOffset = 0x08;
    private const int NetFrameEulersFallbackOffset = 0x10;

    private const int RemotePlayerIdFallbackOffset = 0x78;
    private const int RemotePlayerInitializedFallbackOffset = 0x7C;
    private const int RemotePlayerFrameFallbackOffset = 0x88;

    private const int RemoteClimbotFrameFallbackOffset = 0x40;
    private const int RemoteClimbotOwnerIdFallbackOffset = 0x58;

    private static readonly HarmonyLib.Harmony NetplaySetFrameHarmony = new("CairnMultiplayerMod.NetplaySetFramePatch");

    private static bool _netplaySetFramePatchInstalled;
    private static bool _netplaySetFramePatchFailed;
    private static bool _netplaySetFramePathLogged;
    private static bool _netplaySetFramePatchFailureLogged;
    private static bool _netplayFrameLayoutLogged;

    private static bool _netFrameLayoutReady;
    private static int _netFrameIsValidOffset = NetFrameIsValidFallbackOffset;
    private static int _netFrameFlagsOffset = NetFrameFlagsFallbackOffset;
    private static int _netFramePositionsOffset = NetFramePositionsFallbackOffset;
    private static int _netFrameEulersOffset = NetFrameEulersFallbackOffset;

    private static bool _remotePlayerLayoutReady;
    private static int _remotePlayerIdOffset = RemotePlayerIdFallbackOffset;
    private static int _remotePlayerInitializedOffset = RemotePlayerInitializedFallbackOffset;
    private static int _remotePlayerFrameOffset = RemotePlayerFrameFallbackOffset;

    private static bool _remoteClimbotLayoutReady;
    private static int _remoteClimbotFrameOffset = RemoteClimbotFrameFallbackOffset;
    private static int _remoteClimbotOwnerIdOffset = RemoteClimbotOwnerIdFallbackOffset;

    public static bool IsInstalled => _netplaySetFramePatchInstalled;

    // LOGICAL pause of the patch, without touching Harmony. The patch stays hooked; for a
    // managed ghost the prefixes skip the native call WITHOUT an inline write (return false),
    // staying inert during the save window. We must NEVER let the native SetFrame
    // run on our ghosts (unpopulated dict -> KeyNotFoundException('INVALID')). The goal
    // is to avoid detour churn (UnpatchSelf/Patch) during the bivouac save,
    // which could disturb the sealing/reopening of the native save package (ghost stream)
    // and leave the package disposed -> subsequent saves become silent no-ops.
    private static bool _setFramePatchPaused;
    public static void Pause() => _setFramePatchPaused = true;
    public static void Resume() => _setFramePatchPaused = false;

    public static void Install()
    {
        if (_netplaySetFramePatchInstalled || _netplaySetFramePatchFailed) return;

        try
        {
            var playerOriginal = AccessTools.Method(
                typeof(NetplayRemotePlayer),
                nameof(NetplayRemotePlayer.SetFrame),
                new[] { typeof(int), typeof(string), typeof(NetFrame) });

            var climbotOriginal = AccessTools.Method(
                typeof(NetplayRemoteClimbot),
                nameof(NetplayRemoteClimbot.SetFrame),
                new[] { typeof(int), typeof(NetFrame) });

            var playerPrefix = typeof(NetplaySetFramePatches).GetMethod(
                nameof(NetplaySetFramePatches.NetplayRemotePlayerSetFramePrefix),
                BindingFlags.NonPublic | BindingFlags.Static);

            var climbotPrefix = typeof(NetplaySetFramePatches).GetMethod(
                nameof(NetplaySetFramePatches.NetplayRemoteClimbotSetFramePrefix),
                BindingFlags.NonPublic | BindingFlags.Static);

            if (playerOriginal == null || climbotOriginal == null || playerPrefix == null || climbotPrefix == null)
            {
                _netplaySetFramePatchFailed = true;
                ModLog.Warning("[SetFramePatch] Netplay SetFrame patch methods were not found");
                return;
            }

            NetplaySetFrameHarmony.Patch(playerOriginal, prefix: new HarmonyMethod(playerPrefix));
            NetplaySetFrameHarmony.Patch(climbotOriginal, prefix: new HarmonyMethod(climbotPrefix));

            _netplaySetFramePatchInstalled = true;
            ModLog.Debug("[SetFramePatch] Netplay SetFrame patches installed");
        }
        catch (Exception ex)
        {
            _netplaySetFramePatchFailed = true;
            ModLog.Warning($"[SetFramePatch] Netplay SetFrame patch install failed: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        if (!_netplaySetFramePatchInstalled) return;

        try
        {
            NetplaySetFrameHarmony.UnpatchSelf();
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[SetFramePatch] Netplay SetFrame patch uninstall failed: {ex.Message}");
        }
        finally
        {
            _netplaySetFramePatchInstalled = false;
        }
    }

    // INVARIANT: for a MANAGED ghost, this method ALWAYS returns true -> the prefix skips
    // the native SetFrame. Our ghosts are driven solely by the inline write and are
    // never initialized on the native side; letting the native SetFrame run on them did a
    // lookup in an unpopulated dict -> KeyNotFoundException('INVALID') and crash. On an
    // inline failure (layout not resolved yet, or a write error), we skip the native call and
    // retry the next frame rather than risk that crash.
    private static bool ApplyRemotePlayerSetFramePatch(NetplayRemotePlayer instance, int id, string playerName, NetFrame frame)
    {
        if (instance == null || instance.Pointer == IntPtr.Zero) return true; // nothing to do, definitely not the native call
        if (!IsRenderableNetFrame(frame)) return true;
        if (!EnsureNetFrameLayout() || !EnsureRemotePlayerLayout(instance)) return true; // not ready -> skip native, retry next frame

        try
        {
            var basePtr = (byte*)instance.Pointer;
            *(int*)(basePtr + _remotePlayerIdOffset) = id;
            *(byte*)(basePtr + _remotePlayerInitializedOffset) = 1;
            WriteInlineNetFrame(basePtr + _remotePlayerFrameOffset, frame);
            UpdateRemotePlayerMetadata(instance, id, playerName, frame);
            LogPatchedSetFramePathOnce();
            return true;
        }
        catch (Exception ex)
        {
            LogPatchedSetFrameFailureOnce("player", ex);
            return true; // our write failed -> we skip the native call (dangerous), no native fallback
        }
    }

    // Same invariant as ApplyRemotePlayerSetFramePatch: a managed climbot must never
    // fall back on the native SetFrame ('INVALID' crash on an unpopulated dict).
    private static bool ApplyRemoteClimbotSetFramePatch(NetplayRemoteClimbot instance, int id, NetFrame frame)
    {
        if (instance == null || instance.Pointer == IntPtr.Zero) return true;
        if (!IsRenderableNetFrame(frame)) return true;
        if (!EnsureNetFrameLayout() || !EnsureRemoteClimbotLayout(instance)) return true;

        try
        {
            var basePtr = (byte*)instance.Pointer;
            *(int*)(basePtr + _remoteClimbotOwnerIdOffset) = id;
            WriteInlineNetFrame(basePtr + _remoteClimbotFrameOffset, frame);
            LogPatchedSetFramePathOnce();
            return true;
        }
        catch (Exception ex)
        {
            LogPatchedSetFrameFailureOnce("climbot", ex);
            return true;
        }
    }

    private static bool IsRenderableNetFrame(NetFrame frame)
    {
        try
        {
            return frame != null && frame.isValid && frame.positions != null && frame.positions.Length > 0;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("netplay.validate-frame", exception);
            return false;
        }
    }

    private static void WriteInlineNetFrame(byte* target, NetFrame frame)
    {
        target[_netFrameIsValidOffset] = frame.isValid ? (byte)1 : (byte)0;
        target[_netFrameFlagsOffset] = frame.flags;
        *(IntPtr*)(target + _netFramePositionsOffset) = frame.positions?.Pointer ?? IntPtr.Zero;
        *(IntPtr*)(target + _netFrameEulersOffset) = frame.eulers?.Pointer ?? IntPtr.Zero;
    }

    private static void UpdateRemotePlayerMetadata(NetplayRemotePlayer instance, int id, string playerName, NetFrame frame)
    {
        var safeName = playerName ?? "";

        try
        {
            var nameMesh = instance.nameMesh;
            if (nameMesh != null)
                nameMesh.text = safeName;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("netplay.update-name-label", exception);
        }

        try
        {
            var interactionProvider = instance.interactionProvider;
            if (interactionProvider != null)
                interactionProvider.SetNetPlayerInfo(id, safeName, frame.PawnState);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("netplay.update-interaction-provider", exception);
        }
    }

    private static bool EnsureNetFrameLayout()
    {
        if (_netFrameLayoutReady) return true;

        try
        {
            var klass = IL2CPP.GetIl2CppClass(
                "TheGameBakers.Cairn.Global.dll",
                "TheGameBakers.Cairn.Netplay",
                "NetFrame");

            if (klass != IntPtr.Zero)
            {
                _netFrameIsValidOffset = GetValueTypeFieldOffset(klass, "isValid", NetFrameIsValidFallbackOffset);
                _netFrameFlagsOffset = GetValueTypeFieldOffset(klass, "flags", NetFrameFlagsFallbackOffset);
                _netFramePositionsOffset = GetValueTypeFieldOffset(klass, "positions", NetFramePositionsFallbackOffset);
                _netFrameEulersOffset = GetValueTypeFieldOffset(klass, "eulers", NetFrameEulersFallbackOffset);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[SetFramePatch] NetFrame layout lookup failed, using fallback offsets: {ex.Message}");
        }

        _netFrameLayoutReady = true;
        LogFrameLayoutsOnce();
        return true;
    }

    private static bool EnsureRemotePlayerLayout(NetplayRemotePlayer instance)
    {
        if (_remotePlayerLayoutReady) return true;

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(instance.Pointer);
            _remotePlayerIdOffset = GetInstanceFieldOffset(
                klass,
                RemotePlayerIdFallbackOffset,
                "<Id>k__BackingField",
                "_Id_k__BackingField");
            _remotePlayerInitializedOffset = GetInstanceFieldOffset(
                klass,
                RemotePlayerInitializedFallbackOffset,
                "initialized");
            _remotePlayerFrameOffset = GetInstanceFieldOffset(
                klass,
                RemotePlayerFrameFallbackOffset,
                "currentFrame");
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[SetFramePatch] Remote player layout lookup failed, using fallback offsets: {ex.Message}");
        }

        _remotePlayerLayoutReady = true;
        LogFrameLayoutsOnce();
        return true;
    }

    private static bool EnsureRemoteClimbotLayout(NetplayRemoteClimbot instance)
    {
        if (_remoteClimbotLayoutReady) return true;

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(instance.Pointer);
            _remoteClimbotFrameOffset = GetInstanceFieldOffset(
                klass,
                RemoteClimbotFrameFallbackOffset,
                "currentFrame");
            _remoteClimbotOwnerIdOffset = GetInstanceFieldOffset(
                klass,
                RemoteClimbotOwnerIdFallbackOffset,
                "ownerId");
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[SetFramePatch] Remote climbot layout lookup failed, using fallback offsets: {ex.Message}");
        }

        _remoteClimbotLayoutReady = true;
        LogFrameLayoutsOnce();
        return true;
    }

    private static int GetValueTypeFieldOffset(IntPtr klass, string fieldName, int fallbackOffset)
    {
        var field = IL2CPP.GetIl2CppField(klass, fieldName);
        if (field == IntPtr.Zero) return fallbackOffset;

        var offset = (int)IL2CPP.il2cpp_field_get_offset(field) - 2 * IntPtr.Size;
        return offset >= 0 ? offset : fallbackOffset;
    }

    private static int GetInstanceFieldOffset(IntPtr klass, int fallbackOffset, params string[] fieldNames)
    {
        foreach (var fieldName in fieldNames)
        {
            var field = IL2CPP.GetIl2CppField(klass, fieldName);
            if (field != IntPtr.Zero)
                return (int)IL2CPP.il2cpp_field_get_offset(field);
        }

        return fallbackOffset;
    }

    private static void LogPatchedSetFramePathOnce()
    {
        if (_netplaySetFramePathLogged) return;
        _netplaySetFramePathLogged = true;
        ModLog.Debug("[SetFramePatch] Native SetFrame bypass active for remote NetFrames");
    }

    private static void LogPatchedSetFrameFailureOnce(string label, Exception ex)
    {
        if (_netplaySetFramePatchFailureLogged) return;
        _netplaySetFramePatchFailureLogged = true;
        ModLog.Warning($"[SetFramePatch] Patched {label} SetFrame failed: {ex.Message}");
    }

    private static void LogFrameLayoutsOnce()
    {
        if (_netplayFrameLayoutLogged) return;
        if (!_netFrameLayoutReady || !_remotePlayerLayoutReady || !_remoteClimbotLayoutReady) return;

        _netplayFrameLayoutLogged = true;
        ModLog.Debug(
            "[SetFramePatch] NetFrame patch offsets " +
            $"frame(valid=0x{_netFrameIsValidOffset:X}, flags=0x{_netFrameFlagsOffset:X}, positions=0x{_netFramePositionsOffset:X}, eulers=0x{_netFrameEulersOffset:X}) " +
            $"player(id=0x{_remotePlayerIdOffset:X}, initialized=0x{_remotePlayerInitializedOffset:X}, frame=0x{_remotePlayerFrameOffset:X}) " +
            $"climbot(owner=0x{_remoteClimbotOwnerIdOffset:X}, frame=0x{_remoteClimbotFrameOffset:X})");
    }

    private static class NetplaySetFramePatches
    {
        internal static bool NetplayRemotePlayerSetFramePrefix(
            NetplayRemotePlayer __instance,
            int id,
            string playerName,
            NetFrame frame)
        {
            // Unmanaged instance (the game's native netplay) -> let the native call run.
            if (!RemotePlayerManager.IsManagedNetplayPlayer(__instance))
                return true;

            // Managed ghost: we NEVER let the native SetFrame run (unpopulated dict
            // -> KeyNotFoundException('INVALID')). While paused (bivouac save window), we
            // skip the native call WITHOUT doing the inline write: inert but crash-free.
            if (_setFramePatchPaused)
                return false;

            return !ApplyRemotePlayerSetFramePatch(__instance, id, playerName, frame);
        }

        internal static bool NetplayRemoteClimbotSetFramePrefix(
            NetplayRemoteClimbot __instance,
            int id,
            NetFrame frame)
        {
            // Unmanaged instance (the game's native netplay) -> let the native call run.
            if (!RemotePlayerManager.IsManagedNetplayClimbot(__instance))
                return true;

            // Same invariant: a managed climbot never falls back on the native SetFrame.
            if (_setFramePatchPaused)
                return false;

            return !ApplyRemoteClimbotSetFramePatch(__instance, id, frame);
        }
    }
}
