using System;
using System.IO;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Framework;

/// <summary>
/// Turns any <see cref="IPacket"/> into the payload codec the extension runtime expects.
/// Features serialize the same way the rest of the protocol does — the JSON codec the public
/// API defaults to is fine for third-party mods, but too costly for traffic we send often.
/// </summary>
internal static class FeatureCodec
{
    internal static PayloadCodec<T> For<T>() where T : IPacket, new()
        => new(Serialize, Deserialize<T>);

    private static byte[] Serialize<T>(T message) where T : IPacket
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        message.Serialize(writer);
        writer.Flush();
        return buffer.ToArray();
    }

    private static T Deserialize<T>(byte[] payload) where T : IPacket, new()
    {
        var message = new T();
        using var buffer = new MemoryStream(payload ?? Array.Empty<byte>(), writable: false);
        using var reader = new BinaryReader(buffer);
        message.Deserialize(reader);
        return message;
    }
}
