using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

internal enum CairnGameLifecycleState
{
    Unknown,
    InGame,
    Menu,
    Bivouac,
    Cutscene,
    GameOver,
    Loading,
    RecapPath,
}

public static unsafe partial class CairnGameApi
{
    private static MonoBehaviour _globalGameManagerCached;
    private static MonoBehaviour _bivouacManagerCached;
    private static int _lastGlobalGameManagerSearchFrame;
    private static int _lastBivouacManagerSearchFrame;

    /// <summary>
    /// Lit l'etat global expose par le jeu. C'est plus fiable que la derniere
    /// scene Unity chargee, car le bivouac et le taping utilisent des scenes
    /// additives tout en gardant un MC valide.
    /// </summary>
    internal static bool TryGetGameLifecycle(out CairnGameLifecycleState state, out string detail)
    {
        state = CairnGameLifecycleState.Unknown;
        detail = "unavailable";

        try
        {
            var bivouac = FindBivouacManager();
            if (bivouac != null && bivouac.IsInBivouacOrInTransition)
            {
                state = CairnGameLifecycleState.Bivouac;
                detail = $"bivouacState active={bivouac.IsInBivouac} transition={bivouac.IsInTransition}";
                return true;
            }

            var manager = FindGlobalGameManager();
            if (manager == null)
                return false;

            var gameState = manager.CurrentGameState;
            var loadingState = manager.loadingState;
            detail = $"gameState={gameState} loadingState={loadingState}";
            state = MapGameState(gameState);
            return state != CairnGameLifecycleState.Unknown;
        }
        catch (Exception ex)
        {
            detail = $"failed: {ex.GetType().Name}: {FirstLine(ex.Message)}";
            return false;
        }
    }

    /// <summary>
    /// Lit l'etat brut de GlobalGameManager SANS le court-circuit bivouac. Sert a
    /// detecter un flag BivouacManager reste bloque a la sortie : si le bivouac se
    /// declare encore actif mais que GlobalGameManager rapporte deja InGame, le
    /// bivouac est en realite termine et la suspension doit etre levee.
    /// </summary>
    internal static bool TryGetRawGameState(out CairnGameLifecycleState state)
    {
        state = CairnGameLifecycleState.Unknown;
        try
        {
            var manager = FindGlobalGameManager();
            if (manager == null)
                return false;

            state = MapGameState(manager.CurrentGameState);
            return state != CairnGameLifecycleState.Unknown;
        }
        catch
        {
            return false;
        }
    }

    private static GlobalGameManager FindGlobalGameManager()
    {
        var comp = FindMonoBehaviourByName("GlobalGameManager", ref _globalGameManagerCached,
            ref _lastGlobalGameManagerSearchFrame);
        return comp?.TryCast<GlobalGameManager>();
    }

    private static BivouacManager FindBivouacManager()
    {
        var comp = FindMonoBehaviourByName("BivouacManager", ref _bivouacManagerCached,
            ref _lastBivouacManagerSearchFrame);
        return comp?.TryCast<BivouacManager>();
    }

    /// <summary>
    /// Vrai si le joueur LOCAL est en bivouac (ou en transition d'entree/sortie). Lecture
    /// directe de l'etat natif (BivouacManager) — sert a interdire la teleportation pendant
    /// un bivouac. No-op safe : false si le manager est introuvable ou en cas d'exception.
    /// </summary>
    public static bool IsLocalInBivouac()
    {
        try
        {
            var bivouac = FindBivouacManager();
            return bivouac != null && bivouac.IsInBivouacOrInTransition;
        }
        catch
        {
            return false;
        }
    }

    private static CairnGameLifecycleState MapGameState(GlobalGameManager.GameState gameState)
    {
        return gameState switch
        {
            GlobalGameManager.GameState.InGame => CairnGameLifecycleState.InGame,
            GlobalGameManager.GameState.Menu => CairnGameLifecycleState.Menu,
            GlobalGameManager.GameState.Bivouac => CairnGameLifecycleState.Bivouac,
            GlobalGameManager.GameState.Cutscene => CairnGameLifecycleState.Cutscene,
            GlobalGameManager.GameState.GameOver => CairnGameLifecycleState.GameOver,
            GlobalGameManager.GameState.Loading => CairnGameLifecycleState.Loading,
            GlobalGameManager.GameState.RecapPath => CairnGameLifecycleState.RecapPath,
            _ => CairnGameLifecycleState.Unknown,
        };
    }
}
