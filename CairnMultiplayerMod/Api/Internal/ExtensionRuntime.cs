using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayer.Shared.Extensions;

namespace CairnMultiplayerMod.Api.Internal;

internal interface IExtensionNetworkBridge
{
    bool IsConnected { get; }
    bool IsHost { get; }
    MultiplayerPlayer LocalPlayer { get; }
    IReadOnlyList<MultiplayerPlayer> Players { get; }
    bool IsExtensionEnabled(string extensionId, int playerId);
    void SendCommand(ClientExtensionCommand command);
    void SendCommandResult(int targetPlayerId, ServerExtensionCommandResult result);
    void BroadcastEvent(ServerExtensionEvent multiplayerEvent, string extensionId);
    void BroadcastState(ServerExtensionState state, string extensionId);
    void ReportExtensionFailure(string extensionId, string message);
}

internal sealed class ExtensionRuntime
{
    internal const int MaxManagedPayloadBytes = 48 * 1024;
    private const int MaxCommandsPerWindow = 120;
    private static readonly TimeSpan CommandRateWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);
    private const int CircuitBreakerFailureThreshold = 3;

    private readonly Dictionary<string, ExtensionDefinition> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string extensionId, string commandId), ICommandHandler> _commands = new();
    private readonly Dictionary<(string extensionId, string stateId), IReplicatedStateHandle> _states = new();
    private readonly Dictionary<(string extensionId, string eventId), IMultiplayerEventHandle> _events = new();
    private readonly ConcurrentDictionary<uint, PendingCommand> _pendingCommands = new();
    private readonly Dictionary<(string extensionId, string stateId, int scopePlayerId), ServerExtensionState> _officialStates = new();
    private readonly Dictionary<(int playerId, string extensionId, string commandId), RateWindow> _commandRates = new();
    private readonly Dictionary<string, int> _extensionFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabledExtensions = new(StringComparer.Ordinal);
    private IExtensionNetworkBridge _bridge;
    private int _nextRequestId;

    internal bool IsConnected => _bridge?.IsConnected == true;
    internal bool IsHost => _bridge?.IsHost == true;
    internal MultiplayerPlayer LocalPlayer => _bridge?.LocalPlayer ?? default;
    internal IReadOnlyList<MultiplayerPlayer> Players => _bridge?.Players ?? Array.Empty<MultiplayerPlayer>();

    internal event Action SessionReady;
    internal event Action SessionEnded;
    internal event Action<MultiplayerPlayer> PlayerJoined;
    internal event Action<MultiplayerPlayer> PlayerLeft;
    internal event Action<string, string> ExtensionDisabled;

    internal MultiplayerExtension Register(ExtensionRegistration registration)
    {
        if (registration == null) throw new ArgumentNullException(nameof(registration));
        if (!ExtensionNegotiator.IsValidExtensionId(registration.Id))
            throw new ArgumentException("Extension id must be 3-64 lowercase ASCII characters using letters, digits, '.', '-' or '_'.", nameof(registration));
        if (registration.Version == null)
            throw new ArgumentException("Extension version is required.", nameof(registration));
        if (registration.MinimumPeerVersion != null && registration.MaximumPeerVersion != null &&
            registration.MinimumPeerVersion > registration.MaximumPeerVersion)
            throw new ArgumentException("MinimumPeerVersion cannot be greater than MaximumPeerVersion.", nameof(registration));
        if (IsConnected)
            throw new InvalidOperationException("Extensions must be registered before joining or hosting a lobby.");
        if (_extensions.ContainsKey(registration.Id))
            throw new InvalidOperationException($"Extension '{registration.Id}' is already registered.");

        var definition = new ExtensionDefinition(new ExtensionManifestEntry
        {
            Id = registration.Id,
            Version = registration.Version.ToString(),
            MinimumPeerVersion = registration.MinimumPeerVersion?.ToString() ?? "",
            MaximumPeerVersion = registration.MaximumPeerVersion?.ToString() ?? "",
            Required = registration.Requirement == ExtensionRequirement.Required,
        });
        _extensions.Add(registration.Id, definition);
        return new MultiplayerExtension(this, definition);
    }

    internal MultiplayerCommand<T> RegisterCommand<T>(MultiplayerExtension extension, string commandId,
        Action<HostCommandContext<T>> handler, PayloadCodec<T> codec)
    {
        ValidateContract(extension, commandId, handler, codec);
        var key = (extension.Id, commandId);
        if (_commands.ContainsKey(key)) throw DuplicateContract("command", extension.Id, commandId);
        var command = new MultiplayerCommand<T>(this, extension.Id, commandId, codec);
        _commands.Add(key, new CommandHandler<T>(command, handler));
        return command;
    }

    internal ReplicatedState<T> RegisterState<T>(MultiplayerExtension extension, string stateId, PayloadCodec<T> codec)
    {
        ValidateContract(extension, stateId, codec);
        var key = (extension.Id, stateId);
        if (_states.ContainsKey(key)) throw DuplicateContract("state", extension.Id, stateId);
        var state = new ReplicatedState<T>(extension.Id, stateId, codec,
            message => _bridge?.ReportExtensionFailure(extension.Id, message));
        _states.Add(key, state);
        return state;
    }

    internal MultiplayerEvent<T> RegisterEvent<T>(MultiplayerExtension extension, string eventId, PayloadCodec<T> codec)
    {
        ValidateContract(extension, eventId, codec);
        var key = (extension.Id, eventId);
        if (_events.ContainsKey(key)) throw DuplicateContract("event", extension.Id, eventId);
        var multiplayerEvent = new MultiplayerEvent<T>(extension.Id, eventId, codec,
            message => _bridge?.ReportExtensionFailure(extension.Id, message));
        _events.Add(key, multiplayerEvent);
        return multiplayerEvent;
    }

    internal Task<CommandResult> SendCommandAsync<T>(MultiplayerCommand<T> command, T request,
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
            return Task.FromResult(new CommandResult(0, CommandStatus.Unavailable, "Not connected to a CairnMP session."));

        byte[] payload;
        try { payload = command.Codec.Serialize(request) ?? Array.Empty<byte>(); }
        catch (Exception ex)
        {
            return Task.FromResult(new CommandResult(0, CommandStatus.Failed,
                $"Could not serialize command: {ex.Message}"));
        }
        if (payload.Length > MaxManagedPayloadBytes)
            return Task.FromResult(new CommandResult(0, CommandStatus.Rejected,
                $"Command payload exceeds {MaxManagedPayloadBytes} bytes."));

        uint requestId = NextRequestId();
        var packet = new ClientExtensionCommand
        {
            RequestId = requestId,
            ExtensionId = command.ExtensionId,
            CommandId = command.CommandId,
            Payload = payload,
        };

        if (IsHost)
            return Task.FromResult(ExecuteHostCommand(LocalPlayer.Id, packet));

        // Network results and timeouts complete from NetworkManager.Update on Unity's main thread.
        var completion = new TaskCompletionSource<CommandResult>();
        _pendingCommands.TryAdd(requestId, new PendingCommand(completion, DateTime.UtcNow + DefaultCommandTimeout));
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                if (_pendingCommands.TryRemove(requestId, out _))
                    completion.TrySetCanceled(cancellationToken);
            });
        }
        _bridge.SendCommand(packet);
        return completion.Task;
    }

    internal CommandResult ExecuteHostCommand(int sourcePlayerId, ClientExtensionCommand packet)
    {
        if (!IsHost)
            return new CommandResult(packet.RequestId, CommandStatus.Unavailable, "This peer is not the host.");
        if (_disabledExtensions.Contains(packet.ExtensionId ?? ""))
            return new CommandResult(packet.RequestId, CommandStatus.Unavailable,
                "Extension was disabled for this session after repeated failures.");
        if (!_commands.TryGetValue((packet.ExtensionId ?? "", packet.CommandId ?? ""), out var handler))
            return new CommandResult(packet.RequestId, CommandStatus.Rejected, "Unknown extension command.");
        if (!IsExtensionEnabled(packet.ExtensionId, sourcePlayerId))
            return new CommandResult(packet.RequestId, CommandStatus.Rejected, "Extension is not enabled for this player.");
        if ((packet.Payload?.Length ?? 0) > MaxManagedPayloadBytes)
            return new CommandResult(packet.RequestId, CommandStatus.Rejected, "Command payload is too large.");
        if (!ConsumeCommandBudget(sourcePlayerId, packet.ExtensionId, packet.CommandId))
            return new CommandResult(packet.RequestId, CommandStatus.Rejected, "Extension command rate limit exceeded.");

        var transaction = new ExtensionTransaction(packet.ExtensionId, sourcePlayerId);
        try
        {
            handler.Invoke(FindPlayer(sourcePlayerId), packet.Payload ?? Array.Empty<byte>(), transaction);
            if (transaction.IsRejected)
                return new CommandResult(packet.RequestId, CommandStatus.Rejected, transaction.RejectionReason);
            bool postCommitFailed = Commit(transaction);
            if (!postCommitFailed)
                _extensionFailures.Remove(packet.ExtensionId);
            return new CommandResult(packet.RequestId, CommandStatus.Committed, "");
        }
        catch (Exception ex)
        {
            RecordFailure(packet.ExtensionId, ex);
            return new CommandResult(packet.RequestId, CommandStatus.Failed,
                $"Extension handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal CommandResult CommitHostEffects(MultiplayerExtension extension, Action<AuthoritativeContext> buildEffects)
    {
        if (extension == null) throw new ArgumentNullException(nameof(extension));
        if (buildEffects == null) throw new ArgumentNullException(nameof(buildEffects));
        if (!IsHost)
            return new CommandResult(0, CommandStatus.Unavailable, "Only the authoritative host may commit state.");
        if (_disabledExtensions.Contains(extension.Id))
            return new CommandResult(0, CommandStatus.Unavailable,
                "Extension was disabled for this session after repeated failures.");

        var transaction = new ExtensionTransaction(extension.Id, LocalPlayer.Id);
        try
        {
            buildEffects(new AuthoritativeContext(transaction));
            if (transaction.IsRejected)
                return new CommandResult(0, CommandStatus.Rejected, transaction.RejectionReason);
            bool postCommitFailed = Commit(transaction);
            if (!postCommitFailed) _extensionFailures.Remove(extension.Id);
            return new CommandResult(0, CommandStatus.Committed, "");
        }
        catch (Exception ex)
        {
            RecordFailure(extension.Id, ex);
            return new CommandResult(0, CommandStatus.Failed,
                $"Extension host update failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal bool IsExtensionEnabled(string extensionId, int playerId)
        => _bridge?.IsExtensionEnabled(extensionId, playerId) == true;

    internal ExtensionManifestEntry[] Manifest()
        => _extensions.Values.Select(x => x.Manifest).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();

    internal void Attach(IExtensionNetworkBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    internal void NotifySessionReady() => SafeInvoke(SessionReady, "SessionReady");

    internal void NotifyPlayerJoined(MultiplayerPlayer player) => SafeInvoke(PlayerJoined, player, "PlayerJoined");

    internal void NotifyPlayerLeft(MultiplayerPlayer player)
    {
        if (IsHost)
        {
            var scopedKeys = _officialStates.Keys.Where(key => key.scopePlayerId == player.Id).ToArray();
            foreach (var key in scopedKeys)
            {
                var previous = _officialStates[key];
                var removal = new ServerExtensionState
                {
                    ExtensionId = previous.ExtensionId,
                    StateId = previous.StateId,
                    ScopePlayerId = player.Id,
                    Revision = previous.Revision + 1,
                    Removed = true,
                    Payload = Array.Empty<byte>(),
                };
                _officialStates.Remove(key);
                ApplyState(removal);
                _bridge.BroadcastState(removal, removal.ExtensionId);
            }
        }
        SafeInvoke(PlayerLeft, player, "PlayerLeft");
    }

    internal void Detach()
    {
        if (_bridge == null) return;
        _bridge = null;
        foreach (var pending in _pendingCommands.Values)
            pending.Completion.TrySetResult(new CommandResult(0, CommandStatus.Unavailable, "Session ended."));
        _pendingCommands.Clear();
        _officialStates.Clear();
        _commandRates.Clear();
        _extensionFailures.Clear();
        _disabledExtensions.Clear();
        SafeInvoke(SessionEnded, "SessionEnded");
    }

    internal void CompleteCommand(ServerExtensionCommandResult result)
    {
        if (_pendingCommands.TryRemove(result.RequestId, out var pending))
            pending.Completion.TrySetResult(new CommandResult(result.RequestId, (CommandStatus)result.Status, result.Reason));
    }

    internal void Tick() => ExpirePendingCommands(DateTime.UtcNow);

    internal void ExpirePendingCommands(DateTime now)
    {
        if (_pendingCommands.Count == 0) return;
        var expired = _pendingCommands.Where(pair => pair.Value.Deadline <= now)
            .Select(pair => pair.Key).ToArray();
        foreach (var requestId in expired)
        {
            if (!_pendingCommands.TryRemove(requestId, out var pending)) continue;
            pending.Completion.TrySetResult(new CommandResult(requestId, CommandStatus.TimedOut,
                "The authoritative host did not answer before the timeout."));
        }
    }

    internal void ApplyEvent(ServerExtensionEvent packet)
    {
        if (_disabledExtensions.Contains(packet.ExtensionId ?? "")) return;
        if (!_events.TryGetValue((packet.ExtensionId ?? "", packet.EventId ?? ""), out var handle)) return;
        try { handle.Apply(packet.SourcePlayerId, packet.Payload ?? Array.Empty<byte>()); }
        catch (Exception ex) { RecordFailure(packet.ExtensionId, ex); }
    }

    internal void ApplyState(ServerExtensionState packet)
    {
        if (_disabledExtensions.Contains(packet.ExtensionId ?? "")) return;
        if (!_states.TryGetValue((packet.ExtensionId ?? "", packet.StateId ?? ""), out var handle)) return;
        try { handle.Apply(packet.ScopePlayerId, packet.Revision, packet.Removed, packet.Payload ?? Array.Empty<byte>()); }
        catch (Exception ex) { RecordFailure(packet.ExtensionId, ex); }
    }

    internal IReadOnlyList<ServerExtensionState> SnapshotFor(string extensionId)
        => _officialStates.Values
            .Where(state => string.Equals(state.ExtensionId, extensionId, StringComparison.Ordinal))
            .OrderBy(state => state.StateId, StringComparer.Ordinal)
            .ThenBy(state => state.ScopePlayerId)
            .ToArray();

    private bool Commit(ExtensionTransaction transaction)
    {
        bool postCommitFailed = false;
        foreach (var state in transaction.States)
            state.Handle.Validate(state.Payload, state.Removed);
        foreach (var multiplayerEvent in transaction.Events)
            multiplayerEvent.Handle.Validate(multiplayerEvent.Payload);

        foreach (var state in transaction.States)
        {
            var key = (transaction.ExtensionId, state.StateId, state.ScopePlayerId);
            var packet = new ServerExtensionState
            {
                ExtensionId = transaction.ExtensionId,
                StateId = state.StateId,
                ScopePlayerId = state.ScopePlayerId,
                Revision = state.Revision,
                Removed = state.Removed,
                Payload = state.Payload,
            };
            if (state.Removed) _officialStates.Remove(key);
            else _officialStates[key] = packet;
            state.Handle.Apply(state.ScopePlayerId, state.Revision, state.Removed, state.Payload);
            _bridge.BroadcastState(packet, transaction.ExtensionId);
        }
        foreach (var multiplayerEvent in transaction.Events)
        {
            multiplayerEvent.Handle.Apply(transaction.SourcePlayerId, multiplayerEvent.Payload);
            _bridge.BroadcastEvent(new ServerExtensionEvent
            {
                SourcePlayerId = transaction.SourcePlayerId,
                ExtensionId = transaction.ExtensionId,
                EventId = multiplayerEvent.EventId,
                Payload = multiplayerEvent.Payload,
            }, transaction.ExtensionId);
        }

        foreach (var effect in transaction.AfterCommitEffects)
        {
            try { effect(); }
            catch (Exception ex)
            {
                postCommitFailed = true;
                RecordFailure(transaction.ExtensionId, ex);
                _bridge.ReportExtensionFailure(transaction.ExtensionId,
                    $"Post-commit effect failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return postCommitFailed;
    }

    private bool ConsumeCommandBudget(int playerId, string extensionId, string commandId)
    {
        var key = (playerId, extensionId ?? "", commandId ?? "");
        var now = DateTime.UtcNow;
        if (!_commandRates.TryGetValue(key, out var window) || now - window.Start >= CommandRateWindow)
        {
            _commandRates[key] = new RateWindow(now, 1);
            return true;
        }
        if (window.Count >= MaxCommandsPerWindow) return false;
        _commandRates[key] = new RateWindow(window.Start, window.Count + 1);
        return true;
    }

    private void RecordFailure(string extensionId, Exception exception)
    {
        extensionId ??= "";
        int count = _extensionFailures.TryGetValue(extensionId, out var current) ? current + 1 : 1;
        _extensionFailures[extensionId] = count;
        _bridge?.ReportExtensionFailure(extensionId,
            $"Failure {count}/{CircuitBreakerFailureThreshold}: {exception.GetType().Name}: {exception.Message}");
        if (count < CircuitBreakerFailureThreshold || !_disabledExtensions.Add(extensionId)) return;
        string reason = $"Disabled after {count} failures during this session.";
        SafeInvoke(ExtensionDisabled, extensionId, reason, "ExtensionDisabled");
        _bridge?.ReportExtensionFailure(extensionId, reason);
    }

    private void SafeInvoke(Action handlers, string eventName)
    {
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { _bridge?.ReportExtensionFailure("cairnmp.api", $"{eventName} subscriber failed: {ex.Message}"); }
        }
    }

    private void SafeInvoke<T>(Action<T> handlers, T value, string eventName)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception ex) { _bridge?.ReportExtensionFailure("cairnmp.api", $"{eventName} subscriber failed: {ex.Message}"); }
        }
    }

    private void SafeInvoke<T1, T2>(Action<T1, T2> handlers, T1 first, T2 second, string eventName)
    {
        if (handlers == null) return;
        foreach (Action<T1, T2> handler in handlers.GetInvocationList())
        {
            try { handler(first, second); }
            catch (Exception ex) { _bridge?.ReportExtensionFailure("cairnmp.api", $"{eventName} subscriber failed: {ex.Message}"); }
        }
    }

    private MultiplayerPlayer FindPlayer(int playerId)
    {
        if (LocalPlayer.Id == playerId) return LocalPlayer;
        return Players.FirstOrDefault(player => player.Id == playerId);
    }

    private uint NextRequestId()
    {
        uint id = unchecked((uint)Interlocked.Increment(ref _nextRequestId));
        if (id == 0) id = unchecked((uint)Interlocked.Increment(ref _nextRequestId));
        return id;
    }

    private static void ValidateContract(MultiplayerExtension extension, string contractId, params object[] required)
    {
        if (extension == null) throw new ArgumentNullException(nameof(extension));
        if (!IsValidContractId(contractId))
            throw new ArgumentException("Contract id must be 1-64 lowercase ASCII characters using letters, digits, '.', '-' or '_'.", nameof(contractId));
        if (required.Any(value => value == null)) throw new ArgumentNullException(nameof(required));
    }

    private static bool IsValidContractId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '.' && c != '-' && c != '_')
                return false;
        }
        return true;
    }

    private static InvalidOperationException DuplicateContract(string kind, string extensionId, string contractId)
        => new($"Extension '{extensionId}' already registered {kind} '{contractId}'.");
}

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
