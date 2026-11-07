using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn;
using Savegame = Il2CppTheGameBakers.Cairn.Save.Latest.Savegame;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// A Rope Team bivouac save is a rest: no pitons in the wall, and Climbot not
/// ordered off the belay. The correction lasts for that write only. The pitch
/// on screen is left as it is, and the bag is left to Cairn.
/// </summary>
internal static class CampSaveFilePatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.CampSaveFile");
    private static bool _installed;
    private static bool _waitForCallback;
    private static Savegame _pending;

    public static void Install()
    {
        if (_installed) return;

        try
        {
            var pawn = AccessTools.Method(
                    typeof(PawnControllerSwitcher), nameof(PawnControllerSwitcher.WriteToSavegame))
                ?? throw new MissingMethodException(
                    nameof(PawnControllerSwitcher), nameof(PawnControllerSwitcher.WriteToSavegame));
            var robot = AccessTools.Method(
                    typeof(RobotPawnController), nameof(RobotPawnController.WriteToSavegame))
                ?? throw new MissingMethodException(
                    nameof(RobotPawnController), nameof(RobotPawnController.WriteToSavegame));

            Harmony.Patch(
                pawn,
                prefix: new HarmonyMethod(PatchMethod(nameof(BeginWrite))),
                postfix: new HarmonyMethod(PatchMethod(nameof(AfterWrite))),
                finalizer: new HarmonyMethod(PatchMethod(nameof(WriteFinalizer))));
            Harmony.Patch(robot, postfix: new HarmonyMethod(PatchMethod(nameof(AfterRobot))));
            _installed = true;
            ModLog.Info("[Save] A Rope Team bivouac save leaves the wall empty.");
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            _installed = false;
            CampSaveSession.EndCampFile();
            ModLog.Warning("[Save] Could not write a Rope Team bivouac save: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _waitForCallback = false;
        _pending = null;
        CampSaveSession.EndCampFile();
    }

    private static void BeginWrite(ref Savegame save, ref Il2CppSystem.Action callbackWhenDone)
    {
        _waitForCallback = false;
        _pending = null;
        CampSaveSession.BeginCampFile();
        if (!CampSaveSession.WritingCampFile) return;

        try
        {
            _pending = save;
            var done = callbackWhenDone;
            // Null means the fill is already finished when the method returns.
            // A callback means it can still be writing, so the wall is cleared then.
            _waitForCallback = done != null;
            callbackWhenDone = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                new Action(() => Finish(done)));
        }
        catch
        {
            CampSaveSession.EndCampFile();
            _pending = null;
            _waitForCallback = false;
            throw;
        }
    }

    private static void AfterWrite(ref Savegame save)
    {
        if (!CampSaveSession.WritingCampFile) return;
        _pending = save;
        // The array may already be sized. A callback can still fill it after this
        // returns, and that path clears it again.
        EmptyWall(save);
        if (!_waitForCallback) Finish(null);
    }

    private static void AfterRobot(ref Savegame.World.Robot save)
    {
        if (!CampSaveSession.WritingCampFile) return;
        CampSavePackage.KeepCampBelay(ref save.wasOrderedToStopSecuring, ref save.pitonsPickUpStreak);
    }

    private static Exception WriteFinalizer(Exception __exception)
    {
        if (__exception == null) return null;
        _waitForCallback = false;
        _pending = null;
        CampSaveSession.EndCampFile();
        return __exception;
    }

    private static void Finish(Il2CppSystem.Action done)
    {
        try { EmptyWall(_pending); }
        finally
        {
            _pending = null;
            _waitForCallback = false;
            CampSaveSession.EndCampFile();
            done?.Invoke();
        }
    }

    private static void EmptyWall(Savegame save)
    {
        var world = save?.world;
        if (world == null) return;
        world.placedPitons = new Il2CppStructArray<Savegame.World.Piton>(0L);
        world.offBelayPiton = default;
    }

    private static MethodInfo PatchMethod(string name)
        => typeof(CampSaveFilePatch).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
}
