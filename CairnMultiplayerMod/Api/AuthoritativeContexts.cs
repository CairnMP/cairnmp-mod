using System;
using CairnMultiplayerMod.Internal.Extensions;

namespace CairnMultiplayer.Api;

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
