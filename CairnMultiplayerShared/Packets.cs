using System;
using System.IO;
using System.Text;

namespace CairnMultiplayer.Shared;

/// <summary>
/// Interface commune à tous les types de paquets du protocole CairnMP.
/// La sérialisation utilise BinaryWriter/BinaryReader (stdlib .NET, sans dépendance externe).
/// </summary>
public interface IPacket
{
    void Serialize(BinaryWriter writer);
    void Deserialize(BinaryReader reader);
}

/// <summary>
/// Helpers d'encodage pour le protocole binaire CairnMP.
///
/// Encodage identique à LiteNetLib.Utils.NetDataWriter/Reader :
///   int/uint/float : little-endian (4 octets)
///   bool           : 1 octet (0 ou 1)
///   string         : [uint16 LE : nombre d'octets UTF-8] [octets UTF-8]
///
/// ATTENTION : BinaryWriter.Write(string) utilise un length-prefix 7-bit
/// et n'est pas compatible. Toujours utiliser WriteString/ReadString ici.
/// </summary>
public static class PacketCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private const int MaxFrameVectorCount = 512;
    private const int MaxUInt16Length = ushort.MaxValue;

    /// <summary>Ecrit une string comme [uint16 LE : nbOctets][octets UTF-8].</summary>
    public static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Utf8.GetBytes(s ?? "");
        if (bytes.Length > MaxUInt16Length)
            throw new InvalidDataException($"String payload too large: {bytes.Length} bytes");

        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>Lit une string encodée [uint16 LE : nbOctets][octets UTF-8].</summary>
    public static string ReadString(BinaryReader r)
    {
        int len = r.ReadUInt16();
        var bytes = r.ReadBytes(len);
        return Utf8.GetString(bytes);
    }

    /// <summary>
    /// Construit un frame TCP complet :
    ///   [uint16 LE : longueur payload] [byte : PacketId] [champs du paquet]
    /// </summary>
    public static byte[] Frame(PacketId id, IPacket packet)
    {
        // Encoder le payload (id + champs)
        using var payload = new MemoryStream();
        using (var pw = new BinaryWriter(payload, Utf8, leaveOpen: true))
        {
            pw.Write((byte)id);
            packet.Serialize(pw);
        }
        var payloadBytes = payload.ToArray();
        if (payloadBytes.Length > MaxUInt16Length)
            throw new InvalidDataException($"Packet payload too large: {payloadBytes.Length} bytes");

        // Construire la frame finale avec préfixe de longueur
        using var frame = new MemoryStream(payloadBytes.Length + 2);
        using var fw = new BinaryWriter(frame, Utf8, leaveOpen: true);
        fw.Write((ushort)payloadBytes.Length);
        fw.Write(payloadBytes);
        return frame.ToArray();
    }

    /// <summary>Ecrit un tableau de Vector3 aplati [x0,y0,z0, ...].</summary>
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

    /// <summary>Lit un tableau de Vector3 aplati [x0,y0,z0, ...].</summary>
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
/// Donnees minimales du NetFrame natif de Cairn, serialisables sans reference Unity.
/// Les positions/eulers sont des tableaux de Vector3 aplatis.
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
/// Instantane meteo autoritaire capture par l'hote. Les valeurs d'enums viennent
/// des bindings IL2CPP de Cairn mais restent stockees en int pour garder le paquet
/// partage sans reference au jeu.
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

// ---- Client -> Serveur ------------------------------------------------------

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
    public float[] Positions;  // aplati [x0,y0,z0, ...] espace monde
    public float[] Rotations;  // aplati [x0,y0,z0,w0, ...] quaternion espace monde

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
    public int ItemId; // InventoryItemStringId (juste un int)

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
/// Etat de la lampe (AavaLightStick.CurrentMode) du joueur local. Mode est un
/// enum cote jeu transporte ici en int. Envoye seulement sur changement.
/// </summary>
public struct ClientLampState : IPacket
{
    public int Mode;

    public void Serialize(BinaryWriter w) => w.Write(Mode);
    public void Deserialize(BinaryReader r) => Mode = r.ReadInt32();
}

/// <summary>
/// Position monde d'un marqueur de ping pose par le joueur local en freecam.
/// La duree de vie est une constante client (Protocol.PingLifetimeSeconds) et la
/// couleur est derivee de l'id du joueur, donc rien d'autre n'est transmis.
/// </summary>
public struct ClientPingPlaced : IPacket
{
    public float PosX, PosY, PosZ;

    public void Serialize(BinaryWriter w)
    {
        w.Write(PosX); w.Write(PosY); w.Write(PosZ);
    }

    public void Deserialize(BinaryReader r)
    {
        PosX = r.ReadSingle(); PosY = r.ReadSingle(); PosZ = r.ReadSingle();
    }
}

/// <summary>
/// Pose des doigts du joueur local : 30 os compresses smallest-three
/// (Protocol.HandPosePackedSize octets). Envoye seulement sur changement.
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
/// Etat de sommeil du joueur local (BivouacManager.IsAsleep). Envoye sur
/// changement ; sert a l'hote pour decider si tout le monde dort.
/// </summary>
public struct ClientSleepState : IPacket
{
    public bool Asleep;

    public void Serialize(BinaryWriter w) => w.Write(Asleep);
    public void Deserialize(BinaryReader r) => Asleep = r.ReadBoolean();
}

/// <summary>
/// Le client demande a s'encorder (Clip=true) ou se decorder (Clip=false) avec un
/// autre joueur. L'hote relaie en ServerRopeClip a tous.
/// </summary>
public struct ClientRopeClip : IPacket
{
    public int TargetPlayerId;
    public bool Clip;

    public void Serialize(BinaryWriter w) { w.Write(TargetPlayerId); w.Write(Clip); }
    public void Deserialize(BinaryReader r) { TargetPlayerId = r.ReadInt32(); Clip = r.ReadBoolean(); }
}

// ---- Serveur -> Client ------------------------------------------------------

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
/// Le serveur dit au client de lancer immediatement une nouvelle partie story avec
/// la difficulte et les flags de gameplay donnes. Envoye juste apres le handshake ACK,
/// et a nouveau chaque fois que le serveur decide de relancer la session.
/// </summary>
public struct ServerStartGame : IPacket
{
    public int Difficulty;       // cast depuis GameDifficulty
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

/// <summary>Relais par l'hote du Mode de lampe d'un joueur a tous les autres.</summary>
public struct ServerLampState : IPacket
{
    public int PlayerId;
    public int Mode;

    public void Serialize(BinaryWriter w) { w.Write(PlayerId); w.Write(Mode); }
    public void Deserialize(BinaryReader r) { PlayerId = r.ReadInt32(); Mode = r.ReadInt32(); }
}

/// <summary>
/// Etat cosmetique du joueur local (champ de bits Flags, cf. Protocol.CosmeticFlag*).
/// Pour l'instant : bit 0 = gants lumineux actifs. Envoye seulement sur changement.
/// </summary>
public struct ClientCosmeticState : IPacket
{
    public byte Flags;

    public void Serialize(BinaryWriter w) => w.Write(Flags);
    public void Deserialize(BinaryReader r) => Flags = r.ReadByte();
}

/// <summary>Relais par l'hote de l'etat cosmetique d'un joueur a tous les autres.</summary>
public struct ServerCosmeticState : IPacket
{
    public int PlayerId;
    public byte Flags;

    public void Serialize(BinaryWriter w) { w.Write(PlayerId); w.Write(Flags); }
    public void Deserialize(BinaryReader r) { PlayerId = r.ReadInt32(); Flags = r.ReadByte(); }
}

/// <summary>
/// Heure du jour autoritaire diffusee par l'hote. DayTime01 est la valeur
/// normalisee 0-1 de NightDayCycle.dayTime01 ; AllAsleep indique si tous les
/// joueurs dorment (le fast-forward est autorise). Les clients calent leur
/// horloge visuelle dessus.
/// </summary>
public struct ServerTimeState : IPacket
{
    public float DayTime01;
    public bool AllAsleep;

    public void Serialize(BinaryWriter w) { w.Write(DayTime01); w.Write(AllAsleep); }
    public void Deserialize(BinaryReader r) { DayTime01 = r.ReadSingle(); AllAsleep = r.ReadBoolean(); }
}

/// <summary>Relais par l'hote de la pose de doigts d'un joueur a tous les autres.</summary>
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

/// <summary>
/// Relais par l'hote d'un marqueur de ping aux autres joueurs. FromPlayerId sert
/// a colorer le marqueur (meme palette que les fantomes) cote recepteur.
/// </summary>
public struct ServerPingPlaced : IPacket
{
    public int FromPlayerId;
    public float PosX, PosY, PosZ;

    public void Serialize(BinaryWriter w)
    {
        w.Write(FromPlayerId);
        w.Write(PosX); w.Write(PosY); w.Write(PosZ);
    }

    public void Deserialize(BinaryReader r)
    {
        FromPlayerId = r.ReadInt32();
        PosX = r.ReadSingle(); PosY = r.ReadSingle(); PosZ = r.ReadSingle();
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
/// Ordre de teleportation envoye par l'hote a UN client cible (commande /bring).
/// Le client deplace son personnage local vers (X,Y,Z) avec l'orientation Yaw.
/// Paquet unidirectionnel hote -> client : l'hote ne traite jamais ce paquet recu d'un client.
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
/// Relais par l'hote d'un encordement (Clip=true) ou decordage (Clip=false) entre
/// deux joueurs. Chaque client maintient l'ensemble des liens actifs et rend une
/// corde par lien. FromPlayerId/TargetPlayerId identifient les deux extremites.
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
