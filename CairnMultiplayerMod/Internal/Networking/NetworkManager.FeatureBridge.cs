using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayer.Shared.Extensions;
using CairnMultiplayerMod.Internal.Extensions;
using CairnMultiplayerMod.Internal.Networking.Authoritative;

namespace CairnMultiplayerMod.Internal.Networking;

internal sealed partial class NetworkManager
{
    private readonly Dictionary<int, HashSet<string>> _enabledExtensionsByPlayer = new();
    // Admission belongs to actual Steam presence, not a client-controlled disconnect packet.
    private readonly HashSet<ulong> _manifestHandledPeers = new();
    private ExtensionNetworkBridge _extensionBridge;

    private void InitializeExtensionApi()
    {
        _enabledExtensionsByPlayer.Clear();
        _extensionBridge = new ExtensionNetworkBridge(this);
        MultiplayerApi.Runtime.Attach(_extensionBridge);

        if (_steamLobby?.IsHost == true)
        {
            _enabledExtensionsByPlayer[LocalPlayerId] = MultiplayerApi.Runtime.Manifest()
                .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            MultiplayerApi.Runtime.NotifySessionReady();
            return;
        }

        SendSteamPacketToHost(PacketId.ClientExtensionManifest, new ClientExtensionManifest
        {
            Entries = MultiplayerApi.Runtime.Manifest(),
        }, reliable: true);
    }

    private bool HandleExtensionHostPacket(ulong remoteSteamId, PacketId id, System.IO.BinaryReader reader)
    {
        int playerId = EnsureSteamRemotePlayer(remoteSteamId);
        if (id == PacketId.ClientExtensionManifest)
        {
            HandleClientExtensionManifest(remoteSteamId, playerId, reader);
            return true;
        }

        if (!_enabledExtensionsByPlayer.ContainsKey(playerId))
            return true; // Admission is incomplete: ignore every gameplay/API packet.

        if (id == PacketId.ClientExtensionCommand)
        {
            HandleClientExtensionCommand(playerId, reader);
            return true;
        }

        return false;
    }

    private void HandleClientExtensionManifest(
        ulong remoteSteamId,
        int playerId,
        System.IO.BinaryReader reader)
    {
        if (!_manifestHandledPeers.Add(remoteSteamId)) return;
        var manifest = new ClientExtensionManifest();
        manifest.Deserialize(reader);
        var result = ExtensionNegotiator.Negotiate(MultiplayerApi.Runtime.Manifest(), manifest.Entries);
        var enabled = result.EnabledExtensionIds.ToArray();
        SendSteamPayload(new Il2CppSteamworks.CSteamID(remoteSteamId),
            BuildPayload(PacketId.ServerExtensionManifestResult, new ServerExtensionManifestResult
            {
                Accepted = result.Accepted,
                Reason = result.Reason,
                EnabledExtensionIds = enabled,
            }), reliable: true);

        if (!result.Accepted)
        {
            _enabledExtensionsByPlayer.Remove(playerId);
            ModLog.Warning($"[Extensions] Rejected player {playerId}: {result.Reason}");
            return;
        }

        bool wasAdmitted = _enabledExtensionsByPlayer.ContainsKey(playerId);
        _enabledExtensionsByPlayer[playerId] = enabled.ToHashSet(StringComparer.Ordinal);
        SendPeerStatusesTo(playerId);
        if (!wasAdmitted)
            BroadcastPeerStatus(playerId, joined: true);
        SendSteamSnapshotTo(remoteSteamId);
        SendExtensionSnapshotTo(playerId);
        if (!wasAdmitted)
            MultiplayerApi.Runtime.NotifyPlayerJoined(ToApiPlayer(playerId));
        ModLog.Info($"[Extensions] Player {playerId} admitted with {enabled.Length} extension(s).");
    }

    private void HandleClientExtensionCommand(int playerId, System.IO.BinaryReader reader)
    {
        var command = new ClientExtensionCommand();
        command.Deserialize(reader);
        var result = MultiplayerApi.Runtime.ExecuteHostCommand(playerId, command);
        _extensionBridge.SendCommandResult(playerId, new ServerExtensionCommandResult
        {
            RequestId = result.RequestId,
            Status = (ExtensionCommandStatus)result.Status,
            Reason = result.Reason,
        });
    }

    private bool IsExtensionPeerAdmitted(ulong remoteSteamId)
        => _enabledExtensionsByPlayer.ContainsKey(StableSteamPlayerId(remoteSteamId));

    private void HandleExtensionManifestResult(System.IO.BinaryReader reader)
    {
        if (IsHandshakeComplete) return;
        var result = new ServerExtensionManifestResult();
        result.Deserialize(reader);
        if (!result.Accepted)
        {
            IsHandshakeComplete = false;
            LastError = result.Reason;
            OnHandshakeRejected?.Invoke(result.Reason);
            ModLog.Warning($"[Extensions] Host rejected this client: {result.Reason}");
            _steamLobby?.Leave();
            return;
        }

        var enabled = (result.EnabledExtensionIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        _enabledExtensionsByPlayer[LocalPlayerId] = enabled;
        _enabledExtensionsByPlayer[StableSteamPlayerId(_steamHostId)] = new HashSet<string>(enabled, StringComparer.Ordinal);
        IsHandshakeComplete = true;
        MultiplayerApi.Runtime.NotifySessionReady();
        OnHandshakeAck?.Invoke();
    }

    private void HandleExtensionCommandResult(System.IO.BinaryReader reader)
    {
        var result = new ServerExtensionCommandResult();
        result.Deserialize(reader);
        MultiplayerApi.Runtime.CompleteCommand(result);
    }

    private void HandleExtensionEvent(System.IO.BinaryReader reader)
    {
        var packet = new ServerExtensionEvent();
        packet.Deserialize(reader);
        MultiplayerApi.Runtime.ApplyEvent(packet);
    }

    private void HandleExtensionState(System.IO.BinaryReader reader)
    {
        var packet = new ServerExtensionState();
        packet.Deserialize(reader);
        MultiplayerApi.Runtime.ApplyState(packet);
    }

    private void HandleExtensionPeerStatus(System.IO.BinaryReader reader)
    {
        var status = new ServerExtensionPeerStatus();
        status.Deserialize(reader);
        if (!status.Joined)
        {
            var player = ToApiPlayer(status.PlayerId);
            _enabledExtensionsByPlayer.Remove(status.PlayerId);
            MultiplayerApi.Runtime.NotifyPlayerLeft(player);
            return;
        }

        _enabledExtensionsByPlayer[status.PlayerId] =
            (status.EnabledExtensionIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        if (status.PlayerId != LocalPlayerId)
            MultiplayerApi.Runtime.NotifyPlayerJoined(ToApiPlayer(status.PlayerId));
    }

    private void SendPeerStatusesTo(int targetPlayerId)
    {
        foreach (var pair in _enabledExtensionsByPlayer)
        {
            if (pair.Key == targetPlayerId) continue;
            SteamSink.SendTo(targetPlayerId, PacketId.ServerExtensionPeerStatus,
                new ServerExtensionPeerStatus
                {
                    PlayerId = pair.Key,
                    Joined = true,
                    EnabledExtensionIds = pair.Value.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                }, NetReliability.ReliableOrdered);
        }
    }

    private void BroadcastPeerStatus(int playerId, bool joined)
    {
        var enabled = _enabledExtensionsByPlayer.TryGetValue(playerId, out var set)
            ? set.OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var packet = new ServerExtensionPeerStatus
        {
            PlayerId = playerId,
            Joined = joined,
            EnabledExtensionIds = enabled,
        };
        foreach (var target in _enabledExtensionsByPlayer.Keys.ToArray())
        {
            if (target == LocalPlayerId || target == playerId) continue;
            SteamSink.SendTo(target, PacketId.ServerExtensionPeerStatus, packet, NetReliability.ReliableOrdered);
        }
    }

    private void SendExtensionSnapshotTo(int playerId)
    {
        if (!_enabledExtensionsByPlayer.TryGetValue(playerId, out var enabled)) return;
        foreach (var extensionId in enabled)
            foreach (var state in MultiplayerApi.Runtime.SnapshotFor(extensionId))
                SteamSink.SendTo(playerId, PacketId.ServerExtensionState, state, NetReliability.ReliableOrdered);
    }

    private void NotifyExtensionPeerLeft(int playerId)
    {
        if (!_enabledExtensionsByPlayer.ContainsKey(playerId)) return;
        var player = ToApiPlayer(playerId);
        BroadcastPeerStatus(playerId, joined: false);
        _enabledExtensionsByPlayer.Remove(playerId);
        MultiplayerApi.Runtime.NotifyPlayerLeft(player);
    }

    private void ResetExtensionApi()
    {
        _manifestHandledPeers.Clear();
        _enabledExtensionsByPlayer.Clear();
        _extensionBridge = null;
        MultiplayerApi.Runtime.Detach();
    }

    private MultiplayerPlayer ToApiPlayer(int playerId)
    {
        bool isLocal = playerId == LocalPlayerId;
        bool isHost = playerId == StableSteamPlayerId(_steamHostId);
        string name;
        if (isLocal)
            name = _steamLobby?.LocalPersonaName ?? "Player";
        else if (_remotePlayers.TryGetValue(playerId, out var remote))
            name = remote.Name ?? $"Player{playerId}";
        else
            name = $"Player{playerId}";
        return new MultiplayerPlayer(playerId, name, isLocal, isHost);
    }

    private sealed class ExtensionNetworkBridge : IExtensionNetworkBridge
    {
        private readonly NetworkManager _network;
        internal ExtensionNetworkBridge(NetworkManager network) => _network = network;

        public bool IsConnected => _network._steamTransportActive;
        public bool IsHost => _network._steamLobby?.IsHost == true;
        public MultiplayerPlayer LocalPlayer => _network.ToApiPlayer(_network.LocalPlayerId);
        public IReadOnlyList<MultiplayerPlayer> Players
        {
            get
            {
                var players = new List<MultiplayerPlayer> { LocalPlayer };
                players.AddRange(_network._remotePlayers.Keys
                    .Where(_network._enabledExtensionsByPlayer.ContainsKey)
                    .Select(_network.ToApiPlayer));
                return players;
            }
        }

        public bool IsExtensionEnabled(string extensionId, int playerId)
            => _network._enabledExtensionsByPlayer.TryGetValue(playerId, out var enabled) &&
               enabled.Contains(extensionId);

        public void SendCommand(ClientExtensionCommand command)
            => _network.SendSteamPacketToHost(PacketId.ClientExtensionCommand, command, reliable: true);

        public void SendCommandResult(int targetPlayerId, ServerExtensionCommandResult result)
            => _network.SteamSink.SendTo(targetPlayerId, PacketId.ServerExtensionCommandResult,
                result, NetReliability.ReliableOrdered);

        public void BroadcastEvent(ServerExtensionEvent multiplayerEvent, string extensionId)
        {
            foreach (var pair in _network._enabledExtensionsByPlayer)
            {
                if (pair.Key == _network.LocalPlayerId || !pair.Value.Contains(extensionId)) continue;
                _network.SteamSink.SendTo(pair.Key, PacketId.ServerExtensionEvent,
                    multiplayerEvent, NetReliability.ReliableOrdered);
            }
        }

        public void BroadcastState(ServerExtensionState state, string extensionId)
        {
            foreach (var pair in _network._enabledExtensionsByPlayer)
            {
                if (pair.Key == _network.LocalPlayerId || !pair.Value.Contains(extensionId)) continue;
                _network.SteamSink.SendTo(pair.Key, PacketId.ServerExtensionState,
                    state, NetReliability.ReliableOrdered);
            }
        }

        public void ReportExtensionFailure(string extensionId, string message)
            => ModLog.Warning($"[Extensions:{extensionId}] {message}");
    }
}
