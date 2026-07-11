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
    /// Reads the global state exposed by the game. This is more reliable than the last
    /// loaded Unity scene, because bivouac and taping use additive scenes
    /// while keeping a valid MC.
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
    /// Reads the raw GlobalGameManager state WITHOUT the bivouac short-circuit. Used to
    /// detect a BivouacManager flag stuck on exit: if the bivouac still
    /// declares itself active but GlobalGameManager already reports InGame, the
    /// bivouac is actually finished and the suspension must be lifted.
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
    /// True if the LOCAL player is in a bivouac (or in an enter/exit transition). Direct read
    /// of the native state (BivouacManager) — used to forbid teleportation during
    /// a bivouac. Safe no-op: false if the manager can't be found or on an exception.
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
