using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// What the rope team did today.
///
/// A session of Cairn is a story nobody writes down: who gained the most height, who came off
/// the wall six times, who spent the evening lying on a ledge. Each climber keeps their own
/// tally and hands it over on request, so the recap is the team's rather than one player's.
/// </summary>
internal sealed class AscentLogFeature : MultiplayerFeature
{
    private const float RecapSeconds = 20f;

    private Broadcast<AscentTally> _tally;

    private readonly Dictionary<int, AscentTally> _team = new();
    private readonly List<StandingRow> _rows = new();
    private readonly List<KeyValuePair<int, AscentTally>> _ordered = new();

    private bool _hasStartHeight, _wasFalling, _wasDown;
    private float _startHeight, _startedAt, _climbed;
    private int _falls, _times;
    private float _showUntil;

    public override string Id => "ascent";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _tally = feature.Broadcast<AscentTally>("tally", OnTeamTally);

        feature.Game.Chat.AddCommand("recap", "/recap",
            "Show what the rope team has done this session", _ => RequestRecap());

        feature.EveryFrame(Tick, FeaturePhase.Always);
        feature.OnPlayerLeft((playerId, _) => _team.Remove(playerId));
        feature.OnSessionEnded(Reset);
    }

    private void Tick()
    {
        if (!IsConnected) return;

        TrackHeight();
        TrackFalls();
        TickRecap();
    }

    private void TrackHeight()
    {
        if (!Game.Players.TryGetLocation(LocalPlayerId, out var me)) return;
        if (!_hasStartHeight)
        {
            _hasStartHeight = true;
            _startHeight = me.Y;
            _startedAt = Game.Time.UnscaledTime;
        }

        var climbed = me.Y - _startHeight;
        if (climbed > _climbed) _climbed = climbed;
    }

    private void TrackFalls()
    {
        var falling = Game.Players.IsLocalPlayerFalling;
        if (falling && !_wasFalling) _falls++;
        _wasFalling = falling;

        var down = Game.Life.IsLocalPlayerDown;
        if (down && !_wasDown) _times++;
        _wasDown = down;
    }

    /// <summary>
    /// Asking for the recap sends your own tally and shows what has arrived. Everyone else
    /// answers the same way, so a moment later the table is complete for whoever asked.
    /// </summary>
    private void RequestRecap()
    {
        // The race already owns the standings surface, and its own table says more.
        if (Rules.Mode == MultiplayerMode.Race)
        {
            Game.Chat.AddSystemLine("The race standings are already on screen.");
            return;
        }

        _team[LocalPlayerId] = Snapshot();
        _tally.Send(Snapshot());
        _showUntil = Game.Time.UnscaledTime + RecapSeconds;
        ShowRecap();
    }

    private AscentTally Snapshot() => new(_climbed, ElapsedMinutes(), _falls, _times);

    private int ElapsedMinutes()
        => _hasStartHeight ? (int)((Game.Time.UnscaledTime - _startedAt) / 60f) : 0;

    private void OnTeamTally(int fromPlayerId, AscentTally tally)
    {
        _team[fromPlayerId] = tally;
        // Somebody asking pulls everyone's tally, so answer with ours unless we just asked.
        if (Game.Time.UnscaledTime > _showUntil) _tally.Send(Snapshot());
        if (_showUntil > Game.Time.UnscaledTime) ShowRecap();
    }

    private void TickRecap()
    {
        if (_showUntil <= 0f || Game.Time.UnscaledTime < _showUntil) return;
        _showUntil = 0f;
        Game.Hud.HideStandings();
    }

    private void ShowRecap()
    {
        _ordered.Clear();
        foreach (var pair in _team) _ordered.Add(pair);
        _ordered.Sort((left, right) => right.Value.Climbed.CompareTo(left.Value.Climbed));

        _rows.Clear();
        for (var index = 0; index < _ordered.Count; index++)
        {
            var playerId = _ordered[index].Key;
            var tally = _ordered[index].Value;
            var name = playerId == LocalPlayerId ? LocalPlayerName : GetPlayerName(playerId);
            var detail = tally.TimesDown > 0
                ? $"{tally.Climbed:F0} m  {tally.Falls}f {tally.TimesDown}d"
                : $"{tally.Climbed:F0} m  {tally.Falls}f";
            _rows.Add(new StandingRow(index + 1, name, detail, playerId == LocalPlayerId, false));
        }

        Game.Hud.ShowStandings($"ASCENT - {ElapsedMinutes()} min", _rows);
    }

    private void Reset()
    {
        _team.Clear();
        _rows.Clear();
        _hasStartHeight = false;
        _wasFalling = false;
        _wasDown = false;
        _climbed = 0f;
        _falls = 0;
        _times = 0;
        _showUntil = 0f;
        Game.Hud.HideStandings();
    }
}

internal sealed class AscentTally : IPacket
{
    public AscentTally() { }
    public AscentTally(float climbed, int minutes, int falls, int timesDown)
    {
        Climbed = climbed;
        Minutes = minutes;
        Falls = falls;
        TimesDown = timesDown;
    }

    public float Climbed;
    public int Minutes;
    public int Falls;
    public int TimesDown;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Climbed);
        writer.Write(Minutes);
        writer.Write(Falls);
        writer.Write(TimesDown);
    }

    public void Deserialize(BinaryReader reader)
    {
        Climbed = reader.ReadSingle();
        Minutes = reader.ReadInt32();
        Falls = reader.ReadInt32();
        TimesDown = reader.ReadInt32();
    }
}
