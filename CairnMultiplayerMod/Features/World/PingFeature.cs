using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

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
        feature.EveryFrame(PingMarkerManager.Update, FeaturePhase.Always);

        feature.OnDrawHud(PingMarkerManager.OnGUI);
        feature.OnSessionEnded(PingMarkerManager.ClearAll);
    }

    private void TickInput()
    {
        if (KeyboardCaptured) return;
        if (!FreecamApi.TryIsActive(out var freecamActive) || !freecamActive) return;
        if (Time.unscaledTime < _cooldownUntil) return;

        var pressed = Mouse.current?.leftButton.wasPressedThisFrame == true
                      || Gamepad.current?.rightShoulder.wasPressedThisFrame == true;
        if (!pressed) return;

        // We always aim along the camera's direction (screen centre) — consistent for
        // mouse and pad alike.
        if (!FreecamApi.TryComputePingPoint(out var point)) return;

        _cooldownUntil = Time.unscaledTime + Protocol.PingCooldownSeconds;

        // Shown locally straight away, so the ping feels instant even in solo; the others
        // get it over the network. Broadcast never echoes back to the sender.
        PingMarkerManager.Spawn(LocalPlayerId, point);
        _placed.Send(new PingPlaced(point));
        Mod.Log.Msg($"[Ping] Placed @ ({point.x:F1},{point.y:F1},{point.z:F1})");
    }

    private static void ShowRemotePing(int fromPlayerId, PingPlaced ping)
        => PingMarkerManager.Spawn(fromPlayerId, ping.Position);
}

/// <summary>Where a player dropped a ping.</summary>
internal sealed class PingPlaced : IPacket
{
    public PingPlaced() { }
    public PingPlaced(Vector3 position) => Position = position;

    public Vector3 Position;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Position.x);
        writer.Write(Position.y);
        writer.Write(Position.z);
    }

    public void Deserialize(BinaryReader reader)
        => Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
