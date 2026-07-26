using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Roping;

/// <summary>
/// Save safety net (Radi3nt feedback, 1.36).
///
/// A piton in an inconsistent state — a remote piton spawned by the mod with an
/// unset ClimbingSetting, or a piton left in a wobbly state after a clip-in
/// then a recall — raises a NullReferenceException in Piton.WriteToSavegame.
/// Since the native save serializes pitons one by one from
/// PawnControllerSwitcher.WriteToSavegame, this single NRE makes the ENTIRE
/// save FAIL ("Save FAILED"), with loss of progress — even after recalling
/// all pitons if a ghost entry remains.
///
/// This Harmony finalizer swallows the exception from Piton.WriteToSavegame: the save
/// skips the offending piton and CONTINUES instead of aborting. It is INDEPENDENT of the
/// root cause (whatever field is null), so it also covers the "even after recall" case
/// that the ClimbingSetting fix alone doesn't guarantee.
///
/// Accepted trade-off: the offending piton may be partially/not serialized. In
/// practice these are remote pitons (another player's ghosts) that have no business being in
/// YOUR save anyway. A save-that-succeeds > a save-that-fails.
/// </summary>
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
                Mod.Log.Warning("[SaveGuard] Piton.WriteToSavegame introuvable — protection de sauvegarde NON installee.");
                return;
            }

            SaveGuardHarmony.Patch(target, finalizer: new HarmonyMethod(finalizer));
            _saveGuardInstalled = true;
            // Msg (not LogDebug): the user must be able to confirm the protection is active.
            Mod.Log.Msg("[SaveGuard] Protection sauvegarde active : une NRE sur un piton ne fera plus echouer la sauvegarde.");
        }
        catch (Exception ex)
        {
            _saveGuardFailed = true;
            Mod.Log.Warning($"[SaveGuard] installation echouee : {ex.Message}");
        }
    }

    /// <summary>
    /// Swallows any exception raised by Piton.WriteToSavegame so it doesn't fail the
    /// entire save. Returning null = exception suppressed, the save continues.
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
            Mod.Log.Warning($"[SaveGuard] Erreur de sauvegarde d'un piton supprimee pour proteger la sauvegarde : {__exception.GetType().Name}");
        }
        return null;
    }
}
