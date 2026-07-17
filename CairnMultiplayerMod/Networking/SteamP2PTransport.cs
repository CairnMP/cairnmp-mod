using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSteamworks;
using UnityEngine;

namespace CairnMultiplayerMod.Networking;

public partial class NetworkManager
{
    private const int SteamGameplayChannel = 0;
    private const double SteamReliablePresenceIntervalSeconds = 1.0;

    private SteamLobbyManager _steamLobby;
    private bool _steamTransportActive;
    private ulong _steamLocalId;
    private ulong _steamHostId;
    private double _lastReliablePresenceSentAt;
    private double _lastSteamMemberRefreshAt;
    private double _lastSteamSendFailLogAt;
    private readonly Dictionary<ulong, int> _steamPlayerIds = new();
    private readonly Dictionary<int, ulong> _steamIdsByPlayerId = new();
    private readonly Dictionary<ulong, string> _steamPlayerNames = new();
    private readonly Dictionary<ulong, double> _steamReliableForwardTimes = new();
    // Piton + lamp + weather state moved into SteamAuthoritativeSession (Phase 3 steps 3-4).
    private readonly Dictionary<int, byte> _steamCosmeticSnapshot = new();
    private readonly Dictionary<int, byte[]> _steamHandPoseSnapshot = new();
    private bool _hasSteamTimeSnapshot;
    private ServerTimeState _steamTimeSnapshot;
    private readonly HashSet<ulong> _steamLoggedFirstClientPlayerFrame = new();
    private readonly HashSet<ulong> _steamLoggedFirstClientClimbotFrame = new();
    private Callback<P2PSessionRequest_t> _steamP2PSessionRequestCallback;
    private Callback<P2PSessionConnectFail_t> _steamP2PSessionConnectFailCallback;

    public bool IsSteamTransportActive => _steamTransportActive;

    /// <summary>Activates the Steam P2P transport for the current lobby.</summary>
    public void StartSteamTransport(SteamLobbyManager lobby)
    {
        if (lobby == null || !lobby.IsInLobby) return;

        _steamLobby = lobby;
        _steamTransportActive = true;
        _steamLocalId = SteamUser.GetSteamID().m_SteamID;
        _steamHostId = lobby.HostSteamId;
        _lastReliablePresenceSentAt = 0;
        CurrentLobbyId = lobby.CurrentLobbyId.m_SteamID.ToString();
        CurrentRoomCode = lobby.CurrentRoomCode;
        ServerName = string.IsNullOrEmpty(lobby.CurrentLobbyName) ? "Steam lobby" : lobby.CurrentLobbyName;
        LocalPlayerId = StableSteamPlayerId(_steamLocalId);
        IsHandshakeComplete = true;

        try { SteamNetworking.AllowP2PPacketRelay(true); }
        catch (Exception ex) { Mod.Log.Warning($"[SteamP2P] Allow relay failed: {ex.Message}"); }
        EnsureSteamP2PCallbacks();

        RefreshSteamLobbyMembers(lobby);
        OnHandshakeAck?.Invoke();
        Mod.LogDebug($"[SteamP2P] Transport active. local={_steamLocalId} host={_steamHostId} playerId={LocalPlayerId}");
    }

    /// <summary>Synchronizes the Steam member list with the remote-player stubs.</summary>
    public void RefreshSteamLobbyMembers(SteamLobbyManager lobby)
    {
        if (!_steamTransportActive || lobby == null || !lobby.IsInLobby) return;

        _steamLobby = lobby;
        _steamHostId = lobby.HostSteamId;

        var present = new HashSet<ulong>();
        foreach (var member in lobby.Members)
        {
            if (member.SteamId == 0) continue;
            present.Add(member.SteamId);
            _steamPlayerNames[member.SteamId] = string.IsNullOrEmpty(member.Name) ? $"Player{member.SteamId}" : member.Name;
            var playerId = StableSteamPlayerId(member.SteamId);
            _steamPlayerIds[member.SteamId] = playerId;
            _steamIdsByPlayerId[playerId] = member.SteamId;

            if (member.SteamId == _steamLocalId) continue;

            try { SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(member.SteamId)); }
            catch { }

            if (!_remotePlayers.ContainsKey(playerId))
            {
                var rp = new RemotePlayer
                {
                    Id = playerId,
                    Name = _steamPlayerNames[member.SteamId],
                    State = PlayerState.Connecting,
                };
                _remotePlayers[playerId] = rp;
                OnPlayerJoined?.Invoke(playerId, rp.Name);
                if (_steamLobby.IsHost)
                    SendSteamSnapshotTo(member.SteamId);
            }
            else
            {
                _remotePlayers[playerId].Name = _steamPlayerNames[member.SteamId];
            }
        }

        var toRemove = new List<int>();
        foreach (var kv in _steamIdsByPlayerId)
        {
            if (kv.Value == _steamLocalId) continue;
            if (!present.Contains(kv.Value)) toRemove.Add(kv.Key);
        }

        foreach (var playerId in toRemove)
        {
            if (_steamIdsByPlayerId.TryGetValue(playerId, out var steamId))
            {
                try { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(steamId)); }
                catch { }
                _steamPlayerIds.Remove(steamId);
                _steamPlayerNames.Remove(steamId);
            }
            _steamIdsByPlayerId.Remove(playerId);
            if (_remotePlayers.Remove(playerId))
                OnPlayerLeft?.Invoke(playerId);
        }
    }

    private void PumpSteamTransport()
    {
        if (!_steamTransportActive) return;

        if (_steamLobby == null || !_steamLobby.IsInLobby)
        {
            StopSteamTransport();
            return;
        }

        if (ShouldRefreshSteamMembers())
            RefreshSteamLobbyMembers(_steamLobby);
        try
        {
            uint packetSize;
            int guard = 0;
            while (SteamNetworking.IsP2PPacketAvailable(out packetSize, SteamGameplayChannel) && guard++ < 256)
            {
                if (packetSize == 0) break;

                var buffer = new Il2CppStructArray<byte>((long)packetSize);
                uint bytesRead;
                CSteamID remoteId;
                if (!SteamNetworking.ReadP2PPacket(buffer, packetSize, out bytesRead, out remoteId, SteamGameplayChannel))
                    break;

                var payload = new byte[bytesRead];
                for (int i = 0; i < bytesRead; i++)
                    payload[i] = buffer[i];

                ProcessSteamPacket(remoteId, payload);
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[SteamP2P] Pump failed: {ex}");
        }
    }

    private void ProcessSteamPacket(CSteamID remoteId, byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;

        if (!IsSteamLobbyMember(remoteId.m_SteamID))
        {
            Mod.Log.Warning($"[SteamP2P] Ignored packet from non-lobby peer {remoteId.m_SteamID}");
            return;
        }

        try { SteamNetworking.AcceptP2PSessionWithUser(remoteId); }
        catch { }

        var id = (PacketId)payload[0];
        if (_steamLobby != null && _steamLobby.IsHost)
        {
            ProcessSteamHostPacket(remoteId.m_SteamID, id, payload);
            return;
        }

        ProcessPacket(payload);
    }

    private void ProcessSteamHostPacket(ulong remoteSteamId, PacketId id, byte[] payload)
    {
        using var ms = new MemoryStream(payload, 1, payload.Length - 1, writable: false);
        using var r = new BinaryReader(ms);

        switch (id)
        {
            case PacketId.ClientPlayerState:
            {
                var pkt = new ClientPlayerState();
                pkt.Deserialize(r);
                if (!IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.YawDeg)) break;

                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                ApplyRemotePlayerState(playerId, pkt);
                var reliablePresence = ShouldForwardReliablePresence(remoteSteamId);
                BroadcastSteamServerPacket(PacketId.ServerPlayerState, new ServerPlayerState
                {
                    PlayerId = playerId,
                    X = pkt.X,
                    Y = pkt.Y,
                    Z = pkt.Z,
                    YawDeg = pkt.YawDeg,
                    SceneName = pkt.SceneName,
                    State = pkt.State,
                }, exceptSteamId: remoteSteamId, reliable: reliablePresence);
                break;
            }
            case PacketId.ClientBoneState:
            {
                var pkt = new ClientBoneState();
                pkt.Deserialize(r);
                if (!IsValidBoneState(pkt.BoneCount, pkt.Positions, pkt.Rotations)) break;

                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                if (_remotePlayers.TryGetValue(playerId, out var rp))
                {
                    rp.BoneCount = pkt.BoneCount;
                    rp.BonePositions = pkt.Positions;
                    rp.BoneRotations = pkt.Rotations;
                }
                BroadcastSteamServerPacket(PacketId.ServerBoneState, new ServerBoneState
                {
                    PlayerId = playerId,
                    BoneCount = pkt.BoneCount,
                    Positions = pkt.Positions,
                    Rotations = pkt.Rotations,
                }, exceptSteamId: remoteSteamId, reliable: true);
                break;
            }
            case PacketId.ClientPlayerFrame:
            {
                var pkt = new ClientPlayerFrame();
                pkt.Deserialize(r);
                if (!IsValidNetFrame(pkt.Frame, requirePosition: true, out var rejectReason))
                {
                    LogRejectedNetFrame($"steam-client-player-{remoteSteamId}", $"client player steam={remoteSteamId}", rejectReason);
                    break;
                }

                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                var playerName = _steamPlayerNames.TryGetValue(remoteSteamId, out var knownName)
                    ? knownName
                    : $"Player{playerId}";
                if (_steamLoggedFirstClientPlayerFrame.Add(remoteSteamId))
                    Mod.LogDebug($"[NetSync] First ClientPlayerFrame received from steam={remoteSteamId} id={playerId} name='{playerName}' positions={FrameVectorCount(pkt.Frame.Positions)} eulers={FrameVectorCount(pkt.Frame.Eulers)} flags=0x{pkt.Frame.Flags:X2}");
                ApplyRemotePlayerFrame(playerId, playerName, pkt.Frame);
                BroadcastSteamServerPacket(PacketId.ServerPlayerFrame, new ServerPlayerFrame
                {
                    PlayerId = playerId,
                    PlayerName = playerName,
                    Frame = pkt.Frame,
                }, exceptSteamId: remoteSteamId, reliable: false);
                break;
            }
            case PacketId.ClientClimbotFrame:
            {
                var pkt = new ClientClimbotFrame();
                pkt.Deserialize(r);
                if (!IsValidNetFrame(pkt.Frame, requirePosition: true, out var rejectReason))
                {
                    LogRejectedNetFrame($"steam-client-climbot-{remoteSteamId}", $"client climbot steam={remoteSteamId}", rejectReason);
                    break;
                }

                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                if (_steamLoggedFirstClientClimbotFrame.Add(remoteSteamId))
                    Mod.LogDebug($"[NetSync] First ClientClimbotFrame received from steam={remoteSteamId} id={playerId} positions={FrameVectorCount(pkt.Frame.Positions)} eulers={FrameVectorCount(pkt.Frame.Eulers)} flags=0x{pkt.Frame.Flags:X2}");
                ApplyRemoteClimbotFrame(playerId, pkt.Frame);
                BroadcastSteamServerPacket(PacketId.ServerClimbotFrame, new ServerClimbotFrame
                {
                    PlayerId = playerId,
                    Frame = pkt.Frame,
                }, exceptSteamId: remoteSteamId, reliable: false);
                break;
            }
            case PacketId.ClientChat:
            {
                var pkt = new ClientChat();
                pkt.Deserialize(r);
                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                var playerName = _steamPlayerNames.TryGetValue(remoteSteamId, out var knownName)
                    ? knownName
                    : $"Player{playerId}";
                var outPkt = new ServerChatBroadcast
                {
                    FromPlayerId = playerId,
                    FromPlayerName = playerName,
                    Message = pkt.Message ?? "",
                };
                OnChatReceived?.Invoke(playerId, playerName, outPkt.Message);
                BroadcastSteamServerPacket(PacketId.ServerChatBroadcast, outPkt, exceptSteamId: remoteSteamId, reliable: true);
                break;
            }
            case PacketId.ClientRopeClip:
            {
                var pkt = new ClientRopeClip();
                pkt.Deserialize(r);
                var fromId = EnsureSteamRemotePlayer(remoteSteamId);
                var outPkt = new ServerRopeClip { FromPlayerId = fromId, TargetPlayerId = pkt.TargetPlayerId, Clip = pkt.Clip };
                OnRopeClip?.Invoke(fromId, pkt.TargetPlayerId, pkt.Clip);
                // exceptSteamId:0 -> everyone, sender included (authoritative confirmation).
                BroadcastSteamServerPacket(PacketId.ServerRopeClip, outPkt, exceptSteamId: 0, reliable: true);
                break;
            }
            case PacketId.ClientPitonPlaced:
            case PacketId.ClientPitonRemoved:
                // Delegate to the authoritative core: validation, authoritative id, snapshot,
                // local ghost and rebroadcast except sender (Phase 3 step 3).
                SteamSession.OnClientPacket(EnsureSteamRemotePlayer(remoteSteamId), id, r);
                break;
            case PacketId.ClientWeatherState:
                // Steam weather is authoritative on the host side: clients don't publish this state.
                break;
            case PacketId.ClientLampState:
                // Delegate to the authoritative core (lamp snapshot + local ghost + rebroadcast).
                SteamSession.OnClientPacket(EnsureSteamRemotePlayer(remoteSteamId), id, r);
                break;
            case PacketId.ClientCosmeticState:
            {
                var pkt = new ClientCosmeticState();
                pkt.Deserialize(r);
                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                _steamCosmeticSnapshot[playerId] = pkt.Flags;
                if (_remotePlayers.TryGetValue(playerId, out var rp))
                {
                    rp.CosmeticFlags = pkt.Flags;
                    rp.HasCosmeticState = true;
                }
                var outPkt = new ServerCosmeticState { PlayerId = playerId, Flags = pkt.Flags };
                BroadcastSteamServerPacket(PacketId.ServerCosmeticState, outPkt, exceptSteamId: remoteSteamId, reliable: true);
                break;
            }
            case PacketId.ClientSleepState:
            {
                var pkt = new ClientSleepState();
                pkt.Deserialize(r);
                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                if (_remotePlayers.TryGetValue(playerId, out var rp))
                {
                    rp.IsAsleep = pkt.Asleep;
                    rp.HasSleepState = true;
                }
                // No rebroadcast: only the host needs the sleep states
                // (to decide on fast-forward). The resulting time is broadcast
                // via ServerTimeState.
                break;
            }
            case PacketId.ClientHandPose:
            {
                var pkt = new ClientHandPose();
                pkt.Deserialize(r);
                if (pkt.Packed == null || pkt.Packed.Length != Protocol.HandPosePackedSize) break;

                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                _steamHandPoseSnapshot[playerId] = pkt.Packed;
                if (_remotePlayers.TryGetValue(playerId, out var rp))
                {
                    rp.HandPosePacked = pkt.Packed;
                    rp.HasHandPose = true;
                }
                var outPkt = new ServerHandPose { PlayerId = playerId, Packed = pkt.Packed };
                OnHandPose?.Invoke(outPkt);
                // Reliable: the pose is only sent on change, so we don't want
                // a drop to leave the ghost's fingers frozen on the old pose.
                BroadcastSteamServerPacket(PacketId.ServerHandPose, outPkt, exceptSteamId: remoteSteamId, reliable: true);
                break;
            }
            case PacketId.ClientPingPlaced:
            {
                var pkt = new ClientPingPlaced();
                pkt.Deserialize(r);
                if (!IsValidPose(pkt.PosX, pkt.PosY, pkt.PosZ, 0f)) break;

                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                var outPkt = new ServerPingPlaced
                {
                    FromPlayerId = playerId,
                    PosX = pkt.PosX,
                    PosY = pkt.PosY,
                    PosZ = pkt.PosZ,
                };
                // Pings are ephemeral: no snapshot for late-joiners.
                OnPingPlaced?.Invoke(outPkt);
                BroadcastSteamServerPacket(PacketId.ServerPingPlaced, outPkt, exceptSteamId: remoteSteamId, reliable: true);
                break;
            }
            case PacketId.ClientDisconnect:
            {
                var playerId = EnsureSteamRemotePlayer(remoteSteamId);
                if (_remotePlayers.Remove(playerId))
                    OnPlayerLeft?.Invoke(playerId);
                break;
            }
        }
    }

    private void SendSteamPlayerState(float x, float y, float z, float yaw, string sceneName, PlayerState state)
    {
        if (!IsHandshakeComplete) return;

        var reliablePresence = ShouldSendReliablePresence();
        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerPlayerState, new ServerPlayerState
            {
                PlayerId = LocalPlayerId,
                X = x,
                Y = y,
                Z = z,
                YawDeg = yaw,
                SceneName = sceneName ?? "",
                State = state,
            }, exceptSteamId: 0, reliable: reliablePresence);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPlayerState, new ClientPlayerState
        {
            X = x,
            Y = y,
            Z = z,
            YawDeg = yaw,
            SceneName = sceneName ?? "",
            State = state,
        }, reliable: reliablePresence);
    }

    private void SendSteamBoneState(byte boneCount, float[] positions, float[] rotations)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerBoneState, new ServerBoneState
            {
                PlayerId = LocalPlayerId,
                BoneCount = boneCount,
                Positions = positions,
                Rotations = rotations,
            }, exceptSteamId: 0, reliable: true);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientBoneState, new ClientBoneState
        {
            BoneCount = boneCount,
            Positions = positions,
            Rotations = rotations,
        }, reliable: true);
    }

    private void SendSteamPlayerFrame(NetFrameData frame)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerPlayerFrame, new ServerPlayerFrame
            {
                PlayerId = LocalPlayerId,
                PlayerName = _steamPlayerNames.TryGetValue(_steamLocalId, out var name) ? name : "Player",
                Frame = frame,
            }, exceptSteamId: 0, reliable: false);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPlayerFrame, new ClientPlayerFrame { Frame = frame }, reliable: false);
    }

    private void SendSteamClimbotFrame(NetFrameData frame)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerClimbotFrame, new ServerClimbotFrame
            {
                PlayerId = LocalPlayerId,
                Frame = frame,
            }, exceptSteamId: 0, reliable: false);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientClimbotFrame, new ClientClimbotFrame { Frame = frame }, reliable: false);
    }

    private void SendSteamPitonPlaced(uint pitonId, Vector3 pos, Quaternion rot, byte quality, int hp, int itemId)
    {
        if (!IsHandshakeComplete) return;
        if (!IsValidPitonPayload(pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w, quality, hp, itemId)) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            // Piton placed by the host itself: the authoritative core records it and
            // broadcasts it to everyone (no local ghost, the host has the real piton).
            SteamSession.HandleHostPitonPlaced(LocalPlayerId, pitonId, pos, rot, quality, hp, itemId);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPitonPlaced, new ClientPitonPlaced
        {
            PitonId = pitonId,
            PosX = pos.x,
            PosY = pos.y,
            PosZ = pos.z,
            RotX = rot.x,
            RotY = rot.y,
            RotZ = rot.z,
            RotW = rot.w,
            Quality = quality,
            PitonHp = hp,
            ItemId = itemId,
        }, reliable: true);
    }

    private void SendSteamPitonRemoved(uint pitonId)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            SteamSession.HandleHostPitonRemoved(LocalPlayerId, pitonId);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPitonRemoved, new ClientPitonRemoved { PitonId = pitonId }, reliable: true);
    }

    private void SendSteamWeatherState(WeatherSyncData state, bool reliable)
    {
        if (!IsHandshakeComplete || _steamLobby == null || !_steamLobby.IsHost)
            return;
        SteamSession.HandleHostWeather(state, reliable);
    }

    private void SendSteamLampState(int mode)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            SteamSession.HandleHostLampState(LocalPlayerId, mode);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientLampState, new ClientLampState { Mode = mode }, reliable: true);
    }

    private void SendSteamCosmeticState(byte flags)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            _steamCosmeticSnapshot[LocalPlayerId] = flags;
            BroadcastSteamServerPacket(PacketId.ServerCosmeticState, new ServerCosmeticState
            {
                PlayerId = LocalPlayerId,
                Flags = flags,
            }, exceptSteamId: 0, reliable: true);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientCosmeticState, new ClientCosmeticState { Flags = flags }, reliable: true);
    }

    private void SendSteamSleepState(bool asleep)
    {
        if (!IsHandshakeComplete) return;
        // The host reads its own sleep state locally — no packet to itself.
        if (_steamLobby != null && _steamLobby.IsHost) return;

        SendSteamPacketToHost(PacketId.ClientSleepState, new ClientSleepState { Asleep = asleep }, reliable: true);
    }

    private void SendSteamTimeState(ServerTimeState state)
    {
        if (!IsHandshakeComplete || _steamLobby == null || !_steamLobby.IsHost) return;

        _steamTimeSnapshot = state;
        _hasSteamTimeSnapshot = true;
        BroadcastSteamServerPacket(PacketId.ServerTimeState, state, exceptSteamId: 0, reliable: false);
    }

    private void SendSteamHandPose(byte[] packed)
    {
        if (!IsHandshakeComplete) return;
        if (packed == null || packed.Length != Protocol.HandPosePackedSize) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            _steamHandPoseSnapshot[LocalPlayerId] = packed;
            BroadcastSteamServerPacket(PacketId.ServerHandPose, new ServerHandPose
            {
                PlayerId = LocalPlayerId,
                Packed = packed,
            }, exceptSteamId: 0, reliable: true);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientHandPose, new ClientHandPose { Packed = packed }, reliable: true);
    }

    private void SendSteamPingPlaced(Vector3 pos)
    {
        if (!IsHandshakeComplete) return;
        if (!IsValidPose(pos.x, pos.y, pos.z, 0f)) return;

        // The host broadcasts directly to the others; the sender already shows its
        // own ping locally (no echo to itself via BroadcastSteamServerPacket).
        if (_steamLobby != null && _steamLobby.IsHost)
        {
            BroadcastSteamServerPacket(PacketId.ServerPingPlaced, new ServerPingPlaced
            {
                FromPlayerId = LocalPlayerId,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
            }, exceptSteamId: 0, reliable: true);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientPingPlaced, new ClientPingPlaced
        {
            PosX = pos.x,
            PosY = pos.y,
            PosZ = pos.z,
        }, reliable: true);
    }

    private void SendSteamChat(string message)
    {
        if (!IsHandshakeComplete) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            var playerName = _steamPlayerNames.TryGetValue(_steamLocalId, out var knownName)
                ? knownName
                : (ModConfig.PlayerName.Value ?? "Player");
            var pkt = new ServerChatBroadcast
            {
                FromPlayerId = LocalPlayerId,
                FromPlayerName = playerName,
                Message = message ?? "",
            };
            OnChatReceived?.Invoke(LocalPlayerId, playerName, pkt.Message);
            BroadcastSteamServerPacket(PacketId.ServerChatBroadcast, pkt, exceptSteamId: 0, reliable: true);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientChat, new ClientChat { Message = message ?? "" }, reliable: true);
    }

    private void SendSteamRopeClip(int targetPlayerId, bool clip)
    {
        if (!IsHandshakeComplete || targetPlayerId == LocalPlayerId) return;

        if (_steamLobby != null && _steamLobby.IsHost)
        {
            var pkt = new ServerRopeClip { FromPlayerId = LocalPlayerId, TargetPlayerId = targetPlayerId, Clip = clip };
            OnRopeClip?.Invoke(LocalPlayerId, targetPlayerId, clip);
            // exceptSteamId:0 -> broadcast to everyone (including the sender, who also renders the link).
            BroadcastSteamServerPacket(PacketId.ServerRopeClip, pkt, exceptSteamId: 0, reliable: true);
            return;
        }

        SendSteamPacketToHost(PacketId.ClientRopeClip,
            new ClientRopeClip { TargetPlayerId = targetPlayerId, Clip = clip }, reliable: true);
    }

    private void SendSteamPacketToHost(PacketId id, IPacket packet, bool reliable)
    {
        if (_steamHostId == 0 || _steamHostId == _steamLocalId) return;
        SendSteamPayload(new CSteamID(_steamHostId), BuildPayload(id, packet), reliable);
    }

    /// <summary>
    /// Host only: sends a teleport order to ONE target player (/bring
    /// command). Maps playerId -> steamId then sends a reliable ServerTeleport.
    /// Returns false if we're not the host or if the target player can't be found.
    /// </summary>
    public bool SendTeleportToPlayer(int targetPlayerId, float x, float y, float z, float yaw)
    {
        if (!_steamTransportActive || _steamLobby == null || !_steamLobby.IsHost) return false;
        if (!_steamIdsByPlayerId.TryGetValue(targetPlayerId, out var steamId) || steamId == _steamLocalId)
            return false;

        var pkt = new ServerTeleport { X = x, Y = y, Z = z, Yaw = yaw };
        SendSteamPayload(new CSteamID(steamId), BuildPayload(PacketId.ServerTeleport, pkt), reliable: true);
        Mod.LogDebug($"[CairnMP] Sent ServerTeleport to player {targetPlayerId} (steam={steamId})");
        return true;
    }

    private void SendSteamSnapshotTo(ulong steamId)
    {
        if (steamId == 0 || steamId == _steamLocalId)
            return;

        var target = new CSteamID(steamId);

        if (_hasSteamTimeSnapshot)
            SendSteamPayload(target, BuildPayload(PacketId.ServerTimeState, _steamTimeSnapshot), reliable: true);

        // Weather + pitons + lamps: official state held by the authoritative core.
        SteamSession.SendSnapshotTo(EnsureSteamRemotePlayer(steamId));

        foreach (var kv in _steamCosmeticSnapshot)
        {
            var cosmeticPkt = new ServerCosmeticState { PlayerId = kv.Key, Flags = kv.Value };
            SendSteamPayload(target, BuildPayload(PacketId.ServerCosmeticState, cosmeticPkt), reliable: true);
        }

        foreach (var kv in _steamHandPoseSnapshot)
        {
            var handPkt = new ServerHandPose { PlayerId = kv.Key, Packed = kv.Value };
            SendSteamPayload(target, BuildPayload(PacketId.ServerHandPose, handPkt), reliable: true);
        }

        // Active rope links (late-joiner).
        foreach (var (a, b) in RopeLinkState.Links())
            SendSteamPayload(target, BuildPayload(PacketId.ServerRopeClip,
                new ServerRopeClip { FromPlayerId = a, TargetPlayerId = b, Clip = true }), reliable: true);
    }

    private void BroadcastSteamServerPacket(PacketId id, IPacket packet, ulong exceptSteamId, bool reliable)
    {
        var payload = BuildPayload(id, packet);
        foreach (var member in _steamLobby.Members)
        {
            if (member.SteamId == 0 || member.SteamId == _steamLocalId || member.SteamId == exceptSteamId) continue;
            SendSteamPayload(new CSteamID(member.SteamId), payload, reliable);
        }
    }

    private bool SendSteamPayload(CSteamID target, byte[] payload, bool reliable)
    {
        if (!_steamTransportActive || payload == null || payload.Length == 0) return false;

        try
        {
            var data = new Il2CppStructArray<byte>(payload.Length);
            for (int i = 0; i < payload.Length; i++)
                data[i] = payload[i];

            var forceReliable = !reliable && payload.Length > 1100;
            var sendType = (reliable || forceReliable)
                ? EP2PSend.k_EP2PSendReliable
                : EP2PSend.k_EP2PSendUnreliableNoDelay;
            bool ok = SteamNetworking.SendP2PPacket(target, data, (uint)payload.Length, sendType, SteamGameplayChannel);
            if (!ok && ShouldLogSteamSendFailure())
                Mod.Log.Warning($"[SteamP2P] Send failed target={target.m_SteamID} size={payload.Length} reliable={reliable}");
            return ok;
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[SteamP2P] Send exception target={target.m_SteamID}: {ex.Message}");
            return false;
        }
    }

    private bool ShouldSendReliablePresence()
    {
        var now = NowSeconds();
        if (now - _lastReliablePresenceSentAt < SteamReliablePresenceIntervalSeconds)
            return false;

        _lastReliablePresenceSentAt = now;
        return true;
    }

    private bool ShouldRefreshSteamMembers()
    {
        var now = NowSeconds();
        if (now - _lastSteamMemberRefreshAt < 1.0)
            return false;

        _lastSteamMemberRefreshAt = now;
        return true;
    }

    private bool ShouldLogSteamSendFailure()
    {
        var now = NowSeconds();
        if (now - _lastSteamSendFailLogAt < 2.0)
            return false;

        _lastSteamSendFailLogAt = now;
        return true;
    }

    private bool ShouldForwardReliablePresence(ulong sourceSteamId)
    {
        var now = NowSeconds();
        if (_steamReliableForwardTimes.TryGetValue(sourceSteamId, out var last) &&
            now - last < SteamReliablePresenceIntervalSeconds)
        {
            return false;
        }

        _steamReliableForwardTimes[sourceSteamId] = now;
        return true;
    }

    private static double NowSeconds() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    private int EnsureSteamRemotePlayer(ulong steamId)
    {
        var playerId = StableSteamPlayerId(steamId);
        if (!_steamPlayerIds.ContainsKey(steamId))
        {
            _steamPlayerIds[steamId] = playerId;
            _steamIdsByPlayerId[playerId] = steamId;
        }

        if (!_remotePlayers.ContainsKey(playerId))
        {
            var name = _steamPlayerNames.TryGetValue(steamId, out var knownName)
                ? knownName
                : $"Player{playerId}";
            _remotePlayers[playerId] = new RemotePlayer
            {
                Id = playerId,
                Name = name,
                State = PlayerState.Connecting,
            };
            OnPlayerJoined?.Invoke(playerId, name);
        }

        return playerId;
    }

    private void ApplyRemotePlayerState(int playerId, ClientPlayerState pkt)
    {
        if (!IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.YawDeg)) return;
        if (!_remotePlayers.TryGetValue(playerId, out var p)) return;
        p.X = pkt.X;
        p.Y = pkt.Y;
        p.Z = pkt.Z;
        p.YawDeg = pkt.YawDeg;
        p.SceneName = pkt.SceneName;
        p.State = pkt.State;
        p.LastUpdateTime = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
    }

    private static byte[] BuildPayload(PacketId id, IPacket packet)
    {
        var frame = PacketCodec.Frame(id, packet);
        var payload = new byte[Math.Max(0, frame.Length - 2)];
        Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
        return payload;
    }

    private static int StableSteamPlayerId(ulong steamId)
    {
        var folded = (uint)(steamId ^ (steamId >> 32));
        var id = (int)(folded & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }

    private void EnsureSteamP2PCallbacks()
    {
        try
        {
            _steamP2PSessionRequestCallback ??= Callback<P2PSessionRequest_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<P2PSessionRequest_t>.DispatchDelegate>(
                    new Action<P2PSessionRequest_t>(OnSteamP2PSessionRequest)));
            _steamP2PSessionConnectFailCallback ??= Callback<P2PSessionConnectFail_t>.Create(
                DelegateSupport.ConvertDelegate<Callback<P2PSessionConnectFail_t>.DispatchDelegate>(
                    new Action<P2PSessionConnectFail_t>(OnSteamP2PSessionConnectFail)));
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[SteamP2P] Callback registration failed: {ex.Message}");
        }
    }

    private void OnSteamP2PSessionRequest(P2PSessionRequest_t evt)
    {
        var remoteId = evt.m_steamIDRemote;
        if (!_steamTransportActive || remoteId.m_SteamID == 0) return;
        if (!IsSteamLobbyMember(remoteId.m_SteamID))
        {
            Mod.Log.Warning($"[SteamP2P] Rejected session request from non-lobby peer {remoteId.m_SteamID}");
            return;
        }

        try
        {
            SteamNetworking.AcceptP2PSessionWithUser(remoteId);
            EnsureSteamRemotePlayer(remoteId.m_SteamID);
            Mod.LogDebug($"[SteamP2P] Accepted session request from {remoteId.m_SteamID}");
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[SteamP2P] Accept session failed for {remoteId.m_SteamID}: {ex.Message}");
        }
    }

    private void OnSteamP2PSessionConnectFail(P2PSessionConnectFail_t evt)
    {
        var remoteId = evt.m_steamIDRemote;
        Mod.Log.Warning($"[SteamP2P] Session failed remote={remoteId.m_SteamID} error={evt.m_eP2PSessionError}");
    }

    private bool IsSteamLobbyMember(ulong steamId)
    {
        if (steamId == 0) return false;
        if (steamId == _steamLocalId) return true;
        if (_steamLobby == null || !_steamLobby.IsInLobby) return false;

        // Reads Steam directly to cover the short delay before OnMembersChanged.
        try
        {
            var count = SteamMatchmaking.GetNumLobbyMembers(_steamLobby.CurrentLobbyId);
            for (int i = 0; i < count; i++)
            {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(_steamLobby.CurrentLobbyId, i);
                if (member.m_SteamID == steamId) return true;
            }
        }
        catch { }

        foreach (var member in _steamLobby.Members)
            if (member.SteamId == steamId) return true;

        return false;
    }

    private void StopSteamTransport()
    {
        foreach (var steamId in _steamPlayerIds.Keys)
        {
            if (steamId == 0 || steamId == _steamLocalId) continue;
            try { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(steamId)); }
            catch { }
        }
        ResetSteamTransportState();
    }

    private void DisposeSteamP2PCallbacks()
    {
        try { _steamP2PSessionRequestCallback?.Dispose(); } catch { }
        try { _steamP2PSessionConnectFailCallback?.Dispose(); } catch { }
        _steamP2PSessionRequestCallback = null;
        _steamP2PSessionConnectFailCallback = null;
    }

    private void ResetSteamTransportState()
    {
        _steamTransportActive = false;
        _steamLobby = null;
        _steamLocalId = 0;
        _steamHostId = 0;
        _steamPlayerIds.Clear();
        _steamIdsByPlayerId.Clear();
        _steamPlayerNames.Clear();
        _steamReliableForwardTimes.Clear();
        _steamSession?.Reset(); // pitons + lamps + weather
        _steamCosmeticSnapshot.Clear();
        _steamHandPoseSnapshot.Clear();
        _hasSteamTimeSnapshot = false;
        _steamTimeSnapshot = default;
        _steamLoggedFirstClientPlayerFrame.Clear();
        _steamLoggedFirstClientClimbotFrame.Clear();
        _lastReliablePresenceSentAt = 0;
        _lastSteamMemberRefreshAt = 0;
        _lastSteamSendFailLogAt = 0;
        DisposeSteamP2PCallbacks();
    }
}
