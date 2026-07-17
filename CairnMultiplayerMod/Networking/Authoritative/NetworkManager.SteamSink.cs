using System.Collections.Generic;
using CairnMultiplayer.Shared;
using Il2CppSteamworks;

namespace CairnMultiplayerMod.Networking;

// ============================================================================
// Phase 3 — step 2: adapter exposing the host's Steam relay as an
// IAuthoritativeSink.
//
// This is the SINGLE place that translates playerId <-> CSteamID and NetReliability ->
// Steam flag. The future authoritative core (IAuthoritativeSession) will speak only in
// playerId through this sink; it will never be aware of Steam.
//
// Non-destructive: we don't touch the big existing switch in SteamP2PTransport.
// Step 3 will move the logic there one case at a time, routing it to
// the core which, in turn, will respond via this sink.
// ============================================================================
public partial class NetworkManager
{
    private IAuthoritativeSink _steamSink;

    /// <summary>
    /// Steam sink (host side) for the authoritative core. Created lazily; reuses
    /// the existing send helpers (BuildPayload / SendSteamPayload / BroadcastSteamServerPacket).
    /// </summary>
    public IAuthoritativeSink SteamSink => _steamSink ??= new SteamAuthoritativeSink(this);

    private SteamAuthoritativeSession _steamSession;

    /// <summary>
    /// Authoritative core (pitons for now). Created lazily, wired to the Steam
    /// sink and to this NetworkManager's OnPiton* events (local ghosts on the host side).
    /// </summary>
    private SteamAuthoritativeSession SteamSession => _steamSession ??= new SteamAuthoritativeSession(
        SteamSink,
        pkt => OnPitonPlaced?.Invoke(pkt),
        pkt => OnPitonRemoved?.Invoke(pkt),
        ApplyLocalLampState);

    // Applies a guest's lamp state to their local ghost (host side), as the
    // inline ClientLampState case did before the migration.
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
