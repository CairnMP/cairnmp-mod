using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;

namespace CairnMultiplayerMod.Internal.Game;

internal static unsafe class FreeRoamUnlockPatch
{
    private static readonly HarmonyLib.Harmony FreeRoamUnlockHarmony =
        new("CairnMultiplayerMod.FreeRoamUnlockPatch");

    private static bool _freeRoamUnlockInstalled;
    private static bool _freeRoamUnlockFailed;
    private static bool _freeRoamFieldForced;
    private static bool _freeRoamDifficultyUnhidden;

    // Enabling this before MainMenu sends Cairn through an unready path and causes a black screen.
    private static bool _freeRoamUnlockActive;

    public static bool IsFreeRoamUnlockInstalled => _freeRoamUnlockInstalled;

    /// <summary>The native flag must be reset before a launched game boots.</summary>
    public static void SetActive(bool active)
    {
        if (_freeRoamUnlockActive == active) return;
        _freeRoamUnlockActive = active;

        if (!active)
        {
            TrySetFreeRoamTweakableField(false);
            RestoreFreeRoamModeHidden();
            _freeRoamFieldForced = false;
            _freeRoamDifficultyUnhidden = false;
        }
    }

    /// <summary>
    /// Covers both access paths because Il2CppInterop can patch the public property but not the
    /// field accessor used by some native menu code.
    /// </summary>
    public static void Install()
    {
        if (_freeRoamUnlockInstalled || _freeRoamUnlockFailed) return;

        try
        {
            var postfix = new HarmonyMethod(typeof(FreeRoamUnlockPatches).GetMethod(
                nameof(FreeRoamUnlockPatches.ForceEnabledPostfix),
                BindingFlags.NonPublic | BindingFlags.Static));

            if (postfix.method == null)
            {
                _freeRoamUnlockFailed = true;
                ModLog.Warning("[FreeRoam] FreeRoam unlock: postfix method not found");
                return;
            }

            // Il2CppInterop cannot patch the field accessor, so direct readers are covered
            // by writing the field while only the public property is patched.
            var getter = AccessTools.PropertyGetter(typeof(FreeRoamTweakables),
                nameof(FreeRoamTweakables.EnableFreeRoamFeature));
            if (getter == null)
            {
                _freeRoamUnlockFailed = true;
                ModLog.Warning("[FreeRoam] FreeRoam unlock: EnableFreeRoamFeature getter not found");
                return;
            }

            FreeRoamUnlockHarmony.Patch(getter, postfix: postfix);

            // The native menu builds its buttons only once, so both flags must be correct
            // immediately before InitializeButtons runs.
            var initButtons = AccessTools.Method(
                typeof(MainMenuDifficultySelectElement),
                nameof(MainMenuDifficultySelectElement.InitializeButtons));
            if (initButtons != null)
            {
                var initPrefix = new HarmonyMethod(typeof(FreeRoamUnlockPatches).GetMethod(
                    nameof(FreeRoamUnlockPatches.InitializeButtonsPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static));
                FreeRoamUnlockHarmony.Patch(initButtons, prefix: initPrefix);
            }
            else
            {
                ModLog.Warning("[FreeRoam] FreeRoam unlock: InitializeButtons method not found (rebuild hook skipped)");
            }

            _freeRoamUnlockInstalled = true;
            ModLog.Info("[FreeRoam] FreeRoam feature unlocked (EnableFreeRoamFeature forced true)");
        }
        catch (Exception ex)
        {
            _freeRoamUnlockFailed = true;
            ModLog.Warning($"[FreeRoam] FreeRoam unlock patch install failed: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        try
        {
            SetActive(false);
            FreeRoamUnlockHarmony.UnpatchSelf();
        }
        finally
        {
            _freeRoamUnlockInstalled = false;
            _freeRoamUnlockFailed = false;
            _freeRoamFieldForced = false;
            _freeRoamDifficultyUnhidden = false;
        }
    }

    public static void TryForceTweakableField()
    {
        if (_freeRoamFieldForced || !_freeRoamUnlockActive) return;

        if (TrySetFreeRoamTweakableField(true))
        {
            _freeRoamFieldForced = true;
            ModLog.Debug("[FreeRoam] FreeRoam tweakable field forced true on instance");
        }
    }

    public static void TryUnhideDifficulty()
    {
        if (_freeRoamDifficultyUnhidden || !_freeRoamUnlockActive) return;

        if (ApplyFreeRoamModeVisible(out bool found, out int changed, out int total, out bool stillHidden))
        {
            _freeRoamDifficultyUnhidden = true;
            ModLog.Debug($"[FreeRoam] FreeRoam difficulty unhide: found={found} changed={changed} stillHidden={stillHidden} (total modes={total})");
        }
    }

    /// <summary>
    /// The IL2CPP <c>Mode</c> wrapper is a value type, so its edited boxed copy must be assigned
    /// back into the native array.
    /// </summary>
    private static bool ApplyFreeRoamModeVisible(out bool found, out int changed, out int total, out bool stillHidden)
    {
        found = false; changed = 0; total = 0; stillHidden = false;
        try
        {
            if (!Il2Cpp.TweakableBase<Il2Cpp.DifficultyTweakables>.IsReady) return false;

            var inst = Il2Cpp.TweakableBase<Il2Cpp.DifficultyTweakables>.Instance;
            var modes = inst?.modes;
            if (modes == null) return false;

            total = modes.Length;
            for (int i = 0; i < modes.Length; i++)
            {
                var m = modes[i];
                if (m == null) continue;
                if (m.difficulty != Il2Cpp.DifficultyTweakables.SelectedDifficulty.FreeRoam) continue;

                found = true;
                if (m.isHidden)
                {
                    m.isHidden = false;
                    modes[i] = m; // Il2Cpp list access returns a value-type copy.
                    changed++;
                }
                stillHidden = modes[i].isHidden;
            }
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[FreeRoam] FreeRoam difficulty unhide failed: {ex.Message}");
            return true; // A persistent native error cannot be repaired by polling every frame.
        }
    }

    private static void RestoreFreeRoamModeHidden()
    {
        try
        {
            if (!Il2Cpp.TweakableBase<Il2Cpp.DifficultyTweakables>.IsReady) return;

            var modes = Il2Cpp.TweakableBase<Il2Cpp.DifficultyTweakables>.Instance?.modes;
            if (modes == null) return;

            for (var index = 0; index < modes.Length; index++)
            {
                var mode = modes[index];
                if (mode == null ||
                    mode.difficulty != Il2Cpp.DifficultyTweakables.SelectedDifficulty.FreeRoam)
                    continue;

                mode.isHidden = true;
                modes[index] = mode;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[FreeRoam] Could not restore the vanilla Story menu: {exception.Message}");
        }
    }

    private static bool TrySetFreeRoamTweakableField(bool value)
    {
        try
        {
            if (!Il2Cpp.TweakableBase<FreeRoamTweakables>.IsReady) return false;

            var instance = Il2Cpp.TweakableBase<FreeRoamTweakables>.Instance;
            if (instance == null) return false;

            instance.enableFreeRoamFeature = value;
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[FreeRoam] FreeRoam tweakable field set({value}) failed: {ex.Message}");
            return false;
        }
    }

    private static bool _initButtonsPrefixLogged;

    private static class FreeRoamUnlockPatches
    {
        internal static void ForceEnabledPostfix(ref bool __result)
        {
            if (_freeRoamUnlockActive) __result = true;
        }

        // The native filter reads the field directly, so a property patch applied a frame
        // later cannot affect this one-time button build.
        internal static void InitializeButtonsPrefix()
        {
            if (!_freeRoamUnlockActive) return;

            bool fieldSet = TrySetFreeRoamTweakableField(true);
            ApplyFreeRoamModeVisible(out bool found, out int changed, out int total, out bool stillHidden);
            if (!_initButtonsPrefixLogged)
            {
                _initButtonsPrefixLogged = true;
                ModLog.Debug($"[FreeRoam] InitializeButtons prefix fired (fieldSet={fieldSet} FreeRoam found={found} changed={changed} stillHidden={stillHidden} total={total})");
            }
        }
    }
}
