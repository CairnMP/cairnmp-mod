using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnMultiplayerMod.Internal.Game.Players;

internal static class NetplaySetFramePatch
{
    private static readonly HarmonyLib.Harmony NetplaySetFrameHarmony = new("CairnMultiplayerMod.NetplaySetFramePatch");

    private static bool _netplaySetFramePatchInstalled;
    private static bool _netplaySetFramePatchFailed;
    private static bool _netplaySetFramePathLogged;
    private static bool _netplaySetFramePatchFailureLogged;

    public static bool IsInstalled => _netplaySetFramePatchInstalled;

    // LOGICAL pause of the patch, without touching Harmony. The patch stays hooked; for a
    // managed ghost the prefixes skip the native call WITHOUT updating its frame (return false),
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
    // the native SetFrame. Our ghosts are driven through the generated IL2CPP field
    // setters and are never initialized on the native side; letting the native SetFrame run did a
    // lookup in an unpopulated dict -> KeyNotFoundException('INVALID') and crash. On an
    // field update failure, we skip the native call and
    // retry the next frame rather than risk that crash.
    private static bool ApplyRemotePlayerSetFramePatch(NetplayRemotePlayer instance, int id, string playerName, NetFrame frame)
    {
        if (instance == null || instance.Pointer == IntPtr.Zero) return true;
        if (!IsRenderableNetFrame(frame)) return true;
        try
        {
            instance._Id_k__BackingField = id;
            instance.initialized = true;
            instance.currentFrame = frame;
            UpdateRemotePlayerMetadata(instance, id, playerName, frame);
            LogPatchedSetFramePathOnce();
            return true;
        }
        catch (Exception ex)
        {
            LogPatchedSetFrameFailureOnce("player", ex);
            return true;
        }
    }

    // Same invariant as ApplyRemotePlayerSetFramePatch: a managed climbot must never
    // fall back on the native SetFrame ('INVALID' crash on an unpopulated dict).
    private static bool ApplyRemoteClimbotSetFramePatch(NetplayRemoteClimbot instance, int id, NetFrame frame)
    {
        if (instance == null || instance.Pointer == IntPtr.Zero) return true;
        if (!IsRenderableNetFrame(frame)) return true;
        try
        {
            instance.ownerId = id;
            instance.currentFrame = frame;
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

    private static class NetplaySetFramePatches
    {
        internal static bool NetplayRemotePlayerSetFramePrefix(
            NetplayRemotePlayer __instance,
            int id,
            string playerName,
            NetFrame frame)
        {
            if (!RemotePlayerManager.IsManagedNetplayPlayer(__instance))
                return true;

            // Managed ghost: we NEVER let the native SetFrame run (unpopulated dict
            // -> KeyNotFoundException('INVALID')). While paused (bivouac save window), we
            // skip the native call WITHOUT updating the frame: inert but crash-free.
            if (_setFramePatchPaused)
                return false;

            return !ApplyRemotePlayerSetFramePatch(__instance, id, playerName, frame);
        }

        internal static bool NetplayRemoteClimbotSetFramePrefix(
            NetplayRemoteClimbot __instance,
            int id,
            NetFrame frame)
        {
            if (!RemotePlayerManager.IsManagedNetplayClimbot(__instance))
                return true;

            // Same invariant: a managed climbot never falls back on the native SetFrame.
            if (_setFramePatchPaused)
                return false;

            return !ApplyRemoteClimbotSetFramePatch(__instance, id, frame);
        }
    }
}
