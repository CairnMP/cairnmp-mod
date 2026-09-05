using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>
/// Steam adapter for authoritative services. This is the only translation between stable
/// player ids, Steam ids and transport reliability.
/// </summary>
internal sealed partial class NetworkManager
{
    private IAuthoritativeSink _steamSink;

    /// <summary>
    /// Steam sink (host side) for the authoritative core. Created lazily; reuses
    /// the existing send helpers (BuildPayload / SendSteamPayload / BroadcastSteamServerPacket).
    /// </summary>
    public IAuthoritativeSink SteamSink => _steamSink ??= new SteamAuthoritativeSink(this);

    private PitonAuthority _pitonAuthority;

    /// <summary>
    /// Piton authority. Created lazily, wired to the Steam
    /// sink and to this NetworkManager's OnPiton* events (local ghosts on the host side).
    /// </summary>
    private PitonAuthority Pitons => _pitonAuthority ??= new PitonAuthority(
        SteamSink,
        pkt => OnPitonPlaced?.Invoke(pkt),
        pkt => OnPitonRemoved?.Invoke(pkt));

    private sealed class SteamAuthoritativeSink : IAuthoritativeSink
    {
        private readonly NetworkManager _t;

        public SteamAuthoritativeSink(NetworkManager transport) => _t = transport;

        // The Keys of a Dictionary<int,ulong> implement IReadOnlyCollection<int>.
        public IReadOnlyCollection<int> ConnectedPlayerIds => _t._steamIdsByPlayerId.Keys;

        public void SendTo(int playerId, PacketId id, IPacket packet, NetReliability reliability)
        {
            if (!_t._steamIdsByPlayerId.TryGetValue(playerId, out var steamId)) return;
            if (steamId == 0 || steamId == _t._steamLocalId) return; // never to oneself
            _t.SendSteamPayload(new CSteamID(steamId), BuildPayload(id, packet),
                reliability == NetReliability.ReliableOrdered);
        }

        public void Broadcast(PacketId id, IPacket packet, int exceptPlayerId, NetReliability reliability)
        {
            // exceptPlayerId == 0 -> to everyone (exceptSteamId 0 = nobody excluded).
            ulong exceptSteamId = 0;
            if (exceptPlayerId != 0)
                _t._steamIdsByPlayerId.TryGetValue(exceptPlayerId, out exceptSteamId);
            _t.BroadcastSteamServerPacket(id, packet, exceptSteamId,
                reliability == NetReliability.ReliableOrdered);
        }
    }
}
