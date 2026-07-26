using System;
using System.IO;
using System.Text;

namespace CairnMultiplayer.Shared;

/// <summary>
/// Common interface for all packet types of the CairnMP protocol.
/// Serialization uses BinaryWriter/BinaryReader (.NET stdlib, no external dependency).
/// </summary>
public interface IPacket
{
    void Serialize(BinaryWriter writer);
    void Deserialize(BinaryReader reader);
}

/// <summary>
/// Encoding helpers for the CairnMP binary protocol.
///
/// Encoding identical to LiteNetLib.Utils.NetDataWriter/Reader:
///   int/uint/float : little-endian (4 bytes)
///   bool           : 1 byte (0 or 1)
///   string         : [uint16 LE : number of UTF-8 bytes] [UTF-8 bytes]
///
/// WARNING: BinaryWriter.Write(string) uses a 7-bit length prefix and is not
/// compatible. Always use WriteString/ReadString here.
/// </summary>
public static class PacketCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private const int MaxFrameVectorCount = 512;
    private const int MaxUInt16Length = ushort.MaxValue;

    /// <summary>Writes a string as [uint16 LE : byteCount][UTF-8 bytes].</summary>
    public static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Utf8.GetBytes(s ?? "");
        if (bytes.Length > MaxUInt16Length)
            throw new InvalidDataException($"String payload too large: {bytes.Length} bytes");

        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>Reads a string encoded as [uint16 LE : byteCount][UTF-8 bytes].</summary>
    public static string ReadString(BinaryReader r)
    {
        int len = r.ReadUInt16();
        var bytes = r.ReadBytes(len);
        if (bytes.Length != len)
            throw new EndOfStreamException($"Expected {len} string bytes, received {bytes.Length}");
        return Utf8.GetString(bytes);
    }

    /// <summary>Writes an opaque payload as [uint16 LE length][bytes].</summary>
    public static void WriteBytes(BinaryWriter w, byte[] value)
    {
        value ??= Array.Empty<byte>();
        if (value.Length > MaxUInt16Length)
            throw new InvalidDataException($"Binary payload too large: {value.Length} bytes");

        w.Write((ushort)value.Length);
        w.Write(value);
    }

    /// <summary>Reads an opaque payload written by <see cref="WriteBytes"/>.</summary>
    public static byte[] ReadBytes(BinaryReader r)
    {
        int length = r.ReadUInt16();
        var value = r.ReadBytes(length);
        if (value.Length != length)
            throw new EndOfStreamException($"Expected {length} payload bytes, received {value.Length}");
        return value;
    }

    /// <summary>
    /// Builds a complete TCP frame:
    ///   [uint16 LE : payload length] [byte : PacketId] [packet fields]
    /// </summary>
    public static byte[] Frame(PacketId id, IPacket packet)
    {
        // Encode the payload (id + fields)
        using var payload = new MemoryStream();
        using (var pw = new BinaryWriter(payload, Utf8, leaveOpen: true))
        {
            pw.Write((byte)id);
            packet.Serialize(pw);
        }
        var payloadBytes = payload.ToArray();
        if (payloadBytes.Length > MaxUInt16Length)
            throw new InvalidDataException($"Packet payload too large: {payloadBytes.Length} bytes");

        // Build the final frame with the length prefix
        using var frame = new MemoryStream(payloadBytes.Length + 2);
        using var fw = new BinaryWriter(frame, Utf8, leaveOpen: true);
        fw.Write((ushort)payloadBytes.Length);
        fw.Write(payloadBytes);
        return frame.ToArray();
    }

    /// <summary>Writes a flattened Vector3 array [x0,y0,z0, ...].</summary>
    public static void WriteVectorArray(BinaryWriter w, float[] values)
    {
        var count = values == null ? 0 : values.Length / 3;
        if (values != null && values.Length % 3 != 0)
            throw new InvalidDataException($"Vector array length is not divisible by 3: {values.Length}");
        if (count > MaxFrameVectorCount)
            throw new InvalidDataException($"NetFrame vector count too large: {count}");

        w.Write(count);
        for (int i = 0; i < count * 3; i++)
            w.Write(values[i]);
    }

    /// <summary>Reads a flattened Vector3 array [x0,y0,z0, ...].</summary>
    public static float[] ReadVectorArray(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > MaxFrameVectorCount)
            throw new InvalidDataException($"Invalid NetFrame vector count: {count}");

        var values = new float[count * 3];
        for (int i = 0; i < values.Length; i++)
            values[i] = r.ReadSingle();
        return values;
    }
}

/// <summary>
/// Minimal data from Cairn's native NetFrame, serializable without a Unity reference.
/// Positions/eulers are flattened Vector3 arrays.
/// </summary>
public struct NetFrameData
{
    public bool IsValid;
    public byte Flags;
    public float[] Positions;
    public float[] Eulers;

    public void Serialize(BinaryWriter w)
    {
        w.Write(IsValid);
        w.Write(Flags);
        PacketCodec.WriteVectorArray(w, Positions);
        PacketCodec.WriteVectorArray(w, Eulers);
    }

    public void Deserialize(BinaryReader r)
    {
        IsValid = r.ReadBoolean();
        Flags = r.ReadByte();
        Positions = PacketCodec.ReadVectorArray(r);
        Eulers = PacketCodec.ReadVectorArray(r);
    }
}

/// <summary>
/// Authoritative weather snapshot captured by the host. The enum values come from
/// Cairn's IL2CPP bindings but stay stored as int to keep the shared packet free of
/// any reference to the game.
/// </summary>
public struct WeatherSyncData
{
    public bool IsValid;
    public int WeatherType;
    public int RainType;
    public int ThunderType;
    public int FogType;
    public int CloudsType;
    public int WindType;
    public int WindOverride;
    public int SnowRainForceMode;
    public float RemainingDuration;
    public float UseSnowInsteadOfRain01;
    public float WindForce;
    public float WindForce01;
    public float WindDirX, WindDirY, WindDirZ;
    public float WindAngle;

    public void Serialize(BinaryWriter w)
    {
        w.Write(IsValid);
        w.Write(WeatherType);
        w.Write(RainType);
        w.Write(ThunderType);
        w.Write(FogType);
        w.Write(CloudsType);
        w.Write(WindType);
        w.Write(WindOverride);
        w.Write(SnowRainForceMode);
        w.Write(RemainingDuration);
        w.Write(UseSnowInsteadOfRain01);
        w.Write(WindForce);
        w.Write(WindForce01);
        w.Write(WindDirX);
        w.Write(WindDirY);
        w.Write(WindDirZ);
        w.Write(WindAngle);
    }

    public void Deserialize(BinaryReader r)
    {
        IsValid = r.ReadBoolean();
        WeatherType = r.ReadInt32();
        RainType = r.ReadInt32();
        ThunderType = r.ReadInt32();
        FogType = r.ReadInt32();
        CloudsType = r.ReadInt32();
        WindType = r.ReadInt32();
        WindOverride = r.ReadInt32();
        SnowRainForceMode = r.ReadInt32();
        RemainingDuration = r.ReadSingle();
        UseSnowInsteadOfRain01 = r.ReadSingle();
        WindForce = r.ReadSingle();
        WindForce01 = r.ReadSingle();
        WindDirX = r.ReadSingle();
        WindDirY = r.ReadSingle();
        WindDirZ = r.ReadSingle();
        WindAngle = r.ReadSingle();
    }
}

// ---- Client -> Server -------------------------------------------------------

public struct ClientHandshake : IPacket
{
    public int ProtocolVersion;
    public string PlayerName;
    public string RoomCode;

    public void Serialize(BinaryWriter w)
    {
        w.Write(ProtocolVersion);
        PacketCodec.WriteString(w, PlayerName ?? "");
        PacketCodec.WriteString(w, RoomCode ?? "");
    }

    public void Deserialize(BinaryReader r)
    {
        ProtocolVersion = r.ReadInt32();
        PlayerName = PacketCodec.ReadString(r);
        RoomCode = PacketCodec.ReadString(r);
    }
}

public struct ClientPlayerState : IPacket
{
    public float X, Y, Z;
    public float YawDeg;
    public string SceneName;
    public PlayerState State;

    public void Serialize(BinaryWriter w)
    {
        w.Write(X); w.Write(Y); w.Write(Z);
        w.Write(YawDeg);
        PacketCodec.WriteString(w, SceneName ?? "");
        w.Write((byte)State);
    }

    public void Deserialize(BinaryReader r)
    {
        X = r.ReadSingle(); Y = r.ReadSingle(); Z = r.ReadSingle();
        YawDeg = r.ReadSingle();
        SceneName = PacketCodec.ReadString(r);
        State = (PlayerState)r.ReadByte();
    }
}

public struct ClientChat : IPacket
{
    public string Message;

    public void Serialize(BinaryWriter w) => PacketCodec.WriteString(w, Message ?? "");
    public void Deserialize(BinaryReader r) => Message = PacketCodec.ReadString(r);
}

public struct ClientBoneState : IPacket
{
    public byte BoneCount;
    public float[] Positions;  // flattened [x0,y0,z0, ...] world space
    public float[] Rotations;  // flattened [x0,y0,z0,w0, ...] quaternion, world space

    public void Serialize(BinaryWriter w)
    {
        w.Write(BoneCount);
        for (int i = 0; i < BoneCount * 3; i++) w.Write(Positions[i]);
        for (int i = 0; i < BoneCount * 4; i++) w.Write(Rotations[i]);
    }

    public void Deserialize(BinaryReader r)
    {
        BoneCount = r.ReadByte();
        Positions = new float[BoneCount * 3];
        Rotations = new float[BoneCount * 4];
        for (int i = 0; i < BoneCount * 3; i++) Positions[i] = r.ReadSingle();
        for (int i = 0; i < BoneCount * 4; i++) Rotations[i] = r.ReadSingle();
    }
}

public struct ClientPitonPlaced : IPacket
{
    public uint PitonId;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ, RotW; // quaternion
    public byte Quality; // PitonExecutionQuality
    public int PitonHp;
    public int ItemId; // InventoryItemStringId (just an int)

    public void Serialize(BinaryWriter w)
    {
        w.Write(PitonId);
        w.Write(PosX); w.Write(PosY); w.Write(PosZ);
        w.Write(RotX); w.Write(RotY); w.Write(RotZ); w.Write(RotW);
        w.Write(Quality);
        w.Write(PitonHp);
        w.Write(ItemId);
    }

    public void Deserialize(BinaryReader r)
    {
        PitonId = r.ReadUInt32();
        PosX = r.ReadSingle(); PosY = r.ReadSingle(); PosZ = r.ReadSingle();
        RotX = r.ReadSingle(); RotY = r.ReadSingle(); RotZ = r.ReadSingle(); RotW = r.ReadSingle();
        Quality = r.ReadByte();
        PitonHp = r.ReadInt32();
        ItemId = r.ReadInt32();
    }
}

public struct ClientPitonRemoved : IPacket
{
    public uint PitonId;

    public void Serialize(BinaryWriter w) => w.Write(PitonId);
    public void Deserialize(BinaryReader r) => PitonId = r.ReadUInt32();
}

public struct ClientPlayerFrame : IPacket
{
    public NetFrameData Frame;

    public void Serialize(BinaryWriter w) => Frame.Serialize(w);
    public void Deserialize(BinaryReader r) => Frame.Deserialize(r);
}

public struct ClientClimbotFrame : IPacket
{
    public NetFrameData Frame;

    public void Serialize(BinaryWriter w) => Frame.Serialize(w);
    public void Deserialize(BinaryReader r) => Frame.Deserialize(r);
}

public struct ClientWeatherState : IPacket
{
    public WeatherSyncData State;

    public void Serialize(BinaryWriter w) => State.Serialize(w);
    public void Deserialize(BinaryReader r) => State.Deserialize(r);
}

/// <summary>
/// State of the local player's lamp (AavaLightStick.CurrentMode). Mode is a
/// game-side enum carried here as an int. Sent only on change.
/// </summary>
public struct ClientLampState : IPacket
{
    public int Mode;

    public void Serialize(BinaryWriter w) => w.Write(Mode);
    public void Deserialize(BinaryReader r) => Mode = r.ReadInt32();
}

/// <summary>
/// Finger pose of the local player: 30 bones compressed smallest-three
/// (Protocol.HandPosePackedSize bytes). Sent only on change.
/// </summary>
public struct ClientHandPose : IPacket
{
    public byte[] Packed;

    public void Serialize(BinaryWriter w)
    {
        var bytes = Packed ?? Array.Empty<byte>();
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    public void Deserialize(BinaryReader r)
    {
        int len = r.ReadUInt16();
        Packed = r.ReadBytes(len);
    }
}

/// <summary>
/// Sleep state of the local player (BivouacManager.IsAsleep). Sent on change;
/// used by the host to decide whether everyone is asleep.
/// </summary>
public struct ClientSleepState : IPacket
{
    public bool Asleep;

    public void Serialize(BinaryWriter w) => w.Write(Asleep);
    public void Deserialize(BinaryReader r) => Asleep = r.ReadBoolean();
}

/// <summary>
/// The client requests to rope up (Clip=true) or unrope (Clip=false) with another
/// player. The host relays it as ServerRopeClip to everyone.
/// </summary>
public struct ClientRopeClip : IPacket
{
    public int TargetPlayerId;
    public bool Clip;

    public void Serialize(BinaryWriter w) { w.Write(TargetPlayerId); w.Write(Clip); }
    public void Deserialize(BinaryReader r) { TargetPlayerId = r.ReadInt32(); Clip = r.ReadBoolean(); }
}

// ---- Server -> Client -------------------------------------------------------

public struct ServerHandshakeAck : IPacket
{
    public int AssignedPlayerId;
    public string ServerName;

    public void Serialize(BinaryWriter w)
    {
        w.Write(AssignedPlayerId);
        PacketCodec.WriteString(w, ServerName ?? "");
    }

    public void Deserialize(BinaryReader r)
    {
        AssignedPlayerId = r.ReadInt32();
        ServerName = PacketCodec.ReadString(r);
    }
}

public struct ServerHandshakeReject : IPacket
{
    public string Reason;

    public void Serialize(BinaryWriter w) => PacketCodec.WriteString(w, Reason ?? "");
    public void Deserialize(BinaryReader r) => Reason = PacketCodec.ReadString(r);
}

public struct ServerPlayerJoined : IPacket
{
    public int PlayerId;
    public string PlayerName;

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId);
        PacketCodec.WriteString(w, PlayerName ?? "");
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId = r.ReadInt32();
        PlayerName = PacketCodec.ReadString(r);
    }
}

public struct ServerPlayerLeft : IPacket
{
    public int PlayerId;

    public void Serialize(BinaryWriter w) => w.Write(PlayerId);
    public void Deserialize(BinaryReader r) => PlayerId = r.ReadInt32();
}

public struct ServerPlayerState : IPacket
{
    public int PlayerId;
    public float X, Y, Z;
    public float YawDeg;
    public string SceneName;
    public PlayerState State;

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId);
        w.Write(X); w.Write(Y); w.Write(Z);
        w.Write(YawDeg);
        PacketCodec.WriteString(w, SceneName ?? "");
        w.Write((byte)State);
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId = r.ReadInt32();
        X = r.ReadSingle(); Y = r.ReadSingle(); Z = r.ReadSingle();
        YawDeg = r.ReadSingle();
        SceneName = PacketCodec.ReadString(r);
        State = (PlayerState)r.ReadByte();
    }
}

/// <summary>
/// The server tells the client to immediately start a new story game with the given
/// difficulty and gameplay flags. Sent right after the handshake ACK, and again
/// every time the server decides to restart the session.
/// </summary>
public struct ServerStartGame : IPacket
{
    public int Difficulty;       // cast from GameDifficulty
    public bool SkipTutorials;
    public bool SkipPractice;
    public bool AssistEnabled;

    public void Serialize(BinaryWriter w)
    {
        w.Write(Difficulty);
        w.Write(SkipTutorials);
        w.Write(SkipPractice);
        w.Write(AssistEnabled);
    }

    public void Deserialize(BinaryReader r)
    {
        Difficulty = r.ReadInt32();
        SkipTutorials = r.ReadBoolean();
        SkipPractice = r.ReadBoolean();
        AssistEnabled = r.ReadBoolean();
    }
}

public struct ServerBoneState : IPacket
{
    public int PlayerId;
    public byte BoneCount;
    public float[] Positions;
    public float[] Rotations;

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId);
        w.Write(BoneCount);
        for (int i = 0; i < BoneCount * 3; i++) w.Write(Positions[i]);
        for (int i = 0; i < BoneCount * 4; i++) w.Write(Rotations[i]);
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId = r.ReadInt32();
        BoneCount = r.ReadByte();
        Positions = new float[BoneCount * 3];
        Rotations = new float[BoneCount * 4];
        for (int i = 0; i < BoneCount * 3; i++) Positions[i] = r.ReadSingle();
        for (int i = 0; i < BoneCount * 4; i++) Rotations[i] = r.ReadSingle();
    }
}

public struct ServerPitonPlaced : IPacket
{
    public int FromPlayerId;
    public uint PitonId;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ, RotW;
    public byte Quality;
    public int PitonHp;
    public int ItemId;

    public void Serialize(BinaryWriter w)
    {
        w.Write(FromPlayerId);
        w.Write(PitonId);
        w.Write(PosX); w.Write(PosY); w.Write(PosZ);
        w.Write(RotX); w.Write(RotY); w.Write(RotZ); w.Write(RotW);
        w.Write(Quality);
        w.Write(PitonHp);
        w.Write(ItemId);
    }

    public void Deserialize(BinaryReader r)
    {
        FromPlayerId = r.ReadInt32();
        PitonId = r.ReadUInt32();
        PosX = r.ReadSingle(); PosY = r.ReadSingle(); PosZ = r.ReadSingle();
        RotX = r.ReadSingle(); RotY = r.ReadSingle(); RotZ = r.ReadSingle(); RotW = r.ReadSingle();
        Quality = r.ReadByte();
        PitonHp = r.ReadInt32();
        ItemId = r.ReadInt32();
    }
}

public struct ServerPitonRemoved : IPacket
{
    public int FromPlayerId;
    public uint PitonId;

    public void Serialize(BinaryWriter w) { w.Write(FromPlayerId); w.Write(PitonId); }
    public void Deserialize(BinaryReader r) { FromPlayerId = r.ReadInt32(); PitonId = r.ReadUInt32(); }
}

public struct ServerPlayerFrame : IPacket
{
    public int PlayerId;
    public string PlayerName;
    public NetFrameData Frame;

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId);
        PacketCodec.WriteString(w, PlayerName ?? "");
        Frame.Serialize(w);
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId = r.ReadInt32();
        PlayerName = PacketCodec.ReadString(r);
        Frame.Deserialize(r);
    }
}

public struct ServerClimbotFrame : IPacket
{
    public int PlayerId;
    public NetFrameData Frame;

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId);
        Frame.Serialize(w);
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId = r.ReadInt32();
        Frame.Deserialize(r);
    }
}

public struct ServerWeatherState : IPacket
{
    public WeatherSyncData State;

    public void Serialize(BinaryWriter w) => State.Serialize(w);
    public void Deserialize(BinaryReader r) => State.Deserialize(r);
}

/// <summary>Host relay of a player's lamp Mode to everyone else.</summary>
public struct ServerLampState : IPacket
{
    public int PlayerId;
    public int Mode;

    public void Serialize(BinaryWriter w) { w.Write(PlayerId); w.Write(Mode); }
    public void Deserialize(BinaryReader r) { PlayerId = r.ReadInt32(); Mode = r.ReadInt32(); }
}

/// <summary>
/// Cosmetic state of the local player (Flags bit field, see Protocol.CosmeticFlag*).
/// For now: bit 0 = glowing gloves active. Sent only on change.
/// </summary>
public struct ClientCosmeticState : IPacket
{
    public byte Flags;

    public void Serialize(BinaryWriter w) => w.Write(Flags);
    public void Deserialize(BinaryReader r) => Flags = r.ReadByte();
}

/// <summary>Host relay of a player's cosmetic state to everyone else.</summary>
public struct ServerCosmeticState : IPacket
{
    public int PlayerId;
    public byte Flags;

    public void Serialize(BinaryWriter w) { w.Write(PlayerId); w.Write(Flags); }
    public void Deserialize(BinaryReader r) { PlayerId = r.ReadInt32(); Flags = r.ReadByte(); }
}

/// <summary>
/// Authoritative time of day broadcast by the host. DayTime01 is the normalized
/// 0-1 value of NightDayCycle.dayTime01; AllAsleep indicates whether all players
/// are asleep (fast-forward is allowed). Clients align their visual clock to it.
/// </summary>
public struct ServerTimeState : IPacket
{
    public float DayTime01;
    public bool AllAsleep;

    public void Serialize(BinaryWriter w) { w.Write(DayTime01); w.Write(AllAsleep); }
    public void Deserialize(BinaryReader r) { DayTime01 = r.ReadSingle(); AllAsleep = r.ReadBoolean(); }
}

/// <summary>Host relay of a player's finger pose to everyone else.</summary>
public struct ServerHandPose : IPacket
{
    public int PlayerId;
    public byte[] Packed;

    public void Serialize(BinaryWriter w)
    {
        w.Write(PlayerId);
        var bytes = Packed ?? Array.Empty<byte>();
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    public void Deserialize(BinaryReader r)
    {
        PlayerId = r.ReadInt32();
        int len = r.ReadUInt16();
        Packed = r.ReadBytes(len);
    }
}

public struct ServerChatBroadcast : IPacket
{
    public int FromPlayerId;
    public string FromPlayerName;
    public string Message;

    public void Serialize(BinaryWriter w)
    {
        w.Write(FromPlayerId);
        PacketCodec.WriteString(w, FromPlayerName ?? "");
        PacketCodec.WriteString(w, Message ?? "");
    }

    public void Deserialize(BinaryReader r)
    {
        FromPlayerId = r.ReadInt32();
        FromPlayerName = PacketCodec.ReadString(r);
        Message = PacketCodec.ReadString(r);
    }
}

/// <summary>
/// Teleport order sent by the host to ONE targeted client (the /bring command).
/// The client moves its local character to (X,Y,Z) with the Yaw orientation.
/// One-way host -> client packet: the host never processes this packet received from a client.
/// </summary>
public struct ServerTeleport : IPacket
{
    public float X, Y, Z;
    public float Yaw;

    public void Serialize(BinaryWriter w)
    {
        w.Write(X); w.Write(Y); w.Write(Z);
        w.Write(Yaw);
    }

    public void Deserialize(BinaryReader r)
    {
        X = r.ReadSingle(); Y = r.ReadSingle(); Z = r.ReadSingle();
        Yaw = r.ReadSingle();
    }
}

/// <summary>
/// Host relay of a rope-up (Clip=true) or unrope (Clip=false) between two players.
/// Each client keeps the set of active links and renders one rope per link.
/// FromPlayerId/TargetPlayerId identify the two endpoints.
/// </summary>
public struct ServerRopeClip : IPacket
{
    public int FromPlayerId;
    public int TargetPlayerId;
    public bool Clip;

    public void Serialize(BinaryWriter w)
    {
        w.Write(FromPlayerId);
        w.Write(TargetPlayerId);
        w.Write(Clip);
    }

    public void Deserialize(BinaryReader r)
    {
        FromPlayerId = r.ReadInt32();
        TargetPlayerId = r.ReadInt32();
        Clip = r.ReadBoolean();
    }
}
