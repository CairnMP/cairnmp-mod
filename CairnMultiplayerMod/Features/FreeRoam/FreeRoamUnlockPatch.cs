using System;
using System.Reflection;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;

namespace CairnMultiplayerMod.Features.FreeRoam;

internal static unsafe class FreeRoamUnlockPatch
{
    private static readonly HarmonyLib.Harmony FreeRoamUnlockHarmony =
        new("CairnMultiplayerMod.FreeRoamUnlockPatch");

    private static bool _freeRoamUnlockInstalled;
    private static bool _freeRoamUnlockFailed;
    private static bool _freeRoamFieldForced;
    private static bool _freeRoamDifficultyUnhidden;

    // CRUCIAL GATE: we only force the flag WHILE in the MainMenu. Forcing
    // EnableFreeRoamFeature=true from boot sends the game down a FreeRoam init path
    // that isn't ready (intro/logo scene) -> black screen. During boot this flag stays false, so
    // the native getter returns its real value and startup is normal.
    private static bool _freeRoamUnlockActive;

    public static bool IsFreeRoamUnlockInstalled => _freeRoamUnlockInstalled;

    /// <summary>
    /// Enables/disables the FreeRoam unlock. Called on scene transitions: true at the
    /// MainMenu, false everywhere else. When disabling, we reset the tweakable field to
    /// false so we don't contaminate the boot of an actually launched game.
    /// </summary>
    public static void SetFreeRoamUnlockActive(bool active)
    {
        if (_freeRoamUnlockActive == active) return;
        _freeRoamUnlockActive = active;

        if (!active)
        {
            TrySetFreeRoamTweakableField(false);
            _freeRoamFieldForced = false;        // can re-force on the next menu pass
            _freeRoamDifficultyUnhidden = false; // same for unhiding the mode
        }
    }

    /// <summary>
    /// Re-enables the FreeRoam feature that the game cut off. In a retail build, the native getter
    /// <c>FreeRoamTweakables.EnableFreeRoamFeature</c> returns false, which hides the
    /// <c>SelectedDifficulty.FreeRoam</c> difficulty (= 418187680) from the main menu even
    /// though all the content (FreeRoamManager, warp points, Eagle Eye UI) is present.
    ///
    /// Two complementary levers because we don't know which one the menu queries:
    ///   1. Harmony postfix on the public PROPERTY <c>EnableFreeRoamFeature</c> (a real
    ///      native method, patchable) -> always returns true.
    ///   2. direct write of the <c>enableFreeRoamFeature</c> field on the tweakable instance
    ///      (cf. <see cref="TryForceFreeRoamTweakableField"/>), because the field accessor
    ///      <c>get_enableFreeRoamFeature</c> is NOT patchable by Il2CppInterop.
    /// </summary>
    public static void InstallFreeRoamUnlockPatch()
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
                Mod.Log.Warning("[FreeRoam] FreeRoam unlock: postfix method not found");
                return;
            }

            // Only the public property is patchable; the field accessor throws a
            // "field accessor can't be patched" error on the Il2CppInterop side -> we don't attempt it, and
            // lever #2 (writing the field) covers direct readers of the field.
            var getter = AccessTools.PropertyGetter(typeof(FreeRoamTweakables),
                nameof(FreeRoamTweakables.EnableFreeRoamFeature));
            if (getter == null)
            {
                _freeRoamUnlockFailed = true;
                Mod.Log.Warning("[FreeRoam] FreeRoam unlock: EnableFreeRoamFeature getter not found");
                return;
            }

            FreeRoamUnlockHarmony.Patch(getter, postfix: postfix);

            // Lever #3: prefix on InitializeButtons -> guarantees the data (flag +
            // FreeRoam mode's isHidden) is correct JUST BEFORE the native code (re)builds
            // the difficulty buttons. Without this, the buttons are built once before our
            // unhiding and FreeRoam stays absent from the UI even if the data is correct.
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
                Mod.Log.Warning("[FreeRoam] FreeRoam unlock: InitializeButtons method not found (rebuild hook skipped)");
            }

            _freeRoamUnlockInstalled = true;
            Mod.Log.Msg("[FreeRoam] FreeRoam feature unlocked (EnableFreeRoamFeature forced true)");
        }
        catch (Exception ex)
        {
            _freeRoamUnlockFailed = true;
            Mod.Log.Warning($"[FreeRoam] FreeRoam unlock patch install failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lever #2: forces the <c>enableFreeRoamFeature</c> field to true on the tweakable
    /// instance as soon as it's loaded (from the addressable). Idempotent and best-effort:
    /// call every frame while in the menu until it succeeds. Covers the
    /// case where the menu reads the field directly (unpatchable field accessor).
    /// </summary>
    public static void TryForceFreeRoamTweakableField()
    {
        if (_freeRoamFieldForced || !_freeRoamUnlockActive) return;

        if (TrySetFreeRoamTweakableField(true))
        {
            _freeRoamFieldForced = true;
            Mod.LogDebug("[FreeRoam] FreeRoam tweakable field forced true on instance");
        }
    }

    /// <summary>
    /// Unhides the FreeRoam difficulty in the menu: the mode is present in the
    /// <c>DifficultyTweakables.modes</c> list but with <c>isHidden = true</c>, so the predicate of
    /// <c>MainMenuDifficultySelectElement.InitializeButtons()</c> excludes it. We set its
    /// <c>isHidden</c> to false (Mode is a reference type -> the edit persists in the array).
    /// Call every frame in the menu until it succeeds. Diagnostic log: indicates whether the mode exists
    /// in the list and how many modes there are in total.
    /// </summary>
    public static void TryUnhideFreeRoamDifficulty()
    {
        if (_freeRoamDifficultyUnhidden || !_freeRoamUnlockActive) return;

        if (ApplyFreeRoamModeVisible(out bool found, out int changed, out int total, out bool stillHidden))
        {
            _freeRoamDifficultyUnhidden = true;
            Mod.LogDebug($"[FreeRoam] FreeRoam difficulty unhide: found={found} changed={changed} stillHidden={stillHidden} (total modes={total})");
        }
    }

    /// <summary>
    /// Core of the unhiding: sets <c>isHidden=false</c> on the FreeRoam Mode(s) in
    /// <c>DifficultyTweakables.modes</c>. Ungated (re-appliable on each call) for the
    /// InitializeButtons prefix. Returns false if the instance isn't ready yet.
    ///
    /// IMPORTANT: <c>Mode</c> is a VALUE TYPE (<c>sealed class Mode : Il2CppSystem.ValueType</c>).
    /// The indexer <c>modes[i]</c> returns a boxed COPY -> editing <c>m.isHidden</c> doesn't touch
    /// the array element. You MUST reassign <c>modes[i] = m</c> for the edit to persist.
    /// <paramref name="stillHidden"/> = verification read-back after reassignment.
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
                    modes[i] = m;             // MANDATORY reassignment (value type)
                    changed++;
                }
                // Read back from the array (new copy) to verify persistence.
                stillHidden = modes[i].isHidden;
            }
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[FreeRoam] FreeRoam difficulty unhide failed: {ex.Message}");
            return true; // don't loop indefinitely on error
        }
    }

    /// <summary>Writes <c>enableFreeRoamFeature = value</c> on the tweakable instance if
    /// it is loaded. Returns true if the write took place.</summary>
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
            Mod.Log.Warning($"[FreeRoam] FreeRoam tweakable field set({value}) failed: {ex.Message}");
            return false;
        }
    }

    private static bool _initButtonsPrefixLogged;

    private static class FreeRoamUnlockPatches
    {
        // Forces the FreeRoam feature active ONLY when the menu gate is armed (cf.
        // _freeRoamUnlockActive). Outside the menu (boot, gameplay), we leave the real value.
        internal static void ForceEnabledPostfix(ref bool __result)
        {
            if (_freeRoamUnlockActive) __result = true;
        }

        // Before EACH build of the difficulty buttons: we make sure ALL the
        // data read by the filtering predicate is correct BEFORE the native code runs:
        //   1. the enableFreeRoamFeature FIELD = true (the predicate reads the field directly,
        //      NOT the patched property -> it must be forced here, not a frame later);
        //   2. the FreeRoam mode unhidden (isHidden = false).
        internal static void InitializeButtonsPrefix()
        {
            bool fieldSet = TrySetFreeRoamTweakableField(true);
            ApplyFreeRoamModeVisible(out bool found, out int changed, out int total, out bool stillHidden);
            if (!_initButtonsPrefixLogged)
            {
                _initButtonsPrefixLogged = true;
                Mod.LogDebug($"[FreeRoam] InitializeButtons prefix fired (fieldSet={fieldSet} FreeRoam found={found} changed={changed} stillHidden={stillHidden} total={total})");
            }
        }
    }
}
