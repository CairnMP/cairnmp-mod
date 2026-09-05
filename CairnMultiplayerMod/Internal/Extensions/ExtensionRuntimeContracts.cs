using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Extensions;

internal sealed class ExtensionDefinition
{
    internal ExtensionDefinition(ExtensionManifestEntry manifest) => Manifest = manifest;
    internal ExtensionManifestEntry Manifest { get; }
}

internal interface ICommandHandler
{
    void Invoke(MultiplayerPlayer sender, byte[] payload, ExtensionTransaction transaction);
}

internal sealed class CommandHandler<T> : ICommandHandler
{
    private readonly MultiplayerCommand<T> _command;
    private readonly Action<HostCommandContext<T>> _handler;

    internal CommandHandler(MultiplayerCommand<T> command, Action<HostCommandContext<T>> handler)
    {
        _command = command;
        _handler = handler;
    }

    public void Invoke(MultiplayerPlayer sender, byte[] payload, ExtensionTransaction transaction)
    {
        var request = _command.Codec.Deserialize(payload);
        _handler(new HostCommandContext<T>(sender, request, transaction));
    }
}

internal interface IReplicatedStateHandle
{
    byte[] SerializeObject(object value);
    void Validate(byte[] payload, bool removed);
    void Apply(int scopePlayerId, ulong revision, bool removed, byte[] payload);
    void Reset();
}

internal interface IMultiplayerEventHandle
{
    byte[] SerializeObject(object value);
    void Validate(byte[] payload);
    void Apply(int sourcePlayerId, byte[] payload);
}

internal sealed class ExtensionTransaction
{
    private static ulong _nextRevision = 1;

    internal ExtensionTransaction(string extensionId, int sourcePlayerId)
    {
        ExtensionId = extensionId;
        SourcePlayerId = sourcePlayerId;
    }

    internal string ExtensionId { get; }
    internal int SourcePlayerId { get; }
    internal bool IsRejected { get; private set; }
    internal string RejectionReason { get; private set; } = "";
    internal List<StagedState> States { get; } = new();
    internal List<StagedEvent> Events { get; } = new();
    internal List<Action> AfterCommitEffects { get; } = new();

    internal void StageState<T>(ReplicatedState<T> state, int scopePlayerId, bool removed, T value)
    {
        EnsureOwned(state?.ExtensionId);
        byte[] payload = removed ? Array.Empty<byte>() : state.Codec.Serialize(value) ?? Array.Empty<byte>();
        EnsureSize(payload);
        States.Add(new StagedState(state.StateId, state, scopePlayerId, _nextRevision++, removed, payload));
    }

    internal void StageEvent<T>(MultiplayerEvent<T> multiplayerEvent, T value)
    {
        EnsureOwned(multiplayerEvent?.ExtensionId);
        byte[] payload = multiplayerEvent.Codec.Serialize(value) ?? Array.Empty<byte>();
        EnsureSize(payload);
        Events.Add(new StagedEvent(multiplayerEvent.EventId, multiplayerEvent, payload));
    }

    internal void Reject(string reason)
    {
        IsRejected = true;
        RejectionReason = string.IsNullOrWhiteSpace(reason) ? "Rejected by extension." : reason;
        States.Clear();
        Events.Clear();
        AfterCommitEffects.Clear();
    }

    internal void StageAfterCommit(Action effect)
    {
        if (effect == null) throw new ArgumentNullException(nameof(effect));
        AfterCommitEffects.Add(effect);
    }

    private void EnsureOwned(string ownerId)
    {
        if (!string.Equals(ownerId, ExtensionId, StringComparison.Ordinal))
            throw new InvalidOperationException("A command may only mutate contracts owned by its extension.");
    }

    private static void EnsureSize(byte[] payload)
    {
        if (payload.Length > ExtensionRuntime.MaxManagedPayloadBytes)
            throw new InvalidOperationException($"Managed effect exceeds {ExtensionRuntime.MaxManagedPayloadBytes} bytes.");
    }
}

internal readonly struct StagedState
{
    internal StagedState(string stateId, IReplicatedStateHandle handle, int scopePlayerId,
        ulong revision, bool removed, byte[] payload)
    {
        StateId = stateId;
        Handle = handle;
        ScopePlayerId = scopePlayerId;
        Revision = revision;
        Removed = removed;
        Payload = payload;
    }
    internal string StateId { get; }
    internal IReplicatedStateHandle Handle { get; }
    internal int ScopePlayerId { get; }
    internal ulong Revision { get; }
    internal bool Removed { get; }
    internal byte[] Payload { get; }
}

internal readonly struct StagedEvent
{
    internal StagedEvent(string eventId, IMultiplayerEventHandle handle, byte[] payload)
    {
        EventId = eventId;
        Handle = handle;
        Payload = payload;
    }
    internal string EventId { get; }
    internal IMultiplayerEventHandle Handle { get; }
    internal byte[] Payload { get; }
}

internal readonly struct PendingCommand
{
    internal PendingCommand(TaskCompletionSource<CommandResult> completion, DateTime deadline)
    {
        Completion = completion;
        Deadline = deadline;
    }
    internal TaskCompletionSource<CommandResult> Completion { get; }
    internal DateTime Deadline { get; }
}

internal readonly struct RateWindow
{
    internal RateWindow(DateTime start, int count)
    {
        Start = start;
        Count = count;
    }
    internal DateTime Start { get; }
    internal int Count { get; }
}
