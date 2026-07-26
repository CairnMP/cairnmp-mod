using System;
using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Networking;

public partial class NetworkManager
{
    /// <summary>
    /// Dispatches a payload received from the transport to the appropriate handler.
    /// payload[0] = PacketId; payload[1..] = packet body. Called on the Unity thread.
    /// </summary>
    private void ProcessPacket(byte[] payload)
    {
        // Empty payload = internal disconnection marker.
        if (payload.Length == 0)
        {
            OnDisconnected?.Invoke(LastError ?? "Connection lost");
            return;
        }

        var id = (PacketId)payload[0];
        if (IsSteamTransportActive && !IsHandshakeComplete &&
            id != PacketId.ServerExtensionManifestResult &&
            id != PacketId.ServerHandshakeReject)
        {
            return;
        }
        using var ms = new MemoryStream(payload, 1, payload.Length - 1, writable: false);
        using var r = new BinaryReader(ms);

        switch (id)
        {
            case PacketId.ServerExtensionManifestResult:
                HandleExtensionManifestResult(r);
                break;
            case PacketId.ServerExtensionCommandResult:
                HandleExtensionCommandResult(r);
                break;
            case PacketId.ServerExtensionEvent:
                HandleExtensionEvent(r);
                break;
            case PacketId.ServerExtensionState:
                HandleExtensionState(r);
                break;
            case PacketId.ServerExtensionPeerStatus:
                HandleExtensionPeerStatus(r);
                break;
            case PacketId.ServerHandshakeAck:
                HandleHandshakeAck(r);
                break;
            case PacketId.ServerHandshakeReject:
                HandleHandshakeReject(r);
                break;
            case PacketId.ServerPlayerJoined:
                HandlePlayerJoined(r);
                break;
            case PacketId.ServerPlayerLeft:
                HandlePlayerLeft(r);
                break;
            case PacketId.ServerPlayerState:
                HandlePlayerState(r);
                break;
            case PacketId.ServerStartGame:
                HandleStartGame(r);
                break;
            case PacketId.ServerBoneState:
                HandleBoneState(r);
                break;
            case PacketId.ServerPlayerFrame:
                HandlePlayerFrame(r);
                break;
            case PacketId.ServerClimbotFrame:
                HandleClimbotFrame(r);
                break;
            case PacketId.ServerPitonPlaced:
            {
                var pkt = new ServerPitonPlaced();
                pkt.Deserialize(r);
                if (!IsValidPitonPayload(pkt.PosX, pkt.PosY, pkt.PosZ, pkt.RotX, pkt.RotY, pkt.RotZ, pkt.RotW,
                    pkt.Quality, pkt.PitonHp, pkt.ItemId))
                    break;
                Mod.LogDebug($"[Piton] Remote player {pkt.FromPlayerId} placed piton #{pkt.PitonId}");
                OnPitonPlaced?.Invoke(pkt);
                break;
            }
            case PacketId.ServerPitonRemoved:
            {
                var pkt = new ServerPitonRemoved();
                pkt.Deserialize(r);
                OnPitonRemoved?.Invoke(pkt);
                break;
            }
            case PacketId.ServerWeatherState:
            {
                var pkt = new ServerWeatherState();
                pkt.Deserialize(r);
                if (!IsValidWeatherState(pkt.State))
                    break;
                OnWeatherState?.Invoke(pkt);
                break;
            }
            case PacketId.ServerLampState:
            {
                var pkt = new ServerLampState();
                pkt.Deserialize(r);
                if (_remotePlayers.TryGetValue(pkt.PlayerId, out var p))
                {
                    p.LampMode = pkt.Mode;
                    p.HasLampState = true;
                }
                break;
            }
            case PacketId.ServerCosmeticState:
            {
                var pkt = new ServerCosmeticState();
                pkt.Deserialize(r);
                if (_remotePlayers.TryGetValue(pkt.PlayerId, out var p))
                {
                    p.CosmeticFlags = pkt.Flags;
                    p.HasCosmeticState = true;
                }
                break;
            }
            case PacketId.ServerHandPose:
            {
                var pkt = new ServerHandPose();
                pkt.Deserialize(r);
                if (pkt.Packed == null || pkt.Packed.Length != Protocol.HandPosePackedSize)
                    break;
                if (_remotePlayers.TryGetValue(pkt.PlayerId, out var p))
                {
                    p.HandPosePacked = pkt.Packed;
                    p.HasHandPose = true;
                }
                OnHandPose?.Invoke(pkt);
                break;
            }
            case PacketId.ServerTimeState:
            {
                var pkt = new ServerTimeState();
                pkt.Deserialize(r);
                OnTimeState?.Invoke(pkt);
                break;
            }
            case PacketId.ServerTeleport:
            {
                // Targeted teleport order from the host (/bring command). We validate
                // the position before applying so we don't send the MC into the void.
                var pkt = new ServerTeleport();
                pkt.Deserialize(r);
                if (!IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.Yaw))
                    break;
                // Safety net: we may have entered a bivouac between the host's check and
                // the packet's arrival. We don't yank the player out of a bivouac for a teleport.
                if (GameLifecycleService.IsLocalInBivouac())
                {
                    Mod.LogDebug("[CairnMP] ServerTeleport ignored (local player is in a bivouac)");
                    break;
                }
                Mod.LogDebug($"[CairnMP] ServerTeleport received -> ({pkt.X:F1}, {pkt.Y:F1}, {pkt.Z:F1})");
                TeleportApi.TeleportLocalPlayer(new UnityEngine.Vector3(pkt.X, pkt.Y, pkt.Z), pkt.Yaw);
                break;
            }
            case PacketId.ServerRopeClip:
            {
                var pkt = new ServerRopeClip();
                pkt.Deserialize(r);
                OnRopeClip?.Invoke(pkt.FromPlayerId, pkt.TargetPlayerId, pkt.Clip);
                break;
            }
            default:
                Mod.Log.Warning($"[CairnMP] Unknown packet id {id}");
                break;
        }
    }

    private void HandleHandshakeAck(BinaryReader r)
    {
        var ack = new ServerHandshakeAck();
        ack.Deserialize(r);
        LocalPlayerId = ack.AssignedPlayerId;
        ServerName = ack.ServerName;
        IsHandshakeComplete = true;
        Mod.Log.Msg($"[CairnMP] Handshake OK. id={ack.AssignedPlayerId} server='{ack.ServerName}'");
        OnHandshakeAck?.Invoke();
    }

    private void HandleHandshakeReject(BinaryReader r)
    {
        var rej = new ServerHandshakeReject();
        rej.Deserialize(r);
        LastError = rej.Reason;
        Mod.Log.Error($"[CairnMP] Handshake rejected: {rej.Reason}");
        OnHandshakeRejected?.Invoke(rej.Reason);
    }

    private void HandlePlayerJoined(BinaryReader r)
    {
        var pkt = new ServerPlayerJoined();
        pkt.Deserialize(r);
        _remotePlayers[pkt.PlayerId] = new RemotePlayer { Id = pkt.PlayerId, Name = pkt.PlayerName };
        Mod.Log.Msg($"[CairnMP] Player joined: [{pkt.PlayerId}] {pkt.PlayerName}");
        OnPlayerJoined?.Invoke(pkt.PlayerId, pkt.PlayerName);
    }

    private void HandlePlayerLeft(BinaryReader r)
    {
        var pkt = new ServerPlayerLeft();
        pkt.Deserialize(r);
        if (_remotePlayers.Remove(pkt.PlayerId))
            Mod.Log.Msg($"[CairnMP] Player left: {pkt.PlayerId}");
        OnPlayerLeft?.Invoke(pkt.PlayerId);
    }

    private void HandlePlayerState(BinaryReader r)
    {
        var pkt = new ServerPlayerState();
        pkt.Deserialize(r);
        if (!IsValidPose(pkt.X, pkt.Y, pkt.Z, pkt.YawDeg)) return;

        if (!_remotePlayers.TryGetValue(pkt.PlayerId, out var p))
        {
            // Unknown player -- create a stub to still track the state
            p = new RemotePlayer { Id = pkt.PlayerId, Name = $"Player{pkt.PlayerId}" };
            _remotePlayers[pkt.PlayerId] = p;
        }
        p.X = pkt.X; p.Y = pkt.Y; p.Z = pkt.Z;
        p.YawDeg = pkt.YawDeg;
        p.SceneName = pkt.SceneName;
        p.State = pkt.State;
        p.LastUpdateTime = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
    }

    private void HandleBoneState(BinaryReader r)
    {
        var pkt = new ServerBoneState();
        pkt.Deserialize(r);
        if (!IsValidBoneState(pkt.BoneCount, pkt.Positions, pkt.Rotations)) return;
        if (!_remotePlayers.TryGetValue(pkt.PlayerId, out var p)) return;
        p.BoneCount = pkt.BoneCount;
        p.BonePositions = pkt.Positions;
        p.BoneRotations = pkt.Rotations;
    }

    private void HandlePlayerFrame(BinaryReader r)
    {
        var pkt = new ServerPlayerFrame();
        pkt.Deserialize(r);
        ApplyRemotePlayerFrame(pkt.PlayerId, pkt.PlayerName, pkt.Frame);
    }

    private void HandleClimbotFrame(BinaryReader r)
    {
        var pkt = new ServerClimbotFrame();
        pkt.Deserialize(r);
        ApplyRemoteClimbotFrame(pkt.PlayerId, pkt.Frame);
    }

    private void ApplyRemotePlayerFrame(int playerId, string playerName, NetFrameData frame)
    {
        if (playerId == LocalPlayerId) return;
        if (!IsValidNetFrame(frame, requirePosition: true, out var rejectReason))
        {
            LogRejectedNetFrame($"server-player-{playerId}", $"remote player id={playerId}", rejectReason);
            return;
        }

        if (!_remotePlayers.TryGetValue(playerId, out var p))
        {
            p = new RemotePlayer { Id = playerId, Name = string.IsNullOrWhiteSpace(playerName) ? $"Player{playerId}" : playerName };
            _remotePlayers[playerId] = p;
            OnPlayerJoined?.Invoke(playerId, p.Name);
        }
        else if (!string.IsNullOrWhiteSpace(playerName))
        {
            p.Name = playerName;
        }

        p.HasPlayerFrame = frame.IsValid && frame.Positions != null && frame.Positions.Length >= 3;
        p.PlayerFrame = frame;
        p.State = p.HasPlayerFrame ? PlayerState.InGame : p.State;
        if (p.HasPlayerFrame)
        {
            p.X = frame.Positions[0];
            p.Y = frame.Positions[1];
            p.Z = frame.Positions[2];
            var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
            p.LastUpdateTime = now;
            p.LastPlayerFrameTime = now;
            LogRemotePlayerFrameAccepted(playerId, p.Name, frame);
        }
    }

    private void ApplyRemoteClimbotFrame(int playerId, NetFrameData frame)
    {
        if (playerId == LocalPlayerId) return;
        if (!IsValidNetFrame(frame, requirePosition: true, out var rejectReason))
        {
            LogRejectedNetFrame($"server-climbot-{playerId}", $"remote climbot id={playerId}", rejectReason);
            return;
        }
        if (!_remotePlayers.TryGetValue(playerId, out var p)) return;

        p.HasClimbotFrame = frame.IsValid && frame.Positions != null && frame.Positions.Length >= 3;
        p.ClimbotFrame = frame;
        if (p.HasClimbotFrame)
            LogRemoteClimbotFrameAccepted(playerId, frame);
    }

    private static bool IsValidPose(float x, float y, float z, float yaw)
    {
        const float maxAbsPosition = 100000f;
        return IsFinite(x) && IsFinite(y) && IsFinite(z) && IsFinite(yaw)
            && Math.Abs(x) <= maxAbsPosition
            && Math.Abs(y) <= maxAbsPosition
            && Math.Abs(z) <= maxAbsPosition;
    }

    private static bool IsValidNetFrame(NetFrameData frame, bool requirePosition, out string reason)
    {
        if (!frame.IsValid)
        {
            reason = "isValid=false";
            return false;
        }
        if (!IsValidVectorArray(frame.Positions, requirePosition, "positions", out reason)) return false;
        if (!IsValidVectorArray(frame.Eulers, requirePosition: false, "eulers", out reason)) return false;
        if (frame.Eulers != null && frame.Eulers.Length > 0 && frame.Positions != null && frame.Eulers.Length != frame.Positions.Length)
        {
            reason = $"eulers length {frame.Eulers.Length} does not match positions length {frame.Positions.Length}";
            return false;
        }
        reason = null;
        return true;
    }

    private static bool IsValidVectorArray(float[] values, bool requirePosition, string fieldName, out string reason)
    {
        if (values == null)
        {
            reason = requirePosition ? $"{fieldName}=null" : null;
            return !requirePosition;
        }
        if (values.Length % 3 != 0)
        {
            reason = $"{fieldName} length {values.Length} is not a Vector3 array";
            return false;
        }
        if (requirePosition && values.Length < 3)
        {
            reason = $"{fieldName} has no root position";
            return false;
        }
        if (values.Length > 512 * 3)
        {
            reason = $"{fieldName} has too many vectors ({values.Length / 3})";
            return false;
        }

        for (int i = 0; i < values.Length; i++)
            if (!IsFinite(values[i]))
            {
                reason = $"{fieldName}[{i}] is not finite";
                return false;
            }

        reason = null;
        return true;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal static bool IsValidWeatherState(WeatherSyncData state)
    {
        if (!state.IsValid)
            return false;
        if (!IsInRange(state.WeatherType, 0, 9)
            || !IsInRange(state.RainType, 0, 2)
            || !IsInRange(state.ThunderType, 0, 2)
            || !IsInRange(state.FogType, 0, 1)
            || !IsInRange(state.CloudsType, 0, 2)
            || !IsInRange(state.WindType, 0, 2)
            || !IsInRange(state.WindOverride, 0, 3)
            || !IsInRange(state.SnowRainForceMode, 0, 2))
        {
            return false;
        }

        if (!IsFinite(state.RemainingDuration)
            || !IsFinite(state.UseSnowInsteadOfRain01)
            || !IsFinite(state.WindForce)
            || !IsFinite(state.WindForce01)
            || !IsFinite(state.WindDirX)
            || !IsFinite(state.WindDirY)
            || !IsFinite(state.WindDirZ)
            || !IsFinite(state.WindAngle))
        {
            return false;
        }

        const float maxAbsWind = 100000f;
        return state.RemainingDuration >= 0f && state.RemainingDuration <= 86400f
            && state.UseSnowInsteadOfRain01 >= 0f && state.UseSnowInsteadOfRain01 <= 1f
            && state.WindForce >= 0f && state.WindForce <= maxAbsWind
            && state.WindForce01 >= 0f && state.WindForce01 <= 10f
            && Math.Abs(state.WindDirX) <= maxAbsWind
            && Math.Abs(state.WindDirY) <= maxAbsWind
            && Math.Abs(state.WindDirZ) <= maxAbsWind
            && Math.Abs(state.WindAngle) <= maxAbsWind;
    }

    private static bool IsInRange(int value, int min, int max) => value >= min && value <= max;

    internal static bool IsValidPitonPayload(float x, float y, float z,
        float rotX, float rotY, float rotZ, float rotW, byte quality, int hp, int itemId)
    {
        if (!IsValidPose(x, y, z, 0f))
            return false;
        if (!IsFinite(rotX) || !IsFinite(rotY) || !IsFinite(rotZ) || !IsFinite(rotW))
            return false;

        var lenSq = rotX * rotX + rotY * rotY + rotZ * rotZ + rotW * rotW;
        return lenSq > 0.01f && lenSq < 4f
            && quality <= 10
            && hp >= 0 && hp <= 100000
            && itemId >= 0;
    }

    private static bool IsValidBoneState(byte boneCount, float[] positions, float[] rotations)
    {
        if (boneCount == 0 || boneCount > 128)
            return false;
        if (positions == null || rotations == null)
            return false;
        if (positions.Length != boneCount * 3 || rotations.Length != boneCount * 4)
            return false;

        for (int i = 0; i < positions.Length; i++)
            if (!IsFinite(positions[i]))
                return false;
        for (int i = 0; i < rotations.Length; i++)
            if (!IsFinite(rotations[i]))
                return false;

        return true;
    }

    private void HandleStartGame(BinaryReader r)
    {
        var pkt = new ServerStartGame();
        pkt.Deserialize(r);
        Mod.LogDebug($"[CairnMP] StartGame: difficulty={(GameDifficulty)pkt.Difficulty} skipTut={pkt.SkipTutorials} skipPra={pkt.SkipPractice} assist={pkt.AssistEnabled}");
        OnStartGameReceived?.Invoke(pkt);
    }
}
