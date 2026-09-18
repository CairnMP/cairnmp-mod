using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// Marks left on the face.
///
/// A ping is a call for attention that fades in fifteen seconds. A mark is the opposite: a
/// climber says "this hold works" or "not that way", and it stays there for the rest of the
/// session, for everyone, including whoever joins later. Over an evening a rope team ends up
/// writing its own route onto the mountain.
///
/// The host owns the marks so a latecomer receives them all on arrival, and so no client can
/// bury the face under thousands of them.
/// </summary>
internal sealed class TrailMarksFeature : MultiplayerFeature
{
    private const GameKey MarkKey = GameKey.B;

    /// <summary>Aiming this close to one of your own marks removes it instead of stacking.</summary>
    private const float RemoveRangeMeters = 3f;

    private const int MaxMarksPerPlayer = 8;
    private const float NoticeSeconds = 3f;

    private PerPlayerState<TrailMarkSet> _marks;
    private HostCommand<TrailMarkEdit> _edit;

    private readonly Dictionary<int, List<WorldPosition>> _known = new();

    public override string Id => "marks";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _marks = feature.PerPlayerState<TrailMarkSet>("set", OnMarksChanged);
        _edit = feature.HostCommand<TrailMarkEdit>("edit", OnEditRequested);

        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.OnPlayerLeft((playerId, _) => Forget(playerId));
        feature.OnSessionEnded(Reset);
        feature.OnSceneReset(() => Game.World.ClearTrailMarks());
    }

    private void TickInput()
    {
        if (KeyboardCaptured || !IsConnected) return;
        if (!Game.Input.WasKeyPressed(MarkKey)) return;
        if (!Game.World.TryGetAimPoint(out var point)) return;

        _edit.Send(new TrailMarkEdit(point));
    }

    /// <summary>
    /// The host is the only one who edits a set, which keeps the count honest and makes the
    /// same press mean "place" or "remove" for everyone in the same way.
    /// </summary>
    private void OnEditRequested(HostRequest<TrailMarkEdit> request)
    {
        var owner = request.FromPlayerId;
        var positions = new List<WorldPosition>(Current(owner));

        var removed = RemoveNear(positions, request.Message.Position);
        if (!removed)
        {
            if (positions.Count >= MaxMarksPerPlayer)
            {
                // The oldest goes rather than refusing: a full set must not turn the key dead.
                positions.RemoveAt(0);
            }
            positions.Add(request.Message.Position);
        }

        request.Publish(_marks, new TrailMarkSet(positions));
    }

    private static bool RemoveNear(List<WorldPosition> positions, WorldPosition target)
    {
        for (var index = 0; index < positions.Count; index++)
        {
            if (DistanceSquared(positions[index], target) > RemoveRangeMeters * RemoveRangeMeters)
                continue;
            positions.RemoveAt(index);
            return true;
        }
        return false;
    }

    private static float DistanceSquared(WorldPosition left, WorldPosition right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private IReadOnlyList<WorldPosition> Current(int playerId)
        => _known.TryGetValue(playerId, out var positions)
            ? positions
            : (IReadOnlyList<WorldPosition>)System.Array.Empty<WorldPosition>();

    private void OnMarksChanged(int playerId, TrailMarkSet set)
    {
        var positions = set.Positions ?? new List<WorldPosition>();
        _known[playerId] = positions;
        Game.World.SetTrailMarks(playerId, positions);

        if (playerId != LocalPlayerId) return;
        Game.Hud.ShowMessage("marks",
            positions.Count == 0 ? "Mark removed." : $"Mark left ({positions.Count}/{MaxMarksPerPlayer}).",
            NoticeSeconds);
    }

    private void Forget(int playerId)
    {
        _known.Remove(playerId);
        Game.World.SetTrailMarks(playerId, System.Array.Empty<WorldPosition>());
    }

    private void Reset()
    {
        _known.Clear();
        Game.World.ClearTrailMarks();
    }
}

internal sealed class TrailMarkEdit : IPacket
{
    public TrailMarkEdit() { }
    public TrailMarkEdit(WorldPosition position) => Position = position;

    public WorldPosition Position;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Position.X);
        writer.Write(Position.Y);
        writer.Write(Position.Z);
    }

    public void Deserialize(BinaryReader reader)
        => Position = new WorldPosition(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

internal sealed class TrailMarkSet : IPacket
{
    public TrailMarkSet() { }
    public TrailMarkSet(List<WorldPosition> positions) => Positions = positions;

    public List<WorldPosition> Positions = new();

    public void Serialize(BinaryWriter writer)
    {
        var positions = Positions ?? new List<WorldPosition>();
        writer.Write((byte)positions.Count);
        foreach (var position in positions)
        {
            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(position.Z);
        }
    }

    public void Deserialize(BinaryReader reader)
    {
        var count = reader.ReadByte();
        Positions = new List<WorldPosition>(count);
        for (var index = 0; index < count; index++)
            Positions.Add(new WorldPosition(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
    }
}
