using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

/// <summary>
/// Transport-independent authority for piton state. It validates client requests, assigns
/// collision-free ids, owns the late-join snapshot and emits committed packets through a sink.
/// </summary>
internal sealed class PitonAuthority
{
    private readonly IAuthoritativeSink _sink;
    private readonly Action<ServerPitonPlaced> _onLocalPitonSpawn;
    private readonly Action<ServerPitonRemoved> _onLocalPitonDespawn;

    // Official piton state, indexed by AUTHORITATIVE id.
    private readonly Dictionary<uint, ServerPitonPlaced> _pitons = new();
    // (placer playerId, placer's LOCAL id) -> authoritative id. Makes assignment
    // idempotent and removal translatable without depending on the client's local id.
    private readonly Dictionary<(int playerId, uint clientId), uint> _authIdByClient = new();
    private uint _nextAuthId = 1;
    internal const int MaxPitonsPerPlayer = 128;
    internal const int MaxPitonsPerSession = 1024;
    private readonly PeerBudget _placementBudget;

    internal PitonAuthority(IAuthoritativeSink sink,
        Action<ServerPitonPlaced> onLocalPitonSpawn,
        Action<ServerPitonRemoved> onLocalPitonDespawn)
        : this(sink, onLocalPitonSpawn, onLocalPitonDespawn, null) { }

    internal PitonAuthority(IAuthoritativeSink sink,
        Action<ServerPitonPlaced> onLocalPitonSpawn,
        Action<ServerPitonRemoved> onLocalPitonDespawn, Func<double> clock)
    {
        _placementBudget = new PeerBudget(16, 2, clock);
        _sink = sink;
        _onLocalPitonSpawn = onLocalPitonSpawn;
        _onLocalPitonDespawn = onLocalPitonDespawn;
    }

    /// <summary>Client* packet received from a GUEST (relayed by the host transport).</summary>
    public void OnClientPacket(int playerId, PacketId id, BinaryReader payload)
    {
        switch (id)
        {
            case PacketId.ClientPitonPlaced:
                {
                    var pkt = new ClientPitonPlaced();
                    pkt.Deserialize(payload);
                    if (!PacketValidation.IsValidPitonPayload(pkt.PosX, pkt.PosY, pkt.PosZ,
                            pkt.RotX, pkt.RotY, pkt.RotZ, pkt.RotW, pkt.Quality, pkt.PitonHp, pkt.ItemId))
                        return;
                    // Local ghost on the host + rebroadcast to everyone EXCEPT the placing guest.
                    Place(playerId, pkt.PitonId, ToServerPlaced(playerId, pkt), spawnLocalGhost: true, exceptPlayerId: playerId);
                    break;
                }
            case PacketId.ClientPitonRemoved:
                {
                    var pkt = new ClientPitonRemoved();
                    pkt.Deserialize(payload);
                    Remove(playerId, pkt.PitonId, despawnLocalGhost: true, exceptPlayerId: playerId);
                    break;
                }
        }
    }

    /// <summary>Rejoin: pushes the official piton state to the target player.
    /// Weather and time now replay through the feature framework's own snapshot.</summary>
    public void SendSnapshotTo(int playerId)
    {
        foreach (var pkt in _pitons.Values)
            _sink.SendTo(playerId, PacketId.ServerPitonPlaced, pkt, NetReliability.ReliableOrdered);
    }

    // -- Placements by the HOST itself (it placed/removed a REAL piton) --------

    public void HandleHostPitonPlaced(int hostPlayerId, ClientPitonPlaced packet)
    {
        if (!PacketValidation.IsValidPitonPayload(
                packet.PosX, packet.PosY, packet.PosZ,
                packet.RotX, packet.RotY, packet.RotZ, packet.RotW,
                packet.Quality, packet.PitonHp, packet.ItemId))
            return;
        // No local ghost (the host has the real piton); broadcast to EVERYONE (exceptPlayerId 0).
        Place(hostPlayerId, packet.PitonId, ToServerPlaced(hostPlayerId, packet), spawnLocalGhost: false, exceptPlayerId: 0);
    }

    public void HandleHostPitonRemoved(int hostPlayerId, uint clientPitonId)
        => Remove(hostPlayerId, clientPitonId, despawnLocalGhost: false, exceptPlayerId: 0);

    /// <summary>Full reset (scene change / disconnection).</summary>
    public void Reset()
    {
        _pitons.Clear();
        _authIdByClient.Clear();
        _nextAuthId = 1;
        _placementBudget.Clear();
    }

    internal void RemovePlayer(int playerId)
    {
        var ids = new List<uint>();
        foreach (var key in _authIdByClient.Keys)
            if (key.playerId == playerId) ids.Add(key.clientId);
        foreach (var id in ids) Remove(playerId, id, despawnLocalGhost: true, exceptPlayerId: playerId);
        _placementBudget.Remove(playerId);
    }

    // -- internal --------------------------------------------------------------

    private void Place(int playerId, uint clientId, ServerPitonPlaced fields, bool spawnLocalGhost, int exceptPlayerId)
    {
        if (_authIdByClient.ContainsKey((playerId, clientId))) return;
        if (_pitons.Count >= MaxPitonsPerSession || _nextAuthId == uint.MaxValue) return;
        var owned = 0;
        foreach (var key in _authIdByClient.Keys)
            if (key.playerId == playerId) owned++;
        if (owned >= MaxPitonsPerPlayer || !_placementBudget.Take(playerId)) return;
        fields.PitonId = ResolveAuthId(playerId, clientId); // local id -> authoritative id
        _pitons[fields.PitonId] = fields;
        if (spawnLocalGhost) _onLocalPitonSpawn?.Invoke(fields);
        _sink.Broadcast(PacketId.ServerPitonPlaced, fields, exceptPlayerId, NetReliability.ReliableOrdered);
    }

    private void Remove(int playerId, uint clientId, bool despawnLocalGhost, int exceptPlayerId)
    {
        if (!_authIdByClient.TryGetValue((playerId, clientId), out var authId))
            return; // nothing known to remove
        _authIdByClient.Remove((playerId, clientId));
        _pitons.Remove(authId);
        var outPkt = new ServerPitonRemoved { FromPlayerId = playerId, PitonId = authId };
        if (despawnLocalGhost) _onLocalPitonDespawn?.Invoke(outPkt);
        _sink.Broadcast(PacketId.ServerPitonRemoved, outPkt, exceptPlayerId, NetReliability.ReliableOrdered);
    }

    private uint ResolveAuthId(int playerId, uint clientId)
    {
        if (_authIdByClient.TryGetValue((playerId, clientId), out var existing))
            return existing; // idempotent: re-placing the same piton -> same id
        var authId = _nextAuthId++;
        _authIdByClient[(playerId, clientId)] = authId;
        return authId;
    }

    private static ServerPitonPlaced ToServerPlaced(int playerId, ClientPitonPlaced pkt)
        => new ServerPitonPlaced
        {
            FromPlayerId = playerId,
            // PitonId is set to the authoritative id in Place().
            PosX = pkt.PosX,
            PosY = pkt.PosY,
            PosZ = pkt.PosZ,
            RotX = pkt.RotX,
            RotY = pkt.RotY,
            RotZ = pkt.RotZ,
            RotW = pkt.RotW,
            Quality = pkt.Quality,
            PitonHp = pkt.PitonHp,
            ItemId = pkt.ItemId,
        };
}
