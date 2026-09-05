using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features.World;

/// <summary>
/// Marking a spot for the others. In free camera, a left click (or R1/RB on a pad) drops a
/// coloured waypoint on the aimed point, visible to every player for a few seconds.
///
/// Reference feature for the framework: the whole thing — input, network, cleanup — lives in
/// this one file. Nothing was added to Mod, to the networking layer or to the protocol.
/// </summary>
internal sealed class PingFeature : MultiplayerFeature
{
    public override string Id => "ping";

    private Broadcast<PingPlaced> _placed;
    private float _cooldownUntil;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _placed = feature.Broadcast<PingPlaced>("placed", ShowRemotePing);

        // Always: pings are placed from the free camera, which the game treats as
        // non-gameplay, and the markers must keep expiring even during a bivouac.
        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.EveryFrame(Game.World.TickPings, FeaturePhase.Always);

        feature.OnDrawHud(Game.World.DrawPings);
        feature.OnSessionEnded(Game.World.ClearPings);
    }

    private void TickInput()
    {
        if (KeyboardCaptured) return;
        if (!Game.World.IsFreeCameraActive) return;
        if (Game.Time.UnscaledTime < _cooldownUntil) return;

        var pressed = Game.Input.WasPressed(GameInputAction.PrimaryPointer)
                      || Game.Input.WasPressed(GameInputAction.PingController);
        if (!pressed) return;

        // We always aim along the camera's direction (screen centre) — consistent for
        // mouse and pad alike.
        if (!Game.World.TryGetAimPoint(out var point)) return;

        _cooldownUntil = Game.Time.UnscaledTime + Protocol.PingCooldownSeconds;

        // Shown locally straight away, so the ping feels instant even in solo; the others
        // get it over the network. Broadcast never echoes back to the sender.
        Game.World.SpawnPing(LocalPlayerId, point);
        _placed.Send(new PingPlaced(point));
        LogInfo($"Placed @ ({point.X:F1},{point.Y:F1},{point.Z:F1})");
    }

    private void ShowRemotePing(int fromPlayerId, PingPlaced ping)
        => Game.World.SpawnPing(fromPlayerId, ping.Position);
}

/// <summary>Where a player dropped a ping.</summary>
internal sealed class PingPlaced : IPacket
{
    public PingPlaced() { }
    public PingPlaced(WorldPosition position) => Position = position;

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
