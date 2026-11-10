using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal enum TopAnchorFact : byte
{
    Place = 1,
    Clean = 2,
    Topped = 3,
    Leave = 4,
    Drop = 5,

    /// <summary>A stick left where the leader was taken by a story scene.</summary>
    Cutscene = 6,
}

/// <summary>One fact on the rope.top-anchor stream. Leave carries the sticks already planted.</summary>
internal readonly struct TopAnchorPacket
{
    internal TopAnchorPacket(TopAnchorFact fact, uint id, Vector3 position, uint[] leaveIds)
        : this(fact, id, position, default, null, leaveIds)
    {
    }

    internal TopAnchorPacket(TopAnchorFact fact, uint id, Vector3 position, Vector3 outward, string key, uint[] leaveIds)
    {
        Fact = fact;
        Id = id;
        Position = position;
        Outward = outward;
        Key = key;
        LeaveIds = leaveIds ?? Array.Empty<uint>();
    }

    internal TopAnchorFact Fact { get; }
    internal uint Id { get; }
    internal Vector3 Position { get; }
    internal Vector3 Outward { get; }
    internal string Key { get; }
    internal uint[] LeaveIds { get; }
}

internal static class TopAnchorMessage
{
    internal static byte[] Place(uint id, Vector3 position)
    {
        var payload = new byte[17];
        payload[0] = (byte)TopAnchorFact.Place;
        WriteUInt(payload, 1, id);
        WriteVector(payload, 5, position);
        return payload;
    }

    internal static byte[] Clean(uint id)
    {
        var payload = new byte[5];
        payload[0] = (byte)TopAnchorFact.Clean;
        WriteUInt(payload, 1, id);
        return payload;
    }

    internal static byte[] Topped(Vector3 position)
    {
        var payload = new byte[13];
        payload[0] = (byte)TopAnchorFact.Topped;
        WriteVector(payload, 1, position);
        return payload;
    }

    internal static byte[] Leave(IReadOnlyList<uint> ids)
    {
        var count = ids == null ? 0 : Math.Min(ids.Count, TopAnchorPolicy.MaxLeaveIds);
        var payload = new byte[2 + count * 4];
        payload[0] = (byte)TopAnchorFact.Leave;
        payload[1] = (byte)count;
        for (var i = 0; i < count; i++) WriteUInt(payload, 2 + i * 4, ids[i]);
        return payload;
    }

    internal static byte[] Drop() => new[] { (byte)TopAnchorFact.Drop };

    /// <summary>Where the stick is, the direction it points out of the rock, and which scene owns it.</summary>
    internal static byte[] Cutscene(uint id, Vector3 position, Vector3 outward, string key)
    {
        if (id == 0 || string.IsNullOrEmpty(key)) return null;
        var text = Encoding.UTF8.GetBytes(key);
        if (text.Length == 0 || text.Length > CutsceneStaffPolicy.MaxKeyBytes) return null;
        var payload = new byte[30 + text.Length];
        payload[0] = (byte)TopAnchorFact.Cutscene;
        WriteUInt(payload, 1, id);
        WriteVector(payload, 5, position);
        WriteVector(payload, 17, outward);
        payload[29] = (byte)text.Length;
        Buffer.BlockCopy(text, 0, payload, 30, text.Length);
        return payload;
    }

    internal static bool TryRead(byte[] payload, out TopAnchorPacket packet)
    {
        packet = default;
        if (payload == null || payload.Length < 1) return false;
        var fact = (TopAnchorFact)payload[0];
        switch (fact)
        {
            case TopAnchorFact.Place:
                if (payload.Length < 17 || !TryReadUInt(payload, 1, out var placed) || placed == 0) return false;
                if (!TryReadVector(payload, 5, out var at)) return false;
                packet = new TopAnchorPacket(fact, placed, at, Array.Empty<uint>());
                return true;
            case TopAnchorFact.Clean:
                if (payload.Length < 5 || !TryReadUInt(payload, 1, out var cleaned) || cleaned == 0) return false;
                packet = new TopAnchorPacket(fact, cleaned, default, Array.Empty<uint>());
                return true;
            case TopAnchorFact.Topped:
                if (payload.Length < 13 || !TryReadVector(payload, 1, out var where)) return false;
                packet = new TopAnchorPacket(fact, 0, where, Array.Empty<uint>());
                return true;
            case TopAnchorFact.Leave:
                if (payload.Length < 2) return false;
                var count = payload[1];
                if (count > TopAnchorPolicy.MaxLeaveIds || payload.Length < 2 + count * 4) return false;
                var ids = new uint[count];
                for (var i = 0; i < count; i++)
                {
                    if (!TryReadUInt(payload, 2 + i * 4, out ids[i]) || ids[i] == 0) return false;
                }

                packet = new TopAnchorPacket(fact, 0, default, ids);
                return true;
            case TopAnchorFact.Drop:
                packet = new TopAnchorPacket(fact, 0, default, Array.Empty<uint>());
                return true;
            case TopAnchorFact.Cutscene:
                if (payload.Length < 30 || !TryReadUInt(payload, 1, out var sceneStick) || sceneStick == 0) return false;
                if (!TryReadVector(payload, 5, out var sceneAt) || !TryReadVector(payload, 17, out var sceneOut)) return false;
                var keyBytes = payload[29];
                if (keyBytes == 0 || payload.Length < 30 + keyBytes) return false;
                var sceneKey = Encoding.UTF8.GetString(payload, 30, keyBytes);
                if (string.IsNullOrEmpty(sceneKey)) return false;
                packet = new TopAnchorPacket(fact, sceneStick, sceneAt, sceneOut, sceneKey, Array.Empty<uint>());
                return true;
            default:
                return false;
        }
    }

    private static void WriteUInt(byte[] payload, int offset, uint value)
    {
        payload[offset] = (byte)value;
        payload[offset + 1] = (byte)(value >> 8);
        payload[offset + 2] = (byte)(value >> 16);
        payload[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteVector(byte[] payload, int offset, Vector3 position)
    {
        WriteFloat(payload, offset, position.X);
        WriteFloat(payload, offset + 4, position.Y);
        WriteFloat(payload, offset + 8, position.Z);
    }

    private static void WriteFloat(byte[] payload, int offset, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        payload[offset] = bytes[0];
        payload[offset + 1] = bytes[1];
        payload[offset + 2] = bytes[2];
        payload[offset + 3] = bytes[3];
    }

    private static bool TryReadUInt(byte[] payload, int offset, out uint value)
    {
        value = (uint)(payload[offset]
                       | (payload[offset + 1] << 8)
                       | (payload[offset + 2] << 16)
                       | (payload[offset + 3] << 24));
        return true;
    }

    private static bool TryReadVector(byte[] payload, int offset, out Vector3 position)
    {
        position = new Vector3(ReadFloat(payload, offset), ReadFloat(payload, offset + 4), ReadFloat(payload, offset + 8));
        return TopAnchorPolicy.Finite(position);
    }

    private static float ReadFloat(byte[] payload, int offset)
        => BitConverter.ToSingle(payload, offset);
}
