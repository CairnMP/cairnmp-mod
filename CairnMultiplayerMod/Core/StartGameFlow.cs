using CairnMultiplayer.Shared;
using CairnMultiplayerMod.UI;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public partial class Mod
{
    // Champs
    private ServerStartGame? _pendingStart;
    private float _pendingStartRetryTimer;
    private int _pendingStartRetries;

    // Options de nouvelle partie a RE-APPLIQUER chaque frame tant qu'on est au menu : le flux natif
    // de creation de save reecrit newGameOptions (skip=false par defaut) au moment du clic "nouvelle
    // partie", APRES notre pre-reglage. On le force donc en continu pour qu'il soit bon au moment ou
    // le jeu le lit. Efface des qu'on quitte le menu (chargement).
    private ServerStartGame? _forceNewGameOpts;

    /// <summary>
    /// Pilote le MainMenu vers le menu de sauvegarde natif du jeu lors d'un lancement
    /// autoritaire par l'hote. Appele chaque frame depuis OnUpdate().
    /// <para>
    /// Le mod ne charge plus aucune sauvegarde : il amene simplement chaque joueur dans
    /// le menu de save natif (StoryModeManageSave), exactement comme en solo. Chaque joueur
    /// y cree une nouvelle partie ou choisit une save existante lui-meme.
    ///   Phase 1 (tentatives 1-3, espacees de 0.5s) : force ModeSelect pour que le menu
    ///       soit dans le bon etat.
    ///   Phase 2 (tentative 4) : pre-regle la difficulte de la prochaine nouvelle partie
    ///       puis ouvre le menu de save natif, et s'arrete.
    /// </para>
    /// </summary>
    private void TickStartGameFlow()
    {
        // Ré-applique les options de nouvelle partie en continu tant qu'on est au menu (le flux
        // natif les réécrit au clic "nouvelle partie"). Stop dès qu'on quitte le menu.
        if (_forceNewGameOpts.HasValue)
        {
            if (_currentScene != null && _currentScene.StartsWith("MainMenu"))
            {
                var s = _forceNewGameOpts.Value;
                // On respecte la difficulte choisie par le joueur dans l'ecran natif
                // (FreeRoam inclus) : on ne force QUE les flags skip/assist, jamais la
                // difficulte. Sinon ce re-forçage par frame ecraserait le choix natif.
                CairnGameApi.SetNextGameSkipOptions(
                    s.SkipTutorials, s.SkipPractice, s.AssistEnabled, verbose: false);
            }
            else
            {
                _forceNewGameOpts = null;
            }
        }

        if (_pendingStart.HasValue && _currentScene != null && _currentScene.StartsWith("MainMenu"))
        {
            // CRUCIAL : a l'ouverture du panneau MP, le composant `MainMenu` a ete DESACTIVE
            // (MainMenuMultiplayerButton.SuspendMainMenuInput -> behaviour.enabled=false). Or
            // c'est SA boucle Update() qui lit `ForceStepTransition` et execute TransitionToStep.
            // Tant qu'il est desactive, nos ForceMainMenuStep n'ont AUCUN effet -> menu de save
            // vide et joueur bloque. On le reactive donc avant de piloter les etapes (idempotent).
            MainMenuMultiplayerButton.RestoreMainMenuInput();

            _pendingStartRetryTimer += Time.unscaledDeltaTime;

            // Phase 1 : amener le menu a ModeSelect
            if (_pendingStartRetries < 3 && _pendingStartRetryTimer >= 0.5f)
            {
                _pendingStartRetryTimer = 0f;
                _pendingStartRetries++;
                CairnGameApi.ForceMainMenuStep(CairnGameApi.MainMenuStep.ModeSelect);
                LoggerInstance.Msg($"[StartGame] Force ModeSelect (retry #{_pendingStartRetries})");
            }
            // Phase 2 : pre-regler la difficulte puis ouvrir le menu de save natif
            else if (_pendingStartRetries == 3 && _pendingStartRetryTimer >= 0.5f)
            {
                var start = _pendingStart.Value;
                // Respect du choix natif : on ne pre-regle PAS la difficulte, on force
                // seulement les flags skip/assist (le joueur choisit sa difficulte dans
                // l'ecran natif, FreeRoam compris).
                CairnGameApi.SetNextGameSkipOptions(start.SkipTutorials, start.SkipPractice, start.AssistEnabled);

                // Continue de forcer ces options chaque frame jusqu'au depart du menu (cf. champ).
                _forceNewGameOpts = start;

                CairnGameApi.ForceMainMenuStep(CairnGameApi.MainMenuStep.StoryModeManageSave);
                LoggerInstance.Msg("[StartGame] Opened native save menu — player chooses new/existing (options re-applied each frame)");

                _pendingStart = null;
                _pendingStartRetries = 0;
                _pendingStartRetryTimer = 0f;
                _panel.Hide();
            }
        }
    }
}
