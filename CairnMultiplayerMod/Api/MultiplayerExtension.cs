using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CairnMultiplayerMod.Api.Internal;

namespace CairnMultiplayer.Api;

/// <summary>Registered third-party integration and factory for its managed contracts.</summary>
public sealed class MultiplayerExtension
{
    internal MultiplayerExtension(ExtensionRuntime runtime, ExtensionDefinition definition)
    {
        Runtime = runtime;
        Definition = definition;
    }

    internal ExtensionRuntime Runtime { get; }
    internal ExtensionDefinition Definition { get; }

    public string Id => Definition.Manifest.Id;
    public Version Version => System.Version.Parse(Definition.Manifest.Version);

    public MultiplayerCommand<TRequest> RegisterCommand<TRequest>(
        string commandId,
        Action<HostCommandContext<TRequest>> handler,
        PayloadCodec<TRequest> codec = null)
        => Runtime.RegisterCommand(this, commandId, handler, codec ?? PayloadCodec<TRequest>.Json);

    public ReplicatedState<T> RegisterState<T>(string stateId, PayloadCodec<T> codec = null)
        => Runtime.RegisterState(this, stateId, codec ?? PayloadCodec<T>.Json);

    public MultiplayerEvent<T> RegisterEvent<T>(string eventId, PayloadCodec<T> codec = null)
        => Runtime.RegisterEvent(this, eventId, codec ?? PayloadCodec<T>.Json);

    public bool IsEnabledForPlayer(int playerId) => Runtime.IsExtensionEnabled(Id, playerId);

    /// <summary>Runs a host-originated atomic update without manufacturing a client command.</summary>
    public CommandResult Commit(Action<AuthoritativeContext> buildEffects)
        => Runtime.CommitHostEffects(this, buildEffects);
}

/// <summary>A typed client-to-host command.</summary>
public sealed class MultiplayerCommand<TRequest>
{
    internal MultiplayerCommand(ExtensionRuntime runtime, string extensionId, string commandId, PayloadCodec<TRequest> codec)
    {
        Runtime = runtime;
        ExtensionId = extensionId;
        CommandId = commandId;
        Codec = codec;
    }

    internal ExtensionRuntime Runtime { get; }
    internal string ExtensionId { get; }
    internal string CommandId { get; }
    internal PayloadCodec<TRequest> Codec { get; }

    public Task<CommandResult> SendAsync(TRequest request, CancellationToken cancellationToken = default)
        => Runtime.SendCommandAsync(this, request, cancellationToken);
}

/// <summary>A host-owned value automatically replayed to late joiners.</summary>
public sealed class ReplicatedState<T> : IReplicatedStateHandle
{
    private readonly Dictionary<int, StateValue> _values = new();
    private readonly Dictionary<int, ulong> _revisions = new();

    internal ReplicatedState(string extensionId, string stateId, PayloadCodec<T> codec, Action<string> reportFailure)
    {
        ExtensionId = extensionId;
        StateId = stateId;
        Codec = codec;
        ReportFailure = reportFailure;
    }

    internal string ExtensionId { get; }
    internal string StateId { get; }
    internal PayloadCodec<T> Codec { get; }
    private Action<string> ReportFailure { get; }

    public event Action<ReplicatedStateChange<T>> Changed;

    public bool TryGet(out T value) => TryGetForPlayer(0, out value);

    public bool TryGetForPlayer(int playerId, out T value)
    {
        if (_values.TryGetValue(playerId, out var stored))
        {
            value = stored.Value;
            return true;
        }
        value = default;
        return false;
    }

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
        {
            _values.Remove(scopePlayerId);
        }
        else
        {
            value = Codec.Deserialize(payload ?? Array.Empty<byte>());
            _values[scopePlayerId] = new StateValue(revision, value);
        }

        var change = new ReplicatedStateChange<T>(scopePlayerId, revision, removed, value);
        if (Changed == null) return;
        foreach (Action<ReplicatedStateChange<T>> subscriber in Changed.GetInvocationList())
        {
            try { subscriber(change); }
            catch (Exception ex) { ReportFailure?.Invoke($"State subscriber failed: {ex.Message}"); }
        }
    }

    void IReplicatedStateHandle.Reset()
    {
        _values.Clear();
        _revisions.Clear();
    }

    private readonly struct StateValue
    {
        public StateValue(ulong revision, T value)
        {
            Revision = revision;
            Value = value;
        }
        public ulong Revision { get; }
        public T Value { get; }
    }
}

/// <summary>A transient host-committed event.</summary>
public sealed class MultiplayerEvent<T> : IMultiplayerEventHandle
{
    internal MultiplayerEvent(string extensionId, string eventId, PayloadCodec<T> codec, Action<string> reportFailure)
    {
        ExtensionId = extensionId;
        EventId = eventId;
        Codec = codec;
        ReportFailure = reportFailure;
    }

    internal string ExtensionId { get; }
    internal string EventId { get; }
    internal PayloadCodec<T> Codec { get; }
    private Action<string> ReportFailure { get; }

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
            catch (Exception ex) { ReportFailure?.Invoke($"Event subscriber failed: {ex.Message}"); }
        }
    }
}

/// <summary>
/// Isolated command context. State changes and events are staged until the handler returns;
/// a rejection or exception aborts every managed effect.
/// </summary>
public sealed class HostCommandContext<TRequest>
{
    private readonly ExtensionTransaction _transaction;

    internal HostCommandContext(MultiplayerPlayer sender, TRequest request, ExtensionTransaction transaction)
    {
        Sender = sender;
        Request = request;
        _transaction = transaction;
    }

    public MultiplayerPlayer Sender { get; }
    public TRequest Request { get; }

    public void Set<T>(ReplicatedState<T> state, T value)
        => SetForPlayer(state, 0, value);

    public void SetForPlayer<T>(ReplicatedState<T> state, int playerId, T value)
        => _transaction.StageState(state, playerId, removed: false, value);

    public void Remove<T>(ReplicatedState<T> state)
        => RemoveForPlayer(state, 0);

    public void RemoveForPlayer<T>(ReplicatedState<T> state, int playerId)
        => _transaction.StageState(state, playerId, removed: true, default(T));

    public void Broadcast<T>(MultiplayerEvent<T> multiplayerEvent, T payload)
        => _transaction.StageEvent(multiplayerEvent, payload);

    public void Reject(string reason) => _transaction.Reject(reason);

    /// <summary>
    /// Schedules a non-transactional game-side effect after managed state has committed.
    /// CairnMP isolates failures, but cannot roll back arbitrary Unity/CairnAPI mutations.
    /// </summary>
    public void AfterCommit(Action effect) => _transaction.StageAfterCommit(effect);
}

/// <summary>Transaction builder for state changes initiated directly by the host.</summary>
public sealed class AuthoritativeContext
{
    private readonly ExtensionTransaction _transaction;

    internal AuthoritativeContext(ExtensionTransaction transaction) => _transaction = transaction;

    public void Set<T>(ReplicatedState<T> state, T value) => SetForPlayer(state, 0, value);
    public void SetForPlayer<T>(ReplicatedState<T> state, int playerId, T value)
        => _transaction.StageState(state, playerId, removed: false, value);
    public void Remove<T>(ReplicatedState<T> state) => RemoveForPlayer(state, 0);
    public void RemoveForPlayer<T>(ReplicatedState<T> state, int playerId)
        => _transaction.StageState(state, playerId, removed: true, default(T));
    public void Broadcast<T>(MultiplayerEvent<T> multiplayerEvent, T payload)
        => _transaction.StageEvent(multiplayerEvent, payload);
    public void Reject(string reason) => _transaction.Reject(reason);
    public void AfterCommit(Action effect) => _transaction.StageAfterCommit(effect);
}
