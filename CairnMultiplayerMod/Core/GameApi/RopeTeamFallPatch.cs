using System;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Capture d'un template de piton pour la cordee (systeme repris d'Episure). On patche
/// Piton.Awake pour memoriser le premier piton instancie comme template a cloner
/// (CairnGameApi.UpdateRopeTeamAnchor). C'est plus fiable et moins couteux qu'un
/// Resources.FindObjectsOfTypeAll chaque frame (qui reste le repli dans BelaySecure).
///
/// L'ancienne approche (AddPiton + corde cosmetique + patch anti-respawn) a ete remplacee :
/// la corde NATIVE de la lifeline, clippee sur un piton mobile pose chez le partenaire, sert
/// a la fois de visuel et d'assurage (cf. BelaySecure). Plus de corde cosmetique, plus de
/// « clac » repete, et le rattrapage de chute est gere nativement.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static readonly HarmonyLib.Harmony RopeTeamHarmony = new("CairnMultiplayerMod.RopeTeam");
    private static bool _ropeTeamPatchInstalled;
    private static bool _ropeTeamPatchFailed;

    public static void InstallRopeTeamFallPatch()
    {
        if (_ropeTeamPatchInstalled || _ropeTeamPatchFailed) return;

        try
        {
            var awake = AccessTools.Method(typeof(Piton), "Awake");
            var postfix = typeof(CairnGameApi).GetMethod(
                nameof(PitonAwakePostfix),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (awake == null || postfix == null)
            {
                _ropeTeamPatchFailed = true;
                Mod.Log.Warning("[RopeTeam] Piton.Awake / postfix not found — template capture via scan only.");
                return;
            }

            RopeTeamHarmony.Patch(awake, postfix: new HarmonyMethod(postfix));
            _ropeTeamPatchInstalled = true;
            Mod.LogDebug("[RopeTeam] Piton template capture installed (Piton.Awake).");
        }
        catch (Exception ex)
        {
            _ropeTeamPatchFailed = true;
            Mod.Log.Warning($"[RopeTeam] template patch install failed: {ex.Message}");
        }
    }

    /// <summary>Memorise le premier piton instancie comme template de cordee.</summary>
    private static void PitonAwakePostfix(Piton __instance)
    {
        CapturePitonTemplate(__instance);
    }
}
