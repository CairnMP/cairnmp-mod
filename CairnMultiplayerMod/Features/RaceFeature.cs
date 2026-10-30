using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// A race up the mountain.
///
/// Cairn has no race of its own -- its competitive netplay mode ships without a single level
/// to run it on -- so the rules here are the mod's. They are deliberately the simplest ones
/// that are fair: from the host's go, every climber is scored on the height they gain from
/// wherever they started, and the first to top out wins outright.
///
/// Measuring gain rather than altitude is what makes it work at all: in CairnMP everyone
/// launches their own save, so two climbers are rarely standing in the same place when the
/// race starts. Height gained is theirs alone and needs no common start line.
///
/// The top out is read from the story's summit sequence. A DLC world is a Summit-mode run
/// with no such sequence, so a race there is decided on height alone.
/// </summary>
internal sealed class RaceFeature : MultiplayerFeature
{
    /// <summary>Progress is broadcast when it moves this far, so a hard pitch costs one
    /// message every few metres rather than one per frame.</summary>
    private const float ReportEveryMeters = 3f;

    /// <summary>...and at least this often while climbing, so slow progress still shows.</summary>
    private const float ReportEverySeconds = 5f;

    private const float NoticeSeconds = 8f;

    /// <summary>A claim that never reached the host is sent again, but not every frame.</summary>
    private const float ClaimRetrySeconds = 2f;

    private HostState<RaceStarted> _started;
    private Broadcast<RaceProgress> _progress;
    private HostCommand<RaceFinish> _finish;
    private HostState<RaceResult> _result;

    private readonly Dictionary<int, Climber> _climbers = new();
    private readonly List<StandingRow> _rows = new();
    private readonly List<KeyValuePair<int, Climber>> _ordered = new();

    private bool _racing, _finished, _claimed, _hasStartHeight, _announcedStart;
    private float _startHeight, _startedAt, _bestClimb, _lastSentClimb, _nextSendAt, _nextClaimAt;
    private int _winnerId = -1;

    public override string Id => "race";

    private readonly struct Climber
    {
        internal Climber(float meters, bool isOut) { Meters = meters; IsOut = isOut; }
        internal float Meters { get; }
        internal bool IsOut { get; }
    }

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _started = feature.HostState<RaceStarted>("started", OnRaceStarted);
        _progress = feature.Broadcast<RaceProgress>("progress", OnRemoteProgress);
        // The host referees the finish. Two climbers topping out a moment apart each hear of
        // the other only after their own summit, so left to themselves both would win.
        _finish = feature.HostCommand<RaceFinish>("finish", JudgeFinish);
        _result = feature.HostState<RaceResult>("result", OnRaceDecided);

        // Always: the standings belong on screen in a bivouac and while streaming too, and a
        // climber who is down still holds their place in the ranking.
        feature.EveryFrame(Tick, FeaturePhase.Always);

        feature.OnPlayerLeft((playerId, _) => _climbers.Remove(playerId));
        feature.OnSessionEnded(Reset);
    }

    private void Tick()
    {
        if (!IsConnected)
        {
            if (_racing || _rows.Count > 0) Reset();
            return;
        }

        // A latecomer can hear that the race is on before this client knows the lobby's
        // mode. Throwing the start away here lost it for good — it is announced only once —
        // so the standings just wait for the two to agree.
        if (Rules.Mode != MultiplayerMode.Race)
        {
            if (_rows.Count > 0) HideStandings();
            return;
        }

        AnnounceStartIfHost();
        if (!_racing) return;

        TrackLocalClimb();
        ShowStandings();
    }

    /// <summary>
    /// The host fires the gun once, when they are actually climbing. Latecomers get the same
    /// state replayed, so they join a race already under way rather than never starting one.
    /// </summary>
    private void AnnounceStartIfHost()
    {
        if (_racing || _announcedStart || !IsHost) return;
        if (!Game.State.IsLocalPlayerInGame) return;

        _announcedStart = true;
        _started.Set(new RaceStarted());
        LogInfo("Race started");
    }

    private void OnRaceStarted(RaceStarted started)
    {
        if (_racing) return;
        _racing = true;
        _finished = false;
        _claimed = false;
        _winnerId = -1;
        _hasStartHeight = false;
        _bestClimb = 0f;
        _lastSentClimb = 0f;
        _nextClaimAt = 0f;
        _startedAt = Game.Time.UnscaledTime;
        Game.Hud.ShowMessage("start", "Race! Climb as high as you can.", NoticeSeconds);

        // Both are replayed to a latecomer, in no promised order: an outcome heard before
        // the start still stands.
        if (_result.TryGet(out var decided)) OnRaceDecided(decided);
    }

    private void TrackLocalClimb()
    {
        if (!Game.Players.TryGetLocation(LocalPlayerId, out var me)) return;

        // The start line is wherever this climber happened to be when the race reached them,
        // which is not the same frame the host fired the gun.
        if (!_hasStartHeight)
        {
            _hasStartHeight = true;
            _startHeight = me.Y;
        }

        var climbed = Math.Max(0f, me.Y - _startHeight);
        if (climbed > _bestClimb) _bestClimb = climbed;

        var isOut = Game.Life.IsLocalPlayerDown;
        _climbers[LocalPlayerId] = new Climber(_bestClimb, isOut);

        if (!_finished && !_claimed && Game.State.HasReachedSummit
            && Game.Time.UnscaledTime >= _nextClaimAt)
            ClaimFinish();
        ReportProgress(isOut);
    }

    private void ReportProgress(bool isOut)
    {
        var now = Game.Time.UnscaledTime;
        var moved = _bestClimb - _lastSentClimb >= ReportEveryMeters;
        if (!moved && now < _nextSendAt) return;

        _nextSendAt = now + ReportEverySeconds;
        _lastSentClimb = _bestClimb;
        _progress.Send(new RaceProgress(_bestClimb, isOut));
    }

    /// <summary>
    /// Topping out is a claim, not a verdict: what the host answers is what every scoreboard
    /// shows, this one included.
    /// </summary>
    private void ClaimFinish()
    {
        _claimed = true;
        var meters = _bestClimb;
        LogInfo($"Topped out after {meters:F0} m of climbing");
        _finish.Send(new RaceFinish(meters), (accepted, reason) =>
        {
            if (accepted || _finished) return;
            // Refused because someone was first: their result is already on its way. Lost
            // on the way: the claim still holds, so it goes again.
            LogInfo($"Summit claim not accepted: {reason}");
            _claimed = false;
            _nextClaimAt = Game.Time.UnscaledTime + ClaimRetrySeconds;
        });
    }

    /// <summary>Runs on the host only. The first claim heard wins outright, and any later
    /// one is turned down.</summary>
    private void JudgeFinish(HostRequest<RaceFinish> request)
    {
        if (!_started.TryGet(out _))
        {
            request.Reject("The race has not started.");
            return;
        }
        if (_result.TryGet(out var decided))
        {
            request.Reject($"{GetPlayerName(decided.WinnerId)} topped out first.");
            return;
        }

        var meters = request.Message.Meters;
        if (float.IsNaN(meters) || float.IsInfinity(meters) || meters < 0f) meters = 0f;
        request.Publish(_result, new RaceResult(request.FromPlayerId, meters));
    }

    private void OnRaceDecided(RaceResult result)
    {
        // Before the start this client has nothing to finish yet; the start reads it back.
        if (!_racing || (_finished && _winnerId == result.WinnerId)) return;
        _finished = true;
        _winnerId = result.WinnerId;

        if (result.WinnerId == LocalPlayerId)
        {
            Game.Hud.ShowMessage("result", "You topped out first. The race is yours.", NoticeSeconds);
            LogInfo($"Won the race after {result.Meters:F0} m of climbing");
            return;
        }

        var meters = Math.Max(0f, result.Meters);
        if (_climbers.TryGetValue(result.WinnerId, out var known)) meters = Math.Max(meters, known.Meters);
        _climbers[result.WinnerId] = new Climber(meters, false);
        Game.Hud.ShowMessage("result",
            $"{GetPlayerName(result.WinnerId)} topped out first.", NoticeSeconds);
    }

    private void OnRemoteProgress(int fromPlayerId, RaceProgress progress)
        => _climbers[fromPlayerId] = new Climber(Math.Max(0f, progress.Meters), progress.IsOut);

    private void ShowStandings()
    {
        _ordered.Clear();
        foreach (var pair in _climbers) _ordered.Add(pair);
        // Highest climb first; a climber who is out keeps their metres but sinks below anyone
        // still moving, because the ranking answers "who is winning", not "who climbed most".
        _ordered.Sort((left, right) =>
        {
            if (left.Value.IsOut != right.Value.IsOut) return left.Value.IsOut ? 1 : -1;
            return right.Value.Meters.CompareTo(left.Value.Meters);
        });

        _rows.Clear();
        for (var index = 0; index < _ordered.Count; index++)
        {
            var playerId = _ordered[index].Key;
            var climber = _ordered[index].Value;
            var name = playerId == LocalPlayerId ? LocalPlayerName : GetPlayerName(playerId);
            if (playerId == _winnerId) name += "  *";
            _rows.Add(new StandingRow(index + 1, name, $"{climber.Meters:F0} m",
                playerId == LocalPlayerId, climber.IsOut));
        }

        Game.Hud.ShowStandings(_finished ? "RACE - FINISHED" : $"RACE - {Elapsed()}", _rows);
    }

    private string Elapsed()
    {
        var seconds = Math.Max(0, (int)(Game.Time.UnscaledTime - _startedAt));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private void HideStandings()
    {
        _rows.Clear();
        Game.Hud.HideStandings();
    }

    private void Reset()
    {
        _racing = false;
        _finished = false;
        _claimed = false;
        _announcedStart = false;
        _hasStartHeight = false;
        _winnerId = -1;
        _bestClimb = 0f;
        _lastSentClimb = 0f;
        _nextSendAt = 0f;
        _nextClaimAt = 0f;
        _climbers.Clear();
        HideStandings();
    }
}

internal sealed class RaceStarted : IPacket
{
    public void Serialize(BinaryWriter writer) => writer.Write((byte)1);
    public void Deserialize(BinaryReader reader) => reader.ReadByte();
}

internal sealed class RaceProgress : IPacket
{
    public RaceProgress() { }
    public RaceProgress(float meters, bool isOut) { Meters = meters; IsOut = isOut; }

    public float Meters;
    public bool IsOut;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Meters);
        writer.Write(IsOut);
    }

    public void Deserialize(BinaryReader reader)
    {
        Meters = reader.ReadSingle();
        IsOut = reader.ReadBoolean();
    }
}

internal sealed class RaceFinish : IPacket
{
    public RaceFinish() { }
    public RaceFinish(float meters) => Meters = meters;

    public float Meters;

    public void Serialize(BinaryWriter writer) => writer.Write(Meters);
    public void Deserialize(BinaryReader reader) => Meters = reader.ReadSingle();
}

internal sealed class RaceResult : IPacket
{
    public RaceResult() { }
    public RaceResult(int winnerId, float meters) { WinnerId = winnerId; Meters = meters; }

    public int WinnerId;
    public float Meters;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(WinnerId);
        writer.Write(Meters);
    }

    public void Deserialize(BinaryReader reader)
    {
        WinnerId = reader.ReadInt32();
        Meters = reader.ReadSingle();
    }
}
