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
/// </summary>
internal sealed class RaceFeature : MultiplayerFeature
{
    /// <summary>Progress is broadcast when it moves this far, so a hard pitch costs one
    /// message every few metres rather than one per frame.</summary>
    private const float ReportEveryMeters = 3f;

    /// <summary>...and at least this often while climbing, so slow progress still shows.</summary>
    private const float ReportEverySeconds = 5f;

    private const float NoticeSeconds = 8f;

    private HostState<RaceStarted> _started;
    private Broadcast<RaceProgress> _progress;
    private Broadcast<RaceWon> _won;

    private readonly Dictionary<int, Climber> _climbers = new();
    private readonly List<StandingRow> _rows = new();
    private readonly List<KeyValuePair<int, Climber>> _ordered = new();

    private bool _racing, _finished, _hasStartHeight, _announcedStart;
    private float _startHeight, _startedAt, _bestClimb, _lastSentClimb, _nextSendAt;
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
        _won = feature.Broadcast<RaceWon>("won", OnRemoteWin);

        // Always: the standings belong on screen in a bivouac and while streaming too, and a
        // climber who is down still holds their place in the ranking.
        feature.EveryFrame(Tick, FeaturePhase.Always);

        feature.OnPlayerLeft((playerId, _) => _climbers.Remove(playerId));
        feature.OnSessionEnded(Reset);
    }

    private void Tick()
    {
        if (Rules.Mode != MultiplayerMode.Race || !IsConnected)
        {
            if (_racing || _rows.Count > 0) Reset();
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
        _winnerId = -1;
        _hasStartHeight = false;
        _bestClimb = 0f;
        _lastSentClimb = 0f;
        _startedAt = Game.Time.UnscaledTime;
        Game.Hud.ShowMessage("start", "Race! Climb as high as you can.", NoticeSeconds);
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

        if (!_finished && Game.State.HasReachedSummit) WinLocally();
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

    private void WinLocally()
    {
        _finished = true;
        _winnerId = LocalPlayerId;
        _won.Send(new RaceWon(_bestClimb));
        Game.Hud.ShowMessage("result", "You topped out first. The race is yours.", NoticeSeconds);
        LogInfo($"Won the race after {_bestClimb:F0} m of climbing");
    }

    private void OnRemoteProgress(int fromPlayerId, RaceProgress progress)
        => _climbers[fromPlayerId] = new Climber(Math.Max(0f, progress.Meters), progress.IsOut);

    private void OnRemoteWin(int fromPlayerId, RaceWon won)
    {
        // First message in wins. Among friends that is enough; there is no referee to appeal
        // to, and the alternative is an arbitration nobody asked for.
        if (_finished) return;
        _finished = true;
        _winnerId = fromPlayerId;
        _climbers[fromPlayerId] = new Climber(Math.Max(0f, won.Meters), false);
        Game.Hud.ShowMessage("result",
            $"{GetPlayerName(fromPlayerId)} topped out first.", NoticeSeconds);
    }

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

    private void Reset()
    {
        _racing = false;
        _finished = false;
        _announcedStart = false;
        _hasStartHeight = false;
        _winnerId = -1;
        _bestClimb = 0f;
        _lastSentClimb = 0f;
        _nextSendAt = 0f;
        _climbers.Clear();
        _rows.Clear();
        Game.Hud.HideStandings();
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

internal sealed class RaceWon : IPacket
{
    public RaceWon() { }
    public RaceWon(float meters) => Meters = meters;

    public float Meters;

    public void Serialize(BinaryWriter writer) => writer.Write(Meters);
    public void Deserialize(BinaryReader reader) => Meters = reader.ReadSingle();
}
