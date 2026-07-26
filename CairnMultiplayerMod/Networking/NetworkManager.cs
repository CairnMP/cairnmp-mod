using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Networking;

/// <summary>
/// Client-side representation of another player connected to the same server.
/// Updated from ServerPlayerState packets. The UI / ghost spawner reads this
/// data.
/// </summary>
public class RemotePlayer
{
    public int Id;
    public string Name;
    public float X, Y, Z;
    public float YawDeg;
    public string SceneName;
    public PlayerState State;
    public double LastUpdateTime;

    // Bone data from ServerBoneState packets (world space).
    public byte BoneCount;
    public float[] BonePositions;
    public float[] BoneRotations; // xyzw quaternion

    // Latest native Cairn frames received for the player and their climbot.
    public bool HasPlayerFrame;
    public NetFrameData PlayerFrame;
    public double LastPlayerFrameTime;
    public bool HasClimbotFrame;
    public NetFrameData ClimbotFrame;

    // Lamp mode (AavaLightStick.CurrentMode) — synchronized when another player changes mode.
    public int LampMode;
    public bool HasLampState;

    // Cosmetic state (bitfield, cf. Protocol.CosmeticFlag*) — bit 0 = glowing gloves.
    public byte CosmeticFlags;
    public bool HasCosmeticState;

    // Compressed finger pose (Protocol.HandPosePackedSize bytes) — synchronized
    // to animate the ghost's hands while climbing.
    public byte[] HandPosePacked;
    public bool HasHandPose;
}

/// <summary>
/// The client's session with the other players: connection state, the roster of remote
/// players, and one Send method per kind of state we replicate.
///
/// Transport is Steam P2P (see the SteamP2PTransport half of this class), which delivers
/// packets on the Unity thread — Steam callbacks are pumped by Cairn's own main loop — so
/// the handlers can touch Unity objects directly.
/// </summary>
public partial class NetworkManager : IDisposable
{
    public bool IsConnected => IsSteamTransportActive;
    public bool IsHandshakeComplete { get; private set; }
    public int LocalPlayerId { get; private set; }
    public string ServerName { get; private set; }
    public string LastError { get; private set; }

    // Identifier of the current lobby, set after creation via the API.
    // Used to persist the lobby → save-slot association.
    public string CurrentLobbyId { get; private set; }

    // Shareable short code of the current lobby (format "XXXX-XXXX"), may be empty.
    public string CurrentRoomCode { get; private set; }

    private readonly Dictionary<int, RemotePlayer> _remotePlayers = new();
    public IReadOnlyDictionary<int, RemotePlayer> RemotePlayers => _remotePlayers;

    private bool _debugLoggedFirstLocalPlayerFrameSend;
    private bool _debugLoggedFirstLocalClimbotFrameSend;
    private readonly HashSet<int> _debugLoggedFirstRemotePlayerFrame = new();
    private readonly HashSet<int> _debugLoggedFirstRemoteClimbotFrame = new();
    private readonly Dictionary<string, double> _debugRejectedFrameLogTimes = new();

    public event Action<int, string> OnPlayerJoined;
    public event Action<int> OnPlayerLeft;
    public event Action<int, int, bool> OnRopeClip; // fromId, targetId, clip
    public event Action OnHandshakeAck;
    public event Action<string> OnHandshakeRejected;
    public event Action<ServerStartGame> OnStartGameReceived;
    public event Action<ServerPitonPlaced> OnPitonPlaced;
    public event Action<ServerPitonRemoved> OnPitonRemoved;
    public event Action<ServerHandPose> OnHandPose;

    // Disconnection event for the UI.
    public event Action<string> OnDisconnected;

    public void Disconnect()
    {
        var wasConnected = IsSteamTransportActive;
        if (wasConnected)
            StopSteamTransport();

        Reset();

        if (wasConnected)
            Mod.Log.Msg("[CairnMP] Disconnected.");
    }

    /// <summary>Must be called every Unity frame: receives and dispatches pending packets.</summary>
    public void Update()
    {
        PumpSteamTransport();
        CairnMultiplayer.Api.MultiplayerApi.Runtime.Tick();
    }

    // -- Sending --------------------------------------------------------------

    public void SendPlayerState(float x, float y, float z, float yaw, string sceneName, PlayerState state)
    {
        if (IsSteamTransportActive)
            SendSteamPlayerState(x, y, z, yaw, sceneName, state);
    }

    public void SendPlayerFrame(NetFrameData frame)
    {
        if (!_debugLoggedFirstLocalPlayerFrameSend)
        {
            _debugLoggedFirstLocalPlayerFrameSend = true;
            Mod.LogDebug($"[NetSync] First local player NetFrame sent positions={FrameVectorCount(frame.Positions)} eulers={FrameVectorCount(frame.Eulers)} flags=0x{frame.Flags:X2}");
        }

        if (IsSteamTransportActive)
            SendSteamPlayerFrame(frame);
    }

    public void SendClimbotFrame(NetFrameData frame)
    {
        if (!_debugLoggedFirstLocalClimbotFrameSend)
        {
            _debugLoggedFirstLocalClimbotFrameSend = true;
            Mod.LogDebug($"[NetSync] First local climbot NetFrame sent positions={FrameVectorCount(frame.Positions)} eulers={FrameVectorCount(frame.Eulers)} flags=0x{frame.Flags:X2}");
        }

        if (IsSteamTransportActive)
            SendSteamClimbotFrame(frame);
    }

    private static int FrameVectorCount(float[] values) => values == null ? 0 : values.Length / 3;

    private void LogRemotePlayerFrameAccepted(int playerId, string playerName, NetFrameData frame)
    {
        if (!_debugLoggedFirstRemotePlayerFrame.Add(playerId)) return;

        Mod.LogDebug($"[NetSync] First remote player NetFrame accepted id={playerId} name='{playerName}' positions={FrameVectorCount(frame.Positions)} eulers={FrameVectorCount(frame.Eulers)} flags=0x{frame.Flags:X2}");
    }

    private void LogRemoteClimbotFrameAccepted(int playerId, NetFrameData frame)
    {
        if (!_debugLoggedFirstRemoteClimbotFrame.Add(playerId)) return;

        Mod.LogDebug($"[NetSync] First remote climbot NetFrame accepted id={playerId} positions={FrameVectorCount(frame.Positions)} eulers={FrameVectorCount(frame.Eulers)} flags=0x{frame.Flags:X2}");
    }

    private void LogRejectedNetFrame(string key, string label, string reason)
    {
        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        if (_debugRejectedFrameLogTimes.TryGetValue(key, out var last) && now - last < 2.0)
            return;

        _debugRejectedFrameLogTimes[key] = now;
        Mod.Log.Warning($"[NetSync] Rejected {label} NetFrame: {reason}");
    }

    public void SendPitonPlaced(uint pitonId, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, byte quality, int hp, int itemId)
    {
        if (IsSteamTransportActive)
            SendSteamPitonPlaced(pitonId, pos, rot, quality, hp, itemId);
    }

    public void SendPitonRemoved(uint pitonId)
    {
        if (IsSteamTransportActive)
            SendSteamPitonRemoved(pitonId);
    }

    public void SendHandPose(byte[] packed)
    {
        if (packed == null || packed.Length != Protocol.HandPosePackedSize) return;

        if (IsSteamTransportActive)
            SendSteamHandPose(packed);
    }

    /// <summary>Requests roping up (clip=true) or unroping (clip=false) with a player.</summary>
    public void SendRopeClip(int targetPlayerId, bool clip)
    {
        if (IsSteamTransportActive)
            SendSteamRopeClip(targetPlayerId, clip);
    }

    // -- Internals ------------------------------------------------------------

    /// <summary>Forgets everything tied to the session that just ended.</summary>
    private void Reset()
    {
        IsHandshakeComplete = false;
        LocalPlayerId = 0;
        ServerName = null;
        LastError = null;
        _remotePlayers.Clear();
        _debugLoggedFirstLocalPlayerFrameSend = false;
        _debugLoggedFirstLocalClimbotFrameSend = false;
        _debugLoggedFirstRemotePlayerFrame.Clear();
        _debugLoggedFirstRemoteClimbotFrame.Clear();
        _debugRejectedFrameLogTimes.Clear();
        ResetSteamTransportState();
    }

    public void Dispose() => Disconnect();
}
