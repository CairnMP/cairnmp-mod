using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.IO;
using Il2CppTheGameBakers.Cairn.Save;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team lets Cairn save and load. A live pitch is set aside for that write
/// and put back when the climb continues, or when the write throws. Cairn's own
/// file is what gets stored. The save writer is not patched, and the package is
/// not opened or rewritten here.
/// </summary>
internal static class BivouacSaveGatePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.BivouacSave");
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;

        try
        {
            Patch(AutoSave(), nameof(PrepareContinued));
            Patch(QuickSave(), nameof(PrepareContinued));
            Patch(Save(typeof(Disk.IOOperationCallback)), nameof(PrepareContinued));
            Patch(Save(Type.EmptyTypes), nameof(PrepareContinued));
            Patch(SaveAndClose(), nameof(PrepareLeaving));
            Patch(DeathSegment(), nameof(PrepareContinued));
            Patch(DiskDeathSegment(), nameof(PrepareContinued));
            _installed = true;
            ModLog.Info("[Save] Rope Team saves with Cairn.");
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            BivouacSaveSnapshot.Reset();
            _installed = false;
            ModLog.Warning("[Save] Could not follow Cairn's saves: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        BivouacSaveSnapshot.Reset();
        _installed = false;
    }

    private static void PrepareContinued() => Prepare(leaving: false);

    private static void PrepareLeaving() => Prepare(leaving: true);

    private static void Prepare(bool leaving)
    {
        var hold = false;
        try
        {
            hold = BivouacSaveSnapshot.Enter(
                BivouacSaveSession.RopeTeamSession,
                TeamAnchorSession.RulesArmed || TeamAnchorSession.PitchRemains,
                BivouacSaveSession.AlreadyClean,
                leaving);
            if (!hold) return;
            ClimbotRopePatch.HoldForSnapshot();
            RopeInterop.ParkForeignPitons();
        }
        catch (Exception ex)
        {
            ModLog.Warning("[Save] Could not set the pitch aside: " + ex.Message);
            if (hold) PutPitchBack();
        }
    }

    /// <summary>
    /// Runs after Cairn's method, including when that method throws. Putting the
    /// pitch back does not change the file already taken inside the call.
    /// </summary>
    private static Exception Finish(Exception __exception)
    {
        try
        {
            if (BivouacSaveSnapshot.Exit(__exception != null))
                PutPitchBack();
        }
        catch (Exception ex)
        {
            ModLog.Warning("[Save] Could not put the pitch back: " + ex.Message);
        }

        return __exception;
    }

    private static void PutPitchBack()
    {
        RopeInterop.UnparkForeignPitons();
        ClimbotRopePatch.ReleaseSnapshot();
    }

    private static void Patch(MethodInfo method, string prefix)
    {
        if (method == null) throw new MissingMethodException(prefix);
        Harmony.Patch(
            method,
            prefix: new HarmonyMethod(PatchMethod(prefix)),
            finalizer: new HarmonyMethod(PatchMethod(nameof(Finish))));
    }

    private static MethodInfo AutoSave()
        => AccessTools.Method(typeof(SavegameManager), nameof(SavegameManager.AutoSave), new[] { typeof(bool) });

    private static MethodInfo DeathSegment()
        => AccessTools.Method(
            typeof(SavegameManager),
            nameof(SavegameManager.IncrementDeathSegmentOnSave),
            new[] { typeof(PathSaveIcon), typeof(PathRecorder), typeof(Il2CppSystem.Action<bool>) });

    private static MethodInfo DiskDeathSegment()
        => AccessTools.Method(
            typeof(Disk),
            nameof(Disk.IncrementDeathSegmentOnSave),
            new[] { typeof(SavegamePackage), typeof(string), typeof(PathRecorder), typeof(Il2CppSystem.Action) });

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

    private static MethodInfo PatchMethod(string name)
        => typeof(BivouacSaveGatePatch).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
}
