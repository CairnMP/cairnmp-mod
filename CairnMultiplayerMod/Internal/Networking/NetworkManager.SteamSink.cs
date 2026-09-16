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

    public IAuthoritativeSink SteamSink => _steamSink ??= new SteamAuthoritativeSink(this);

    private PitonAuthority _pitonAuthority;

    private PitonAuthority Pitons => _pitonAuthority ??= new PitonAuthority(
        SteamSink,
        pkt => OnPitonPlaced?.Invoke(pkt),
        pkt => OnPitonRemoved?.Invoke(pkt));

    private sealed class SteamAuthoritativeSink : IAuthoritativeSink
    {
        private readonly NetworkManager _t;

        public SteamAuthoritativeSink(NetworkManager transport) => _t = transport;

        public IReadOnlyCollection<int> ConnectedPlayerIds => _t._steamIdsByPlayerId.Keys;

        public void SendTo(int playerId, PacketId id, IPacket packet, NetReliability reliability)
        {
            if (!_t._steamIdsByPlayerId.TryGetValue(playerId, out var steamId)) return;
            if (steamId == 0 || steamId == _t._steamLocalId) return;
            _t.SendSteamPayload(new CSteamID(steamId), BuildPayload(id, packet),
                reliability == NetReliability.ReliableOrdered);
        }

        public void Broadcast(PacketId id, IPacket packet, int exceptPlayerId, NetReliability reliability)
        {
            ulong exceptSteamId = 0;
            if (exceptPlayerId != 0)
                _t._steamIdsByPlayerId.TryGetValue(exceptPlayerId, out exceptSteamId);
            _t.BroadcastSteamServerPacket(id, packet, exceptSteamId,
                reliability == NetReliability.ReliableOrdered);
        }
    }
}
