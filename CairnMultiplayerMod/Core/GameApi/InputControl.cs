using System;
using System.Text;
using UnityEngine.EventSystems;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    // Valeur de InputManager.disableInputs sauvegardee avant le gel, restauree au degel.
    private static bool _savedDisableInputs;
    // Etat NATIF reellement applique (pas l'intention) : ne bascule qu'apres un appel
    // reussi avec un InputManager non-null. Une frame ou le manager est introuvable laisse
    // l'etat inchange -> reessai la frame suivante.
    private static bool _blockApplied;
    private static bool _mapDiagLogged;

    /// <summary>
    /// Reconcilie le blocage des inputs gameplay avec <paramref name="wantBlocked"/> (= chat
    /// ouvert). Appele CHAQUE frame depuis ChatController.Update : idempotent (n'agit que sur
    /// transition), donc "chat ferme" converge TOUJOURS vers "input rendu" — meme si une frame
    /// echoue a resoudre l'InputManager, la suivante reessaie. Plus de restauration one-shot.
    ///
    /// Blocage : <c>disableInputs=true</c> + <c>SetIgnoreInputEvents(true)</c> (le grimpeur ne
    /// bouge pas pendant la frappe). Deblocage SYMETRIQUE : baisser ces deux flags ne suffit PAS
    /// a reactiver les action maps (il n'existe pas d'EnableAllMaps cote jeu) ; il faut
    /// <c>EnableInputs()</c> + <c>UpdateInputContext()</c> pour que le jeu reconstruise l'etat
    /// des maps du contexte courant. C'etait LA cause du blocage apres fermeture du chat.
    ///
    /// L'overlay IMGUI du chat continue de recevoir les frappes : il lit l'event system legacy
    /// d'Unity, et le jeu ne desactive jamais les devices clavier — donc le chat reste fermable.
    /// </summary>
    public static void ReconcileGameplayInput(bool wantBlocked)
    {
        if (wantBlocked == _blockApplied) return; // deja dans l'etat voulu

        try
        {
            var mgr = FindInputManager();
            if (mgr == null) return; // etat inchange, on reessaie la frame suivante

            if (wantBlocked)
            {
                _savedDisableInputs = mgr.disableInputs;
                mgr.disableInputs = true;
                mgr.SetIgnoreInputEvents(true);
                _blockApplied = true;
            }
            else
            {
                mgr.SetIgnoreInputEvents(false);
                mgr.disableInputs = _savedDisableInputs;
                mgr.EnableInputs();        // reactive les action maps...
                mgr.UpdateInputContext();  // ...recalculees pour le contexte courant
                _blockApplied = false;
                LogMapStatusOnce(mgr);
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] ReconcileGameplayInput failed: {ex.Message}");
        }
    }

    // Etat du blocage des maps du menu principal (panneau multijoueur ouvert).
    private static bool _menuMapsBlocked;
    private static bool _menuBlockDiagLogged;

    /// <summary>
    /// Desactive les action maps du MENU PRINCIPAL (mainMenuUIMap + gameplayUIActionMap) tant que le
    /// panneau multijoueur est ouvert : sinon les touches (Suppr, fleches, retour) naviguent en
    /// arriere-plan. Idempotent, a appeler chaque frame tant que le menu est visible (re-affirme le
    /// blocage si le jeu reactive les maps via un changement de contexte). Notre menu (souris +
    /// saisie TMP) passe par l'EventSystem (module UI), independant de ces maps -> reste interactif.
    /// </summary>
    public static void BlockMainMenuActionMaps()
    {
        try
        {
            // 1) Coupe la navigation clavier/manette de l'EventSystem (move/submit/cancel) : c'est
            //    par la que le menu reagit en arriere-plan. La souris (pointer) + la saisie TMP de
            //    notre panneau ne sont PAS des evenements de navigation -> restent actives.
            var es = EventSystem.current;
            if (es != null) es.sendNavigationEvents = false;

            // 2) Desactive aussi les action maps natives du menu (ceinture + bretelles).
            var mgr = FindInputManager();
            if (mgr != null)
            {
                mgr.mainMenuUIMap?.Disable();
                mgr.gameplayUIActionMap?.Disable();
            }

            _menuMapsBlocked = true;

            if (!_menuBlockDiagLogged)
            {
                _menuBlockDiagLogged = true;
                Mod.Log.Msg($"[CairnGameApi] menu input block: eventSystem={(es != null)} inputManager={(mgr != null)}");
                if (mgr != null)
                {
                    try
                    {
                        var maps = mgr.GetMapsEnabledAndDisabled();
                        if (maps != null)
                        {
                            var sb = new StringBuilder();
                            foreach (var kv in maps) sb.Append(kv.Key).Append('=').Append(kv.Value ? "ON" : "off").Append("  ");
                            Mod.Log.Msg($"[CairnGameApi] action maps: {sb}");
                        }
                    }
                    catch (Exception ex2) { Mod.Log.Msg($"[CairnGameApi] map enum failed: {ex2.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] BlockMainMenuActionMaps failed: {ex.Message}");
        }
    }

    /// <summary>Restaure la navigation + les maps du menu a la fermeture du panneau.</summary>
    public static void RestoreMainMenuActionMaps()
    {
        if (!_menuMapsBlocked) return;
        try
        {
            var es = EventSystem.current;
            if (es != null) es.sendNavigationEvents = true;

            var mgr = FindInputManager();
            if (mgr != null)
            {
                mgr.EnableInputs();
                mgr.UpdateInputContext();
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] RestoreMainMenuActionMaps failed: {ex.Message}");
        }
        _menuMapsBlocked = false;
        _menuBlockDiagLogged = false;
    }

    /// <summary>
    /// Failsafe panique (touche F10) : force le deblocage total quel que soit l'etat interne.
    /// Independant du chat. A appeler depuis un raccourci lu sur le device clavier brut (jamais
    /// affecte par le blocage), place avant tout return anticipe d'OnUpdate.
    /// </summary>
    public static void ForceClearInputBlock()
    {
        try
        {
            var mgr = FindInputManager();
            if (mgr != null)
            {
                mgr.SetIgnoreInputEvents(false);
                mgr.disableInputs = false;
                mgr.EnableInputs();
                mgr.UpdateInputContext();
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] ForceClearInputBlock failed: {ex.Message}");
        }
        _blockApplied = false;
    }

    // Diagnostic une seule fois par session : apres un deblocage, confirme que les action maps
    // gameplay sont bien reactivees. Si elles restent OFF, le restore est insuffisant (a
    // escalader vers PushInputContext). Defensif : si l'iteration du dict il2cpp echoue, on skip.
    private static void LogMapStatusOnce(Il2Cpp.InputManager mgr)
    {
        if (_mapDiagLogged) return;
        _mapDiagLogged = true;
        try
        {
            var maps = mgr.GetMapsEnabledAndDisabled();
            if (maps == null) return;
            int enabled = 0, total = 0;
            var on = new StringBuilder();
            foreach (var kv in maps)
            {
                total++;
                if (kv.Value)
                {
                    enabled++;
                    if (on.Length < 220) on.Append(kv.Key).Append(' ');
                }
            }
            Mod.LogDebug($"[CairnGameApi] after-unblock action maps enabled {enabled}/{total}: {on}");
        }
        catch (Exception ex)
        {
            Mod.LogDebug($"[CairnGameApi] map-status diag skipped: {ex.Message}");
        }
    }
}
