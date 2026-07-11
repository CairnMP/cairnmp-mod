using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Synchronisation de l'heure du jour (NightDayCycle.dayTime01) et de l'etat de
/// sommeil (PlayerStateFeedbacks.IsAsleep) entre joueurs.
///
/// IMPORTANT : dayTime01 est une valeur DERIVEE, recalculee chaque frame par
/// NightDayCycle.UpdateDayTime01() a partir de TimeManager.GameTime. Ecrire le
/// champ directement (ndc.dayTime01 = x) est donc instantanement ecrase -> c'est
/// pourquoi l'ancienne synchro ne tenait pas. On force desormais l'heure via le
/// mecanisme de GEL natif (isFrozen + lastDayTime01OnFreeze) : quand isFrozen est
/// vrai, UpdateDayTime01 conserve lastDayTime01OnFreeze au lieu de recalculer. C'est
/// l'equivalent jour/nuit de ForceInfiniteWeatherState cote meteo.
///
/// L'horloge visuelle jour-nuit est host-autoritaire : l'hote diffuse son
/// dayTime01, les clients GELENT leur cycle sur cette valeur chaque frame. Quand un
/// joueur dort sans consensus, l'hote gele le cycle a la derniere heure normale ; le
/// fast-forward visuel ne se produit donc que quand tous dorment (hote libere le gel).
///
/// Robustesse : singletons absents (chargement, menu) ou exceptions IL2CPP ->
/// no-op, jamais de crash. La synchro est additive : un echec n'affecte ni le
/// sommeil natif ni le gameplay.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static MonoBehaviour _playerStateFeedbacksCached;
    private static int _lastPlayerStateFeedbacksSearchFrame;
    private static int _isAsleepFieldOffset = -1; // -1 non resolu, -2 echec, >=0 offset
    private static bool _timeApiDumped;
    private static bool _nightDayCycleWarned;

    // Vrai tant que NOUS maintenons le gel du cycle jour/nuit. Sert a ne liberer que
    // notre propre gel (UnfreezeDayCycle no-op sinon) -> on ne casse pas un gel pose
    // par le jeu lui-meme (mode photo, etc.).
    private static bool _dayCycleFrozenByUs;

    // Noms candidats du champ booleen "is asleep" sur PlayerStateFeedbacks
    // (resolu en runtime : le type n'est pas accessible en type compile-time).
    private static readonly string[] IsAsleepFieldCandidates =
    {
        "<IsAsleep>k__BackingField",
        "isAsleep",
        "IsAsleep",
    };

    /// <summary>Lit l'heure du jour normalisee (0-1) depuis NightDayCycle.</summary>
    public static bool TryGetDayTime01(out float dayTime01)
    {
        dayTime01 = 0f;
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;
            dayTime01 = ndc.dayTime01;
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    /// <summary>
    /// Force et maintient l'heure du jour (0-1) via le gel natif du NightDayCycle.
    /// A rappeler chaque frame : on re-pousse la valeur pour suivre l'heure de l'hote.
    /// isFrozen empeche UpdateDayTime01 de recalculer depuis TimeManager.GameTime ; on
    /// ecrit aussi dayTime01 pour la frame courante (anti-flicker, peu importe l'ordre
    /// d'execution entre notre tick et NightDayCycle.Update).
    /// </summary>
    public static bool FreezeDayCycle(float dayTime01)
    {
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;

            dayTime01 = Mathf.Repeat(dayTime01, 1f); // cyclique, borne 0-1

            // SetFreezeDayTime01 est l'API dediee ; on reaffirme ensuite les champs pour
            // avoir le dernier mot quelle que soit son implementation interne.
            ndc.SetFreezeDayTime01(dayTime01);
            ndc.isFrozen = true;
            ndc.lastDayTime01OnFreeze = dayTime01;
            ndc.dayTime01 = dayTime01;
            _dayCycleFrozenByUs = true;
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    /// <summary>
    /// Libere NOTRE gel du cycle jour/nuit pour laisser l'heure reprendre naturellement
    /// (heure normale ou fast-forward natif). No-op si nous n'avions pas gele (on ne
    /// touche pas a un gel pose par le jeu).
    /// </summary>
    public static bool UnfreezeDayCycle()
    {
        if (!_dayCycleFrozenByUs) return true;
        try
        {
            var ndc = NightDayCycle.Instance;
            if (ndc == null) return false;
            ndc.isFrozen = false;
            _dayCycleFrozenByUs = false;
            return true;
        }
        catch (Exception ex)
        {
            WarnNightDayCycleOnce(ex);
            return false;
        }
    }

    /// <summary>
    /// Indique si le joueur local dort. Lit le booleen IsAsleep de
    /// PlayerStateFeedbacks par offset de champ (resolu en runtime).
    /// </summary>
    public static bool TryIsLocalAsleep(out bool asleep)
    {
        asleep = false;

        var psf = FindMonoBehaviourByName("PlayerStateFeedbacks",
            ref _playerStateFeedbacksCached, ref _lastPlayerStateFeedbacksSearchFrame);
        if (psf == null || psf.Pointer == IntPtr.Zero) return false;

        var offset = EnsureIsAsleepOffset(psf);
        if (offset < 0) return false;

        try
        {
            asleep = *((byte*)psf.Pointer + offset) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int EnsureIsAsleepOffset(MonoBehaviour psf)
    {
        if (_isAsleepFieldOffset != -1) return _isAsleepFieldOffset;

        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(psf.Pointer);
            foreach (var candidate in IsAsleepFieldCandidates)
            {
                var field = IL2CPP.GetIl2CppField(klass, candidate);
                if (field == IntPtr.Zero) continue;

                _isAsleepFieldOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
                Mod.LogDebug($"[TimeSync] Resolved PlayerStateFeedbacks IsAsleep field '{candidate}' @ offset {_isAsleepFieldOffset}");
                return _isAsleepFieldOffset;
            }

            _isAsleepFieldOffset = -2;
            Mod.Log.Warning("[TimeSync] PlayerStateFeedbacks IsAsleep field not found — sleep detection disabled.");
        }
        catch (Exception ex)
        {
            _isAsleepFieldOffset = -2;
            Mod.Log.Warning($"[TimeSync] IsAsleep field resolution failed: {ex.GetType().Name}: {ex.Message}");
        }

        return _isAsleepFieldOffset;
    }

    public static void ResetTimeSyncCache()
    {
        _playerStateFeedbacksCached = null;
        _lastPlayerStateFeedbacksSearchFrame = 0;
        // Libere notre gel pour ne pas laisser le cycle bloque apres une deconnexion.
        UnfreezeDayCycle();
    }

    /// <summary>Logue une fois l'etat de resolution de l'API temps (debug in-game).</summary>
    public static void DumpTimeApi()
    {
        if (_timeApiDumped) return;
        _timeApiDumped = true;

        try
        {
            var hasTime = TryGetDayTime01(out var t);
            var hasAsleep = TryIsLocalAsleep(out var a);
            Mod.LogDebug($"[TimeSync] API dump: dayTime01={(hasTime ? t.ToString("F3") : "n/a")} " +
                        $"playerStateFeedbacks={(hasAsleep ? $"found(asleep={a})" : "n/a")}");
        }
        catch { }
    }

    private static void WarnNightDayCycleOnce(Exception ex)
    {
        if (_nightDayCycleWarned) return;
        _nightDayCycleWarned = true;
        Mod.Log.Warning($"[TimeSync] NightDayCycle access failed: {ex.GetType().Name}: {ex.Message}");
    }
}
