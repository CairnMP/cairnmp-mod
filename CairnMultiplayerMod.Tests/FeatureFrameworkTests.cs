using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Extensions;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// Exercises the feature framework without Unity. This is the point of routing feature logic
/// through Framework/ instead of the networking layer: what a feature sends, receives and
/// cleans up becomes assertable.
/// </summary>
public sealed class FeatureFrameworkTests : IDisposable
{
    [Fact]
    public void WorkerCompletionIsDeliveredOnlyByTheGameThreadPump()
    {
        var runtime = new ExtensionRuntime();
        var thread = Environment.CurrentManagedThreadId;
        var source = new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var calledOn = 0;
        runtime.ObserveCompletion(source.Task, _ => calledOn = Environment.CurrentManagedThreadId);
        var worker = new Thread(() => source.SetResult(7));
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calledOn);
        runtime.Tick();
        Assert.Equal(thread, calledOn);
    }

    [Fact]
    public void PendingCompletionIsNotLostWhenThePumpRunsEarly()
    {
        var runtime = new ExtensionRuntime();
        var source = new System.Threading.Tasks.TaskCompletionSource<int>();
        var calls = 0;
        runtime.ObserveCompletion(source.Task, _ => calls++);
        runtime.Tick();
        Assert.Equal(0, calls);
        source.SetCanceled();
        runtime.Tick();
        runtime.Tick();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DifferentFeaturesCanReuseAllLocalContractNames()
    {
        var runtime = NewRuntime(out _);
        var host = new FeatureHost(runtime);
        host.RegisterAll(new MultiplayerFeature[] { new SharedNamesFeature("alpha"), new SharedNamesFeature("beta") }, new Version(1, 0));
    }

    private sealed class SharedNamesFeature : MultiplayerFeature
    {
        internal SharedNamesFeature(string id) => Id = id;
        public override string Id { get; }
        protected internal override void OnRegister(FeatureBuilder feature)
        {
            feature.HostState<Point>("current", _ => { });
            feature.PerPlayerState<Point>("player", (_, _) => { });
            feature.Broadcast<Point>("line", (_, _) => { });
            feature.HostCommand<Point>("try", _ => { });
        }
    }

    private readonly List<string> _warnings = new();
    private readonly List<string> _errors = new();

    public FeatureFrameworkTests()
        => FeatureLog.SetSink(_ => { }, _warnings.Add, _errors.Add);

    public void Dispose() => FeatureLog.SetSink(null, null, null);

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryFeatureGetsItsOnRegisterCalled()
    {
        var feature = new RecordingFeature("alpha");
        HostWith(feature);

        Assert.True(feature.Registered);
    }

    [Fact]
    public void FeaturesRegisterInIdOrderWhateverTheOrderTheyComeIn()
    {
        var order = new List<string>();
        var zulu = new RecordingFeature("zulu", onRegister: () => order.Add("zulu"));
        var alpha = new RecordingFeature("alpha", onRegister: () => order.Add("alpha"));

        HostWith(zulu, alpha);

        Assert.Equal(new[] { "alpha", "zulu" }, order);
    }

    [Fact]
    public void TwoFeaturesSharingAnIdIsRefusedAtStartup()
    {
        var host = new FeatureHost(NewRuntime(out _));

        var error = Assert.Throws<InvalidOperationException>(() => host.RegisterAll(
            new MultiplayerFeature[] { new RecordingFeature("ping"), new RecordingFeature("ping") },
            new Version(1, 0, 0)));

        Assert.Contains("ping", error.Message);
    }

    [Fact]
    public void AFeatureThatThrowsWhileDeclaringNamesItselfInTheError()
    {
        var host = new FeatureHost(NewRuntime(out _));
        var broken = new RecordingFeature("broken",
            onRegister: () => throw new InvalidOperationException("bad declaration"));

        var error = Assert.Throws<InvalidOperationException>(
            () => host.RegisterAll(new MultiplayerFeature[] { broken }, new Version(1, 0, 0)));

        Assert.Contains("broken", error.Message);
    }

    [Fact]
    public void AFailedFeatureDeclarationReleasesItsGameRegistrations()
    {
        var game = new FakeGameApi();
        var host = new FeatureHost(NewRuntime(out _), game);

        Assert.Throws<InvalidOperationException>(() => host.RegisterAll(
            new MultiplayerFeature[] { new RegisteringThenThrowingFeature() },
            new Version(1, 0, 0)));

        Assert.False(game.Menu.RegistrationIsActive);
    }

    [Fact]
    public void AFeatureReceivesTheInjectedGameFacade()
    {
        var game = new FakeGameApi();
        var feature = new MenuFeature();
        var host = new FeatureHost(NewRuntime(out _), game);

        host.RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));

        Assert.Equal("menu.menu-test", game.Menu.ButtonId);
        Assert.Equal("Test menu", game.Menu.ButtonLabel);
        Assert.True(feature.Registration.IsActive);

        game.Menu.Click();
        Assert.Equal(1, feature.Clicks);

        host.Dispose();
        Assert.False(feature.Registration.IsActive);
    }

    [Fact]
    public void GameApiContractsExposeNoEngineOrInteropTypes()
    {
        var contractTypes = new[]
        {
            typeof(IGameApi), typeof(IGameRegistration), typeof(IMainMenuApi),
            typeof(IGameStateApi), typeof(IGameTimeApi), typeof(IGameInputApi),
            typeof(IGameHudApi), typeof(IChatApi), typeof(IInventoryApi), typeof(ShareableItem),
            typeof(IClockApi), typeof(IPlayersApi), typeof(PlayerLocation),
            typeof(IWeatherApi), typeof(IWorldApi), typeof(WorldPosition),
        };
        var exposedTypes = contractTypes.SelectMany(TypesInPublicSignatures)
            .SelectMany(FlattenType).ToArray();
        var forbiddenPrefixes = new[] { "Unity", "Il2Cpp", "Steamworks", "Harmony" };

        Assert.DoesNotContain(exposedTypes, type =>
            forbiddenPrefixes.Any(prefix =>
                (type.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)));
    }

    // ── Ticks and phases ──────────────────────────────────────────────────────

    [Fact]
    public void ATickOnlyRunsInThePhaseItAskedFor()
    {
        var always = 0;
        var gameplay = 0;
        var feature = new TickingFeature(() => always++, () => gameplay++);
        var host = HostWith(feature);

        host.Tick(FeaturePhase.Always);

        Assert.Equal(1, always);
        Assert.Equal(0, gameplay);

        host.Tick(FeaturePhase.Gameplay);

        Assert.Equal(1, always);
        Assert.Equal(1, gameplay);
    }

    [Fact]
    public void TicksAreDormantWhenTheMultiplayerModeIsInactive()
    {
        var active = false;
        var ticks = 0;
        var draws = 0;
        var host = new FeatureHost(NewRuntime(out _), isActive: () => active);
        host.RegisterAll(new MultiplayerFeature[]
        {
            new TickingFeature(() => ticks++, null),
            new DrawingFeature(() => draws++),
        }, new Version(1, 0, 0));

        host.Tick(FeaturePhase.Always);
        host.DrawHud();
        Assert.Equal(0, ticks);
        Assert.Equal(0, draws);

        active = true;
        host.Tick(FeaturePhase.Always);
        host.DrawHud();
        Assert.Equal(1, ticks);
        Assert.Equal(1, draws);
    }

    [Fact]
    public void AFeatureThrowingInItsTickNeitherStopsTheOthersNorItself()
    {
        var healthyTicks = 0;
        var host = HostWith(
            new TickingFeature(() => throw new InvalidOperationException("boom"), null, "aaa-broken"),
            new TickingFeature(() => healthyTicks++, null, "bbb-healthy"));

        host.Tick(FeaturePhase.Always);
        host.Tick(FeaturePhase.Always);

        Assert.Equal(2, healthyTicks);            // the healthy one kept running
        Assert.Single(_errors);                 // repeated frame failures are rate limited
        Assert.Contains("aaa-broken", _errors[0]);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [Fact]
    public void SessionAndSceneCallbacksReachTheFeature()
    {
        var events = new List<string>();
        var feature = new LifecycleFeature(events);
        var host = HostWith(feature);

        host.NotifySessionStarted();
        host.NotifySceneReset();
        host.NotifySessionEnded();

        Assert.Equal(new[] { "started", "scene", "ended" }, events);
    }

    // ── Network channels ──────────────────────────────────────────────────────

    [Fact]
    public void BroadcastFromAClientIsRelayedToEveryoneByTheHost()
    {
        var runtime = NewRuntime(out var bridge);
        var feature = new PingLikeFeature();
        new FeatureHost(runtime).RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        // Player 2 asks the host to broadcast a ping.
        var result = runtime.ExecuteHostCommand(2, CommandPacket("ping", "placed", new Point(1f, 2f, 3f)));

        Assert.True(result.Committed);
        Assert.Single(bridge.Events);
        Assert.Equal(2, bridge.Events[0].SourcePlayerId);
    }

    [Fact]
    public void TheSenderDoesNotReceiveItsOwnBroadcast()
    {
        var runtime = NewRuntime(out var bridge);
        var feature = new PingLikeFeature();
        new FeatureHost(runtime).RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        // The host (player 1 in FakeBridge) broadcasts: it must not call itself back,
        // otherwise a ping would appear twice on the sender's screen.
        runtime.ExecuteHostCommand(1, CommandPacket("ping", "placed", new Point(4f, 5f, 6f)));

        Assert.Empty(feature.Received);

        // A different player's broadcast does reach us.
        runtime.ExecuteHostCommand(2, CommandPacket("ping", "placed", new Point(7f, 8f, 9f)));

        Assert.Single(feature.Received);
        Assert.Equal(7f, feature.Received[0].X);
    }

    [Fact]
    public void HostStateIsPublishedAndReadBack()
    {
        var runtime = NewRuntime(out var bridge);
        var feature = new WeatherLikeFeature();
        new FeatureHost(runtime).RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        feature.Publish(new Point(0.5f, 0f, 0f));

        Assert.Single(bridge.States);                       // sent on the wire for latecomers
        Assert.True(feature.Current.TryGet(out var value));
        Assert.Equal(0.5f, value.X);
        Assert.Single(feature.Changes);
    }

    [Fact]
    public void AClientPublishingHostStateIsIgnoredWithAWarning()
    {
        var runtime = NewRuntime(out var bridge, isHost: false);
        var feature = new WeatherLikeFeature();
        new FeatureHost(runtime).RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        feature.Publish(new Point(1f, 0f, 0f));

        Assert.Empty(bridge.States);
        Assert.Contains(_warnings, warning => warning.Contains("only the host"));
    }

    [Fact]
    public void AHostCommandCanBeRefusedAndNothingIsBroadcast()
    {
        var runtime = NewRuntime(out var bridge);
        var feature = new GatedFeature(accept: false);
        new FeatureHost(runtime).RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        var result = runtime.ExecuteHostCommand(2, CommandPacket("gated", "try", new Point(1f, 1f, 1f)));

        Assert.False(result.Committed);
        Assert.Equal("nope", result.Reason);
        Assert.Empty(bridge.States);
        Assert.Empty(bridge.Events);
    }

    [Fact]
    public void PrivilegedFeaturesAreNotRateLimited()
    {
        var runtime = NewRuntime(out var bridge);
        new FeatureHost(runtime).RegisterAll(
            new MultiplayerFeature[] { new PingLikeFeature() }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        // Well past the 120-per-10s budget that protects third-party extensions: a feature
        // sending every frame must not be throttled.
        for (int i = 0; i < 300; i++)
        {
            var result = runtime.ExecuteHostCommand(2, CommandPacket("ping", "placed", new Point(i, 0f, 0f)));
            Assert.True(result.Committed, $"send #{i} was throttled: {result.Reason}");
        }
    }

    [Fact]
    public void AHostCommandAnswersOnTheCallingThread()
    {
        var runtime = NewRuntime(out var bridge);
        var feature = new AnsweringFeature();
        new FeatureHost(runtime).RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        int? answerThread = null;
        feature.Try.Send(new Point(1f, 1f, 1f), (_, _) => answerThread = Thread.CurrentThread.ManagedThreadId);

        Assert.Equal(Thread.CurrentThread.ManagedThreadId, answerThread);
    }

    [Fact]
    public void AThrowingAnswerHandlerIsContained()
    {
        var runtime = NewRuntime(out var bridge);
        var feature = new AnsweringFeature();
        new FeatureHost(runtime).RegisterAll(new MultiplayerFeature[] { feature }, new Version(1, 0, 0));
        runtime.Attach(bridge);

        feature.Try.Send(new Point(1f, 1f, 1f), (_, _) => throw new InvalidOperationException("boom"));

        Assert.Contains(_errors, error => error.Contains("boom"));
    }

    // ── Real-time streams ─────────────────────────────────────────────────────

    [Fact]
    public void AStreamPayloadReachesTheFeatureThatDeclaredIt()
    {
        var router = new FeatureStreamRouter();
        var received = new List<(int From, byte[] Payload)>();
        var channel = router.Register("ping.pose", (from, payload) => received.Add((from, payload)));

        router.Dispatch(7, channel, new byte[] { 1, 2, 3 });

        Assert.Single(received);
        Assert.Equal(7, received[0].From);
        Assert.Equal(new byte[] { 1, 2, 3 }, received[0].Payload);
    }

    [Fact]
    public void AnUnknownChannelIsIgnoredRatherThanThrowing()
    {
        var router = new FeatureStreamRouter();
        router.Register("known.stream", (_, _) => { });

        // A peer on a newer build streaming something we have never heard of.
        router.Dispatch(2, 0xBEEF, new byte[] { 9 });
    }

    [Fact]
    public void AStreamHandlerThatThrowsIsContained()
    {
        var router = new FeatureStreamRouter();
        var channel = router.Register("broken.stream", (_, _) => throw new InvalidOperationException("boom"));

        router.Dispatch(1, channel, new byte[] { 0 });

        Assert.Single(_errors);
        Assert.Contains("broken.stream", _errors[0]);
    }

    [Fact]
    public void DeclaringTheSameStreamTwiceIsRefused()
    {
        var router = new FeatureStreamRouter();
        router.Register("ping.pose", (_, _) => { });

        var error = Assert.Throws<InvalidOperationException>(() => router.Register("ping.pose", (_, _) => { }));

        Assert.Contains("twice", error.Message);
    }

    [Fact]
    public void ChannelHashingIsStableAndNeverZero()
    {
        // Both peers compute the channel id from the name, so this has to be identical
        // everywhere and across runs — a drift would silently route to nothing.
        Assert.Equal(FeatureStreamRouter.Hash("player.pose"), FeatureStreamRouter.Hash("player.pose"));
        Assert.NotEqual(FeatureStreamRouter.Hash("player.pose"), FeatureStreamRouter.Hash("player.frame"));
        Assert.NotEqual(0, FeatureStreamRouter.Hash(""));
    }

    // ── Feature message encoding ──────────────────────────────────────────────
    // Ported from the protocol tests when these packets moved into features: the wire
    // format still has to survive a round trip, it is just declared elsewhere now.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SleepReportRoundTrips(bool asleep)
    {
        var got = RoundTrip(new SleepReport { Asleep = asleep });

        Assert.Equal(asleep, got.Asleep);
    }

    [Fact]
    public void ClockStateRoundTrips()
    {
        var got = RoundTrip(new ClockState { DayTime01 = 0.4275f, AllAsleep = true });

        Assert.Equal(0.4275f, got.DayTime01);
        Assert.True(got.AllAsleep);
    }

    [Fact]
    public void HandPoseRoundTrips()
    {
        var packed = new byte[Protocol.HandPosePackedSize];
        for (int i = 0; i < packed.Length; i++) packed[i] = (byte)(i * 7 + 3);

        var got = RoundTrip(new HandPose { Packed = packed });

        Assert.Equal(packed, got.Packed);
    }

    [Fact]
    public void ChatMessageRoundTrips()
    {
        var got = RoundTrip(new ChatMessage { FromName = "Ana", Text = "héllo 🌍" });

        Assert.Equal("Ana", got.FromName);
        Assert.Equal("héllo 🌍", got.Text);
    }

    [Fact]
    public void WeatherStateRoundTripsThroughItsWrapper()
    {
        var got = RoundTrip(new WeatherState
        {
            Data = new WeatherSyncData { IsValid = true, WeatherType = 3, WindForce = 12.25f },
        });

        Assert.True(got.Data.IsValid);
        Assert.Equal(3, got.Data.WeatherType);
        Assert.Equal(12.25f, got.Data.WindForce);
    }

    private static T RoundTrip<T>(T message) where T : IPacket, new()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            message.Serialize(writer);

        buffer.Position = 0;
        using var reader = new BinaryReader(buffer);
        var got = new T();
        got.Deserialize(reader);
        return got;
    }

    private static IEnumerable<Type> TypesInPublicSignatures(Type contract)
    {
        foreach (var property in contract.GetProperties()) yield return property.PropertyType;
        foreach (var method in contract.GetMethods().Where(method => !method.IsSpecialName))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
        }
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        while (type.HasElementType) type = type.GetElementType();
        yield return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (!type.IsGenericType) yield break;
        foreach (var argument in type.GetGenericArguments())
            foreach (var nested in FlattenType(argument))
                yield return nested;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FeatureHost HostWith(params MultiplayerFeature[] features)
    {
        var host = new FeatureHost(NewRuntime(out _));
        host.RegisterAll(features, new Version(1, 0, 0));
        return host;
    }

    private static ExtensionRuntime NewRuntime(out FakeBridge bridge, bool isHost = true)
    {
        bridge = new FakeBridge(isHost);
        return new ExtensionRuntime();
    }

    private static ClientExtensionCommand CommandPacket(string featureId, string commandId, Point payload)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        payload.Serialize(writer);
        writer.Flush();
        return new ClientExtensionCommand
        {
            RequestId = 1,
            ExtensionId = FeatureHost.CoreExtensionId,
            CommandId = $"{featureId}.{commandId}",
            Payload = buffer.ToArray(),
        };
    }

    /// <summary>Minimal feature message — same serialization contract as the rest of the protocol.</summary>
    public sealed class Point : IPacket
    {
        public Point() { }
        public Point(float x, float y, float z) { X = x; Y = y; Z = z; }

        public float X, Y, Z;

        public void Serialize(BinaryWriter writer) { writer.Write(X); writer.Write(Y); writer.Write(Z); }
        public void Deserialize(BinaryReader reader) { X = reader.ReadSingle(); Y = reader.ReadSingle(); Z = reader.ReadSingle(); }
    }

    private sealed class RecordingFeature : MultiplayerFeature
    {
        private readonly Action _onRegister;
        internal RecordingFeature(string id, Action onRegister = null) { Id = id; _onRegister = onRegister; }

        public override string Id { get; }
        internal bool Registered { get; private set; }

        protected internal override void OnRegister(FeatureBuilder feature)
        {
            Registered = true;
            _onRegister?.Invoke();
        }
    }

    private sealed class TickingFeature : MultiplayerFeature
    {
        private readonly Action _always;
        private readonly Action _gameplay;

        internal TickingFeature(Action always, Action gameplay, string id = "ticking")
        {
            _always = always;
            _gameplay = gameplay;
            Id = id;
        }

        public override string Id { get; }

        protected internal override void OnRegister(FeatureBuilder feature)
        {
            if (_always != null) feature.EveryFrame(_always, FeaturePhase.Always);
            if (_gameplay != null) feature.EveryFrame(_gameplay, FeaturePhase.Gameplay);
        }
    }

    private sealed class DrawingFeature : MultiplayerFeature
    {
        private readonly Action _draw;

        internal DrawingFeature(Action draw) => _draw = draw;

        public override string Id => "drawing";

        protected internal override void OnRegister(FeatureBuilder feature)
            => feature.OnDrawHud(_draw);
    }

    private sealed class MenuFeature : MultiplayerFeature
    {
        public override string Id => "menu";
        internal IGameRegistration Registration { get; private set; }
        internal int Clicks { get; private set; }

        protected internal override void OnRegister(FeatureBuilder feature)
            => Registration = feature.Game.MainMenu.AddButton(
                "menu-test", "Test menu", () => Clicks++);
    }

    private sealed class RegisteringThenThrowingFeature : MultiplayerFeature
    {
        public override string Id => "broken-menu";

        protected internal override void OnRegister(FeatureBuilder feature)
        {
            feature.Game.MainMenu.AddButton("broken", "Broken", () => { });
            throw new InvalidOperationException("declaration failed");
        }
    }

    private sealed class FakeGameApi : IGameApi
    {
        internal FakeMainMenuApi Menu { get; } = new();
        public IMainMenuApi MainMenu => Menu;
        public IGameStateApi State => UnavailableGameApi.Instance.State;
        public IGameTimeApi Time => UnavailableGameApi.Instance.Time;
        public IGameInputApi Input => UnavailableGameApi.Instance.Input;
        public IGameHudApi Hud => UnavailableGameApi.Instance.Hud;
        public IChatApi Chat => UnavailableGameApi.Instance.Chat;
        public IInventoryApi Inventory => UnavailableGameApi.Instance.Inventory;
        public IClockApi Clock => UnavailableGameApi.Instance.Clock;
        public IPlayersApi Players => UnavailableGameApi.Instance.Players;
        public IWeatherApi Weather => UnavailableGameApi.Instance.Weather;
        public IWorldApi World => UnavailableGameApi.Instance.World;
    }

    private sealed class FakeMainMenuApi : IMainMenuApi
    {
        private FakeRegistration _registration;
        private Action _onClick;

        internal string ButtonId { get; private set; }
        internal string ButtonLabel { get; private set; }
        internal bool RegistrationIsActive => _registration?.IsActive == true;

        public IGameRegistration AddButton(string id, string label, Action onClick)
        {
            ButtonId = id;
            ButtonLabel = label;
            _onClick = onClick;
            return _registration = new FakeRegistration(id);
        }

        internal void Click() => _onClick();

        private sealed class FakeRegistration : IGameRegistration
        {
            internal FakeRegistration(string id) => Id = id;
            public string Id { get; }
            public bool IsActive { get; private set; } = true;
            public void Dispose() => IsActive = false;
        }
    }

    private sealed class LifecycleFeature : MultiplayerFeature
    {
        private readonly List<string> _events;
        internal LifecycleFeature(List<string> events) => _events = events;

        public override string Id => "lifecycle";

        protected internal override void OnRegister(FeatureBuilder feature)
        {
            feature.OnSessionStarted(() => _events.Add("started"));
            feature.OnSceneReset(() => _events.Add("scene"));
            feature.OnSessionEnded(() => _events.Add("ended"));
        }
    }

    private sealed class PingLikeFeature : MultiplayerFeature
    {
        public override string Id => "ping";
        internal List<Point> Received { get; } = new();

        protected internal override void OnRegister(FeatureBuilder feature)
            => feature.Broadcast<Point>("placed", (_, point) => Received.Add(point));
    }

    private sealed class WeatherLikeFeature : MultiplayerFeature
    {
        public override string Id => "weather";
        internal HostState<Point> Current { get; private set; }
        internal List<Point> Changes { get; } = new();

        public override string ToString() => Id;

        protected internal override void OnRegister(FeatureBuilder feature)
            => Current = feature.HostState<Point>("current", Changes.Add);

        internal void Publish(Point value) => Current.Set(value);
    }

    private sealed class GatedFeature : MultiplayerFeature
    {
        private readonly bool _accept;
        internal GatedFeature(bool accept) => _accept = accept;

        public override string Id => "gated";

        protected internal override void OnRegister(FeatureBuilder feature)
            => feature.HostCommand<Point>("try", request =>
            {
                if (!_accept) request.Reject("nope");
            });
    }

    private sealed class AnsweringFeature : MultiplayerFeature
    {
        public override string Id => "answering";

        internal HostCommand<Point> Try;

        protected internal override void OnRegister(FeatureBuilder feature)
            => Try = feature.HostCommand<Point>("try", _ => { });
    }

    private sealed class FakeBridge : IExtensionNetworkBridge
    {
        private readonly bool _isHost;
        internal FakeBridge(bool isHost = true) => _isHost = isHost;

        public bool IsConnected => true;
        public bool IsHost => _isHost;
        public MultiplayerPlayer LocalPlayer => new(1, "Host", isLocal: true, isHost: _isHost);
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
