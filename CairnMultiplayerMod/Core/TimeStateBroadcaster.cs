using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public partial class Mod
{
    private float _timeStateTimer;
    private bool _hasLastSentSleepState;
    private bool _lastSentSleepState;

    // Authoritative day time frozen by the host when it sleeps without consensus.
    private float _heldDayTime01;
    private bool _hasHeldDayTime01;

    // Last time received from the host (client side) to apply every frame.
    private ServerTimeState _remoteTimeState;
    private bool _hasRemoteTimeState;

    /// <summary>
    /// Synchronizes the day time (NightDayCycle.dayTime01) between players and
    /// handles the bivouac fast-forward: it only happens when EVERYONE is asleep.
    ///
    /// Must run BEFORE the bivouac suspension (Mod.OnUpdate short-circuits all sync
    /// during a bivouac, which is exactly when we sleep). Runs as long as the
    /// handshake is complete to keep the time in sync even while climbing.
    /// </summary>
    private void TickTimeSync()
    {
        if (_network == null || !_network.IsHandshakeComplete)
            return;

        if (LocalState == PlayerState.InGame)
            CairnGameApi.DumpTimeApi();

        ReportLocalSleepState();

        if (_lobby?.IsHost == true)
            TickHostTime();
        else
            TickClientTime();
    }

    /// <summary>Reports the local sleep state to the host, only on change.</summary>
    private void ReportLocalSleepState()
    {
        if (!CairnGameApi.TryIsLocalAsleep(out var asleep))
            return;

        if (_hasLastSentSleepState && _lastSentSleepState == asleep)
            return;

        _hasLastSentSleepState = true;
        _lastSentSleepState = asleep;
        _network.SendSleepState(asleep);
    }

    private void TickHostTime()
    {
        bool hostAsleep = CairnGameApi.TryIsLocalAsleep(out var a) && a;
        bool allAsleep = hostAsleep && AllRemoteInGameAsleep();

        float authoritative;
        if (hostAsleep && !allAsleep)
        {
            // The host sleeps without consensus: we FREEZE the cycle at the last normal
            // time captured before sleeping (otherwise the bivouac's native acceleration
            // would advance time for everyone). The native freeze persists, unlike the old
            // direct write of dayTime01 which was recomputed every frame.
            if (!_hasHeldDayTime01 && CairnGameApi.TryGetDayTime01(out var cur))
            {
                _heldDayTime01 = cur;
                _hasHeldDayTime01 = true;
            }
            authoritative = _heldDayTime01;
            CairnGameApi.FreezeDayCycle(authoritative);
        }
        else
        {
            // No freeze: we release our freeze and follow the natural time (normal, or
            // fast-forward when everyone sleeps) while keeping the baseline up to date.
            CairnGameApi.UnfreezeDayCycle();
            if (CairnGameApi.TryGetDayTime01(out var cur))
            {
                _heldDayTime01 = cur;
                _hasHeldDayTime01 = true;
                authoritative = cur;
            }
            else
            {
                authoritative = _heldDayTime01;
            }
        }

        var interval = allAsleep
            ? Protocol.TimeStateFastForwardIntervalSeconds
            : Protocol.TimeStateUpdateIntervalSeconds;

        _timeStateTimer += Time.unscaledDeltaTime;
        if (_timeStateTimer >= interval)
        {
            _timeStateTimer = 0f;
            _network.SendTimeState(new ServerTimeState
            {
                DayTime01 = authoritative,
                AllAsleep = allAsleep,
            });
        }
    }

    private void TickClientTime()
    {
        // Freeze the day/night cycle on the host's time every frame: neutralizes the
        // acceleration of a lone sleeper (their local fast-forward is overwritten) and,
        // above all, locks the day/night visuals to the host — the native freeze PERSISTS,
        // whereas the old direct write of dayTime01 was recomputed every frame.
        if (_hasRemoteTimeState)
            CairnGameApi.FreezeDayCycle(_remoteTimeState.DayTime01);
    }

    /// <summary>
    /// True if all InGame remote players are asleep. Non-InGame players
    /// (loading, menu) don't block the consensus.
    /// </summary>
    private bool AllRemoteInGameAsleep()
    {
        foreach (var kv in _network.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || rp.State != PlayerState.InGame)
                continue;
            if (!rp.HasSleepState || !rp.IsAsleep)
                return false;
        }
        return true;
    }

    private void ResetTimeSyncState()
    {
        _timeStateTimer = 0f;
        _hasLastSentSleepState = false;
        _lastSentSleepState = false;
        _heldDayTime01 = 0f;
        _hasHeldDayTime01 = false;
        _hasRemoteTimeState = false;
        _remoteTimeState = default;
        CairnGameApi.ResetTimeSyncCache();
    }
}
