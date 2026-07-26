using System;
using System.Collections.Generic;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Api.Internal;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class ExtensionTransactionTests
{
    [Fact]
    public void SuccessfulCommandCommitsStateAndEventTogether()
    {
        var runtime = HostRuntime(out var bridge);
        var extension = runtime.Register(new ExtensionRegistration("com.example.atomic", new Version(1, 0, 0)));
        var state = extension.RegisterState<int>("score");
        var multiplayerEvent = extension.RegisterEvent<string>("announced");
        var stateChanges = 0;
        var receivedEvents = 0;
        state.Changed += _ => stateChanges++;
        multiplayerEvent.Received += _ => receivedEvents++;
        var command = extension.RegisterCommand<int>("add", context =>
        {
            context.Set(state, context.Request);
            context.Broadcast(multiplayerEvent, "committed");
        });
        runtime.Attach(bridge);

        var result = runtime.ExecuteHostCommand(1, Packet(command, 7));

        Assert.True(result.Committed);
        Assert.True(state.TryGet(out var score));
        Assert.Equal(7, score);
        Assert.Equal(1, stateChanges);
        Assert.Equal(1, receivedEvents);
        Assert.Single(bridge.States);
        Assert.Single(bridge.Events);
    }

    [Fact]
    public void RejectedCommandRollsBackAllManagedEffects()
    {
        var runtime = HostRuntime(out var bridge);
        var extension = runtime.Register(new ExtensionRegistration("com.example.reject", new Version(1, 0, 0)));
        var state = extension.RegisterState<int>("score");
        var multiplayerEvent = extension.RegisterEvent<string>("announced");
        var command = extension.RegisterCommand<int>("add", context =>
        {
            context.Set(state, context.Request);
            context.Broadcast(multiplayerEvent, "must-not-leak");
            context.Reject("not allowed");
        });
        runtime.Attach(bridge);

        var result = runtime.ExecuteHostCommand(1, Packet(command, 7));

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.False(state.TryGet(out _));
        Assert.Empty(bridge.States);
        Assert.Empty(bridge.Events);
    }

    [Fact]
    public void ThrowingCommandRollsBackAllManagedEffects()
    {
        var runtime = HostRuntime(out var bridge);
        var extension = runtime.Register(new ExtensionRegistration("com.example.throwing", new Version(1, 0, 0)));
        var state = extension.RegisterState<int>("score");
        var command = extension.RegisterCommand<int>("add", context =>
        {
            context.Set(state, context.Request);
            throw new InvalidOperationException("boom");
        });
        runtime.Attach(bridge);

        var result = runtime.ExecuteHostCommand(1, Packet(command, 7));

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Contains("boom", result.Reason);
        Assert.False(state.TryGet(out _));
        Assert.Empty(bridge.States);
    }

    [Fact]
    public void CommandCannotMutateAnotherExtensionsState()
    {
        var runtime = HostRuntime(out var bridge);
        var first = runtime.Register(new ExtensionRegistration("com.example.first", new Version(1, 0, 0)));
        var second = runtime.Register(new ExtensionRegistration("com.example.second", new Version(1, 0, 0)));
        var foreignState = second.RegisterState<int>("score");
        var command = first.RegisterCommand<int>("steal", context => context.Set(foreignState, 99));
        runtime.Attach(bridge);

        var result = runtime.ExecuteHostCommand(1, Packet(command, 0));

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.False(foreignState.TryGet(out _));
        Assert.Empty(bridge.States);
    }

    [Fact]
    public void RepeatedHandlerFailuresOpenOnlyThatExtensionsCircuitBreaker()
    {
        var runtime = HostRuntime(out var bridge);
        var extension = runtime.Register(new ExtensionRegistration("com.example.unstable", new Version(1, 0, 0)));
        var calls = 0;
        var command = extension.RegisterCommand<int>("fail", _ =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        });
        runtime.Attach(bridge);

        Assert.Equal(CommandStatus.Failed, runtime.ExecuteHostCommand(1, Packet(command, 0)).Status);
        Assert.Equal(CommandStatus.Failed, runtime.ExecuteHostCommand(1, Packet(command, 0)).Status);
        Assert.Equal(CommandStatus.Failed, runtime.ExecuteHostCommand(1, Packet(command, 0)).Status);
        var isolated = runtime.ExecuteHostCommand(1, Packet(command, 0));

        Assert.Equal(3, calls);
        Assert.Equal(CommandStatus.Unavailable, isolated.Status);
        Assert.Contains("disabled", isolated.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostCommitFailureCannotUndoCommittedManagedState()
    {
        var runtime = HostRuntime(out var bridge);
        var extension = runtime.Register(new ExtensionRegistration("com.example.effect", new Version(1, 0, 0)));
        var state = extension.RegisterState<int>("score");
        var command = extension.RegisterCommand<int>("apply", context =>
        {
            context.Set(state, 5);
            context.AfterCommit(() => throw new InvalidOperationException("external mutation failed"));
        });
        runtime.Attach(bridge);

        var result = runtime.ExecuteHostCommand(1, Packet(command, 0));

        Assert.True(result.Committed);
        Assert.True(state.TryGet(out var value));
        Assert.Equal(5, value);
        Assert.Single(bridge.States);
    }

    [Fact]
    public void CommandSpamIsRejectedWithoutInvokingTheHandler()
    {
        var runtime = HostRuntime(out var bridge);
        var extension = runtime.Register(new ExtensionRegistration("com.example.ratelimit", new Version(1, 0, 0)));
        var calls = 0;
        var command = extension.RegisterCommand<int>("ping", _ => calls++);
        runtime.Attach(bridge);

        for (int i = 0; i < 120; i++)
            Assert.True(runtime.ExecuteHostCommand(1, Packet(command, i)).Committed);
        var rejected = runtime.ExecuteHostCommand(1, Packet(command, 121));

        Assert.Equal(120, calls);
        Assert.Equal(CommandStatus.Rejected, rejected.Status);
        Assert.Contains("rate limit", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task ClientCommandCompletesAsTimedOutWhenHostDoesNotAnswer()
    {
        var runtime = new ExtensionRuntime();
        var bridge = new FakeBridge(isHost: false);
        var extension = runtime.Register(new ExtensionRegistration("com.example.timeout", new Version(1, 0, 0)));
        var command = extension.RegisterCommand<int>("wait", _ => { });
        runtime.Attach(bridge);

        var pending = command.SendAsync(1);
        runtime.ExpirePendingCommands(DateTime.MaxValue);
        var result = await pending;

        Assert.Equal(CommandStatus.TimedOut, result.Status);
    }

    [Fact]
    public void HostCanCommitStateWithoutSendingACommandToItself()
    {
        var runtime = HostRuntime(out var bridge);
        var extension = runtime.Register(new ExtensionRegistration("com.example.hoststate", new Version(1, 0, 0)));
        var state = extension.RegisterState<string>("phase");
        runtime.Attach(bridge);

        var result = extension.Commit(context => context.Set(state, "summit"));

        Assert.True(result.Committed);
        Assert.True(state.TryGet(out var phase));
        Assert.Equal("summit", phase);
        Assert.Single(bridge.States);
    }

    [Fact]
    public void DetachClearsStateAndAcceptsLowerRevisionFromNextSession()
    {
        var runtime = new ExtensionRuntime();
        var extension = runtime.Register(new ExtensionRegistration(
            "com.example.reconnect", new Version(1, 0, 0)));
        var state = extension.RegisterState<string>("phase");
        runtime.Attach(new FakeBridge(isHost: false));
        runtime.ApplyState(new ServerExtensionState
        {
            ExtensionId = extension.Id,
            StateId = "phase",
            Revision = 100,
            Payload = PayloadCodec<string>.Json.Serialize("old-session"),
        });

        runtime.Detach();

        Assert.False(state.TryGet(out _));

        runtime.Attach(new FakeBridge(isHost: false));
        runtime.ApplyState(new ServerExtensionState
        {
            ExtensionId = extension.Id,
            StateId = "phase",
            Revision = 1,
            Payload = PayloadCodec<string>.Json.Serialize("new-session"),
        });

        Assert.True(state.TryGet(out var phase));
        Assert.Equal("new-session", phase);
    }

    private static ExtensionRuntime HostRuntime(out FakeBridge bridge)
    {
        var runtime = new ExtensionRuntime();
        bridge = new FakeBridge();
        return runtime;
    }

    private static ClientExtensionCommand Packet<T>(MultiplayerCommand<T> command, T request)
        => new()
        {
            RequestId = 1,
            ExtensionId = command.ExtensionId,
            CommandId = command.CommandId,
            Payload = command.Codec.Serialize(request),
        };

    private sealed class FakeBridge : IExtensionNetworkBridge
    {
        private readonly bool _isHost;
        internal FakeBridge(bool isHost = true) => _isHost = isHost;
        public bool IsConnected => true;
        public bool IsHost => _isHost;
        public MultiplayerPlayer LocalPlayer => new(1, "Host", isLocal: true, isHost: true);
        public IReadOnlyList<MultiplayerPlayer> Players => new[] { LocalPlayer };
        public List<ServerExtensionEvent> Events { get; } = new();
        public List<ServerExtensionState> States { get; } = new();

        public bool IsExtensionEnabled(string extensionId, int playerId) => true;
        public void SendCommand(ClientExtensionCommand command) { }
        public void SendCommandResult(int targetPlayerId, ServerExtensionCommandResult result) { }
        public void BroadcastEvent(ServerExtensionEvent multiplayerEvent, string extensionId) => Events.Add(multiplayerEvent);
        public void BroadcastState(ServerExtensionState state, string extensionId) => States.Add(state);
        public void ReportExtensionFailure(string extensionId, string message) { }
    }
}
