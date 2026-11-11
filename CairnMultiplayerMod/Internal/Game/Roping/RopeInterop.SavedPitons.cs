using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    private static readonly List<Il2Cpp.Piton> HollowPitons = new();
    private static bool _worldStarted;
    private static bool _hollowSweepOwed;
    private static int _hollowSweepPasses;
    private static int _hollowRemoved;
    private static float _hollowSweepRetryAt;
    private const float HollowSweepRetrySeconds = 1f;

    /// <summary>
    /// Once after each world load, in every mode: saved pitons that came back without a
    /// place leave the Lifeline. The load is read from Cairn's own loading state, so
    /// nothing is patched. A piton with a real position is never touched.
    /// </summary>
    internal static void TickSavedPitonCleanup()
    {
        try
        {
            var started = WorldHasStarted();
            if (SavedPitonLoadGuard.LoadJustFinished(_worldStarted, started))
            {
                _hollowSweepOwed = true;
                _hollowSweepPasses = 0;
                _hollowRemoved = 0;
                _hollowSweepRetryAt = 0f;
            }

            _worldStarted = started;
            if (!started)
            {
                _hollowSweepOwed = false;
                return;
            }

            if (!_hollowSweepOwed || Time.unscaledTime < _hollowSweepRetryAt) return;

            // The climber may not be in the world on the first frames. Wait for her.
            var lifeline = TryGetLifeline();
            if (lifeline == null || !TryCollectHollowPitons(lifeline)) return;

            var failed = 0;
            foreach (var piton in HollowPitons)
            {
                if (TryRemoveHollowPiton(lifeline, piton)) _hollowRemoved++;
                else failed++;
            }

            _hollowSweepPasses++;
            if (SavedPitonLoadGuard.ShouldRetry(failed, _hollowSweepPasses))
            {
                _hollowSweepRetryAt = Time.unscaledTime + HollowSweepRetrySeconds;
                return;
            }

            _hollowSweepOwed = false;
            if (failed > 0)
                ModLog.Warning($"[Save] Removed {_hollowRemoved} saved piton(s) with no valid position after the load. {failed} could not be removed.");
            else if (_hollowRemoved > 0)
                ModLog.Info($"[Save] Removed {_hollowRemoved} saved piton(s) with no valid position after the load.");
        }
        catch (Exception exception)
        {
            // Runs in every mode from the mod's update. A failure here must not end the session.
            _hollowSweepOwed = false;
            ModLog.SuppressedException("rope.saved-piton-cleanup", exception);
        }
        finally
        {
            HollowPitons.Clear();
        }
    }

    private static bool WorldHasStarted()
    {
        var manager = Il2Cpp.GlobalGameManager.Instance;
        return manager != null && manager.loadingState == Il2Cpp.GlobalGameManager.LoadingState.GameStarted;
    }

    private static bool TryCollectHollowPitons(Il2Cpp.Lifeline lifeline)
    {
        HollowPitons.Clear();
        var placed = lifeline.PlacedPitons;
        if (placed == null) return false;

        for (var i = 0; i < placed.Count; i++)
        {
            var piton = placed[i]?.Piton;
            if (piton == null || piton.Pointer == IntPtr.Zero) continue;
            try
            {
                var transform = piton.transform;
                if (transform == null || transform.Pointer == IntPtr.Zero) continue;
                var position = transform.position;
                if (SavedPitonLoadGuard.IsHollow(position.x, position.y, position.z))
                    HollowPitons.Add(piton);
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("rope.saved-piton-cleanup", exception);
            }
        }

        return true;
    }

    /// <summary>
    /// Cairn's own removal: off the rope when it is clipped, then off the Lifeline, then the
    /// hardware. <see cref="Il2Cpp.Lifeline.DetachPiton"/> is the ripped-out path. It hands
    /// the piton to the physics, and this one has no place to fall from.
    /// </summary>
    private static bool TryRemoveHollowPiton(Il2Cpp.Lifeline lifeline, Il2Cpp.Piton piton)
    {
        try
        {
            var pointer = piton.Pointer;
            if (lifeline.AttachedPitonsContains(piton))
            {
                var rope = lifeline.securingRope;
                var quickdraw = piton.Quickdraw;
                if (rope != null && quickdraw != null && quickdraw.Pointer != IntPtr.Zero)
                    rope.ExternalRemoveQuickdraw(quickdraw);
            }

            lifeline.RemovePiton(piton);
            // Still listed: destroying it would leave a dangling entry in the next save.
            if (LifelineHolds(lifeline, pointer)) return false;
            DestroyReleasedPiton(piton);
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.saved-piton-cleanup", exception);
            return false;
        }
    }
}
