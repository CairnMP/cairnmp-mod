using CairnMultiplayerMod.Internal.Diagnostics;
using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>Routes validated transport payloads to the matching network domain.</summary>
internal sealed partial class NetworkManager
{
    /// <summary>
    /// Dispatches a payload received from the transport. The first byte is the packet id;
    /// the remaining bytes are the serialized packet body. Called on the Unity thread.
    /// </summary>
    private void ProcessPacket(byte[] payload)
    {
        // An empty payload is the transport's internal disconnection marker.
        if (payload.Length == 0)
        {
            OnDisconnected?.Invoke(LastError ?? "Connection lost");
            return;
        }

        var id = (PacketId)payload[0];
        if (IsSteamTransportActive && !IsHandshakeComplete
            && id != PacketId.ServerExtensionManifestResult
            && id != PacketId.ServerHandshakeReject)
        {
            return;
        }

        using var stream = new MemoryStream(payload, 1, payload.Length - 1, writable: false);
        using var reader = new BinaryReader(stream);

        switch (id)
        {
            case PacketId.ServerExtensionManifestResult:
                HandleExtensionManifestResult(reader);
                break;
            case PacketId.ServerExtensionCommandResult:
                HandleExtensionCommandResult(reader);
                break;
            case PacketId.ServerExtensionEvent:
                HandleExtensionEvent(reader);
                break;
            case PacketId.ServerExtensionState:
                HandleExtensionState(reader);
                break;
            case PacketId.ServerExtensionPeerStatus:
                HandleExtensionPeerStatus(reader);
                break;
            case PacketId.ServerHandshakeAck:
                HandleHandshakeAck(reader);
                break;
            case PacketId.ServerHandshakeReject:
                HandleHandshakeReject(reader);
                break;
            case PacketId.ServerPlayerJoined:
                HandlePlayerJoined(reader);
                break;
            case PacketId.ServerPlayerLeft:
                HandlePlayerLeft(reader);
                break;
            case PacketId.ServerPlayerState:
                HandlePlayerState(reader);
                break;
            case PacketId.ServerStartGame:
                HandleStartGame(reader);
                break;
            case PacketId.ServerBoneState:
                HandleBoneState(reader);
                break;
            case PacketId.ServerPlayerFrame:
                HandlePlayerFrame(reader);
                break;
            case PacketId.ServerClimbotFrame:
                HandleClimbotFrame(reader);
                break;
            case PacketId.ServerPitonPlaced:
                HandlePitonPlaced(reader);
                break;
            case PacketId.ServerPitonRemoved:
                HandlePitonRemoved(reader);
                break;
            case PacketId.ServerTeleport:
                HandleTeleport(reader);
                break;
            case PacketId.ServerFeatureStream:
                HandleFeatureStream(reader);
                break;
            case PacketId.ServerRopeClip:
                HandleRopeClip(reader);
                break;
            default:
                ModLog.Warning($"[CairnMP] Unknown packet id {id}");
                break;
        }
    }
}
