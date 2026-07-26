using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Networking.Authoritative;

// ============================================================================
// Phase 3 — step 3: authoritative core for PITON sync.
//
// All of the host's piton logic (validation, official state, rejoin snapshot,
// rebroadcast) lives here, transport-agnostic: it speaks only in playerId and
// writes only through the IAuthoritativeSink. The transport (NetworkManager on the Steam
// side) now only passes it the Client* packets and carries the bytes.
//
// Underlying fix brought by the migration: the HOST assigns an AUTHORITATIVE id to
// each piton (monotonic counter + map (placer, localId) -> authId). Before, each
// client numbered its pitons from 1 on its own -> two different placers
// produced the same id and stepped on each other (snapshot and ghost collision).
// ============================================================================
public sealed class SteamAuthoritativeSession : IAuthoritativeSession
{
    private readonly IAuthoritativeSink _sink;
    private readonly Action<ServerPitonPlaced> _onLocalPitonSpawn;
    private readonly Action<ServerPitonRemoved> _onLocalPitonDespawn;
    private readonly Action<int, int> _onLocalLampApply;

    // Official piton state, indexed by AUTHORITATIVE id.
    private readonly Dictionary<uint, ServerPitonPlaced> _pitons = new();
    // (placer playerId, placer's LOCAL id) -> authoritative id. Makes assignment
    // idempotent and removal translatable without depending on the client's local id.
    private readonly Dictionary<(int playerId, uint clientId), uint> _authIdByClient = new();
    private uint _nextAuthId = 1;

    // Per-player lamp state (playerId -> packed mode). The key IS the playerId,
    // no authoritative id needed: one lamp per player.
    private readonly Dictionary<int, int> _lampByPlayer = new();

    // Weather: authoritative on the host side (guests never publish it).

    public SteamAuthoritativeSession(IAuthoritativeSink sink,
        Action<ServerPitonPlaced> onLocalPitonSpawn,
        Action<ServerPitonRemoved> onLocalPitonDespawn,
        Action<int, int> onLocalLampApply)
    {
        _sink = sink;
        _onLocalPitonSpawn = onLocalPitonSpawn;
        _onLocalPitonDespawn = onLocalPitonDespawn;
        _onLocalLampApply = onLocalLampApply;
    }

    // -- IAuthoritativeSession -------------------------------------------------

    // Pitons persist in-game when a player joins/leaves: nothing to do here.
    public void OnPlayerJoined(int playerId, string playerName) { }
    public void OnPlayerLeft(int playerId) { }

    /// <summary>Client* packet received from a GUEST (relayed by the host transport).</summary>
    public void OnClientPacket(int playerId, PacketId id, BinaryReader payload)
    {
        switch (id)
        {
            case PacketId.ClientPitonPlaced:
            {
                var pkt = new ClientPitonPlaced();
                pkt.Deserialize(payload);
                if (!NetworkManager.IsValidPitonPayload(pkt.PosX, pkt.PosY, pkt.PosZ,
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
            case PacketId.ClientLampState:
            {
                var pkt = new ClientLampState();
                pkt.Deserialize(payload);
                // Applies to the guest's ghost on the host + rebroadcasts except sender.
                ApplyLamp(playerId, pkt.Mode, applyLocalGhost: true, exceptPlayerId: playerId);
                break;
            }
        }
    }

    /// <summary>Rejoin: pushes all the official state (pitons, lamps) to the target player.
    /// Weather and time now replay through the feature framework's own snapshot.</summary>
    public void SendSnapshotTo(int playerId)
    {
        foreach (var pkt in _pitons.Values)
            _sink.SendTo(playerId, PacketId.ServerPitonPlaced, pkt, NetReliability.ReliableOrdered);

        foreach (var kv in _lampByPlayer)
            _sink.SendTo(playerId, PacketId.ServerLampState,
                new ServerLampState { PlayerId = kv.Key, Mode = kv.Value }, NetReliability.ReliableOrdered);
    }

    // -- Placements by the HOST itself (it placed/removed a REAL piton) --------

    public void HandleHostPitonPlaced(int hostPlayerId, uint clientPitonId,
        Vector3 pos, Quaternion rot, byte quality, int hp, int itemId)
    {
        if (!NetworkManager.IsValidPitonPayload(pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w, quality, hp, itemId))
            return;
        var pkt = new ClientPitonPlaced
        {
            PitonId = clientPitonId,
            PosX = pos.x, PosY = pos.y, PosZ = pos.z,
            RotX = rot.x, RotY = rot.y, RotZ = rot.z, RotW = rot.w,
            Quality = quality, PitonHp = hp, ItemId = itemId,
        };
        // No local ghost (the host has the real piton); broadcast to EVERYONE (exceptPlayerId 0).
        Place(hostPlayerId, clientPitonId, ToServerPlaced(hostPlayerId, pkt), spawnLocalGhost: false, exceptPlayerId: 0);
    }

    public void HandleHostPitonRemoved(int hostPlayerId, uint clientPitonId)
        => Remove(hostPlayerId, clientPitonId, despawnLocalGhost: false, exceptPlayerId: 0);

    /// <summary>Lamp of the HOST itself: no local ghost, broadcast to everyone.</summary>
    public void HandleHostLampState(int hostPlayerId, int mode)
        => ApplyLamp(hostPlayerId, mode, applyLocalGhost: false, exceptPlayerId: 0);

    /// <summary>Full reset (scene change / disconnection).</summary>
    public void Reset()
    {
        _pitons.Clear();
        _authIdByClient.Clear();
        _nextAuthId = 1;
        _lampByPlayer.Clear();
    }

    private void ApplyLamp(int playerId, int mode, bool applyLocalGhost, int exceptPlayerId)
    {
        _lampByPlayer[playerId] = mode;
        if (applyLocalGhost) _onLocalLampApply?.Invoke(playerId, mode);
        _sink.Broadcast(PacketId.ServerLampState, new ServerLampState { PlayerId = playerId, Mode = mode },
            exceptPlayerId, NetReliability.ReliableOrdered);
    }

    // -- internal --------------------------------------------------------------

    private void Place(int playerId, uint clientId, ServerPitonPlaced fields, bool spawnLocalGhost, int exceptPlayerId)
    {
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
            PosX = pkt.PosX, PosY = pkt.PosY, PosZ = pkt.PosZ,
            RotX = pkt.RotX, RotY = pkt.RotY, RotZ = pkt.RotZ, RotW = pkt.RotW,
            Quality = pkt.Quality, PitonHp = pkt.PitonHp, ItemId = pkt.ItemId,
        };
}
