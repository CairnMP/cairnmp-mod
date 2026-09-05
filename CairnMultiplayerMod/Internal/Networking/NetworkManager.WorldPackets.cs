using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;

namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>Applies packets that produce world-side actions or feature notifications.</summary>
internal sealed partial class NetworkManager
{
    private void HandlePitonPlaced(BinaryReader reader)
    {
        var packet = new ServerPitonPlaced();
        packet.Deserialize(reader);
        if (!PacketValidation.IsValidPitonPayload(
                packet.PosX, packet.PosY, packet.PosZ,
                packet.RotX, packet.RotY, packet.RotZ, packet.RotW,
                packet.Quality, packet.PitonHp, packet.ItemId))
        {
            return;
        }

        ModLog.Debug($"[Piton] Remote player {packet.FromPlayerId} placed piton #{packet.PitonId}");
        OnPitonPlaced?.Invoke(packet);
    }

    private void HandlePitonRemoved(BinaryReader reader)
    {
        var packet = new ServerPitonRemoved();
        packet.Deserialize(reader);
        OnPitonRemoved?.Invoke(packet);
    }

    private void HandleTeleport(BinaryReader reader)
    {
        var packet = new ServerTeleport();
        packet.Deserialize(reader);
        if (!PacketValidation.IsValidPose(packet.X, packet.Y, packet.Z, packet.Yaw)) return;

        OnTeleport?.Invoke(packet);
    }

    private void HandleFeatureStream(BinaryReader reader)
    {
        var packet = new ServerFeatureStream();
        packet.Deserialize(reader);
        OnFeatureStream?.Invoke(packet.FromPlayerId, packet.Channel, packet.Payload);
    }

    private void HandleRopeClip(BinaryReader reader)
    {
        var packet = new ServerRopeClip();
        packet.Deserialize(reader);
        OnRopeClip?.Invoke(packet.FromPlayerId, packet.TargetPlayerId, packet.Clip);
    }
}
