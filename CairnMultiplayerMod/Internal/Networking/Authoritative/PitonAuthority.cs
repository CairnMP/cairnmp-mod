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
    private readonly Action<uint> _onLocalPitonRecovered;

    private readonly Dictionary<uint, ServerPitonPlaced> _pitons = new();
    // (placer playerId, placer's LOCAL id) -> authoritative id. Makes assignment
    // idempotent and removal translatable without depending on the client's local id.
    private readonly Dictionary<(int playerId, uint clientId), uint> _authIdByClient = new();
    // Placed by the host itself: in the host's world they are its own, not remote copies.
    private readonly HashSet<uint> _hostOwned = new();
    private uint _nextAuthId = 1;
    internal const int MaxPitonsPerPlayer = 128;
    internal const int MaxPitonsPerSession = 1024;
    private readonly PeerBudget _placementBudget;
    private readonly PeerBudget _recoveryBudget;

    internal PitonAuthority(IAuthoritativeSink sink,
        Action<ServerPitonPlaced> onLocalPitonSpawn,
        Action<ServerPitonRemoved> onLocalPitonDespawn)
        : this(sink, onLocalPitonSpawn, onLocalPitonDespawn, null) { }

    internal PitonAuthority(IAuthoritativeSink sink,
        Action<ServerPitonPlaced> onLocalPitonSpawn,
        Action<ServerPitonRemoved> onLocalPitonDespawn, Func<double> clock)
        : this(sink, onLocalPitonSpawn, onLocalPitonDespawn, clock, null) { }

    /// <param name="onLocalPitonRecovered">A client took up one of the host's own pitons;
    /// receives the host's local id for it.</param>
    internal PitonAuthority(IAuthoritativeSink sink,
        Action<ServerPitonPlaced> onLocalPitonSpawn,
        Action<ServerPitonRemoved> onLocalPitonDespawn, Func<double> clock,
        Action<uint> onLocalPitonRecovered)
    {
        _placementBudget = new PeerBudget(16, 2, clock);
        _recoveryBudget = new PeerBudget(16, 2, clock);
        _sink = sink;
        _onLocalPitonSpawn = onLocalPitonSpawn;
        _onLocalPitonDespawn = onLocalPitonDespawn;
        _onLocalPitonRecovered = onLocalPitonRecovered;
    }

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
            case PacketId.ClientPitonRecovered:
                {
                    var pkt = new ClientPitonRecovered();
                    pkt.Deserialize(payload);
                    Recover(playerId, pkt.PitonId, requesterIsHost: false);
                    break;
                }
        }
    }

    /// <summary>Only pitons need this legacy snapshot; feature state has its own replay path.</summary>
    public void SendSnapshotTo(int playerId)
    {
        foreach (var pkt in _pitons.Values)
            _sink.SendTo(playerId, PacketId.ServerPitonPlaced, pkt, NetReliability.ReliableOrdered);
    }


    public void HandleHostPitonPlaced(int hostPlayerId, ClientPitonPlaced packet)
    {
        if (!PacketValidation.IsValidPitonPayload(
                packet.PosX, packet.PosY, packet.PosZ,
                packet.RotX, packet.RotY, packet.RotZ, packet.RotW,
                packet.Quality, packet.PitonHp, packet.ItemId))
            return;
        Place(hostPlayerId, packet.PitonId, ToServerPlaced(hostPlayerId, packet), spawnLocalGhost: false, exceptPlayerId: 0);
    }

    public void HandleHostPitonRemoved(int hostPlayerId, uint clientPitonId)
        => Remove(hostPlayerId, clientPitonId, despawnLocalGhost: false, exceptPlayerId: 0);

    public void HandleHostPitonRecovered(int hostPlayerId, uint pitonId)
        => Recover(hostPlayerId, pitonId, requesterIsHost: true);

    public void Reset()
    {
        _pitons.Clear();
        _authIdByClient.Clear();
        _hostOwned.Clear();
        _nextAuthId = 1;
        _placementBudget.Clear();
        _recoveryBudget.Clear();
    }

    internal void RemovePlayer(int playerId)
    {
        var ids = new List<uint>();
        foreach (var key in _authIdByClient.Keys)
            if (key.playerId == playerId) ids.Add(key.clientId);
        foreach (var id in ids) Remove(playerId, id, despawnLocalGhost: true, exceptPlayerId: playerId);
        _placementBudget.Remove(playerId);
        _recoveryBudget.Remove(playerId);
    }


    private void Place(int playerId, uint clientId, ServerPitonPlaced fields, bool spawnLocalGhost, int exceptPlayerId)
    {
        if (_authIdByClient.ContainsKey((playerId, clientId))) return;
        if (_pitons.Count >= MaxPitonsPerSession || _nextAuthId == uint.MaxValue) return;
        var owned = 0;
        foreach (var key in _authIdByClient.Keys)
            if (key.playerId == playerId) owned++;
        if (owned >= MaxPitonsPerPlayer || !_placementBudget.Take(playerId)) return;
        fields.PitonId = ResolveAuthId(playerId, clientId);
        _pitons[fields.PitonId] = fields;
        if (!spawnLocalGhost) _hostOwned.Add(fields.PitonId);
        if (spawnLocalGhost) _onLocalPitonSpawn?.Invoke(fields);
        _sink.Broadcast(PacketId.ServerPitonPlaced, fields, exceptPlayerId, NetReliability.ReliableOrdered);
    }

    private void Remove(int playerId, uint clientId, bool despawnLocalGhost, int exceptPlayerId)
    {
        if (!_authIdByClient.TryGetValue((playerId, clientId), out var authId))
            return;
        _authIdByClient.Remove((playerId, clientId));
        _pitons.Remove(authId);
        _hostOwned.Remove(authId);
        var outPkt = new ServerPitonRemoved { FromPlayerId = playerId, PitonId = authId };
        if (despawnLocalGhost) _onLocalPitonDespawn?.Invoke(outPkt);
        _sink.Broadcast(PacketId.ServerPitonRemoved, outPkt, exceptPlayerId, NetReliability.ReliableOrdered);
    }

    /// <summary>
    /// A climber took up a piton they did not place. It leaves every other world, its
    /// owner's included — the owner through the one id they know it by. Left alone, the
    /// owner could take it up a second time and the piton would be in two bags at once.
    /// </summary>
    private void Recover(int requesterId, uint authId, bool requesterIsHost)
    {
        if (!_pitons.ContainsKey(authId)) return;

        (int playerId, uint clientId) owner = default;
        var found = false;
        foreach (var pair in _authIdByClient)
        {
            if (pair.Value != authId) continue;
            owner = pair.Key;
            found = true;
            break;
        }
        if (!found) return;

        if (owner.playerId == requesterId)
        {
            Remove(owner.playerId, owner.clientId, despawnLocalGhost: !requesterIsHost, exceptPlayerId: requesterId);
            return;
        }
        if (!_recoveryBudget.Take(requesterId)) return;

        var hostOwned = _hostOwned.Remove(authId);
        _authIdByClient.Remove(owner);
        _pitons.Remove(authId);

        var removed = new ServerPitonRemoved { FromPlayerId = owner.playerId, PitonId = authId };
        // The host's own world, unless the host is the one who took it up.
        if (!requesterIsHost)
        {
            if (hostOwned) _onLocalPitonRecovered?.Invoke(owner.clientId);
            else _onLocalPitonDespawn?.Invoke(removed);
        }

        _sink.Broadcast(PacketId.ServerPitonRemoved, removed, requesterId, NetReliability.ReliableOrdered);
        if (!hostOwned)
            _sink.SendTo(owner.playerId, PacketId.ServerPitonRecovered,
                new ServerPitonRecovered { ByPlayerId = requesterId, PitonId = owner.clientId },
                NetReliability.ReliableOrdered);
    }

    private uint ResolveAuthId(int playerId, uint clientId)
    {
        if (_authIdByClient.TryGetValue((playerId, clientId), out var existing))
            return existing;
        var authId = _nextAuthId++;
        _authIdByClient[(playerId, clientId)] = authId;
        return authId;
    }

    private static ServerPitonPlaced ToServerPlaced(int playerId, ClientPitonPlaced pkt)
        => new ServerPitonPlaced
        {
            FromPlayerId = playerId,
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
