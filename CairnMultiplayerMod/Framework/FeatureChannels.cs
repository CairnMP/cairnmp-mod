using System;
using System.Threading;
using System.Threading.Tasks;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Api.Internal;

namespace CairnMultiplayerMod.Framework;

internal static class FeatureContinuation
{
    /// <summary>Runs the handler inline. The runtime completes on Unity's main thread, and a
    /// feature callback may touch the game.</summary>
    internal static void OnCompletion<TResult>(Task<TResult> task, Action<Task<TResult>> handler)
        => task.ContinueWith(handler, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

/// <summary>
/// "Everyone sees this." Send from any player; the host relays to every other peer, so the
/// host stays the single source of truth even though clients can originate the message.
/// The sender is NOT called back — show the local effect yourself, immediately, before
/// sending. That is what makes a ping feel instant on the player's own screen.
/// </summary>
internal sealed class Broadcast<T> where T : IPacket, new()
{
    private readonly ExtensionRuntime _runtime;
    private readonly MultiplayerCommand<T> _request;
    private readonly string _label;

    internal Broadcast(ExtensionRuntime runtime, MultiplayerCommand<T> request, string label)
    {
        _runtime = runtime;
        _request = request;
        _label = label;
    }

    /// <summary>
    /// Sends to everyone else. Fire-and-forget: a lost message is logged, never thrown — a
    /// feature must not have to guard every send with a try/catch.
    /// </summary>
    public void Send(T message)
    {
        if (!_runtime.IsConnected) return;

        FeatureContinuation.OnCompletion(_request.SendAsync(message), task =>
        {
            if (task.IsFaulted)
                FeatureLog.Warn($"[Feature:{_label}] send failed: {task.Exception?.GetBaseException().Message}");
            else if (task.IsCompletedSuccessfully && !task.Result.Committed)
                FeatureLog.Warn($"[Feature:{_label}] rejected by host: {task.Result.Reason}");
        });
    }
}

/// <summary>
/// "The host decides, latecomers catch up." Only the host may <see cref="Set"/>; every peer
/// is notified, and a player joining mid-session receives the current value automatically.
/// This is what a client-sent broadcast cannot give you: state that survives the moment it
/// was sent.
/// </summary>
internal sealed class HostState<T> where T : IPacket, new()
{
    private readonly ExtensionRuntime _runtime;
    private readonly MultiplayerExtension _extension;
    private readonly ReplicatedState<T> _state;
    private readonly string _label;

    internal HostState(ExtensionRuntime runtime, MultiplayerExtension extension,
        ReplicatedState<T> state, string label)
    {
        _runtime = runtime;
        _extension = extension;
        _state = state;
        _label = label;
    }

    /// <summary>The value currently known on this peer, if one has been published yet.</summary>
    public bool TryGet(out T value) => _state.TryGet(out value);

    /// <summary>Publishes a new value. No-op with a warning when called off the host.</summary>
    public void Set(T value)
    {
        if (!_runtime.IsConnected) return;
        if (!_runtime.IsHost)
        {
            FeatureLog.Warn($"[Feature:{_label}] Set ignored: only the host publishes this state.");
            return;
        }

        var result = _extension.Commit(context => context.Set(_state, value));
        if (!result.Committed)
            FeatureLog.Warn($"[Feature:{_label}] state update refused: {result.Reason}");
    }
}

/// <summary>
/// Host state held per player rather than globally — one lamp mode per climber, one outfit
/// per climber. Latecomers receive everyone's current value on arrival, and a player's entry
/// is dropped automatically when they leave.
/// </summary>
internal sealed class PerPlayerState<T> where T : IPacket, new()
{
    private readonly ExtensionRuntime _runtime;
    private readonly MultiplayerExtension _extension;
    private readonly ReplicatedState<T> _state;
    private readonly string _label;

    internal PerPlayerState(ExtensionRuntime runtime, MultiplayerExtension extension,
        ReplicatedState<T> state, string label)
    {
        _runtime = runtime;
        _extension = extension;
        _state = state;
        _label = label;
    }

    /// <summary>The underlying contract, so a host handler can publish inside its own
    /// transaction (see <see cref="HostRequest{T}.Publish{TState}"/>).</summary>
    internal ReplicatedState<T> Inner => _state;

    /// <summary>The value currently known for that player, if any.</summary>
    public bool TryGet(int playerId, out T value) => _state.TryGetForPlayer(playerId, out value);

    /// <summary>Publishes a player's value. Host only.</summary>
    public void Set(int playerId, T value)
    {
        if (!_runtime.IsConnected) return;
        if (!_runtime.IsHost)
        {
            FeatureLog.Warn($"[Feature:{_label}] Set ignored: only the host publishes this state.");
            return;
        }

        var result = _extension.Commit(context => context.SetForPlayer(_state, playerId, value));
        if (!result.Committed)
            FeatureLog.Warn($"[Feature:{_label}] state update refused: {result.Reason}");
    }

    /// <summary>Drops a player's value. Host only. Leaving players are cleared for you.</summary>
    public void Remove(int playerId)
    {
        if (!_runtime.IsConnected || !_runtime.IsHost) return;
        _extension.Commit(context => context.RemoveForPlayer(_state, playerId));
    }
}

/// <summary>
/// "I ask, the host decides." The handler runs on the host only and may refuse. Use it when
/// a client must not be able to impose the outcome by itself — roping up with a partner,
/// for instance.
/// </summary>
internal sealed class HostCommand<T> where T : IPacket, new()
{
    private readonly ExtensionRuntime _runtime;
    private readonly MultiplayerCommand<T> _command;
    private readonly string _label;

    internal HostCommand(ExtensionRuntime runtime, MultiplayerCommand<T> command, string label)
    {
        _runtime = runtime;
        _command = command;
        _label = label;
    }

    /// <summary>
    /// Sends the request. <paramref name="onAnswer"/> (optional) receives the host's verdict
    /// and the reason when refused, on the main thread, so it may touch the game. Never throws.
    /// </summary>
    public void Send(T request, Action<bool, string> onAnswer = null)
    {
        if (!_runtime.IsConnected)
        {
            Answer(onAnswer, false, "Not connected.");
            return;
        }

        FeatureContinuation.OnCompletion(_command.SendAsync(request), task =>
        {
            if (task.IsFaulted)
            {
                var reason = task.Exception?.GetBaseException().Message ?? "unknown error";
                FeatureLog.Warn($"[Feature:{_label}] command failed: {reason}");
                Answer(onAnswer, false, reason);
                return;
            }

            Answer(onAnswer, task.Result.Committed, task.Result.Reason);
        });
    }

    private void Answer(Action<bool, string> onAnswer, bool committed, string reason)
    {
        try
        {
            onAnswer?.Invoke(committed, reason);
        }
        catch (Exception ex)
        {
            FeatureLog.Error($"[Feature:{_label}] answer handler failed: {ex}");
        }
    }
}

/// <summary>What the host handler receives for a <see cref="HostCommand{T}"/>.</summary>
internal readonly struct HostRequest<T>
{
    private readonly HostCommandContext<T> _context;

    internal HostRequest(HostCommandContext<T> context) => _context = context;

    /// <summary>Id of the player who asked.</summary>
    public int FromPlayerId => _context.Sender.Id;

    /// <summary>What they asked for.</summary>
    public T Message => _context.Request;

    /// <summary>Refuses the request; the sender receives the reason.</summary>
    public void Reject(string reason) => _context.Reject(reason);

    /// <summary>
    /// Publishes the sender's value as part of answering. Doing it here rather than calling
    /// Set afterwards keeps it in the same transaction: if the handler rejects or throws,
    /// nothing is published at all.
    /// </summary>
    public void Publish<TState>(PerPlayerState<TState> state, TState value) where TState : IPacket, new()
        => _context.SetForPlayer(state.Inner, FromPlayerId, value);
}
