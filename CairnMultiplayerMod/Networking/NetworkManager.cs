using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Core;

namespace CairnMultiplayerMod.Networking;

/// <summary>
/// Representation cote client d'un autre joueur connecte au meme serveur.
/// Mis a jour depuis les paquets ServerPlayerState. L'UI / le spawner de
/// fantomes lit ces donnees.
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

    // Donnees d'os depuis les paquets ServerBoneState (espace monde).
    public byte BoneCount;
    public float[] BonePositions;
    public float[] BoneRotations; // quaternion xyzw

    // Dernieres frames natives Cairn recues pour le joueur et son climbot.
    public bool HasPlayerFrame;
    public NetFrameData PlayerFrame;
    public double LastPlayerFrameTime;
    public bool HasClimbotFrame;
    public NetFrameData ClimbotFrame;

    // Mode de la lampe (AavaLightStick.CurrentMode) — synchronise quand un autre joueur change de mode.
    public int LampMode;
    public bool HasLampState;

    // Etat cosmetique (champ de bits, cf. Protocol.CosmeticFlag*) — bit 0 = gants lumineux.
    public byte CosmeticFlags;
    public bool HasCosmeticState;

    // Pose des doigts compressee (Protocol.HandPosePackedSize octets) — synchronisee
    // pour animer les mains du fantome en escalade.
    public byte[] HandPosePacked;
    public bool HasHandPose;

    // Etat de sommeil au bivouac (BivouacManager.IsAsleep) — l'hote l'agrege pour
    // decider si tout le monde dort (autorise le fast-forward du temps).
    public bool IsAsleep;
    public bool HasSleepState;
}

/// <summary>
/// Gère la connexion TCP au serveur de jeu.
///
/// Architecture threading :
///   - ReadLoop() s'exécute dans un thread dédié ; lit les frames TCP et les
///     place dans _pending (thread-safe).
///   - Update() est appelé chaque frame depuis le thread Unity ; vide _pending
///     et dispatche les paquets — les handlers modifient l'état Unity de manière sûre.
///   - Les méthodes Send* verrouillent _streamLock pour être thread-safe.
/// </summary>
public partial class NetworkManager : IDisposable
{
    private TcpClient _tcp;
    private NetworkStream _stream;
    private Thread _readThread;
    private volatile bool _running;

    // File de frames reçues à traiter sur le thread Unity.
    private readonly ConcurrentQueue<byte[]> _pending = new();

    // Verrou pour les écritures sur le stream (plusieurs goroutines peuvent envoyer).
    private readonly object _streamLock = new();

    public bool IsConnected => IsSteamTransportActive || (_tcp?.Connected == true && _running);
    public bool IsHandshakeComplete { get; private set; }
    public int LocalPlayerId { get; private set; }
    public string ServerName { get; private set; }
    public string LastError { get; private set; }

    // Identifiant du lobby courant, défini après création via l'API.
    // Utilisé pour persister l'association lobby → slot de sauvegarde.
    public string CurrentLobbyId { get; private set; }

    // Code court partageable du lobby courant (format "XXXX-XXXX"), peut être vide.
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

    // Evénement de déconnexion pour l'UI.
    public event Action<string> OnDisconnected;

    /// <summary>
    /// Se connecte directement à une adresse TCP ip:port.
    /// Conservé pour compatibilité avec l'ancien contrat public ; le flux de
    /// jeu actuel utilise le transport P2P Steam.
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

            // Envoyer le handshake immédiatement
            var hs = new ClientHandshake
            {
                ProtocolVersion = Protocol.Version,
                PlayerName      = ModConfig.PlayerName.Value ?? "Player",
                RoomCode        = ModConfig.RoomCode.Value ?? "",
            };
            SendFrame(PacketCodec.Frame(PacketId.ClientHandshake, hs));

            // Démarrer le thread de lecture
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
                // Signaler la déconnexion propre au serveur
                SendFrame(PacketCodec.Frame(PacketId.ClientDisconnect, new ClientChat { Message = "" }));
            }
            catch { /* ignore les erreurs d'envoi lors du disconnect */ }
        }

        var wasConnected = _running;
        Reset();

        if (wasConnected)
            Mod.Log.Msg("[CairnMP] Disconnected.");
    }

    /// <summary>
    /// Doit être appelé chaque frame Unity. Vide la file de paquets reçus et les traite.
    /// </summary>
    public void Update()
    {
        PumpSteamTransport();

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

    // -- Envoi ----------------------------------------------------------------

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

    public void SendBoneState(byte boneCount, float[] positions, float[] rotations)
    {
        if (IsSteamTransportActive)
        {
            SendSteamBoneState(boneCount, positions, rotations);
            return;
        }
        if (!IsHandshakeComplete) return;
        var pkt = new ClientBoneState
        {
            BoneCount = boneCount,
            Positions = positions,
            Rotations = rotations,
        };
        SendFrameNonBlocking(PacketCodec.Frame(PacketId.ClientBoneState, pkt));
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

    /// <summary>Demande d'encordement (clip=true) ou decordage (clip=false) avec un joueur.</summary>
    public void SendRopeClip(int targetPlayerId, bool clip)
    {
        if (IsSteamTransportActive)
            SendSteamRopeClip(targetPlayerId, clip);
    }

    // -- Internals ------------------------------------------------------------

    /// <summary>Ecrit une frame TCP de manière thread-safe (bloquant).</summary>
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
    /// Envoi non-bloquant : abandonne sans exception si le verrou n'est pas disponible.
    /// Utilisé pour les paquets à haute fréquence (position/bones).
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
            // Sinon : on abandonne silencieusement (paquet sequenced)
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
    /// Thread de lecture TCP. Lit les frames en continu et les place dans _pending.
    /// S'arrête quand _running devient false ou en cas d'erreur réseau.
    /// </summary>
    private void ReadLoop()
    {
        var lenBuf = new byte[2];
        try
        {
            while (_running)
            {
                // Lire le préfixe de longueur (2 octets)
                if (!ReadExact(_stream, lenBuf, 2)) break;
                int len = lenBuf[0] | (lenBuf[1] << 8); // uint16 LE

                // Lire le payload
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

    /// <summary>Lit exactement count octets depuis le stream.</summary>
    private static bool ReadExact(Stream stream, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = stream.Read(buf, offset, count - offset);
            if (n == 0) return false; // connexion fermée
            offset += n;
        }
        return true;
    }

    /// <summary>Appelé depuis le thread de lecture en cas d'erreur ou de déconnexion.</summary>
    private void HandleReadError()
    {
        if (!_running) return;
        Mod.Log.Msg("[CairnMP] Connection lost.");
        // Enqueue un marqueur de déconnexion pour le thread Unity (payload vide = déconnexion)
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
