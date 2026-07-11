using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Networking.Authoritative;

// ============================================================================
// Phase 3 — Authoritative host: transport-agnostic seam.
//
// Observation (2026-07): the host is ALREADY authoritative, but the logic (validation,
// roster, snapshots, rebroadcasting "Server*" to everyone-except-the-sender) is buried inline
// in SteamP2PTransport — mixed together with "how the bytes travel". Impossible to
// test out-of-game, and every new sync has to re-wire the same plumbing.
//
// Goal: extract this logic into a SINGLE authoritative core
// (IAuthoritativeSession) where authority, validation, official state and
// snapshots live. The transport becomes just an IAuthoritativeSink: it carries the bytes,
// it decides nothing. We stay all-Steam; this decoupling keeps the core testable and
// ready for the transport migration to SteamNetworkingSockets.
//
// These types depend ONLY on the shared protocol (PacketId / IPacket) — no Il2Cpp
// type, no SteamId. That's what makes them testable out-of-game.
// ============================================================================

/// <summary>
/// Send reliability, transport-agnostic. Each sink translates it to its own world:
/// LiteNetLib <see cref="!:DeliveryMethod"/>, or the SteamNetworking flags.
/// </summary>
public enum NetReliability
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
public interface IAuthoritativeSink
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

/// <summary>
/// Transport-agnostic authoritative core: holds the session's official state
/// (roster, pitons, weather, lamps, cosmetics...) and enforces the rules. The single
/// place where authority and validation live. Fed by a transport via
/// <see cref="OnClientPacket"/>, it responds by broadcasting the "Server*" packets through the sink.
/// </summary>
public interface IAuthoritativeSession
{
    /// <summary>New player admitted after handshake (playerId assigned by the transport).</summary>
    void OnPlayerJoined(int playerId, string playerName);

    /// <summary>Player gone (disconnect / timeout): purges its state, notifies the others.</summary>
    void OnPlayerLeft(int playerId);

    /// <summary>
    /// A <c>Client*</c> packet received from a player. The core deserializes, validates, updates
    /// the official state, then rebroadcasts the corresponding <c>Server*</c> via the sink.
    /// </summary>
    void OnClientPacket(int playerId, PacketId id, BinaryReader payload);

    /// <summary>
    /// (Re)connection: pushes the full official state (placed pitons, current weather,
    /// lamp states...) to the single target player, to eliminate the
    /// "I connected after the event" desyncs.
    /// </summary>
    void SendSnapshotTo(int playerId);
}
