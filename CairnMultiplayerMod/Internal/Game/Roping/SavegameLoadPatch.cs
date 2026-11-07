using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.IO;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Lets a walking save finish loading when Climbot's belay exit has no coroutine.
/// The skip applies only while <c>ReadFromSavegame</c> is running, so a missing
/// transition during play still surfaces. Skipping that one call keeps the rest
/// of the pawn restore, including its completion callback. If the exception still
/// escapes the pawn read, the callback is invoked here so the loading screen is
/// not left waiting.
/// </summary>
internal static class SavegameLoadPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.SaveLoad");
    private static bool _installed;
    private static bool _logged;
    private static bool _readingSave;

    public static void Install()
    {
        if (_installed) return;

        try
        {
            var goToState = AccessTools.Method(
                    typeof(RobotPawnController), nameof(RobotPawnController.GoToState))
                ?? throw new MissingMethodException(nameof(RobotPawnController), nameof(RobotPawnController.GoToState));
            var read = AccessTools.Method(
                    typeof(PawnControllerSwitcher), nameof(PawnControllerSwitcher.ReadFromSavegame))
                ?? throw new MissingMethodException(
                    nameof(PawnControllerSwitcher), nameof(PawnControllerSwitcher.ReadFromSavegame));

            Harmony.Patch(goToState, finalizer: new HarmonyMethod(PatchMethod(nameof(GoToStateFinalizer))));
            Harmony.Patch(
                read,
                prefix: new HarmonyMethod(PatchMethod(nameof(BeginSaveRead))),
                finalizer: new HarmonyMethod(PatchMethod(nameof(ReadFromSavegameFinalizer))));
            _installed = true;
            ModLog.Info("[Save] A missing Climbot state coroutine no longer stops a save from loading.");
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            _installed = false;
            ModLog.Warning("[Save] Could not keep a missing Climbot coroutine from stopping the load: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        Harmony.UnpatchSelf();
        _installed = false;
        _logged = false;
        _readingSave = false;
    }

    private static void BeginSaveRead() => _readingSave = true;

    private static Exception GoToStateFinalizer(
        Exception __exception, RobotPawnController.State nextState)
    {
        if (!_readingSave || !SavegameLoadPolicy.IsMissingStateCoroutine(__exception)) return __exception;
        LogOnce(nextState.ToString());
        return null;
    }

    private static Exception ReadFromSavegameFinalizer(
        Exception __exception, Disk.IOOperationCallback callback)
    {
        _readingSave = false;
        if (!SavegameLoadPolicy.IsMissingStateCoroutine(__exception)) return __exception;

        LogOnce("save");
        try
        {
            // The pawn read did not reach its own callback. Report success so the
            // loader moves on; the position written before the belay restore stays.
            callback?.Invoke(true);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("save.load-callback", exception);
        }

        return null;
    }

    private static void LogOnce(string state)
    {
        if (_logged) return;
        _logged = true;
        ModLog.Warning(
            "[Save] Climbot state " + state
            + " had no coroutine. That transition was skipped so the save can finish loading.");
    }

    private static MethodInfo PatchMethod(string name)
        => typeof(SavegameLoadPatch).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
}
