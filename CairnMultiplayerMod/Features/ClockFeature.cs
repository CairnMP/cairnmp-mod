using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;

namespace CairnMultiplayerMod.Features;

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

    private readonly Dictionary<int, bool> _asleepByPlayer = new();

    private float _publishTimer;
    private bool _hasReportedSleep;
    private bool _lastReportedSleep;
    private float _sleepRefreshTimer;

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
        feature.OnSceneReset(ResetForSceneChange);
        feature.OnSessionEnded(Reset);
    }

    private void Tick()
    {
        if (!IsConnected) return;

        if (Game.State.IsLocalPlayerInGame)
            Game.Clock.LogDiagnosticsOnce();

        ReportLocalSleep();

        if (IsHost) TickHost();
        else TickClient();
    }

    private void ReportLocalSleep()
    {
        if (!Game.Clock.TryGetLocalSleep(out var asleep)) return;
        _sleepRefreshTimer += Game.Time.UnscaledDeltaTime;
        if (_hasReportedSleep && _lastReportedSleep == asleep && _sleepRefreshTimer < 1f) return;

        _sleepRefreshTimer = 0f;
        _hasReportedSleep = true;
        _lastReportedSleep = asleep;
        _sleep.Send(new SleepReport { Asleep = asleep });
    }

    private void OnSleepReported(HostRequest<SleepReport> request)
        => _asleepByPlayer[request.FromPlayerId] = request.Message.Asleep;

    private void TickHost()
    {
        bool hostAsleep = Game.Clock.TryGetLocalSleep(out var a) && a;
        bool allAsleep = hostAsleep && EveryoneElseAsleep();

        float authoritative;
        if (hostAsleep && !allAsleep)
        {
            // Sleeping without consensus: freeze the cycle at the last normal time, or the
            // native bivouac acceleration would move time on for everyone.
            if (!_hasHeldDayTime01 && Game.Clock.TryGetDayTime(out var current))
            {
                _heldDayTime01 = current;
                _hasHeldDayTime01 = true;
            }
            authoritative = _heldDayTime01;
            Game.Clock.Freeze(authoritative);
        }
        else
        {
            // Follow natural time (normal, or fast-forward once everyone sleeps) and keep
            // the baseline current.
            Game.Clock.Unfreeze();
            if (Game.Clock.TryGetDayTime(out var current))
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

        _publishTimer += Game.Time.UnscaledDeltaTime;
        if (_publishTimer < interval) return;

        _publishTimer = 0f;
        _clock.Set(new ClockState { DayTime01 = authoritative, AllAsleep = allAsleep });
    }

    private void TickClient()
    {
        // Re-freeze every frame: neutralises a lone sleeper's local acceleration and locks
        // the day/night visuals onto the host.
        if (_lastReceived != null)
            Game.Clock.Freeze(_lastReceived.DayTime01);
    }

    private bool EveryoneElseAsleep()
    {
        foreach (var playerId in Game.Players.RemoteSleepParticipants)
        {
            if (!_asleepByPlayer.TryGetValue(playerId, out var asleep) || !asleep) return false;
        }
        return true;
    }

    /// <summary>
    /// A scene boundary (including entering and leaving a bivouac) replaces the native cycle,
    /// but the session carries on. Only the freeze ownership and the local send cadence are
    /// scene-bound; the host's time, the held baseline and who is asleep all belong to the
    /// session. Dropping them here unfroze the sky and stopped the fast-forward until the
    /// next packet — and a bivouac is exactly when both matter.
    /// </summary>
    private void ResetForSceneChange()
    {
        _publishTimer = 0f;
        // Re-report our sleep state promptly: it may well have changed across the boundary.
        _hasReportedSleep = false;
        _sleepRefreshTimer = 0f;
        Game.Clock.OnSceneChanged();
    }

    private void Reset()
    {
        ResetForSceneChange();
        _lastReportedSleep = false;
        _heldDayTime01 = 0f;
        _hasHeldDayTime01 = false;
        _lastReceived = null;
        _asleepByPlayer.Clear();
        Game.Clock.Reset();
    }
}
