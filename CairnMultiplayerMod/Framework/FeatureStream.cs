using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking;

namespace CairnMultiplayerMod.Framework;

/// <summary>
/// "Send this constantly." For data replaced by the next packet within milliseconds —
/// positions, animation frames, finger poses. Unlike the other channels it does NOT go
/// through the host transaction machinery: at 30-60 Hz an acknowledgement per packet
/// would cost more than the payload, and a lost packet does not matter because a fresher
/// one is already on its way.
///
/// The trade-off is explicit: no ordering guarantee, no delivery guarantee, no replay for
/// latecomers. If any of those matter, use <see cref="HostState{T}"/> instead.
/// </summary>
internal sealed class Stream<T> where T : IPacket, new()
{
    private readonly NetworkManager _network;
    private readonly ushort _channel;
    private readonly bool _reliable;

    internal Stream(NetworkManager network, ushort channel, bool reliable)
    {
        _network = network;
        _channel = channel;
        _reliable = reliable;
    }

    /// <summary>Sends to the other players. Cheap enough to call every frame.</summary>
    public void Send(T message)
    {
        if (_network == null || !_network.IsHandshakeComplete) return;

        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer))
            {
                message.Serialize(writer);
                writer.Flush();
            }
            _network.SendFeatureStream(_channel, buffer.ToArray(), _reliable);
        }
        catch (Exception ex)
        {
            FeatureLog.Warn($"[Feature] stream send failed on channel {_channel}: {ex.Message}");
        }
    }
}

/// <summary>
/// Routes incoming stream payloads to the feature that declared them.
///
/// Channels are identified by a 16-bit hash of "feature.stream" rather than by their name,
/// so the wire cost stays two bytes on packets sent every frame. Collisions would silently
/// cross two features' data, so they are checked when the channel is declared and refused
/// loudly at startup — never left to be discovered in game.
/// </summary>
internal sealed class FeatureStreamRouter
{
    private readonly Dictionary<ushort, Action<int, byte[]>> _handlers = new();
    private readonly Dictionary<ushort, string> _namesByChannel = new();

    /// <summary>Registers a channel and returns its id. Throws if the name collides.</summary>
    internal ushort Register(string channelName, Action<int, byte[]> handler)
    {
        var channel = Hash(channelName);
        if (_namesByChannel.TryGetValue(channel, out var existing))
        {
            if (existing == channelName)
                throw new InvalidOperationException($"Stream '{channelName}' is declared twice.");

            throw new InvalidOperationException(
                $"Stream '{channelName}' and '{existing}' hash to the same channel ({channel}). " +
                "Rename one of them — their payloads would otherwise be delivered to each other.");
        }

        _namesByChannel[channel] = channelName;
        _handlers[channel] = handler;
        return channel;
    }

    /// <summary>Delivers a payload. Unknown channels are ignored: a peer running a newer
    /// build may stream something this one has never heard of.</summary>
    internal void Dispatch(int fromPlayerId, ushort channel, byte[] payload)
    {
        if (!_handlers.TryGetValue(channel, out var handler)) return;

        try
        {
            handler(fromPlayerId, payload);
        }
        catch (Exception ex)
        {
            FeatureLog.Error($"[Feature] stream handler failed on '{_namesByChannel[channel]}': {ex}");
        }
    }

    /// <summary>FNV-1a, folded to 16 bits. Stable across runs and machines, which matters:
    /// both peers must compute the same channel id from the same name.</summary>
    internal static ushort Hash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }

        var folded = (ushort)((hash >> 16) ^ (hash & 0xFFFF));
        // 0 is reserved so an uninitialised channel field never looks like a real one.
        return folded == 0 ? (ushort)1 : folded;
    }
}
