using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.IO;
using Il2CppTheGameBakers.Cairn.Save;
using Savegame = Il2CppTheGameBakers.Cairn.Save.Latest.Savegame;
using CannotSaveReason = Il2CppTheGameBakers.Cairn.Save.SavegameManager.CannotSaveReason;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Rope Team does not start a vanilla save unless the climber is at a bivouac and
/// the rest has finished, and does not load a file from the wall. Cairn writes
/// the bivouac save on the way out, once the flag is already clear.
/// lastBivouacAge on that file is still the exit. A refused write that carries a
/// callback still reports failure, because the pause-menu quit waits on that
/// callback. Cairn's own write and read run when the answer is yes. A new game
/// does not come through the load callback.
/// </summary>
internal static class BivouacSaveGatePatch
{
    private const string LoadCallbackSuffix = "_IOCallback_b__0";

    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.BivouacSave");
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
            Patch(QuickSave(), nameof(BlockQuickSave));
            Patch(Save(typeof(Disk.IOOperationCallback)), nameof(BlockSave));
            Patch(Save(Type.EmptyTypes), nameof(BlockWrite));
            Patch(SaveAndClose(), nameof(BlockSave));
            Patch(PawnCanSave(), nameof(BlockCanSave));
            Patch(LoadCallback(), nameof(BeforeLoad));
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
        if (BivouacSaveSession.MayWrite()) return true;
        __result = false;
        return false;
    }

    private static bool BlockCanSaveReason(ref CannotSaveReason reason, ref bool __result)
    {
        if (BivouacSaveSession.MayWrite()) return true;
        reason = CannotSaveReason.PawnSaidNo;
        __result = false;
        return false;
    }

    private static bool BlockWrite()
    {
        if (BivouacSaveSession.MayWrite()) return true;
        LogBlocked();
        return false;
    }

    // QuickSave names the callback "then". Save and SaveAndClose name it "thenMT".
    // Harmony only supplies a prefix argument when the name matches.
    private static bool BlockQuickSave(Disk.IOOperationCallback then) => FinishWithoutWriting(then);

    private static bool BlockSave(Disk.IOOperationCallback thenMT) => FinishWithoutWriting(thenMT);

    /// <summary>
    /// QuickSave, on a failed CanSave, invokes its callback with false and does not write.
    /// The pause-menu quit is that callback: false still leaves. Skipping the method
    /// without the call leaves the menu waiting on a save that never finishes.
    /// </summary>
    private static bool FinishWithoutWriting(Disk.IOOperationCallback done)
    {
        if (BivouacSaveSession.MayWrite()) return true;
        LogBlocked();
        if (done != null) done.Invoke(false);
        return false;
    }

    /// <summary>
    /// The closure already holds the disk result. Clearing its success flag is Cairn's
    /// own failure path, so the original method still runs and finishes the load.
    /// </summary>
    private static bool BeforeLoad(object __instance)
    {
        if (__instance == null || MayLoad(__instance)) return true;
        var success = false;
        __instance.GetType().GetProperty("success")?.SetValue(__instance, success);
        LogBlocked();
        return true;
    }

    /// <summary>
    /// Solo and the other modes load any file. Rope Team loads a readable file whose
    /// flag is set, or whose lastBivouacAge is still the exit that wrote it.
    /// </summary>
    private static bool MayLoad(object closure)
    {
        var copy = closure.GetType().GetProperty("copy")?.GetValue(closure) as Savegame;
        if (copy == null || copy.world == null)
            return BivouacSaveSession.MayLoad(fileIsInBivouac: false, float.PositiveInfinity);

        var camp = copy.world.bivouac;
        return BivouacSaveSession.MayLoad(camp.isInBivouac, camp.lastBivouacAge);
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
        => new[] { typeof(CannotSaveReason).MakeByRefType() };

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

    /// <summary>The nested load callback, found by its suffix so a closure number change still matches.</summary>
    private static MethodInfo LoadCallback()
    {
        foreach (var type in typeof(SavegameManager).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (type.Name.IndexOf("DisplayClass", StringComparison.Ordinal) < 0) continue;
            if (type.GetProperty("success") == null || type.GetProperty("copy") == null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name.EndsWith(LoadCallbackSuffix, StringComparison.Ordinal)) return method;
            }
        }

        return null;
    }

    private static MethodInfo PatchMethod(string name)
        => typeof(BivouacSaveGatePatch).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
}
