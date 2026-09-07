using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Extensions;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class GameplayRegressionTests
{
    [Fact]
    public void CommandRouterDispatchesRegisteredFeatureCommand()
    {
        string received = null;
        var commands = new Dictionary<string, ChatCommandDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["probe"] = new ChatCommandDefinition("probe", "/probe <value>", "test", value => received = value),
        };
        var router = new CommandRouter(null, () => false, _ => { }, commands);

        Assert.True(router.TryHandle("/probe hello world"));
        Assert.Equal("hello world", received);
    }

    [Fact]
    public void ItemOfferRejectsMultilineDisplayName()
    {
        var (runtime, _, _) = Create(new ShareItemsFeature());
        var result = runtime.ExecuteHostCommand(1, Command("share-items.offer", new ShareItemOffer
        {
            TransferId = 1,
            TargetPlayerId = 2,
            DefinitionId = 104,
            Count = 1,
            ItemName = "energy bar\nHost: forged",
        }));

        Assert.NotEqual(CommandStatus.Committed, result.Status);
    }

    /// <summary>
    /// Giving only exists as a native backpack action: out of range the action is greyed
    /// out, and forcing it anyway (the recipient walked away between the hint and the key
    /// press) must not take the item out of the bag.
    /// </summary>
    [Fact]
    public void NativeGiveActionGivesNothingWhenNobodyIsCloseEnough()
    {
        var (_, _, game) = Create(new ShareItemsFeature());
        game.PlayersImpl.TargetX = 8;

        Assert.False(game.InventoryImpl.CanGive);
        game.InventoryImpl.TriggerGiveUnchecked();

        Assert.Equal(5, game.InventoryImpl.Count);
        Assert.Contains(game.ChatImpl.SystemLines,
            line => line.Contains("No player is close enough", StringComparison.Ordinal));
    }

    /// <summary>The host arbitrates the distance too: a client can never forge proximity.</summary>
    [Fact]
    public void HostRejectsAnOfferBetweenPlayersTooFarApart()
    {
        var (runtime, _, game) = Create(new ShareItemsFeature());
        game.PlayersImpl.TargetX = 8;

        var result = runtime.ExecuteHostCommand(2, Command("share-items.offer", new ShareItemOffer
        {
            TransferId = 7,
            TargetPlayerId = 1,
            DefinitionId = 104,
            Count = 1,
            ItemName = "energy bar",
        }));

        Assert.NotEqual(CommandStatus.Committed, result.Status);
        Assert.Contains("within 3.5 m", result.Reason, StringComparison.Ordinal);
        Assert.Equal(5, game.InventoryImpl.Count);
    }

    [Fact]
    public void HostRecipientCanAcknowledgeDeliveryDuringOfferCommit()
    {
        var (runtime, _, game) = Create(new ShareItemsFeature());

        var result = runtime.ExecuteHostCommand(2, Command("share-items.offer", new ShareItemOffer
        {
            TransferId = 42,
            TargetPlayerId = 1,
            DefinitionId = 104,
            Count = 1,
            ItemName = "energy bar",
        }));

        Assert.Equal(CommandStatus.Committed, result.Status);
        Assert.Equal(6, game.InventoryImpl.Count);
        Assert.Contains(game.ChatImpl.SystemLines,
            line => line.Contains("Received energy bar x1", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeGiveActionTransfersOneSelectedItemToNearestPlayer()
    {
        var (runtime, _, game) = Create(new ShareItemsFeature());

        game.InventoryImpl.TriggerGive();
        runtime.Tick();

        Assert.Equal(4, game.InventoryImpl.Count);
        var receipt = runtime.ExecuteHostCommand(2, Command("share-items.receipt", new ShareItemReceipt
        { TransferId = 1, SenderPlayerId = 1, Accepted = true }));
        Assert.Equal(CommandStatus.Committed, receipt.Status);
        // The item left the bag when it was sent: acknowledging it must not remove a second one.
        Assert.Equal(4, game.InventoryImpl.Count);
        Assert.Contains(game.ChatImpl.SystemLines,
            line => line.Contains("Gave energy bar x1 to Guest", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeDropActionCreatesOneHostOwnedGroundItem()
    {
        var (runtime, _, game) = Create(new ShareItemsFeature());

        game.InventoryImpl.TriggerDrop();
        runtime.Tick();

        Assert.Equal(4, game.InventoryImpl.Count);
        var item = Assert.Single(game.InventoryImpl.GroundItems);
        Assert.Equal(104, item.DefinitionId);
        Assert.Equal("Test", item.Scene);
    }

    [Fact]
    public void GroundItemCanOnlyBeClaimedOnce()
    {
        var (runtime, _, game) = Create(new ShareItemsFeature());
        game.PlayersImpl.TargetX = 1;
        game.InventoryImpl.TriggerDrop();
        runtime.Tick();
        var id = Assert.Single(game.InventoryImpl.GroundItems).Id;

        var first = runtime.ExecuteHostCommand(2, Command("share-items.pickup",
            new PickupGroundItemRequest { GroundItemId = id }));
        var second = runtime.ExecuteHostCommand(2, Command("share-items.pickup",
            new PickupGroundItemRequest { GroundItemId = id }));

        Assert.Equal(CommandStatus.Committed, first.Status);
        Assert.NotEqual(CommandStatus.Committed, second.Status);
        Assert.Empty(game.InventoryImpl.GroundItems);
    }

    [Fact]
    public void NearbyDropsPopulateCarouselAndClosingEligibilityClearsIt()
    {
        var (runtime, host, game) = Create(new ShareItemsFeature());
        game.InventoryImpl.TriggerDrop();
        runtime.Tick();
        game.InventoryImpl.TriggerDrop();
        runtime.Tick();
        game.InventoryImpl.InventoryOpen = false;
        host.Tick(FeaturePhase.Always);
        Assert.Equal(2, game.InventoryImpl.PickupItems.Count);
        Assert.Equal(game.InventoryImpl.GroundItems.Select(item => item.Id).OrderBy(id => id),
            game.InventoryImpl.PickupItems.Select(item => item.Id));
        game.InventoryImpl.InventoryOpen = true;
        host.Tick(FeaturePhase.Always);
        Assert.Empty(game.InventoryImpl.PickupItems);
    }

    [Fact]
    public void RejectedPickupReturnsItemToGround()
    {
        var (runtime, _, game) = Create(new ShareItemsFeature());
        game.PlayersImpl.TargetX = 1;
        game.InventoryImpl.TriggerDrop();
        runtime.Tick();
        var id = Assert.Single(game.InventoryImpl.GroundItems).Id;
        runtime.ExecuteHostCommand(2, Command("share-items.pickup",
            new PickupGroundItemRequest { GroundItemId = id }));

        var receipt = runtime.ExecuteHostCommand(2, Command("share-items.pickup-receipt",
            new GroundItemReceipt { GroundItemId = id, Accepted = false, Reason = "Backpack full." }));

        Assert.Equal(CommandStatus.Committed, receipt.Status);
        Assert.Equal(id, Assert.Single(game.InventoryImpl.GroundItems).Id);
    }

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
        public void SendCommand(ClientExtensionCommand command) { }
        public void SendCommandResult(int targetPlayerId, ServerExtensionCommandResult result) { }
        public void BroadcastEvent(ServerExtensionEvent message, string extensionId) { }
        public void BroadcastState(ServerExtensionState state, string extensionId) { }
        public void ReportExtensionFailure(string extensionId, string message) { }
    }

    private sealed class Game : IGameApi
    {
        internal readonly Clock ClockImpl = new();
        internal readonly Chat ChatImpl = new();
        internal readonly Inventory InventoryImpl = new();
        internal readonly Players PlayersImpl = new();
        public IMainMenuApi MainMenu => UnavailableGameApi.Instance.MainMenu;
        public IGameStateApi State { get; } = new State();
        public IGameTimeApi Time { get; } = new FrameTime();
        public IGameInputApi Input => UnavailableGameApi.Instance.Input;
        public IGameHudApi Hud => UnavailableGameApi.Instance.Hud;
        public IChatApi Chat => ChatImpl;
        public IInventoryApi Inventory => InventoryImpl;
        public IClockApi Clock => ClockImpl;
        public IPlayersApi Players => PlayersImpl;
        public IWeatherApi Weather => UnavailableGameApi.Instance.Weather;
        public IWorldApi World => UnavailableGameApi.Instance.World;
    }

    private sealed class State : IGameStateApi
    {
        public PlayerState LocalPlayerState => PlayerState.InGame;
        public bool IsLocalPlayerInGame => true;
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
        public void LogDiagnosticsOnce() { }
    }

    private sealed class Chat : IChatApi
    {
        internal string Name, Message;
        internal readonly List<string> SystemLines = new();
        public bool IsTyping => false;
        public IGameRegistration Configure(Action<string> send, Func<bool> host, Func<bool> canType)
            => UnavailableGameApi.Instance.Chat.Configure(send, host, canType);
        public IGameRegistration AddCommand(string name, string usage, string description, Action<string> execute)
            => UnavailableGameApi.Instance.Chat.AddCommand(name, usage, description, execute);
        public void AddRemoteLine(string name, string message) { Name = name; Message = message; }
        public void AddSystemLine(string text) => SystemLines.Add(text);
        public void Tick() { }
        public void Draw() { }
        public void ForceClose() { }
    }

    private sealed class Players : IPlayersApi
    {
        internal float TargetX = 2;
        public IReadOnlyList<int> RemotePlayersInGame => Array.Empty<int>();
        public IReadOnlyList<int> RemoteSleepParticipants => new[] { 2 };
        public bool TryGetLocation(int playerId, out PlayerLocation location)
        {
            location = playerId switch
            {
                1 => new PlayerLocation(0, 0, 0, "Test", PlayerState.InGame),
                2 => new PlayerLocation(TargetX, 0, 0, "Test", PlayerState.InGame),
                _ => default,
            };
            return playerId is 1 or 2;
        }
        public bool TryCaptureHandPose(out byte[] packed) { packed = null; return false; }
        public bool TryCaptureAppearance(out int packed) { packed = 0; return false; }
        public bool TryCaptureCosmetics(out byte flags) { flags = 0; return false; }
        public void SetRemoteHandPose(int id, byte[] packed) { }
        public void SetRemoteAppearance(int id, int packed) { }
        public void SetRemoteCosmetics(int id, byte flags) { }
        public void ResetHandPoseCaches() { }
        public void ResetAppearanceCaches() { }
    }

    private sealed class Inventory : IInventoryApi
    {
        internal int Count = 5;
        internal bool InventoryOpen = true;
        internal IReadOnlyList<GroundItem> PickupItems = Array.Empty<GroundItem>();
        internal IReadOnlyList<GroundItem> GroundItems = Array.Empty<GroundItem>();
        private Func<bool> _canGive, _canDrop;
        private Action<ShareableItem> _give, _drop;
        public bool TryGetSelectedShareableItem(out ShareableItem item)
        {
            item = Count > 0 ? new ShareableItem(7, 104, "energy bar", Count) : default;
            return InventoryOpen && Count > 0;
        }
        public IGameRegistration AddShareActions(Func<bool> canGive, Action<ShareableItem> give,
            Func<bool> canDrop, Action<ShareableItem> drop)
        {
            _canGive = canGive; _give = give; _canDrop = canDrop; _drop = drop;
            return new Registration();
        }
        public void SetGroundItems(IReadOnlyList<GroundItem> items) => GroundItems = items;
        public void DrawGroundItems() { }
        public uint SelectGroundItem(IReadOnlyList<GroundItem> nearbyItems)
        {
            PickupItems = nearbyItems.ToArray();
            return nearbyItems.Count == 0 ? 0 : nearbyItems[0].Id;
        }
        public bool IsShareableDefinition(int definitionId) => definitionId == 104;
        internal bool CanGive => _canGive();
        internal void TriggerGive()
        { Assert.True(_canGive()); Assert.True(TryGetSelectedShareableItem(out var item)); _give(item); }
        /// <summary>Fires the give action without checking it is offered (out-of-range race).</summary>
        internal void TriggerGiveUnchecked()
        { Assert.True(TryGetSelectedShareableItem(out var item)); _give(item); }
        internal void TriggerDrop()
        { Assert.True(_canDrop()); Assert.True(TryGetSelectedShareableItem(out var item)); _drop(item); }
        public bool CanAccept(int definitionId, int count, out string reason)
        { reason = ""; return definitionId == 104 && count > 0; }
        public bool TryRemove(ushort uniqueId, int definitionId, int count, out string reason)
            => TryRemoveAny(definitionId, count, out reason);
        public bool TryRemoveAny(int definitionId, int count, out string reason)
        {
            if (definitionId != 104 || count <= 0 || count > Count)
            { reason = "Not enough items."; return false; }
            Count -= count; reason = ""; return true;
        }
        public bool TryAdd(int definitionId, int count, out string reason)
        {
            if (definitionId != 104 || count <= 0)
            { reason = "Invalid item."; return false; }
            Count += count; reason = ""; return true;
        }

        private sealed class Registration : IGameRegistration
        {
            public string Id => "test.share-actions";
            public bool IsActive => true;
            public void Dispose() { }
        }
    }
}
