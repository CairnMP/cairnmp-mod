using System;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Extensions;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class GameplayRegressionTests
{
    [Fact]
    public void ChatUsesAuthenticatedRosterNameInsteadOfPayloadName()
    {
        var (runtime, _, game) = Create(new ChatFeature());
        var result = runtime.ExecuteHostCommand(2, Command("chat.line", new ChatMessage { FromName = "Host", Text = "Hello" }));
        Assert.Equal(CommandStatus.Committed, result.Status);
        Assert.Equal("Guest", game.ChatImpl.Name);
        Assert.Equal("Hello", game.ChatImpl.Message);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void ChatRejectsMultilineImpersonation(string text)
    {
        var (runtime, _, game) = Create(new ChatFeature());
        var result = runtime.ExecuteHostCommand(2, Command("chat.line", new ChatMessage { Text = text }));
        Assert.NotEqual(CommandStatus.Committed, result.Status);
        Assert.Null(game.ChatImpl.Message);
    }

    [Fact]
    public void AwakeBivouacParticipantBlocksFastForwardUntilTheirSleepReport()
    {
        var (runtime, host, game) = Create(new ClockFeature());
        // Bivouac players are intentionally absent from the visual gameplay roster.
        Assert.Empty(game.Players.RemotePlayersInGame);
        host.Tick(FeaturePhase.Always);
        Assert.True(game.ClockImpl.Frozen);
        runtime.ExecuteHostCommand(2, Command("clock.sleep", new SleepReport { Asleep = true }));
        host.Tick(FeaturePhase.Always);
        Assert.False(game.ClockImpl.Frozen);
        runtime.ExecuteHostCommand(2, Command("clock.sleep", new SleepReport { Asleep = false }));
        host.Tick(FeaturePhase.Always);
        Assert.True(game.ClockImpl.Frozen);
        host.NotifySessionEnded();
        Assert.False(game.ClockImpl.Frozen);
    }

    private static (ExtensionRuntime runtime, FeatureHost host, Game game) Create(MultiplayerFeature feature)
    {
        var runtime = new ExtensionRuntime();
        var game = new Game();
        var host = new FeatureHost(runtime, game);
        host.RegisterAll(new[] { feature }, new Version(1, 0));
        runtime.Attach(new Bridge());
        return (runtime, host, game);
    }

    private static ClientExtensionCommand Command(string id, IPacket payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        payload.Serialize(writer);
        return new ClientExtensionCommand { RequestId = 1, ExtensionId = FeatureHost.CoreExtensionId, CommandId = id, Payload = stream.ToArray() };
    }

    private sealed class Bridge : IExtensionNetworkBridge
    {
        public bool IsConnected => true;
        public bool IsHost => true;
        public MultiplayerPlayer LocalPlayer => new(1, "Host", true, true);
        public IReadOnlyList<MultiplayerPlayer> Players => new[] { LocalPlayer, new MultiplayerPlayer(2, "Guest", false, false) };
        public bool IsExtensionEnabled(string extensionId, int playerId) => true;
        public void SendCommand(ClientExtensionCommand command) {}
        public void SendCommandResult(int targetPlayerId, ServerExtensionCommandResult result) {}
        public void BroadcastEvent(ServerExtensionEvent message, string extensionId) {}
        public void BroadcastState(ServerExtensionState state, string extensionId) {}
        public void ReportExtensionFailure(string extensionId, string message) {}
    }

    private sealed class Game : IGameApi
    {
        internal readonly Clock ClockImpl = new();
        internal readonly Chat ChatImpl = new();
        public IMainMenuApi MainMenu => UnavailableGameApi.Instance.MainMenu;
        public IGameStateApi State => UnavailableGameApi.Instance.State;
        public IGameTimeApi Time { get; } = new FrameTime();
        public IGameInputApi Input => UnavailableGameApi.Instance.Input;
        public IGameHudApi Hud => UnavailableGameApi.Instance.Hud;
        public IChatApi Chat => ChatImpl;
        public IClockApi Clock => ClockImpl;
        public IPlayersApi Players { get; } = new Players();
        public IWeatherApi Weather => UnavailableGameApi.Instance.Weather;
        public IWorldApi World => UnavailableGameApi.Instance.World;
    }

    private sealed class FrameTime : IGameTimeApi
    {
        public float UnscaledTime => 1;
        public float UnscaledDeltaTime => 1;
        public bool IsPaused => false;
    }

    private sealed class Clock : IClockApi
    {
        internal bool Frozen;
        public bool TryGetDayTime(out float value) { value = .5f; return true; }
        public bool TryGetLocalSleep(out bool value) { value = true; return true; }
        public bool Freeze(float value) { Frozen = true; return true; }
        public bool Unfreeze() { Frozen = false; return true; }
        public void Reset() => Frozen = false;
        public void LogDiagnosticsOnce() {}
    }

    private sealed class Chat : IChatApi
    {
        internal string Name, Message;
        public bool IsTyping => false;
        public IGameRegistration Configure(Action<string> send, Func<bool> host, Func<bool> canType)
            => UnavailableGameApi.Instance.Chat.Configure(send, host, canType);
        public void AddRemoteLine(string name, string message) { Name = name; Message = message; }
        public void AddSystemLine(string text) {}
        public void Tick() {}
        public void Draw() {}
        public void ForceClose() {}
    }

    private sealed class Players : IPlayersApi
    {
        public IReadOnlyList<int> RemotePlayersInGame => Array.Empty<int>();
        public IReadOnlyList<int> RemoteSleepParticipants => new[] { 2 };
        public bool TryCaptureHandPose(out byte[] packed) { packed = null; return false; }
        public bool TryCaptureAppearance(out int packed) { packed = 0; return false; }
        public bool TryCaptureCosmetics(out byte flags) { flags = 0; return false; }
        public void SetRemoteHandPose(int id, byte[] packed) {}
        public void SetRemoteAppearance(int id, int packed) {}
        public void SetRemoteCosmetics(int id, byte flags) {}
        public void ResetHandPoseCaches() {}
        public void ResetAppearanceCaches() {}
    }
}
