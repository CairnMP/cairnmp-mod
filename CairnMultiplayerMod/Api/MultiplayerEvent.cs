using System;
using CairnMultiplayerMod.Api.Internal;

namespace CairnMultiplayer.Api;

/// <summary>A transient host-committed event.</summary>
public sealed class MultiplayerEvent<T> : IMultiplayerEventHandle
{
    private readonly Action<string> _reportFailure;

    internal MultiplayerEvent(string extensionId, string eventId, PayloadCodec<T> codec, Action<string> reportFailure)
    {
        ExtensionId = extensionId;
        EventId = eventId;
        Codec = codec;
        _reportFailure = reportFailure;
    }

    internal string ExtensionId { get; }
    internal string EventId { get; }
    internal PayloadCodec<T> Codec { get; }

    public event Action<MultiplayerEventMessage<T>> Received;

    byte[] IMultiplayerEventHandle.SerializeObject(object value) => Codec.Serialize((T)value);

    void IMultiplayerEventHandle.Validate(byte[] payload)
        => Codec.Deserialize(payload ?? Array.Empty<byte>());

    void IMultiplayerEventHandle.Apply(int sourcePlayerId, byte[] payload)
    {
        var message = new MultiplayerEventMessage<T>(sourcePlayerId,
            Codec.Deserialize(payload ?? Array.Empty<byte>()));
        if (Received == null) return;
        foreach (Action<MultiplayerEventMessage<T>> subscriber in Received.GetInvocationList())
        {
            try { subscriber(message); }
            catch (Exception ex) { _reportFailure?.Invoke($"Event subscriber failed: {ex.Message}"); }
        }
    }
}
