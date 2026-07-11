using System;
using CairnMultiplayer.Shared;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    /// <summary>
    /// Pré-règle les options de la prochaine nouvelle partie (difficulté, skip tutoriel/practice,
    /// assist) sur le MainMenu natif, via l'API TYPÉE Il2CppInterop (pas d'arithmétique de pointeurs).
    ///
    /// nextGameStartOptions.newGameOptions est une STRUCT (NewGameLaunchOptions) : on en lit une
    /// copie, on modifie les champs, puis on la réécrit via le setter. GameDifficulty (Shared) a
    /// déjà les mêmes valeurs hashées que SelectedDifficulty natif -> cast direct.
    /// </summary>
    public static bool SetNextGameDifficulty(GameDifficulty difficulty,
        bool skipTutorials, bool skipPractice, bool assistEnabled, bool verbose = true)
    {
        try
        {
            var menu = FindMainMenuComponent();
            if (menu == null)
            {
                Mod.Log.Warning("[CairnGameApi] MainMenu component not found");
                return false;
            }

            var opts = menu.nextGameStartOptions;
            if (opts == null)
            {
                Mod.Log.Warning("[CairnGameApi] nextGameStartOptions is null on MainMenu");
                return false;
            }

            // Struct -> copie locale, modif, réécriture via le setter.
            var ng = opts.newGameOptions;
            var targetDifficulty = (DifficultyTweakables.SelectedDifficulty)(int)difficulty;

            // Idempotence CRUCIALE : ce setter est appele CHAQUE frame tant que le menu de
            // save natif est ouvert (StartGameFlow). Reecrire la meme valeur fait emettre au
            // jeu une notification "difficulte changee" en boucle. On ne reecrit donc que si
            // au moins un champ differe reellement (typiquement apres que le flux natif a
            // remis newGameOptions a ses defauts au clic "nouvelle partie").
            if (ng.currentSelectedDifficulty == targetDifficulty
                && ng.skipTutorials == skipTutorials
                && ng.skipPractice == skipPractice
                && ng.assistEnabled == assistEnabled)
            {
                return true;
            }

            ng.skipTutorials = skipTutorials;
            ng.skipPractice = skipPractice;
            ng.assistEnabled = assistEnabled;
            ng.currentSelectedDifficulty = targetDifficulty;
            opts.newGameOptions = ng;

            if (verbose)
            {
                // Relit pour confirmer (les struct Il2Cpp peuvent surprendre).
                var check = opts.newGameOptions;
                Mod.LogDebug($"[CairnGameApi] NewGameOptions set (typed): difficulty={difficulty} " +
                    $"skipTut={check.skipTutorials} skipPra={check.skipPractice} assist={check.assistEnabled}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[CairnGameApi] SetNextGameDifficulty failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Variante de <see cref="SetNextGameDifficulty"/> qui force UNIQUEMENT les flags
    /// skip tutoriel/practice + assist, en PRESERVANT la difficulte choisie par le joueur
    /// dans l'ecran natif. Sert au flux de lancement quand on veut respecter le choix de
    /// difficulte natif (ex. FreeRoam) au lieu de le forcer : le flux natif reinitialise les
    /// flags skip a chaque "nouvelle partie", donc on les ré-applique en continu, mais sans
    /// jamais reecrire currentSelectedDifficulty.
    /// </summary>
    public static bool SetNextGameSkipOptions(bool skipTutorials, bool skipPractice,
        bool assistEnabled, bool verbose = true)
    {
        try
        {
            var menu = FindMainMenuComponent();
            if (menu == null) return false;

            var opts = menu.nextGameStartOptions;
            if (opts == null) return false;

            var ng = opts.newGameOptions;

            // Idempotence : ne réécrire que si un flag skip/assist diffère (la difficulté
            // n'est jamais comparée ni touchée — elle reste celle du joueur).
            if (ng.skipTutorials == skipTutorials
                && ng.skipPractice == skipPractice
                && ng.assistEnabled == assistEnabled)
            {
                return true;
            }

            ng.skipTutorials = skipTutorials;
            ng.skipPractice = skipPractice;
            ng.assistEnabled = assistEnabled;
            opts.newGameOptions = ng;

            if (verbose)
            {
                var check = opts.newGameOptions;
                Mod.LogDebug($"[CairnGameApi] NewGameOptions skip-only set: skipTut={check.skipTutorials} " +
                    $"skipPra={check.skipPractice} assist={check.assistEnabled} (difficulty preserved={check.currentSelectedDifficulty})");
            }
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[CairnGameApi] SetNextGameSkipOptions failed: {ex}");
            return false;
        }
    }

    /// <summary>Trouve le composant MainMenu (UI) actif dans la scène.</summary>
    private static Il2CppTheGameBakers.Cairn.UI.MainMenu FindMainMenuComponent()
    {
        var menuGo = GameObject.Find("MainMenu");
        if (menuGo == null) return null;

        var menu = menuGo.GetComponent<Il2CppTheGameBakers.Cairn.UI.MainMenu>();
        if (menu != null) return menu;

        // Repli : parcours des composants avec TryCast (selon l'enregistrement du type Il2Cpp).
        var components = menuGo.GetComponents<MonoBehaviour>();
        for (int i = 0; i < components.Count; i++)
        {
            var cast = components[i] != null
                ? components[i].TryCast<Il2CppTheGameBakers.Cairn.UI.MainMenu>() : null;
            if (cast != null) return cast;
        }
        return null;
    }
}
