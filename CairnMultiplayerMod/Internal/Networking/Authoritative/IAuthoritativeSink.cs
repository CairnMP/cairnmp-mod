using System.Collections.Generic;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative
{
    /// <summary>
    /// Send reliability, transport-agnostic. Each sink translates it to its own world:
    /// LiteNetLib <see cref="!:DeliveryMethod"/>, or the SteamNetworking flags.
    /// </summary>
    internal enum NetReliability
    {
        /// <summary>Position / bones / netframes: loss tolerated, last value wins.</summary>
        UnreliableSequenced,
        /// <summary>Pitons / chat / handshake / snapshots: guaranteed, ordered delivery.</summary>
        ReliableOrdered,
    }

    /// <summary>
    /// OUTBOUND channel from the authoritative core to the transport. The core reasons only
    /// in terms of <c>playerId</c> (int); it's the sink that knows which NetPeer / SteamId that
    /// maps to and how to route the bytes.
    /// </summary>
    internal interface IAuthoritativeSink
    {
        /// <summary>The currently connected playerIds (for iterating / snapshots).</summary>
        IReadOnlyCollection<int> ConnectedPlayerIds { get; }

        /// <summary>Sends a packet to a specific player.</summary>
        void SendTo(int playerId, PacketId id, IPacket packet, NetReliability reliability);

        /// <summary>
        /// Broadcasts a packet to all players, optionally excluding the sender.
        /// <paramref name="exceptPlayerId"/> = 0 means "to everyone, sender included"
        /// (useful for an authoritative confirmation, cf. ServerRopeClip).
        /// </summary>
        void Broadcast(PacketId id, IPacket packet, int exceptPlayerId, NetReliability reliability);
    }

}
