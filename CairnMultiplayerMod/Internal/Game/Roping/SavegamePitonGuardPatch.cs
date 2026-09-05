using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>Reports piton save failures without hiding a failed or partial save.</summary>
internal static unsafe class SavegamePitonGuardPatch
{
    private static readonly HarmonyLib.Harmony SaveGuardHarmony = new("CairnMultiplayerMod.SaveGuard");
    private static bool _saveGuardInstalled;
    private static bool _saveGuardFailed;
    private static float _lastSaveGuardLogAt;
    private const float SaveGuardLogIntervalSeconds = 2f;

    public static void Install()
    {
        if (_saveGuardInstalled || _saveGuardFailed) return;

        try
        {
            var target = AccessTools.Method(typeof(Piton), "WriteToSavegame");
            var finalizer = typeof(SavegamePitonGuardPatch).GetMethod(
                nameof(PitonWriteToSavegameFinalizer),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (target == null || finalizer == null)
            {
                _saveGuardFailed = true;
                ModLog.Warning("[SaveGuard] Piton.WriteToSavegame introuvable — protection de sauvegarde NON installee.");
                return;
            }

            SaveGuardHarmony.Patch(target, finalizer: new HarmonyMethod(finalizer));
            _saveGuardInstalled = true;
            // Msg (not LogDebug): the user must be able to confirm the protection is active.
            ModLog.Info("[SaveGuard] Diagnostic sauvegarde actif : les erreurs restent visibles et interrompent la sauvegarde.");
        }
        catch (Exception ex)
        {
            _saveGuardFailed = true;
            ModLog.Warning($"[SaveGuard] installation echouee : {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        try { SaveGuardHarmony.UnpatchSelf(); }
        finally
        {
            _saveGuardInstalled = false;
            _saveGuardFailed = false;
            _lastSaveGuardLogAt = 0f;
        }
    }

    /// <summary>
    /// Reports any exception raised by Piton.WriteToSavegame without concealing the
    /// failed save. Never skip a partially written entry or report success after failure.
    /// </summary>
    private static Exception PitonWriteToSavegameFinalizer(Exception __exception)
    {
        if (__exception == null) return null;

        // Visible (Warning, not LogDebug gated by VerboseLogging): if this fires,
        // the user MUST see it in the console — unlike the silent crash before.
        var now = Time.unscaledTime;
        if (now - _lastSaveGuardLogAt >= SaveGuardLogIntervalSeconds)
        {
            _lastSaveGuardLogAt = now;
            ModLog.Warning($"[SaveGuard] Echec de sauvegarde d'un piton (erreur conservee) : {__exception.GetType().Name}");
        }
        return __exception;
    }
}
