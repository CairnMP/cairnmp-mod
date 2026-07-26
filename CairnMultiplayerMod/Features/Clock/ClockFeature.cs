using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Clock;

/// <summary>The authoritative time of day, plus whether everyone is asleep.</summary>
internal sealed class ClockState : IPacket
{
    public float DayTime01;
    public bool AllAsleep;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(DayTime01);
        writer.Write(AllAsleep);
    }

    public void Deserialize(BinaryReader reader)
    {
        DayTime01 = reader.ReadSingle();
        AllAsleep = reader.ReadBoolean();
    }
}

/// <summary>A player telling the host whether they are asleep in a bivouac.</summary>
internal sealed class SleepReport : IPacket
{
    public bool Asleep;

    public void Serialize(BinaryWriter writer) => writer.Write(Asleep);
    public void Deserialize(BinaryReader reader) => Asleep = reader.ReadBoolean();
}

/// <summary>
/// Time of day, and the bivouac fast-forward that only happens once everyone sleeps.
///
/// The visual day/night clock is host-authoritative: the host publishes its dayTime01 and
/// clients freeze their own cycle onto it every frame. Freezing is what makes it stick —
/// writing dayTime01 directly gets recomputed away by the game on the next frame.
///
/// Sleep is reported to the host and stays there: only the host needs to know who sleeps,
/// to decide whether time may fast-forward. Everyone else learns the outcome through
/// AllAsleep on the published state.
/// </summary>
internal sealed class ClockFeature : MultiplayerFeature
{
    public override string Id => "clock";

    private HostState<ClockState> _clock;
    private HostCommand<SleepReport> _sleep;

    // Host side: who is asleep, by player id.
    private readonly Dictionary<int, bool> _asleepByPlayer = new();

    private float _publishTimer;
    private bool _hasReportedSleep;
    private bool _lastReportedSleep;

    // Time held by the host while it sleeps alone, so its bivouac fast-forward does not
    // drag everyone else's clock with it.
    private float _heldDayTime01;
    private bool _hasHeldDayTime01;

    private ClockState _lastReceived;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _clock = feature.HostState<ClockState>("time", received => _lastReceived = received);
        _sleep = feature.HostCommand<SleepReport>("sleep", OnSleepReported);

        // Always: the bivouac is exactly when we sleep and time accelerates, and gameplay
        // sync is suspended there — so this has to run outside it.
        feature.EveryFrame(Tick, FeaturePhase.Always);

        feature.OnPlayerLeft((playerId, _) => _asleepByPlayer.Remove(playerId));
        feature.OnSceneReset(Reset);
        feature.OnSessionEnded(Reset);
    }

    private void Tick()
    {
        if (!IsConnected) return;

        if (Mod.Instance.LocalState == PlayerState.InGame)
            TimeApi.DumpTimeApi();

        ReportLocalSleep();

        if (IsHost) TickHost();
        else TickClient();
    }

    /// <summary>Tells the host when our own sleep state flips — not every frame.</summary>
    private void ReportLocalSleep()
    {
        if (!TimeApi.TryIsLocalAsleep(out var asleep)) return;
        if (_hasReportedSleep && _lastReportedSleep == asleep) return;

        _hasReportedSleep = true;
        _lastReportedSleep = asleep;
        _sleep.Send(new SleepReport { Asleep = asleep });
    }

    private void OnSleepReported(HostRequest<SleepReport> request)
        => _asleepByPlayer[request.FromPlayerId] = request.Message.Asleep;

    private void TickHost()
    {
        bool hostAsleep = TimeApi.TryIsLocalAsleep(out var a) && a;
        bool allAsleep = hostAsleep && EveryoneElseAsleep();

        float authoritative;
        if (hostAsleep && !allAsleep)
        {
            // Sleeping without consensus: freeze the cycle at the last normal time, or the
            // native bivouac acceleration would move time on for everyone.
            if (!_hasHeldDayTime01 && TimeApi.TryGetDayTime01(out var current))
            {
                _heldDayTime01 = current;
                _hasHeldDayTime01 = true;
            }
            authoritative = _heldDayTime01;
            TimeApi.FreezeDayCycle(authoritative);
        }
        else
        {
            // Follow natural time (normal, or fast-forward once everyone sleeps) and keep
            // the baseline current.
            TimeApi.UnfreezeDayCycle();
            if (TimeApi.TryGetDayTime01(out var current))
            {
                _heldDayTime01 = current;
                _hasHeldDayTime01 = true;
                authoritative = current;
            }
            else
            {
                authoritative = _heldDayTime01;
            }
        }

        var interval = allAsleep
            ? Protocol.TimeStateFastForwardIntervalSeconds
            : Protocol.TimeStateUpdateIntervalSeconds;

        _publishTimer += Time.unscaledDeltaTime;
        if (_publishTimer < interval) return;

        _publishTimer = 0f;
        _clock.Set(new ClockState { DayTime01 = authoritative, AllAsleep = allAsleep });
    }

    private void TickClient()
    {
        // Re-freeze every frame: neutralises a lone sleeper's local acceleration and locks
        // the day/night visuals onto the host.
        if (_lastReceived != null)
            TimeApi.FreezeDayCycle(_lastReceived.DayTime01);
    }

    /// <summary>True when every other player who is actually in game is asleep. Players
    /// loading or in a menu do not block the consensus.</summary>
    private bool EveryoneElseAsleep()
    {
        foreach (var kv in Mod.Instance.Network.RemotePlayers)
        {
            var player = kv.Value;
            if (player == null || player.State != PlayerState.InGame) continue;
            if (!_asleepByPlayer.TryGetValue(kv.Key, out var asleep) || !asleep) return false;
        }
        return true;
    }

    private void Reset()
    {
        _publishTimer = 0f;
        _hasReportedSleep = false;
        _lastReportedSleep = false;
        _heldDayTime01 = 0f;
        _hasHeldDayTime01 = false;
        _lastReceived = null;
        _asleepByPlayer.Clear();
        TimeApi.ResetCaches();
    }
}
