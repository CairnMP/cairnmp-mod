using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Extensions;

namespace CairnMultiplayer.Api;

/// <summary>A host-owned value automatically replayed to late joiners.</summary>
public sealed class ReplicatedState<T> : IReplicatedStateHandle
{
    private readonly Dictionary<int, T> _values = new();
    private readonly Dictionary<int, ulong> _revisions = new();
    private readonly Action<string> _reportFailure;

    internal ReplicatedState(string extensionId, string stateId, PayloadCodec<T> codec, Action<string> reportFailure)
    {
        ExtensionId = extensionId;
        StateId = stateId;
        Codec = codec;
        _reportFailure = reportFailure;
    }

    internal string ExtensionId { get; }
    internal string StateId { get; }
    internal PayloadCodec<T> Codec { get; }

    public event Action<ReplicatedStateChange<T>> Changed;

    public bool TryGet(out T value) => TryGetForPlayer(0, out value);

    public bool TryGetForPlayer(int playerId, out T value)
        => _values.TryGetValue(playerId, out value);

    byte[] IReplicatedStateHandle.SerializeObject(object value) => Codec.Serialize((T)value);

    void IReplicatedStateHandle.Validate(byte[] payload, bool removed)
    {
        if (!removed) Codec.Deserialize(payload ?? Array.Empty<byte>());
    }

    void IReplicatedStateHandle.Apply(int scopePlayerId, ulong revision, bool removed, byte[] payload)
    {
        if (_revisions.TryGetValue(scopePlayerId, out var currentRevision) && currentRevision >= revision)
            return;
        _revisions[scopePlayerId] = revision;

        T value = default;
        if (removed)
            _values.Remove(scopePlayerId);
        else
        {
            value = Codec.Deserialize(payload ?? Array.Empty<byte>());
            _values[scopePlayerId] = value;
        }

        NotifyChanged(new ReplicatedStateChange<T>(scopePlayerId, revision, removed, value));
    }

    void IReplicatedStateHandle.Reset()
    {
        _values.Clear();
        _revisions.Clear();
    }

    private void NotifyChanged(ReplicatedStateChange<T> change)
    {
        if (Changed == null) return;
        foreach (Action<ReplicatedStateChange<T>> subscriber in Changed.GetInvocationList())
        {
            try { subscriber(change); }
            catch (Exception ex) { _reportFailure?.Invoke($"State subscriber failed: {ex.Message}"); }
        }
    }
}
