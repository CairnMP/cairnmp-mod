using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Networking.Authoritative;

// ============================================================================
// Phase 3 — etape 3 : cœur autoritatif de la synchro PITONS.
//
// Toute la logique piton de l'hote (validation, etat officiel, snapshot de rejoin,
// rediffusion) vit ici, agnostique du transport : elle ne parle qu'en playerId et
// n'ecrit que par l'IAuthoritativeSink. Le transport (NetworkManager cote Steam) ne
// fait plus que lui passer les packets Client* et convoyer les octets.
//
// Correctif de fond apporte par la migration : l'HOTE attribue un id AUTORITAIRE a
// chaque piton (compteur monotone + map (poseur, idLocal) -> idAuto). Avant, chaque
// client numerotait ses pitons depuis 1 dans son coin -> deux poseurs differents
// produisaient le meme id et se marchaient dessus (collision de snapshot et de ghost).
// ============================================================================
public sealed class SteamAuthoritativeSession : IAuthoritativeSession
{
    private readonly IAuthoritativeSink _sink;
    private readonly Action<ServerPitonPlaced> _onLocalPitonSpawn;
    private readonly Action<ServerPitonRemoved> _onLocalPitonDespawn;
    private readonly Action<int, int> _onLocalLampApply;

    // Etat officiel des pitons, indexe par id AUTORITAIRE.
    private readonly Dictionary<uint, ServerPitonPlaced> _pitons = new();
    // (playerId poseur, id LOCAL du poseur) -> id autoritaire. Rend l'attribution
    // idempotente et le retrait traduisible sans dependre de l'id local du client.
    private readonly Dictionary<(int playerId, uint clientId), uint> _authIdByClient = new();
    private uint _nextAuthId = 1;

    // Etat lampe par joueur (playerId -> mode empaquete). La cle EST le playerId,
    // pas besoin d'id autoritaire : une seule lampe par joueur.
    private readonly Dictionary<int, int> _lampByPlayer = new();

    // Meteo : autoritaire cote hote (les invites ne la publient jamais).
    private ServerWeatherState _weather;
    private bool _hasWeather;

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

    // Les pitons persistent en jeu quand un joueur rejoint/part : rien a faire ici.
    public void OnPlayerJoined(int playerId, string playerName) { }
    public void OnPlayerLeft(int playerId) { }

    /// <summary>Packet Client* recu d'un INVITE (relaye par le transport hote).</summary>
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
                // Ghost local chez l'hote + rediffusion a tous SAUF l'invite poseur.
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
                // Applique au ghost de l'invite chez l'hote + rediffuse sauf emetteur.
                ApplyLamp(playerId, pkt.Mode, applyLocalGhost: true, exceptPlayerId: playerId);
                break;
            }
        }
    }

    /// <summary>Rejoin : pousse tout l'etat officiel (meteo, pitons, lampes) au joueur cible.</summary>
    public void SendSnapshotTo(int playerId)
    {
        if (_hasWeather)
            _sink.SendTo(playerId, PacketId.ServerWeatherState, _weather, NetReliability.ReliableOrdered);

        foreach (var pkt in _pitons.Values)
            _sink.SendTo(playerId, PacketId.ServerPitonPlaced, pkt, NetReliability.ReliableOrdered);

        foreach (var kv in _lampByPlayer)
            _sink.SendTo(playerId, PacketId.ServerLampState,
                new ServerLampState { PlayerId = kv.Key, Mode = kv.Value }, NetReliability.ReliableOrdered);
    }

    // -- Placements de l'HOTE lui-meme (il a pose/retire un VRAI piton) --------

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
        // Pas de ghost local (l'hote a le vrai piton) ; broadcast a TOUS (exceptPlayerId 0).
        Place(hostPlayerId, clientPitonId, ToServerPlaced(hostPlayerId, pkt), spawnLocalGhost: false, exceptPlayerId: 0);
    }

    public void HandleHostPitonRemoved(int hostPlayerId, uint clientPitonId)
        => Remove(hostPlayerId, clientPitonId, despawnLocalGhost: false, exceptPlayerId: 0);

    /// <summary>Lampe de l'HOTE lui-meme : pas de ghost local, broadcast a tous.</summary>
    public void HandleHostLampState(int hostPlayerId, int mode)
        => ApplyLamp(hostPlayerId, mode, applyLocalGhost: false, exceptPlayerId: 0);

    /// <summary>
    /// Meteo publiee par l'HOTE (source autoritaire). Valide, memorise pour le rejoin,
    /// diffuse a tous. <paramref name="reliable"/> suit l'appelant (rafale vs etat stable).
    /// </summary>
    public void HandleHostWeather(WeatherSyncData state, bool reliable)
    {
        if (!NetworkManager.IsValidWeatherState(state))
            return;
        _weather = new ServerWeatherState { State = state };
        _hasWeather = true;
        _sink.Broadcast(PacketId.ServerWeatherState, _weather, exceptPlayerId: 0,
            reliable ? NetReliability.ReliableOrdered : NetReliability.UnreliableSequenced);
    }

    /// <summary>Reset complet (changement de scene / deconnexion).</summary>
    public void Reset()
    {
        _pitons.Clear();
        _authIdByClient.Clear();
        _nextAuthId = 1;
        _lampByPlayer.Clear();
        _weather = default;
        _hasWeather = false;
    }

    private void ApplyLamp(int playerId, int mode, bool applyLocalGhost, int exceptPlayerId)
    {
        _lampByPlayer[playerId] = mode;
        if (applyLocalGhost) _onLocalLampApply?.Invoke(playerId, mode);
        _sink.Broadcast(PacketId.ServerLampState, new ServerLampState { PlayerId = playerId, Mode = mode },
            exceptPlayerId, NetReliability.ReliableOrdered);
    }

    // -- interne ---------------------------------------------------------------

    private void Place(int playerId, uint clientId, ServerPitonPlaced fields, bool spawnLocalGhost, int exceptPlayerId)
    {
        fields.PitonId = ResolveAuthId(playerId, clientId); // id local -> id autoritaire
        _pitons[fields.PitonId] = fields;
        if (spawnLocalGhost) _onLocalPitonSpawn?.Invoke(fields);
        _sink.Broadcast(PacketId.ServerPitonPlaced, fields, exceptPlayerId, NetReliability.ReliableOrdered);
    }

    private void Remove(int playerId, uint clientId, bool despawnLocalGhost, int exceptPlayerId)
    {
        if (!_authIdByClient.TryGetValue((playerId, clientId), out var authId))
            return; // rien de connu a retirer
        _authIdByClient.Remove((playerId, clientId));
        _pitons.Remove(authId);
        var outPkt = new ServerPitonRemoved { FromPlayerId = playerId, PitonId = authId };
        if (despawnLocalGhost) _onLocalPitonDespawn?.Invoke(outPkt);
        _sink.Broadcast(PacketId.ServerPitonRemoved, outPkt, exceptPlayerId, NetReliability.ReliableOrdered);
    }

    private uint ResolveAuthId(int playerId, uint clientId)
    {
        if (_authIdByClient.TryGetValue((playerId, clientId), out var existing))
            return existing; // idempotent : re-placement du meme piton -> meme id
        var authId = _nextAuthId++;
        _authIdByClient[(playerId, clientId)] = authId;
        return authId;
    }

    private static ServerPitonPlaced ToServerPlaced(int playerId, ClientPitonPlaced pkt)
        => new ServerPitonPlaced
        {
            FromPlayerId = playerId,
            // PitonId est fixe a l'id autoritaire dans Place().
            PosX = pkt.PosX, PosY = pkt.PosY, PosZ = pkt.PosZ,
            RotX = pkt.RotX, RotY = pkt.RotY, RotZ = pkt.RotZ, RotW = pkt.RotW,
            Quality = pkt.Quality, PitonHp = pkt.PitonHp, ItemId = pkt.ItemId,
        };
}
