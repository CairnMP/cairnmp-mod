using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.IO;
using Il2CppTheGameBakers.Cairn.Save;
using Savegame = Il2CppTheGameBakers.Cairn.Save.Latest.Savegame;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team does not start a vanilla save unless the climber is at a bivouac,
/// and does not load a file that resumes anywhere else. The file itself is left
/// on disk. A new game does not come through <c>LoadInternal</c>.
/// </summary>
internal static class CampSaveGatePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.CampSave");
    private static bool _installed;
    private static float _nextLogAt;

    public static void Install()
    {
        if (_installed) return;

        try
        {
            Patch(CanSave(), nameof(BlockCanSave));
            Patch(CanSave(ReasonRef()), nameof(BlockCanSaveReason));
            Patch(CanAutoSave(), nameof(BlockCanSave));
            Patch(AutoSave(), nameof(BlockWrite));
            Patch(QuickSave(), nameof(BlockWrite));
            Patch(Save(typeof(Disk.IOOperationCallback)), nameof(BlockWrite));
            Patch(Save(Type.EmptyTypes), nameof(BlockWrite));
            Patch(SaveAndClose(), nameof(BlockWrite));
            Patch(PawnCanSave(), nameof(BlockCanSave));
            Patch(LoadInternal(), nameof(BeforeLoad));
            _installed = true;
            ModLog.Info("[Save] Rope Team saves and loads only at a bivouac.");
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            _installed = false;
            ModLog.Warning("[Save] Could not keep Rope Team saves at a bivouac: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _nextLogAt = 0f;
    }

    private static bool BlockCanSave(ref bool __result)
    {
        if (CampSaveSession.MayWrite()) return true;
        __result = false;
        return false;
    }

    private static bool BlockCanSaveReason(ref SavegameManager.CannotSaveReason reason, ref bool __result)
    {
        if (CampSaveSession.MayWrite()) return true;
        reason = SavegameManager.CannotSaveReason.PawnSaidNo;
        __result = false;
        return false;
    }

    private static bool BlockWrite()
    {
        if (CampSaveSession.MayWrite()) return true;
        LogBlocked();
        return false;
    }

    private static void BeforeLoad(ref bool success, ref Savegame save)
    {
        // success is the disk read. Clearing it is Cairn's own failure path, so the
        // original method still runs and finishes the load. A new game never arrives here.
        if (!success || save == null || save.world == null) return;
        if (CampSaveSession.MayLoad(save.world.bivouac.isInBivouac)) return;
        LogBlocked();
        success = false;
    }

    private static void LogBlocked()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextLogAt) return;
        _nextLogAt = now + 5f;
        ModLog.Info("[Save] Rope Team saves at a bivouac.");
    }

    private static void Patch(MethodInfo method, string prefix)
    {
        if (method == null) throw new MissingMethodException(prefix);
        Harmony.Patch(method, prefix: new HarmonyMethod(PatchMethod(prefix)));
    }

    private static MethodInfo CanSave()
        => AccessTools.Method(typeof(SavegameManager), nameof(SavegameManager.CanSave), Type.EmptyTypes);

    private static MethodInfo CanSave(Type[] types)
        => AccessTools.Method(typeof(SavegameManager), nameof(SavegameManager.CanSave), types);

    private static Type[] ReasonRef()
        => new[] { typeof(SavegameManager.CannotSaveReason).MakeByRefType() };

    private static MethodInfo CanAutoSave()
        => AccessTools.Method(
            typeof(SavegameManager),
            nameof(SavegameManager.CanAutoSave),
            new[] { typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });

    private static MethodInfo AutoSave()
        => AccessTools.Method(typeof(SavegameManager), nameof(SavegameManager.AutoSave), new[] { typeof(bool) });

    private static MethodInfo QuickSave()
        => AccessTools.Method(
            typeof(SavegameManager),
            nameof(SavegameManager.QuickSave),
            new[] { typeof(Disk.IOOperationCallback), typeof(bool) });

    private static MethodInfo Save(Type argument)
        => AccessTools.Method(typeof(SavegameManager), nameof(SavegameManager.Save), new[] { argument });

    private static MethodInfo Save(Type[] types)
        => AccessTools.Method(typeof(SavegameManager), nameof(SavegameManager.Save), types);

    private static MethodInfo SaveAndClose()
        => AccessTools.Method(
            typeof(SavegameManager),
            nameof(SavegameManager.SaveAndClose),
            new[] { typeof(Disk.IOOperationCallback) });

    private static MethodInfo PawnCanSave()
        => AccessTools.Method(typeof(PawnControllerSwitcher), nameof(PawnControllerSwitcher.CanSave), Type.EmptyTypes);

    private static MethodInfo LoadInternal()
        => AccessTools.Method(
            typeof(SavegameManager),
            "LoadInternal",
            new[]
            {
                typeof(bool),
                typeof(Savegame).MakeByRefType(),
                typeof(ISavegamePackage),
            });

    private static MethodInfo PatchMethod(string name)
        => typeof(CampSaveGatePatch).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
}
