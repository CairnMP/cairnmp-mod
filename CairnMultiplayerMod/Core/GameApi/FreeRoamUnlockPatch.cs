using System;
using System.Reflection;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    private static readonly HarmonyLib.Harmony FreeRoamUnlockHarmony =
        new("CairnMultiplayerMod.FreeRoamUnlockPatch");

    private static bool _freeRoamUnlockInstalled;
    private static bool _freeRoamUnlockFailed;
    private static bool _freeRoamFieldForced;
    private static bool _freeRoamDifficultyUnhidden;

    // GATE CRUCIAL : on ne force le flag QUE pendant le MainMenu. Forcer
    // EnableFreeRoamFeature=true des le boot envoie le jeu sur un chemin d'init FreeRoam
    // pas pret (scene intro/logo) -> ecran noir. Pendant le boot ce flag reste false, donc
    // le getter natif renvoie sa vraie valeur et le demarrage est normal.
    private static bool _freeRoamUnlockActive;

    public static bool IsFreeRoamUnlockInstalled => _freeRoamUnlockInstalled;

    /// <summary>
    /// Active/desactive le deblocage FreeRoam. Appele sur transition de scene : true au
    /// MainMenu, false partout ailleurs. En desactivant, on remet le champ du tweakable a
    /// false pour ne pas contaminer le boot d'une partie reellement lancee.
    /// </summary>
    public static void SetFreeRoamUnlockActive(bool active)
    {
        if (_freeRoamUnlockActive == active) return;
        _freeRoamUnlockActive = active;

        if (!active)
        {
            TrySetFreeRoamTweakableField(false);
            _freeRoamFieldForced = false;        // pourra re-forcer au prochain passage menu
            _freeRoamDifficultyUnhidden = false; // idem pour le demasquage du mode
        }
    }

    /// <summary>
    /// Reactive la feature FreeRoam coupee du jeu. En build retail, le getter natif
    /// <c>FreeRoamTweakables.EnableFreeRoamFeature</c> renvoie false, ce qui masque la
    /// difficulte <c>SelectedDifficulty.FreeRoam</c> (= 418187680) du menu principal alors
    /// que tout le contenu (FreeRoamManager, warp points, UI Eagle Eye) est present.
    ///
    /// Deux leviers complementaires car on ignore lequel le menu interroge :
    ///   1. postfix Harmony sur la PROPRIETE publique <c>EnableFreeRoamFeature</c> (vraie
    ///      methode native, patchable) -> renvoie toujours true.
    ///   2. ecriture directe du champ <c>enableFreeRoamFeature</c> sur l'instance du tweakable
    ///      (cf. <see cref="TryForceFreeRoamTweakableField"/>), car le field accessor
    ///      <c>get_enableFreeRoamFeature</c> n'est PAS patchable par Il2CppInterop.
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
                Mod.Log.Warning("[CairnGameApi] FreeRoam unlock: postfix method not found");
                return;
            }

            // Seule la propriete publique est patchable ; le field accessor lance une erreur
            // "field accessor can't be patched" cote Il2CppInterop -> on ne le tente pas, le
            // levier #2 (ecriture du champ) couvre les lecteurs directs du champ.
            var getter = AccessTools.PropertyGetter(typeof(FreeRoamTweakables),
                nameof(FreeRoamTweakables.EnableFreeRoamFeature));
            if (getter == null)
            {
                _freeRoamUnlockFailed = true;
                Mod.Log.Warning("[CairnGameApi] FreeRoam unlock: EnableFreeRoamFeature getter not found");
                return;
            }

            FreeRoamUnlockHarmony.Patch(getter, postfix: postfix);

            // Levier #3 : prefix sur InitializeButtons -> garantit que la donnee (flag +
            // isHidden du mode FreeRoam) est correcte JUSTE AVANT que le natif (re)construise
            // les boutons de difficulte. Sans ca, les boutons sont batis une fois avant notre
            // demasquage et FreeRoam reste absent de l'UI meme si la donnee est bonne.
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
                Mod.Log.Warning("[CairnGameApi] FreeRoam unlock: InitializeButtons method not found (rebuild hook skipped)");
            }

            _freeRoamUnlockInstalled = true;
            Mod.Log.Msg("[CairnGameApi] FreeRoam feature unlocked (EnableFreeRoamFeature forced true)");
        }
        catch (Exception ex)
        {
            _freeRoamUnlockFailed = true;
            Mod.Log.Warning($"[CairnGameApi] FreeRoam unlock patch install failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Levier #2 : force le champ <c>enableFreeRoamFeature</c> a true sur l'instance du
    /// tweakable des qu'elle est chargee (depuis l'addressable). Idempotent et best-effort :
    /// a appeler chaque frame tant qu'on est au menu jusqu'a ce que ca reussisse. Couvre le
    /// cas ou le menu lit le champ directement (field accessor impatchable).
    /// </summary>
    public static void TryForceFreeRoamTweakableField()
    {
        if (_freeRoamFieldForced || !_freeRoamUnlockActive) return;

        if (TrySetFreeRoamTweakableField(true))
        {
            _freeRoamFieldForced = true;
            Mod.LogDebug("[CairnGameApi] FreeRoam tweakable field forced true on instance");
        }
    }

    /// <summary>
    /// Demasque la difficulte FreeRoam dans le menu : le mode est present dans la liste
    /// <c>DifficultyTweakables.modes</c> mais avec <c>isHidden = true</c>, donc le predicat de
    /// <c>MainMenuDifficultySelectElement.InitializeButtons()</c> l'exclut. On met son
    /// <c>isHidden</c> a false (Mode est un type reference -> l'edition persiste dans le tableau).
    /// A appeler chaque frame au menu jusqu'a succes. Log diagnostic : indique si le mode existe
    /// dans la liste et combien de modes au total.
    /// </summary>
    public static void TryUnhideFreeRoamDifficulty()
    {
        if (_freeRoamDifficultyUnhidden || !_freeRoamUnlockActive) return;

        if (ApplyFreeRoamModeVisible(out bool found, out int changed, out int total, out bool stillHidden))
        {
            _freeRoamDifficultyUnhidden = true;
            Mod.LogDebug($"[CairnGameApi] FreeRoam difficulty unhide: found={found} changed={changed} stillHidden={stillHidden} (total modes={total})");
        }
    }

    /// <summary>
    /// Coeur du demasquage : met <c>isHidden=false</c> sur le(s) Mode(s) FreeRoam de
    /// <c>DifficultyTweakables.modes</c>. Non garde (re-appliquable a chaque appel) pour le
    /// prefix de InitializeButtons. Renvoie false si l'instance n'est pas encore prete.
    ///
    /// IMPORTANT : <c>Mode</c> est un TYPE VALEUR (<c>sealed class Mode : Il2CppSystem.ValueType</c>).
    /// L'indexeur <c>modes[i]</c> renvoie une COPIE boxee -> editer <c>m.isHidden</c> ne touche pas
    /// l'element du tableau. Il FAUT reaffecter <c>modes[i] = m</c> pour que l'edition persiste.
    /// <paramref name="stillHidden"/> = relecture de verification apres reaffectation.
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
                    modes[i] = m;             // reaffectation OBLIGATOIRE (type valeur)
                    changed++;
                }
                // Relecture depuis le tableau (nouvelle copie) pour verifier la persistance.
                stillHidden = modes[i].isHidden;
            }
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] FreeRoam difficulty unhide failed: {ex.Message}");
            return true; // ne pas boucler indefiniment sur erreur
        }
    }

    /// <summary>Ecrit <c>enableFreeRoamFeature = value</c> sur l'instance du tweakable si
    /// elle est chargee. Renvoie true si l'ecriture a eu lieu.</summary>
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
            Mod.Log.Warning($"[CairnGameApi] FreeRoam tweakable field set({value}) failed: {ex.Message}");
            return false;
        }
    }

    private static bool _initButtonsPrefixLogged;

    private static class FreeRoamUnlockPatches
    {
        // Force la feature FreeRoam active UNIQUEMENT quand le gate menu est arme (cf.
        // _freeRoamUnlockActive). Hors menu (boot, gameplay), on laisse la vraie valeur.
        internal static void ForceEnabledPostfix(ref bool __result)
        {
            if (_freeRoamUnlockActive) __result = true;
        }

        // Avant CHAQUE construction des boutons de difficulte : on s'assure que TOUTE la
        // donnee lue par le predicat de filtrage est correcte AVANT que le natif tourne :
        //   1. le CHAMP enableFreeRoamFeature = true (le predicat lit le champ directement,
        //      PAS la propriete patchee -> il faut le forcer ici, pas une frame plus tard) ;
        //   2. le mode FreeRoam demasque (isHidden = false).
        internal static void InitializeButtonsPrefix()
        {
            bool fieldSet = TrySetFreeRoamTweakableField(true);
            ApplyFreeRoamModeVisible(out bool found, out int changed, out int total, out bool stillHidden);
            if (!_initButtonsPrefixLogged)
            {
                _initButtonsPrefixLogged = true;
                Mod.LogDebug($"[CairnGameApi] InitializeButtons prefix fired (fieldSet={fieldSet} FreeRoam found={found} changed={changed} stillHidden={stillHidden} total={total})");
            }
        }
    }
}
