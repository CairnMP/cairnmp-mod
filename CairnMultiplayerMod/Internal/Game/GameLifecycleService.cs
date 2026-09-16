using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;

namespace CairnMultiplayerMod.Internal.Game
{
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

    /// <summary>
    /// Reads the game's own lifecycle state (in-game / menu / bivouac / cutscene…) from the
    /// native GlobalGameManager and BivouacManager. More reliable than the last loaded Unity
    /// scene, because bivouac and taping use additive scenes while keeping a valid MC.
    /// </summary>
    internal static class GameLifecycleService
    {
        /// <summary>Kept as a lifecycle hook; native managers are read from their singletons.</summary>
        internal static void ResetCaches() { }

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
                detail = $"failed: {ex.GetType().Name}: {GameInterop.FirstLine(ex.Message)}";
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
            catch (Exception exception)
            {
                ModLog.SuppressedException("lifecycle.read-local-pawn", exception);
                return false;
            }
        }

        private static GlobalGameManager FindGlobalGameManager()
            => GlobalGameManager.Instance;

        internal static BivouacManager FindBivouacManager()
            => BivouacManager.Instance;

        internal static bool IsLocalInBivouac()
        {
            try
            {
                var bivouac = FindBivouacManager();
                return bivouac != null && bivouac.IsInBivouacOrInTransition;
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("lifecycle.read-player-state", exception);
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
}
