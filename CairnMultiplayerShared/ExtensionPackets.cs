using System;
using System.IO;

namespace CairnMultiplayer.Shared;

/// <summary>One third-party extension advertised during the managed API handshake.</summary>
public struct ExtensionManifestEntry
{
    public string Id;
    public string Version;
    public string MinimumPeerVersion;
    public string MaximumPeerVersion;
    public bool Required;

    public void Serialize(BinaryWriter writer)
    {
        PacketCodec.WriteString(writer, Id ?? "");
        PacketCodec.WriteString(writer, Version ?? "");
        PacketCodec.WriteString(writer, MinimumPeerVersion ?? "");
        PacketCodec.WriteString(writer, MaximumPeerVersion ?? "");
        writer.Write(Required);
    }

    public void Deserialize(BinaryReader reader)
    {
        Id = PacketCodec.ReadString(reader);
        Version = PacketCodec.ReadString(reader);
        MinimumPeerVersion = PacketCodec.ReadString(reader);
        MaximumPeerVersion = PacketCodec.ReadString(reader);
        Required = reader.ReadBoolean();
    }
}

/// <summary>Client extension inventory, sent to the authoritative host after lobby entry.</summary>
public struct ClientExtensionManifest : IPacket
{
    public const int MaxEntries = 128;
    public ExtensionManifestEntry[] Entries;

    public void Serialize(BinaryWriter writer)
    {
        int count = Entries?.Length ?? 0;
        EnsureValidEntryCount(count, "Too many extensions in manifest");

        writer.Write((ushort)count);
        for (int i = 0; i < count; i++)
            Entries[i].Serialize(writer);
    }

    public void Deserialize(BinaryReader reader)
    {
        int count = reader.ReadUInt16();
        EnsureValidEntryCount(count, "Too many extensions in manifest");

        Entries = new ExtensionManifestEntry[count];
        for (int i = 0; i < count; i++)
            Entries[i].Deserialize(reader);
    }

    internal static void EnsureValidEntryCount(int count, string errorPrefix)
    {
        if (count > MaxEntries)
            throw new InvalidDataException($"{errorPrefix}: {count}");
    }
}

/// <summary>A typed extension command sent by a client to the authoritative host.</summary>
public struct ClientExtensionCommand : IPacket
{
    public uint RequestId;
    public string ExtensionId;
    public string CommandId;
    public byte[] Payload;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(RequestId);
        PacketCodec.WriteString(writer, ExtensionId ?? "");
        PacketCodec.WriteString(writer, CommandId ?? "");
        PacketCodec.WriteBytes(writer, Payload);
    }

    public void Deserialize(BinaryReader reader)
    {
        RequestId = reader.ReadUInt32();
        ExtensionId = PacketCodec.ReadString(reader);
        CommandId = PacketCodec.ReadString(reader);
        Payload = PacketCodec.ReadBytes(reader);
    }
}

/// <summary>Host decision after comparing its extension inventory with a client.</summary>
public struct ServerExtensionManifestResult : IPacket
{
    public bool Accepted;
    public string Reason;
    public string[] EnabledExtensionIds;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Accepted);
        PacketCodec.WriteString(writer, Reason ?? "");
        ExtensionPacketSerialization.WriteExtensionIds(writer, EnabledExtensionIds);
    }

    public void Deserialize(BinaryReader reader)
    {
        Accepted = reader.ReadBoolean();
        Reason = PacketCodec.ReadString(reader);
        EnabledExtensionIds = ExtensionPacketSerialization.ReadExtensionIds(reader);
    }
}

/// <summary>Completion of a client command after the host commits or aborts it.</summary>
public struct ServerExtensionCommandResult : IPacket
{
    public uint RequestId;
    public ExtensionCommandStatus Status;
    public string Reason;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(RequestId);
        writer.Write((byte)Status);
        PacketCodec.WriteString(writer, Reason ?? "");
    }

    public void Deserialize(BinaryReader reader)
    {
        RequestId = reader.ReadUInt32();
        Status = (ExtensionCommandStatus)reader.ReadByte();
        Reason = PacketCodec.ReadString(reader);
    }
}

/// <summary>A transient extension event emitted by the authoritative host.</summary>
public struct ServerExtensionEvent : IPacket
{
    public int SourcePlayerId;
    public string ExtensionId;
    public string EventId;
    public byte[] Payload;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(SourcePlayerId);
        PacketCodec.WriteString(writer, ExtensionId ?? "");
        PacketCodec.WriteString(writer, EventId ?? "");
        PacketCodec.WriteBytes(writer, Payload);
    }

    public void Deserialize(BinaryReader reader)
    {
        SourcePlayerId = reader.ReadInt32();
        ExtensionId = PacketCodec.ReadString(reader);
        EventId = PacketCodec.ReadString(reader);
        Payload = PacketCodec.ReadBytes(reader);
    }
}

/// <summary>Latest host-owned value of one replicated extension state key.</summary>
public struct ServerExtensionState : IPacket
{
    public string ExtensionId;
    public string StateId;
    public int ScopePlayerId;
    public ulong Revision;
    public bool Removed;
    public byte[] Payload;

    public void Serialize(BinaryWriter writer)
    {
        PacketCodec.WriteString(writer, ExtensionId ?? "");
        PacketCodec.WriteString(writer, StateId ?? "");
        writer.Write(ScopePlayerId);
        writer.Write(Revision);
        writer.Write(Removed);
        PacketCodec.WriteBytes(writer, Payload);
    }

    public void Deserialize(BinaryReader reader)
    {
        ExtensionId = PacketCodec.ReadString(reader);
        StateId = PacketCodec.ReadString(reader);
        ScopePlayerId = reader.ReadInt32();
        Revision = reader.ReadUInt64();
        Removed = reader.ReadBoolean();
        Payload = PacketCodec.ReadBytes(reader);
    }
}

/// <summary>Host-authoritative extension capability update for one lobby member.</summary>
public struct ServerExtensionPeerStatus : IPacket
{
    public int PlayerId;
    public bool Joined;
    public string[] EnabledExtensionIds;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(PlayerId);
        writer.Write(Joined);
        ExtensionPacketSerialization.WriteExtensionIds(writer, EnabledExtensionIds);
    }

    public void Deserialize(BinaryReader reader)
    {
        PlayerId = reader.ReadInt32();
        Joined = reader.ReadBoolean();
        EnabledExtensionIds = ExtensionPacketSerialization.ReadExtensionIds(reader);
    }
}

internal static class ExtensionPacketSerialization
{
    internal static void WriteExtensionIds(BinaryWriter writer, string[] extensionIds)
    {
        int count = extensionIds?.Length ?? 0;
        ClientExtensionManifest.EnsureValidEntryCount(count, "Too many enabled extensions");
        writer.Write((ushort)count);
        for (int i = 0; i < count; i++)
            PacketCodec.WriteString(writer, extensionIds[i] ?? "");
    }

    internal static string[] ReadExtensionIds(BinaryReader reader)
    {
        int count = reader.ReadUInt16();
        ClientExtensionManifest.EnsureValidEntryCount(count, "Too many enabled extensions");
        var extensionIds = new string[count];
        for (int i = 0; i < count; i++)
            extensionIds[i] = PacketCodec.ReadString(reader);
        return extensionIds;
    }
}
