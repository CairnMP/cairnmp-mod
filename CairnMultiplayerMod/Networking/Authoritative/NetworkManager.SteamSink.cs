using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Networking.Authoritative;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Networking;

// ============================================================================
// Phase 3 — etape 2 : adaptateur exposant le relais Steam de l'hote comme
// IAuthoritativeSink.
//
// C'est l'UNIQUE endroit qui traduit playerId <-> CSteamID et NetReliability ->
// flag Steam. Le futur cœur autoritatif (IAuthoritativeSession) ne parlera qu'en
// playerId a travers ce sink ; il n'aura jamais connaissance de Steam.
//
// Non destructif : on ne touche pas au gros switch existant de SteamP2PTransport.
// L'etape 3 y deplacera la logique un case a la fois, en la faisant router vers
// le cœur qui, lui, repondra via ce sink.
// ============================================================================
public partial class NetworkManager
{
    private IAuthoritativeSink _steamSink;

    /// <summary>
    /// Sink Steam (cote hote) pour le cœur autoritatif. Cree paresseusement ; reutilise
    /// les helpers d'envoi existants (BuildPayload / SendSteamPayload / BroadcastSteamServerPacket).
    /// </summary>
    public IAuthoritativeSink SteamSink => _steamSink ??= new SteamAuthoritativeSink(this);

    private SteamAuthoritativeSession _steamSession;

    /// <summary>
    /// Cœur autoritatif (pitons pour l'instant). Cree paresseusement, cable sur le sink
    /// Steam et sur les events OnPiton* de ce NetworkManager (ghosts locaux cote hote).
    /// </summary>
    private SteamAuthoritativeSession SteamSession => _steamSession ??= new SteamAuthoritativeSession(
        SteamSink,
        pkt => OnPitonPlaced?.Invoke(pkt),
        pkt => OnPitonRemoved?.Invoke(pkt),
        ApplyLocalLampState);

    // Applique l'etat lampe d'un invite sur son ghost local (cote hote), comme le
    // faisait le case ClientLampState inline avant la migration.
    private void ApplyLocalLampState(int playerId, int mode)
    {
        if (_remotePlayers.TryGetValue(playerId, out var rp))
        {
            rp.LampMode = mode;
            rp.HasLampState = true;
        }
    }

    private sealed class SteamAuthoritativeSink : IAuthoritativeSink
    {
        private readonly NetworkManager _t;

        public SteamAuthoritativeSink(NetworkManager transport) => _t = transport;

        // Les Keys d'un Dictionary<int,ulong> implementent IReadOnlyCollection<int>.
        public IReadOnlyCollection<int> ConnectedPlayerIds => _t._steamIdsByPlayerId.Keys;

        public void SendTo(int playerId, PacketId id, IPacket packet, NetReliability reliability)
        {
            if (!_t._steamIdsByPlayerId.TryGetValue(playerId, out var steamId)) return;
            if (steamId == 0 || steamId == _t._steamLocalId) return; // jamais a soi-meme
            _t.SendSteamPayload(new CSteamID(steamId), BuildPayload(id, packet),
                reliability == NetReliability.ReliableOrdered);
        }

        public void Broadcast(PacketId id, IPacket packet, int exceptPlayerId, NetReliability reliability)
        {
            // exceptPlayerId == 0 -> a tout le monde (exceptSteamId 0 = personne d'exclu).
            ulong exceptSteamId = 0;
            if (exceptPlayerId != 0)
                _t._steamIdsByPlayerId.TryGetValue(exceptPlayerId, out exceptSteamId);
            _t.BroadcastSteamServerPacket(id, packet, exceptSteamId,
                reliability == NetReliability.ReliableOrdered);
        }
    }
}
