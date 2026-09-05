using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.UI;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Bivouac;

/// <summary>
/// Suspends the gameplay sync while the local player is in a bivouac, and brings it back
/// on exit. During a bivouac Cairn drives the pawn, the camera and the taping hands
/// itself, so the mod steps back to a minimal network presence: ghosts cleared, rope
/// anchors released, SetFrame injection paused.
///
/// The gate owns three timing subtleties that are easy to get wrong:
/// <list type="bullet">
/// <item>the SetFrame patch resumes on a DELAY after exit, so the native save package has
/// time to seal (resuming inside that window left it disposed — one save then nothing);</item>
/// <item>the native bivouac flag can stay stuck on exit, so a movement heuristic forces
/// the resume rather than leaving the session desynced forever;</item>
/// <item>a debug snapshot is logged on every phase change plus a heartbeat, because this
/// path can only be diagnosed after the fact, from two clients' logs.</item>
/// </list>
/// </summary>
internal sealed class BivouacSyncGate
{
    private const float DebugLogIntervalSeconds = 3f;
    private const float RecoveryWatchSeconds = 30f;
    private const float RecoveryLogIntervalSeconds = 3f;
    private const float PatchResumeGraceSeconds = 3f;
    private const float StuckResumeSeconds = 8f;
    private const float StuckMoveThresholdSqr = 2.25f; // ~1.5 m of movement

    private readonly NetworkManager _network;
    private readonly IMultiplayerPanel _panel;
    private readonly RuntimeState _state;
    private readonly Action _resetSyncTimers;
    private readonly Action _resetSceneBoundSyncState;
    private readonly Action<PlayerState> _setLocalState;

    private bool _suspended;
    private float _suspendedSince;
    private float _nextDebugLogAt;

    // Watch window armed on exit: did the ghosts actually come back, or are both sides
    // stuck at Loading (the mutual-deadlock case)?
    private float _recoveryWatchUntil;
    private float _nextRecoveryLogAt;

    // Anti-lockup guard: pawn position on entry + how long the flag has looked stale.
    private Vector3 _suspendPawnPos;
    private bool _hasSuspendPawnPos;
    private float _stuckSince;

    private bool _patchPaused;
    private float _patchResumeAt; // 0 = no deferred resume scheduled

    internal BivouacSyncGate(
        NetworkManager network,
        IMultiplayerPanel panel,
        RuntimeState state,
        Action resetSyncTimers,
        Action resetSceneBoundSyncState,
        Action<PlayerState> setLocalState)
    {
        _network = network;
        _panel = panel;
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _resetSyncTimers = resetSyncTimers ?? throw new ArgumentNullException(nameof(resetSyncTimers));
        _resetSceneBoundSyncState = resetSceneBoundSyncState
            ?? throw new ArgumentNullException(nameof(resetSceneBoundSyncState));
        _setLocalState = setLocalState ?? throw new ArgumentNullException(nameof(setLocalState));
    }

    /// <summary>True while the mod is holding the gameplay sync back for a bivouac.</summary>
    public bool IsSuspended => _suspended;

    /// <summary>
    /// Whether callers should skip applying remote gameplay state right now. Unlike
    /// <see cref="IsSuspended"/> this also covers the frame we enter a bivouac, before
    /// <see cref="Update"/> has run.
    /// </summary>
    public bool BlocksGameplaySync()
        => _network?.IsConnected == true && (_suspended || ShouldSuspend());

    /// <summary>Reconciles the gate with the game state. Runs before the network pump.</summary>
    public void Update()
    {
        UpdateSuspension();
        // Must run every frame, including while suspended (it self-cancels then).
        UpdateDeferredPatchResume();
    }

    /// <summary>Heartbeat snapshot, logged while the gate holds the sync back.</summary>
    public void TickSuspendedLog()
    {
        if (Time.unscaledTime < _nextDebugLogAt)
            return;

        _nextDebugLogAt = Time.unscaledTime + DebugLogIntervalSeconds;
        LogPhase("heartbeat");
    }

    /// <summary>
    /// Snapshot of the recovery window armed on exit, to verify the sync comes back.
    /// No-op outside that window.
    /// </summary>
    public void TickRecoveryLog()
    {
        if (_recoveryWatchUntil <= 0f)
            return;

        var now = Time.unscaledTime;
        if (now >= _recoveryWatchUntil)
        {
            _recoveryWatchUntil = 0f;
            LogPhase("recovery-end");
            return;
        }

        if (now < _nextRecoveryLogAt)
            return;

        _nextRecoveryLogAt = now + RecoveryLogIntervalSeconds;
        LogPhase("recovery");
    }

    /// <summary>
    /// Drops the gate and resumes the SetFrame patch immediately — for teardowns
    /// (disconnect, leaving the lobby) where no native save is in flight.
    /// </summary>
    public void ForceResume()
    {
        _suspended = false;
        ResumePatch(immediate: true);
    }

    private void UpdateSuspension()
    {
        if (_network == null || !_network.IsConnected)
        {
            if (_suspended)
                LogPhase("network-disconnected");

            ForceResume();
            return;
        }

        var shouldSuspend = ShouldSuspend();
        if (shouldSuspend == _suspended)
            return;

        _suspended = shouldSuspend;
        if (_suspended)
            EnterBivouac();
        else
            ExitBivouac();
    }

    private void EnterBivouac()
    {
        ModLog.Info("[State] Gameplay sync suspended for bivouac");
        _suspendedSince = Time.unscaledTime;
        _nextDebugLogAt = 0f;
        PausePatch();
        _resetSyncTimers();
        WeatherInterop.ResetRemoteState();
        RemotePlayerManager.ClearAll();
        // Release the native rope-team anchors: ClearAll destroys the ghosts, so a rope
        // left pinned to their harness would point into the void. We KEEP the logical link
        // (RopeLinkState) — it'll be re-anchored on exit when the partner's ghost returns.
        // The rope tick doesn't run during a bivouac, hence this release here.
        RopeInterop.ReleaseAllAnchors();
        _setLocalState(PlayerState.Loading);
        // Remember the pawn's position on entry: used by the anti-lockup guard
        // (a pawn that has moved = we're climbing again).
        _hasSuspendPawnPos = LocalPlayerInterop.TryGetPose(out _suspendPawnPos, out _);
        _stuckSince = 0f;
        LogPhase("enter");
    }

    private void ExitBivouac()
    {
        LogPhase("exit");
        _recoveryWatchUntil = Time.unscaledTime + RecoveryWatchSeconds;
        _nextRecoveryLogAt = 0f;
        _hasSuspendPawnPos = false;
        _stuckSince = 0f;
        ResumePatch(immediate: false);
        ModLog.Info("[State] Gameplay sync resumed");
        _resetSceneBoundSyncState();
    }

    private bool ShouldSuspend()
    {
        if (SceneRoles.IsBivouac(_state.CurrentScene))
        {
            _stuckSince = 0f;
            return true;
        }

        if (!GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out _)
            || lifecycle != CairnGameLifecycleState.Bivouac)
        {
            _stuckSince = 0f;
            return false;
        }

        // The lifecycle is deduced from the BivouacManager flag, which can stay stuck on
        // exit (the reported permanent desync). Require ~8 s of a stale-looking flag
        // before overriding it, so a real entry/exit transition isn't mistaken for one.
        if (!IsFlagLikelyStuck())
        {
            _stuckSince = 0f;
            return true;
        }

        if (_stuckSince <= 0f)
            _stuckSince = Time.unscaledTime;

        if (Time.unscaledTime - _stuckSince < StuckResumeSeconds)
            return true;

        ModLog.Warning(
            "[State] Bivouac flag appears stuck (game=InGame, pawn moved) — forcing gameplay sync resume");
        _stuckSince = 0f;
        return false;
    }

    /// <summary>
    /// Heuristic: the bivouac flag is probably stuck if the game itself reports InGame,
    /// we're on a gameplay scene, and the pawn has moved significantly since entering the
    /// bivouac. Conservative by design: any missing signal means "a real bivouac".
    /// </summary>
    private bool IsFlagLikelyStuck()
    {
        if (!SceneRoles.IsGameplayRoot(_state.CurrentScene))
            return false;

        if (!GameLifecycleService.TryGetRawGameState(out var raw) || raw != CairnGameLifecycleState.InGame)
            return false;

        if (!_hasSuspendPawnPos)
            return false;

        if (!LocalPlayerInterop.TryGetPose(out var pos, out _))
            return false;

        return (pos - _suspendPawnPos).sqrMagnitude >= StuckMoveThresholdSqr;
    }

    // Pause/resume of the SetFrame patch goes through a flag — the patch stays installed.
    // Uninstalling/reinstalling (UnpatchSelf/Patch) fell inside the bivouac's native save
    // window and could break the sealing of the save package -> 1 save OK then nothing.
    private void PausePatch()
    {
        // Cancel a deferred resume in flight (we re-entered a bivouac within the grace
        // window) -> the patch must stay paused.
        _patchResumeAt = 0f;

        if (_patchPaused)
            return;

        _patchPaused = true;
        NetplaySetFramePatch.Pause();
        ModLog.Info("[State] Netplay SetFrame patch paused for bivouac");
    }

    /// <summary>
    /// Resumes the SetFrame patch. Immediate for teardowns, where no save is in progress;
    /// deferred when leaving a bivouac, to let the native save package seal before
    /// injection resumes.
    /// </summary>
    private void ResumePatch(bool immediate)
    {
        if (immediate)
        {
            _patchResumeAt = 0f;
            if (!_patchPaused)
                return;

            _patchPaused = false;
            NetplaySetFramePatch.Resume();
            ModLog.Info("[State] Netplay SetFrame patch resumed after bivouac");
            return;
        }

        if (!_patchPaused)
            return;

        _patchResumeAt = Time.unscaledTime + PatchResumeGraceSeconds;
        ModLog.Info(
            $"[State] Netplay SetFrame patch resume scheduled in {PatchResumeGraceSeconds:F0}s (save-seal grace)");
    }

    private void UpdateDeferredPatchResume()
    {
        if (_patchResumeAt <= 0f)
            return;

        // Back in a bivouac / outside gameplay -> cancel; we stay paused until we're
        // stably back in game.
        if (_suspended || ShouldSuspend())
        {
            _patchResumeAt = 0f;
            return;
        }

        if (Time.unscaledTime < _patchResumeAt)
            return;

        _patchResumeAt = 0f;
        if (!_patchPaused)
            return;

        _patchPaused = false;
        NetplaySetFramePatch.Resume();
        ModLog.Info("[State] Netplay SetFrame patch resumed after bivouac (save-seal grace elapsed)");
    }

    /// <summary>
    /// One-line snapshot of everything that matters to diagnose a bivouac desync after
    /// the fact: local and remote lifecycle states, ghost count, patch state.
    /// </summary>
    public void LogPhase(string phase)
    {
        var elapsed = _suspendedSince > 0f
            ? Math.Max(0f, Time.unscaledTime - _suspendedSince)
            : 0f;
        var networkState = _network == null
            ? "network=null"
            : $"network=connected:{_network.IsConnected} handshake:{_network.IsHandshakeComplete} remotes:{_network.RemotePlayers.Count}";

        ModLog.Info(
            $"[BivouacDebug] phase={phase} elapsed={elapsed:0.0}s scene='{_state.CurrentScene ?? ""}' " +
            $"lastGameplay='{_state.LastGameplayScene ?? ""}' local={_state.LocalPlayerState} suspended={_suspended} " +
            $"{DescribeRemoteStates()} " +
            $"panelVisible={_panel?.IsVisible == true} {networkState} {RemotePlayerManager.DebugSummary()} " +
            $"setFramePatchInstalled={NetplaySetFramePatch.IsInstalled} " +
            $"patchPaused={_patchPaused} " +
            BivouacDiagnostics.BuildBivouacDebugSnapshot());
    }

    /// <summary>Lifecycle state of each known remote player: if one side stays at Loading
    /// after exiting, its ghosts never reappear.</summary>
    private string DescribeRemoteStates()
    {
        if (_network == null)
            return "remoteStates=none";

        var summary = "remoteStates=[";
        var first = true;
        foreach (var kv in _network.RemotePlayers)
        {
            if (!first)
                summary += ",";
            first = false;
            var rp = kv.Value;
            summary += $"{kv.Key}:{(rp == null ? "null" : rp.State.ToString())}";
        }
        return summary + "]";
    }
}
