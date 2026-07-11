using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
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

    public static bool IsNetplaySetFramePatchInstalled => _netplaySetFramePatchInstalled;

    // Pause LOGIQUE du patch, sans toucher Harmony. Le patch reste detoure ; pour un
    // ghost gere les prefixes skippent le natif SANS inline write (return false), restant
    // inertes pendant la fenetre de save. On ne laisse SURTOUT PAS tourner le SetFrame
    // natif sur nos ghosts (dico non peuple -> KeyNotFoundException('INVALID')). Le but
    // est d'eviter le churn de detour (UnpatchSelf/Patch) pendant la sauvegarde du bivouac,
    // qui pouvait perturber le scellage/reouverture du package de save natif (ghost stream)
    // et laisser le package dispose -> les saves suivantes deviennent des no-op silencieux.
    private static bool _setFramePatchPaused;
    public static void PauseSetFramePatch() => _setFramePatchPaused = true;
    public static void ResumeSetFramePatch() => _setFramePatchPaused = false;

    public static void InstallNetplaySetFramePatch()
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
                Mod.Log.Warning("[CairnGameApi] Netplay SetFrame patch methods were not found");
                return;
            }

            NetplaySetFrameHarmony.Patch(playerOriginal, prefix: new HarmonyMethod(playerPrefix));
            NetplaySetFrameHarmony.Patch(climbotOriginal, prefix: new HarmonyMethod(climbotPrefix));

            _netplaySetFramePatchInstalled = true;
            Mod.LogDebug("[CairnGameApi] Netplay SetFrame patches installed");
        }
        catch (Exception ex)
        {
            _netplaySetFramePatchFailed = true;
            Mod.Log.Warning($"[CairnGameApi] Netplay SetFrame patch install failed: {ex.Message}");
        }
    }

    public static void UninstallNetplaySetFramePatch()
    {
        if (!_netplaySetFramePatchInstalled) return;

        try
        {
            NetplaySetFrameHarmony.UnpatchSelf();
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] Netplay SetFrame patch uninstall failed: {ex.Message}");
        }
        finally
        {
            _netplaySetFramePatchInstalled = false;
        }
    }

    // INVARIANT : pour un ghost GERE, cette methode renvoie TOUJOURS true -> le prefix skippe
    // le SetFrame natif. Nos ghosts sont pilotes uniquement par l'ecriture inline et ne sont
    // jamais initialises cote natif ; laisser tourner le SetFrame natif sur eux faisait un
    // lookup dans un dico non peuple -> KeyNotFoundException('INVALID') et crash. En cas
    // d'echec inline (layout pas encore resolu, ou write en erreur), on skip le natif et on
    // reessaie la frame suivante plutot que de risquer ce crash.
    private static bool ApplyRemotePlayerSetFramePatch(NetplayRemotePlayer instance, int id, string playerName, NetFrame frame)
    {
        if (instance == null || instance.Pointer == IntPtr.Zero) return true; // rien a faire, surtout pas le natif
        if (!IsRenderableNetFrame(frame)) return true;
        if (!EnsureNetFrameLayout() || !EnsureRemotePlayerLayout(instance)) return true; // pas pret -> skip natif, retry frame suivante

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
            return true; // notre write a echoue -> on skip le natif (dangereux), pas de fallback natif
        }
    }

    // Meme invariant que ApplyRemotePlayerSetFramePatch : un climbot gere ne doit jamais
    // retomber sur le SetFrame natif (crash 'INVALID' sur dico non peuple).
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
        catch
        {
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
        catch
        {
            // Le nom est cosmetique ; la frame reste prioritaire.
        }

        try
        {
            var interactionProvider = instance.interactionProvider;
            if (interactionProvider != null)
                interactionProvider.SetNetPlayerInfo(id, safeName, frame.PawnState);
        }
        catch
        {
            // Les interactions distantes ne doivent pas bloquer le rendu.
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
            Mod.Log.Warning($"[CairnGameApi] NetFrame layout lookup failed, using fallback offsets: {ex.Message}");
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
            Mod.Log.Warning($"[CairnGameApi] Remote player layout lookup failed, using fallback offsets: {ex.Message}");
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
            Mod.Log.Warning($"[CairnGameApi] Remote climbot layout lookup failed, using fallback offsets: {ex.Message}");
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
        Mod.LogDebug("[CairnGameApi] Native SetFrame bypass active for remote NetFrames");
    }

    private static void LogPatchedSetFrameFailureOnce(string label, Exception ex)
    {
        if (_netplaySetFramePatchFailureLogged) return;
        _netplaySetFramePatchFailureLogged = true;
        Mod.Log.Warning($"[CairnGameApi] Patched {label} SetFrame failed: {ex.Message}");
    }

    private static void LogFrameLayoutsOnce()
    {
        if (_netplayFrameLayoutLogged) return;
        if (!_netFrameLayoutReady || !_remotePlayerLayoutReady || !_remoteClimbotLayoutReady) return;

        _netplayFrameLayoutLogged = true;
        Mod.LogDebug(
            "[CairnGameApi] NetFrame patch offsets " +
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
            // Instance non geree (netplay natif du jeu) -> on laisse tourner le natif.
            if (!RemotePlayerManager.IsManagedNetplayPlayer(__instance))
                return true;

            // Ghost gere : on ne laisse JAMAIS tourner le SetFrame natif (dico non peuple
            // -> KeyNotFoundException('INVALID')). En pause (fenetre de save bivouac), on
            // skippe le natif SANS faire l'inline write : inerte mais sans crash.
            if (_setFramePatchPaused)
                return false;

            return !ApplyRemotePlayerSetFramePatch(__instance, id, playerName, frame);
        }

        internal static bool NetplayRemoteClimbotSetFramePrefix(
            NetplayRemoteClimbot __instance,
            int id,
            NetFrame frame)
        {
            // Instance non geree (netplay natif du jeu) -> on laisse tourner le natif.
            if (!RemotePlayerManager.IsManagedNetplayClimbot(__instance))
                return true;

            // Meme invariant : un climbot gere ne retombe jamais sur le SetFrame natif.
            if (_setFramePatchPaused)
                return false;

            return !ApplyRemoteClimbotSetFramePatch(__instance, id, frame);
        }
    }
}
