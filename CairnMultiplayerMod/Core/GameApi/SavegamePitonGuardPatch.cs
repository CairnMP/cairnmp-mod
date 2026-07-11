using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Filet de securite sauvegarde (feedback Radi3nt, 1.36).
///
/// Un piton dans un etat incoherent — piton distant spawne par le mod avec un
/// ClimbingSetting non renseigne, ou piton laisse dans un etat bancal apres un clip-in
/// puis un recall — fait lever une NullReferenceException dans Piton.WriteToSavegame.
/// Comme la sauvegarde native serialise les pitons un par un depuis
/// PawnControllerSwitcher.WriteToSavegame, cette seule NRE fait ECHOUER TOUTE la
/// sauvegarde (« Save FAILED »), avec perte de progression — meme apres avoir rappele
/// tous les pitons si une entree fantome subsiste.
///
/// Ce finalizer Harmony avale l'exception de Piton.WriteToSavegame : la sauvegarde
/// saute le piton fautif et CONTINUE au lieu d'avorter. Il est INDEPENDANT de la cause
/// racine (quel que soit le champ null), donc il couvre aussi le cas « meme apres recall »
/// que le correctif ClimbingSetting seul ne garantit pas.
///
/// Compromis assume : le piton fautif peut etre partiellement/pas serialise. Ce sont en
/// pratique des pitons distants (ghosts d'un autre joueur) qui n'ont rien a faire dans
/// TA sauvegarde de toute facon. Sauvegarde-qui-aboutit > sauvegarde-qui-echoue.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static readonly HarmonyLib.Harmony SaveGuardHarmony = new("CairnMultiplayerMod.SaveGuard");
    private static bool _saveGuardInstalled;
    private static bool _saveGuardFailed;
    private static float _lastSaveGuardLogAt;
    private const float SaveGuardLogIntervalSeconds = 2f;

    public static void InstallSavegamePitonGuardPatch()
    {
        if (_saveGuardInstalled || _saveGuardFailed) return;

        try
        {
            var target = AccessTools.Method(typeof(Piton), "WriteToSavegame");
            var finalizer = typeof(CairnGameApi).GetMethod(
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
            // Msg (pas LogDebug) : l'utilisateur doit pouvoir confirmer que la protection est active.
            Mod.Log.Msg("[SaveGuard] Protection sauvegarde active : une NRE sur un piton ne fera plus echouer la sauvegarde.");
        }
        catch (Exception ex)
        {
            _saveGuardFailed = true;
            Mod.Log.Warning($"[SaveGuard] installation echouee : {ex.Message}");
        }
    }

    /// <summary>
    /// Avale toute exception levee par Piton.WriteToSavegame pour ne pas faire echouer la
    /// sauvegarde entiere. Retourner null = exception supprimee, la sauvegarde continue.
    /// </summary>
    private static Exception PitonWriteToSavegameFinalizer(Exception __exception)
    {
        if (__exception == null) return null;

        // Visible (Warning, pas LogDebug gate par VerboseLogging) : si ca se declenche,
        // l'utilisateur DOIT le voir dans la console — contrairement au crash silencieux d'avant.
        var now = Time.unscaledTime;
        if (now - _lastSaveGuardLogAt >= SaveGuardLogIntervalSeconds)
        {
            _lastSaveGuardLogAt = now;
            Mod.Log.Warning($"[SaveGuard] Erreur de sauvegarde d'un piton supprimee pour proteger la sauvegarde : {__exception.GetType().Name}");
        }
        return null;
    }
}
