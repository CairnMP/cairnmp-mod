using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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

    // Sleep state at the bivouac (BivouacManager.IsAsleep) — the host aggregates it to
    // decide whether everyone is asleep (allows fast-forwarding time).
    public bool IsAsleep;
    public bool HasSleepState;
}

/// <summary>
/// Manages the TCP connection to the game server.
///
/// Threading architecture:
///   - ReadLoop() runs on a dedicated thread; reads TCP frames and
///     enqueues them into _pending (thread-safe).
///   - Update() is called every frame from the Unity thread; drains _pending
///     and dispatches the packets — the handlers modify Unity state safely.
///   - The Send* methods lock _streamLock to be thread-safe.
/// </summary>
public partial class NetworkManager : IDisposable
{
    private TcpClient _tcp;
    private NetworkStream _stream;
    private Thread _readThread;
    private volatile bool _running;

    // Queue of received frames to process on the Unity thread.
    private readonly ConcurrentQueue<byte[]> _pending = new();

    // Lock for stream writes (several threads may send).
    private readonly object _streamLock = new();

    public bool IsConnected => IsSteamTransportActive || (_tcp?.Connected == true && _running);
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
    public event Action<int, string, string> OnChatReceived; // fromId, fromName, message
    public event Action<int, int, bool> OnRopeClip; // fromId, targetId, clip
    public event Action OnHandshakeAck;
    public event Action<string> OnHandshakeRejected;
    public event Action<ServerStartGame> OnStartGameReceived;
    public event Action<ServerPitonPlaced> OnPitonPlaced;
    public event Action<ServerPitonRemoved> OnPitonRemoved;
    public event Action<ServerWeatherState> OnWeatherState;
    public event Action<ServerPingPlaced> OnPingPlaced;
    public event Action<ServerHandPose> OnHandPose;
    public event Action<ServerTimeState> OnTimeState;

    // Disconnection event for the UI.
    public event Action<string> OnDisconnected;

    /// <summary>
    /// Connects directly to an ip:port TCP address.
    /// Kept for compatibility with the old public contract; the current game
    /// flow uses the Steam P2P transport.
    /// </summary>
    public void ConnectToAddress(string ip, int port)
    {
        if (IsConnected)
        {
            Mod.Log.Warning("[CairnMP] Already connected!");
            return;
        }

        Reset();

        try
        {
            _tcp = new TcpClient();
            _tcp.Connect(ip, port);
            _stream = _tcp.GetStream();
            _running = true;

            Mod.Log.Msg($"[CairnMP] Connected to {ip}:{port}. Sending handshake...");

            // Send the handshake immediately
            var hs = new ClientHandshake
            {
                ProtocolVersion = Protocol.Version,
                PlayerName      = ModConfig.PlayerName.Value ?? "Player",
                RoomCode        = ModConfig.RoomCode.Value ?? "",
            };
            SendFrame(PacketCodec.Frame(PacketId.ClientHandshake, hs));

            // Start the read thread
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "CairnMP-Read" };
            _readThread.Start();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Mod.Log.Error($"[CairnMP] Connect failed: {ex.Message}");
            Reset();
        }
    }

    public void Disconnect()
    {
        if (IsSteamTransportActive)
            StopSteamTransport();

        if (_stream != null && _running)
        {
            try
            {
                // Signal a clean disconnect to the server
                SendFrame(PacketCodec.Frame(PacketId.ClientDisconnect, new ClientChat { Message = "" }));
            }
            catch { /* ignore send errors during disconnect */ }
        }

        var wasConnected = _running;
        Reset();

        if (wasConnected)
            Mod.Log.Msg("[CairnMP] Disconnected.");
    }

    /// <summary>
    /// Must be called every Unity frame. Drains the queue of received packets and processes them.
    /// </summary>
    public void Update()
    {
        PumpSteamTransport();
        CairnMultiplayer.Api.MultiplayerApi.Runtime.Tick();

        while (_pending.TryDequeue(out var payload))
        {
            try
            {
                ProcessPacket(payload);
            }
            catch (Exception ex)
            {
                Mod.Log.Error($"[CairnMP] Packet handler exception: {ex}");
            }
        }
    }

    // -- Sending --------------------------------------------------------------

    public void SendPlayerState(float x, float y, float z, float yaw, string sceneName, PlayerState state)
    {
        if (IsSteamTransportActive)
        {
            SendSteamPlayerState(x, y, z, yaw, sceneName, state);
            return;
        }
        if (!IsHandshakeComplete) return;
        var pkt = new ClientPlayerState
        {
            X = x, Y = y, Z = z,
            YawDeg = yaw,
            SceneName = sceneName ?? "",
            State = state,
        };
        SendFrameNonBlocking(PacketCodec.Frame(PacketId.ClientPlayerState, pkt));
    }

    public void SendPlayerFrame(NetFrameData frame)
    {
        if (!_debugLoggedFirstLocalPlayerFrameSend)
        {
            _debugLoggedFirstLocalPlayerFrameSend = true;
            Mod.LogDebug($"[NetSync] First local player NetFrame sent positions={FrameVectorCount(frame.Positions)} eulers={FrameVectorCount(frame.Eulers)} flags=0x{frame.Flags:X2}");
        }

        if (IsSteamTransportActive)
        {
            SendSteamPlayerFrame(frame);
            return;
        }
        if (!IsHandshakeComplete) return;
        SendFrameNonBlocking(PacketCodec.Frame(PacketId.ClientPlayerFrame, new ClientPlayerFrame { Frame = frame }));
    }

    public void SendClimbotFrame(NetFrameData frame)
    {
        if (!_debugLoggedFirstLocalClimbotFrameSend)
        {
            _debugLoggedFirstLocalClimbotFrameSend = true;
            Mod.LogDebug($"[NetSync] First local climbot NetFrame sent positions={FrameVectorCount(frame.Positions)} eulers={FrameVectorCount(frame.Eulers)} flags=0x{frame.Flags:X2}");
        }

        if (IsSteamTransportActive)
        {
            SendSteamClimbotFrame(frame);
            return;
        }
        if (!IsHandshakeComplete) return;
        SendFrameNonBlocking(PacketCodec.Frame(PacketId.ClientClimbotFrame, new ClientClimbotFrame { Frame = frame }));
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
        {
            SendSteamPitonPlaced(pitonId, pos, rot, quality, hp, itemId);
            return;
        }
        if (!IsHandshakeComplete) return;
        var pkt = new ClientPitonPlaced
        {
            PitonId = pitonId,
            PosX = pos.x, PosY = pos.y, PosZ = pos.z,
            RotX = rot.x, RotY = rot.y, RotZ = rot.z, RotW = rot.w,
            Quality = quality,
            PitonHp = hp,
            ItemId = itemId,
        };
        SendFrame(PacketCodec.Frame(PacketId.ClientPitonPlaced, pkt));
    }

    public void SendPitonRemoved(uint pitonId)
    {
        if (IsSteamTransportActive)
        {
            SendSteamPitonRemoved(pitonId);
            return;
        }
        if (!IsHandshakeComplete) return;
        var pkt = new ClientPitonRemoved { PitonId = pitonId };
        SendFrame(PacketCodec.Frame(PacketId.ClientPitonRemoved, pkt));
    }

    public void SendWeatherState(WeatherSyncData state, bool reliable = false)
    {
        if (!IsValidWeatherState(state)) return;

        if (IsSteamTransportActive)
        {
            SendSteamWeatherState(state, reliable);
            return;
        }
        if (!IsHandshakeComplete) return;

        var frame = PacketCodec.Frame(PacketId.ClientWeatherState, new ClientWeatherState { State = state });
        if (reliable)
            SendFrame(frame);
        else
            SendFrameNonBlocking(frame);
    }

    public void SendLampState(int mode)
    {
        if (IsSteamTransportActive)
        {
            SendSteamLampState(mode);
            return;
        }
        if (!IsHandshakeComplete) return;

        SendFrame(PacketCodec.Frame(PacketId.ClientLampState, new ClientLampState { Mode = mode }));
    }

    public void SendCosmeticState(byte flags)
    {
        if (IsSteamTransportActive)
        {
            SendSteamCosmeticState(flags);
            return;
        }
        if (!IsHandshakeComplete) return;

        SendFrame(PacketCodec.Frame(PacketId.ClientCosmeticState, new ClientCosmeticState { Flags = flags }));
    }

    public void SendSleepState(bool asleep)
    {
        if (IsSteamTransportActive)
        {
            SendSteamSleepState(asleep);
            return;
        }
        if (!IsHandshakeComplete) return;
        SendFrameNonBlocking(PacketCodec.Frame(PacketId.ClientSleepState, new ClientSleepState { Asleep = asleep }));
    }

    public void SendTimeState(ServerTimeState state)
    {
        if (IsSteamTransportActive)
        {
            SendSteamTimeState(state);
            return;
        }
        if (!IsHandshakeComplete) return;
        SendFrameNonBlocking(PacketCodec.Frame(PacketId.ServerTimeState, state));
    }

    public void SendHandPose(byte[] packed)
    {
        if (packed == null || packed.Length != Protocol.HandPosePackedSize) return;

        if (IsSteamTransportActive)
        {
            SendSteamHandPose(packed);
            return;
        }
        if (!IsHandshakeComplete) return;

        SendFrameNonBlocking(PacketCodec.Frame(PacketId.ClientHandPose, new ClientHandPose { Packed = packed }));
    }

    public void SendPingPlaced(UnityEngine.Vector3 pos)
    {
        if (IsSteamTransportActive)
        {
            SendSteamPingPlaced(pos);
            return;
        }
        if (!IsHandshakeComplete) return;

        var pkt = new ClientPingPlaced { PosX = pos.x, PosY = pos.y, PosZ = pos.z };
        SendFrame(PacketCodec.Frame(PacketId.ClientPingPlaced, pkt));
    }

    public void SendChat(string message)
    {
        if (IsSteamTransportActive)
        {
            SendSteamChat(message);
            return;
        }
        if (!IsHandshakeComplete) return;
        var pkt = new ClientChat { Message = message ?? "" };
        SendFrame(PacketCodec.Frame(PacketId.ClientChat, pkt));
    }

    /// <summary>Requests roping up (clip=true) or unroping (clip=false) with a player.</summary>
    public void SendRopeClip(int targetPlayerId, bool clip)
    {
        if (IsSteamTransportActive)
            SendSteamRopeClip(targetPlayerId, clip);
    }

    // -- Internals ------------------------------------------------------------

    /// <summary>Writes a TCP frame in a thread-safe way (blocking).</summary>
    private void SendFrame(byte[] frame)
    {
        if (!_running || _stream == null) return;
        try
        {
            lock (_streamLock)
            {
                _stream.Write(frame, 0, frame.Length);
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[CairnMP] Send error: {ex.Message}");
            HandleReadError();
        }
    }

    /// <summary>
    /// Non-blocking send: gives up without an exception if the lock isn't available.
    /// Used for high-frequency packets (position/bones).
    /// </summary>
    private void SendFrameNonBlocking(byte[] frame)
    {
        if (!_running || _stream == null) return;
        bool lockAcquired = false;
        try
        {
            lockAcquired = Monitor.TryEnter(_streamLock);
            if (lockAcquired)
                _stream.Write(frame, 0, frame.Length);
            // Otherwise: drop it silently (sequenced packet)
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[CairnMP] Send error: {ex.Message}");
            HandleReadError();
        }
        finally
        {
            if (lockAcquired)
                Monitor.Exit(_streamLock);
        }
    }

    /// <summary>
    /// TCP read thread. Continuously reads frames and enqueues them into _pending.
    /// Stops when _running becomes false or on a network error.
    /// </summary>
    private void ReadLoop()
    {
        var lenBuf = new byte[2];
        try
        {
            while (_running)
            {
                // Read the length prefix (2 bytes)
                if (!ReadExact(_stream, lenBuf, 2)) break;
                int len = lenBuf[0] | (lenBuf[1] << 8); // uint16 LE

                // Read the payload
                var payload = new byte[len];
                if (!ReadExact(_stream, payload, len)) break;

                _pending.Enqueue(payload);
            }
        }
        catch (Exception ex) when (_running)
        {
            Mod.Log.Error($"[CairnMP] Read error: {ex.Message}");
        }

        if (_running)
            HandleReadError();
    }

    /// <summary>Reads exactly count bytes from the stream.</summary>
    private static bool ReadExact(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = stream.Read(buf, offset, count - offset);
            if (n == 0) return false; // connection closed
            offset += n;
        }
        return true;
    }

    /// <summary>Called from the read thread on an error or disconnection.</summary>
    private void HandleReadError()
    {
        if (!_running) return;
        Mod.Log.Msg("[CairnMP] Connection lost.");
        // Enqueue a disconnection marker for the Unity thread (empty payload = disconnect)
        _pending.Enqueue(Array.Empty<byte>());
        Reset();
    }

    private void Reset()
    {
        _running = false;
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
        _readThread = null;
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

        while (_pending.TryDequeue(out _)) { }
    }

    public void Dispose() => Disconnect();
}
