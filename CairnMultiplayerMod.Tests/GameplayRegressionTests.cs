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

    [Fact]
    public void DyingAloneEndsTheRunAsItWouldInSolo()
    {
        var (_, _, game) = Create(new ReviveFeature());

        Assert.False(game.LifeImpl.ReportDeath());
        Assert.False(game.LifeImpl.IsDown);
    }

    [Fact]
    public void DyingWithATeammateStandingLeavesTheClimberDown()
    {
        var (_, _, game) = Create(new ReviveFeature());
        game.PlayersImpl.InGame = new[] { 2 };

        Assert.True(game.LifeImpl.ReportDeath());
        Assert.True(game.LifeImpl.IsDown);
        Assert.False(game.LifeImpl.RunEnded);
    }

    [Fact]
    public void ATeammateStandsTheDownedClimberBackUpOnPartialHealth()
    {
        var (runtime, host, game) = Create(new ReviveFeature());
        game.PlayersImpl.InGame = new[] { 2 };
        Assert.True(game.LifeImpl.ReportDeath());

        runtime.ExecuteHostCommand(2, Command("revive.request", new ReviveRequest(1)));

        Assert.Equal(0.3f, game.LifeImpl.RevivedAtRatio);
        Assert.False(game.LifeImpl.IsDown);
        host.Tick(FeaturePhase.Always);
        Assert.False(game.LifeImpl.RunEnded);
    }

    [Fact]
    public void NobodyComingInTimeEndsTheRun()
    {
        var (_, host, game) = Create(new ReviveFeature());
        game.PlayersImpl.InGame = new[] { 2 };
        Assert.True(game.LifeImpl.ReportDeath());

        game.TimeImpl.Now += 119f;
        host.Tick(FeaturePhase.Always);
        Assert.False(game.LifeImpl.RunEnded);

        game.TimeImpl.Now += 2f;
        host.Tick(FeaturePhase.Always);
        Assert.True(game.LifeImpl.RunEnded);
    }

    [Fact]
    public void TheRunEndsAsSoonAsTheLastStandingTeammateGoesDown()
    {
        var (runtime, host, game) = Create(new ReviveFeature());
        game.PlayersImpl.InGame = new[] { 2 };
        Assert.True(game.LifeImpl.ReportDeath());

        runtime.ExecuteHostCommand(2, Command("revive.downed", new DownedChanged(true)));
        host.Tick(FeaturePhase.Always);

        Assert.True(game.LifeImpl.RunEnded);
    }

    [Fact]
    public void RevivingCostsAHealingItemAndIsRefusedWithoutOne()
    {
        var (runtime, _, game) = Create(new ReviveFeature());
        runtime.ExecuteHostCommand(2, Command("revive.downed", new DownedChanged(true)));

        Assert.True(game.LifeImpl.CanRevive(2));
        game.LifeImpl.OnRevive(2);
        Assert.Equal(0, game.InventoryImpl.HealingItems);

        Assert.False(game.LifeImpl.CanRevive(2));
    }

    [Fact]
    public void ADownedClimberCannotRescueAnybody()
    {
        var (_, _, game) = Create(new ReviveFeature());
        game.PlayersImpl.InGame = new[] { 2 };
        Assert.True(game.LifeImpl.ReportDeath());

        Assert.False(game.LifeImpl.CanRevive(2));
        Assert.Equal(1, game.InventoryImpl.HealingItems);
    }

    [Fact]
    public void FreeSoloKeepsAFallenClimberInTheWorldWithNoWayBack()
    {
        var (_, host, game) = Create(new ReviveFeature(), MultiplayerMode.FreeSolo);
        game.PlayersImpl.InGame = new[] { 2 };

        Assert.True(game.LifeImpl.ReportDeath());
        Assert.True(game.LifeImpl.IsDown);

        // No countdown, no rescue prompt: the run does not end just because time passed.
        game.TimeImpl.Now += 600f;
        host.Tick(FeaturePhase.Always);
        Assert.False(game.LifeImpl.RunEnded);
        Assert.False(game.LifeImpl.CanRevive(2));
    }

    [Fact]
    public void AWipedOutFreeSoloTeamDoesNotWatchForever()
    {
        var (runtime, host, game) = Create(new ReviveFeature(), MultiplayerMode.FreeSolo);
        game.PlayersImpl.InGame = new[] { 2 };
        Assert.True(game.LifeImpl.ReportDeath());

        runtime.ExecuteHostCommand(2, Command("revive.downed", new DownedChanged(true)));
        host.Tick(FeaturePhase.Always);

        Assert.True(game.LifeImpl.RunEnded);
    }

    [Fact]
    public void CampCallsTheFallenBackAndSitsThemByTheFire()
    {
        var (runtime, host, game) = Create(new ReviveFeature(), MultiplayerMode.FreeSolo);
        game.PlayersImpl.InGame = new[] { 2 };
        Assert.True(game.LifeImpl.ReportDeath());

        runtime.ExecuteHostCommand(2, Command("revive.recall",
            new BivouacRecall(new WorldPosition(10, 20, 30))));

        Assert.Equal(0.5f, game.LifeImpl.RevivedAtRatio);
        Assert.False(game.LifeImpl.IsDown);

        host.Tick(FeaturePhase.Always);
        Assert.True(game.WorldImpl.TeleportedTo.HasValue);
        Assert.Equal(20, game.WorldImpl.TeleportedTo.Value.Y);
    }

    [Fact]
    public void ARecalledClimberIsStillMovedWhenTheFirstAttemptIsRefused()
    {
        var (runtime, host, game) = Create(new ReviveFeature(), MultiplayerMode.FreeSolo);
        game.PlayersImpl.InGame = new[] { 2 };
        Assert.True(game.LifeImpl.ReportDeath());
        game.WorldImpl.TeleportAllowed = false;

        runtime.ExecuteHostCommand(2, Command("revive.recall",
            new BivouacRecall(new WorldPosition(1, 2, 3))));
        host.Tick(FeaturePhase.Always);
        Assert.Null(game.WorldImpl.TeleportedTo);

        // The climber is on their feet a frame later and the pending move lands.
        game.WorldImpl.TeleportAllowed = true;
        host.Tick(FeaturePhase.Always);
        Assert.True(game.WorldImpl.TeleportedTo.HasValue);
    }

    [Fact]
    public void TheCampPromptOnlyShowsWhereItCanBeUsed()
    {
        var (runtime, host, game) = Create(new ReviveFeature(), MultiplayerMode.FreeSolo);
        game.PlayersImpl.InGame = new[] { 2 };
        runtime.ExecuteHostCommand(2, Command("revive.downed", new DownedChanged(true)));

        // Outside a camp there is nothing to offer, however many climbers are down.
        host.Tick(FeaturePhase.Always);
        Assert.False(game.HudImpl.Messages.ContainsKey("revive.recall"));

        game.StateImpl.InBivouac = true;
        host.Tick(FeaturePhase.Always);
        Assert.Contains("revive.recall", game.HudImpl.Messages.Keys);

        // And it goes away again when everyone is back on their feet.
        runtime.ExecuteHostCommand(2, Command("revive.downed", new DownedChanged(false)));
        host.Tick(FeaturePhase.Always);
        Assert.False(game.HudImpl.Messages.ContainsKey("revive.recall"));
    }

    [Fact]
    public void DyingInFreeSoloOpensTheSpectatorSeat()
    {
        var (_, host, game) = Create(new SpectatorFeature(), MultiplayerMode.FreeSolo);
        game.LifeImpl.IsDown = true;

        host.Tick(FeaturePhase.Always);

        Assert.True(game.SpectatorImpl.IsActive);
        Assert.False(game.SpectatorImpl.IsFollowing);
    }

    [Fact]
    public void ASpectatorCanLockOntoAClimberAndBackOff()
    {
        var (_, host, game) = Create(new SpectatorFeature(), MultiplayerMode.FreeSolo);
        game.LifeImpl.IsDown = true;
        game.PlayersImpl.InGame = new[] { 2 };
        game.SpectatorImpl.Watchable.Add(2);
        host.Tick(FeaturePhase.Always);

        game.InputImpl.Press(GameKey.F);
        host.Tick(FeaturePhase.Always);
        Assert.Equal(2, game.SpectatorImpl.FollowedPlayerId);

        game.InputImpl.Press(GameKey.F);
        host.Tick(FeaturePhase.Always);
        Assert.False(game.SpectatorImpl.IsFollowing);
    }

    [Fact]
    public void StandingBackUpGivesTheViewBack()
    {
        var (_, host, game) = Create(new SpectatorFeature(), MultiplayerMode.FreeSolo);
        game.LifeImpl.IsDown = true;
        host.Tick(FeaturePhase.Always);
        Assert.True(game.SpectatorImpl.IsActive);

        game.LifeImpl.IsDown = false;
        host.Tick(FeaturePhase.Always);

        Assert.False(game.SpectatorImpl.IsActive);
        Assert.Equal(1, game.SpectatorImpl.Left);
    }

    [Fact]
    public void ARopeTeamLobbyNeverTakesTheCameraAway()
    {
        var (_, host, game) = Create(new SpectatorFeature());
        game.LifeImpl.IsDown = true;

        host.Tick(FeaturePhase.Always);

        Assert.False(game.SpectatorImpl.IsActive);
    }

    [Fact]
    public void ARaceRanksClimbersByTheHeightTheyGain()
    {
        var (runtime, host, game) = Create(new RaceFeature(), MultiplayerMode.Race);

        // The host fires the gun, and the start line is wherever each climber stands.
        game.PlayersImpl.LocalY = 100f;
        host.Tick(FeaturePhase.Always);

        game.PlayersImpl.LocalY = 140f;
        host.Tick(FeaturePhase.Always);
        runtime.ExecuteHostCommand(2, Command("race.progress", new RaceProgress(70f, false)));
        host.Tick(FeaturePhase.Always);

        Assert.Equal(2, game.HudImpl.Standings.Count);
        Assert.Equal("Guest", game.HudImpl.Standings[0].Name);
        Assert.Equal("70 m", game.HudImpl.Standings[0].Detail);
        Assert.Equal("40 m", game.HudImpl.Standings[1].Detail);
        Assert.True(game.HudImpl.Standings[1].IsLocal);
    }

    [Fact]
    public void LosingHeightNeverCostsAClimberTheirBest()
    {
        var (_, host, game) = Create(new RaceFeature(), MultiplayerMode.Race);
        game.PlayersImpl.LocalY = 0f;
        host.Tick(FeaturePhase.Always);

        game.PlayersImpl.LocalY = 60f;
        host.Tick(FeaturePhase.Always);
        game.PlayersImpl.LocalY = 10f;
        host.Tick(FeaturePhase.Always);

        Assert.Equal("60 m", game.HudImpl.Standings[0].Detail);
    }

    [Fact]
    public void AClimberWhoIsOutSinksBelowAnyoneStillMoving()
    {
        var (runtime, host, game) = Create(new RaceFeature(), MultiplayerMode.Race);
        game.PlayersImpl.LocalY = 0f;
        host.Tick(FeaturePhase.Always);

        game.PlayersImpl.LocalY = 200f;
        game.LifeImpl.IsDown = true;
        host.Tick(FeaturePhase.Always);
        runtime.ExecuteHostCommand(2, Command("race.progress", new RaceProgress(5f, false)));
        host.Tick(FeaturePhase.Always);

        // Two hundred metres of climbing does not help a climber who is no longer in the race.
        Assert.Equal("Guest", game.HudImpl.Standings[0].Name);
        Assert.True(game.HudImpl.Standings[1].IsOut);
    }

    [Fact]
    public void ToppingOutFinishesTheRace()
    {
        var (_, host, game) = Create(new RaceFeature(), MultiplayerMode.Race);
        game.PlayersImpl.LocalY = 0f;
        host.Tick(FeaturePhase.Always);

        game.StateImpl.Summit = true;
        host.Tick(FeaturePhase.Always);

        Assert.Equal("RACE - FINISHED", game.HudImpl.StandingsTitle);
        Assert.Contains("*", game.HudImpl.Standings[0].Name);
    }

    [Fact]
    public void TheFirstClimberToTopOutTakesIt()
    {
        var (runtime, host, game) = Create(new RaceFeature(), MultiplayerMode.Race);
        game.PlayersImpl.LocalY = 0f;
        host.Tick(FeaturePhase.Always);

        runtime.ExecuteHostCommand(2, Command("race.won", new RaceWon(900f)));
        game.StateImpl.Summit = true;
        host.Tick(FeaturePhase.Always);

        Assert.Equal("RACE - FINISHED", game.HudImpl.StandingsTitle);
        Assert.Contains(game.HudImpl.Standings, row => row.Name.Contains("Guest") && row.Name.Contains("*"));
        Assert.DoesNotContain(game.HudImpl.Standings, row => row.IsLocal && row.Name.Contains("*"));
    }

    [Fact]
    public void NoStandingsOutsideARaceLobby()
    {
        var (_, host, game) = Create(new RaceFeature(), MultiplayerMode.FreeSolo);
        game.PlayersImpl.LocalY = 50f;

        host.Tick(FeaturePhase.Always);

        Assert.Empty(game.HudImpl.Standings);
    }

    [Fact]
    public void ASpectatorCanStillPointAtTheFace()
    {
        var (_, host, game) = Create(new PingFeature(), MultiplayerMode.FreeSolo);
        game.SpectatorImpl.Enter();
        game.InputImpl.Press(GameInputAction.PrimaryPointer);

        host.Tick(FeaturePhase.Always);

        Assert.Single(game.WorldImpl.Pings);
    }

    [Fact]
    public void APartnerComingOffTheWallShakesTheRopeTeam()
    {
        var (runtime, host, game) = Create(new RopeShakeFeature());
        game.PlayersImpl.Roped.Add(2);

        runtime.ExecuteHostCommand(2, Command("rope-shake.fell", new PartnerFell()));

        Assert.Equal(1, game.PlayersImpl.Shaken);
    }

    [Fact]
    public void AFallOnAnotherRopeIsNotFelt()
    {
        var (runtime, _, game) = Create(new RopeShakeFeature());

        runtime.ExecuteHostCommand(2, Command("rope-shake.fell", new PartnerFell()));

        Assert.Equal(0, game.PlayersImpl.Shaken);
    }

    [Fact]
    public void ALongFallIsOneShakeNotOnePerFrame()
    {
        var (_, host, game) = Create(new RopeShakeFeature());
        game.PlayersImpl.Falling = true;

        host.Tick(FeaturePhase.Gameplay);
        host.Tick(FeaturePhase.Gameplay);
        host.Tick(FeaturePhase.Gameplay);

        // The host commits its own broadcast locally, and the feature filters itself out, so
        // what is observable here is that a second send never happens while still falling.
        game.PlayersImpl.Falling = false;
        host.Tick(FeaturePhase.Gameplay);
        Assert.Equal(0, game.PlayersImpl.Shaken);
    }

    [Fact]
    public void ARationOpenedOnTheRopeFeedsThePartners()
    {
        var (runtime, _, game) = Create(new SharedRationsFeature());
        game.PlayersImpl.Roped.Add(2);

        runtime.ExecuteHostCommand(2, Command("rations.shared", new RationShared(104)));

        Assert.Equal(new[] { 104 }, game.InventoryImpl.SharedApplied);
    }

    [Fact]
    public void ARationIsNotSharedAcrossRopes()
    {
        var (runtime, _, game) = Create(new SharedRationsFeature());

        runtime.ExecuteHostCommand(2, Command("rations.shared", new RationShared(104)));

        Assert.Empty(game.InventoryImpl.SharedApplied);
    }

    [Fact]
    public void RacersDoNotHandRivalsTheirWater()
    {
        var (_, _, game) = Create(new SharedRationsFeature(), MultiplayerMode.Race);
        game.PlayersImpl.InGame = new[] { 2 };
        game.PlayersImpl.Roped.Add(2);

        game.InventoryImpl.ItemUsed(104);

        Assert.Empty(game.InventoryImpl.SharedApplied);
    }

    [Fact]
    public void AMarkLeftOnTheFaceStaysThere()
    {
        var (runtime, _, game) = Create(new TrailMarksFeature());

        runtime.ExecuteHostCommand(2, Command("marks.edit",
            new TrailMarkEdit(new WorldPosition(10, 20, 30))));

        Assert.Single(game.WorldImpl.Marks[2]);
        Assert.Equal(20, game.WorldImpl.Marks[2][0].Y);
    }

    [Fact]
    public void MarkingTheSameSpotTwiceTakesItBack()
    {
        var (runtime, _, game) = Create(new TrailMarksFeature());
        var spot = new WorldPosition(10, 20, 30);

        runtime.ExecuteHostCommand(2, Command("marks.edit", new TrailMarkEdit(spot)));
        runtime.ExecuteHostCommand(2, Command("marks.edit", new TrailMarkEdit(spot)));

        Assert.Empty(game.WorldImpl.Marks[2]);
    }

    [Fact]
    public void AFullSetDropsItsOldestMarkRatherThanRefusing()
    {
        var (runtime, _, game) = Create(new TrailMarksFeature());

        for (var index = 0; index < 12; index++)
            runtime.ExecuteHostCommand(2, Command("marks.edit",
                new TrailMarkEdit(new WorldPosition(index * 100, 0, 0))));

        var marks = game.WorldImpl.Marks[2];
        Assert.Equal(8, marks.Count);
        // The first four were pushed out, so the oldest kept is the fifth placed.
        Assert.Equal(400, marks[0].X);
    }

    [Fact]
    public void AMarkSetSurvivesTheLossOfItsOwner()
    {
        var (runtime, _, game) = Create(new TrailMarksFeature());
        runtime.ExecuteHostCommand(2, Command("marks.edit",
            new TrailMarkEdit(new WorldPosition(1, 2, 3))));

        runtime.NotifyPlayerLeft(new CairnMultiplayer.Api.MultiplayerPlayer(2, "Guest", false, false));

        Assert.Empty(game.WorldImpl.Marks[2]);
    }

    [Fact]
    public void APartnerHangingOnYourRopeIsWeightYouCarry()
    {
        var (runtime, host, game) = Create(new ReviveFeature());
        game.PlayersImpl.InGame = new[] { 2 };
        game.PlayersImpl.Roped.Add(2);
        runtime.ExecuteHostCommand(2, Command("revive.downed", new DownedChanged(true)));

        host.Tick(FeaturePhase.Always);

        // Two per second, one carried partner, one second of frame time.
        Assert.Equal(2f, game.LifeImpl.Exhausted);
    }

    [Fact]
    public void AClimberOnAnotherRopeCostsYouNothing()
    {
        var (runtime, host, game) = Create(new ReviveFeature());
        game.PlayersImpl.InGame = new[] { 2 };
        runtime.ExecuteHostCommand(2, Command("revive.downed", new DownedChanged(true)));

        host.Tick(FeaturePhase.Always);

        Assert.Equal(0f, game.LifeImpl.Exhausted);
    }

    [Fact]
    public void TheRecapRanksTheRopeTeamByHeightGained()
    {
        var (runtime, host, game) = Create(new AscentLogFeature());
        game.PlayersImpl.LocalY = 0f;
        host.Tick(FeaturePhase.Always);
        game.PlayersImpl.LocalY = 40f;
        host.Tick(FeaturePhase.Always);

        runtime.ExecuteHostCommand(2, Command("ascent.tally", new AscentTally(120f, 5, 2, 1)));
        game.ChatImpl.Run("recap");

        Assert.Equal(2, game.HudImpl.Standings.Count);
        Assert.Equal("Guest", game.HudImpl.Standings[0].Name);
        Assert.Contains("120 m", game.HudImpl.Standings[0].Detail);
        Assert.Contains("40 m", game.HudImpl.Standings[1].Detail);
    }

    [Fact]
    public void TheRecapCountsFallsAndTimesDown()
    {
        var (_, host, game) = Create(new AscentLogFeature());
        game.PlayersImpl.LocalY = 0f;
        host.Tick(FeaturePhase.Always);

        game.PlayersImpl.Falling = true;
        host.Tick(FeaturePhase.Always);
        game.PlayersImpl.Falling = false;
        host.Tick(FeaturePhase.Always);
        game.LifeImpl.IsDown = true;
        host.Tick(FeaturePhase.Always);

        game.ChatImpl.Run("recap");

        Assert.Contains("1f 1d", game.HudImpl.Standings[0].Detail);
    }

    [Fact]
    public void TheRecapStandsAsideDuringARace()
    {
        var (_, _, game) = Create(new AscentLogFeature(), MultiplayerMode.Race);

        game.ChatImpl.Run("recap");

        Assert.Empty(game.HudImpl.Standings);
    }

    [Fact]
    public void EveryClimbersRouteIsRecordedWhetherOrNotItIsShown()
    {
        var (_, host, game) = Create(new ClimbTrailsFeature());
        game.PlayersImpl.InGame = new[] { 2 };

        host.Tick(FeaturePhase.Always);

        Assert.False(game.WorldImpl.TrailsVisible);
        Assert.Contains(1, game.WorldImpl.Trails.Keys);
        Assert.Contains(2, game.WorldImpl.Trails.Keys);
    }

    [Fact]
    public void TrailsToggleOnCommandAndOnTheKey()
    {
        var (_, host, game) = Create(new ClimbTrailsFeature());

        game.ChatImpl.Run("trails");
        Assert.True(game.WorldImpl.TrailsVisible);

        game.InputImpl.Press(GameKey.T);
        host.Tick(FeaturePhase.Always);
        Assert.False(game.WorldImpl.TrailsVisible);
    }

    [Fact]
    public void ALeavingClimberTakesTheirTrailWithThem()
    {
        var (runtime, host, game) = Create(new ClimbTrailsFeature());
        game.PlayersImpl.InGame = new[] { 2 };
        host.Tick(FeaturePhase.Always);

        runtime.NotifyPlayerLeft(new CairnMultiplayer.Api.MultiplayerPlayer(2, "Guest", false, false));

        Assert.DoesNotContain(2, game.WorldImpl.Trails.Keys);
    }

    private static (ExtensionRuntime runtime, FeatureHost host, Game game) Create(
        MultiplayerFeature feature, MultiplayerMode mode = MultiplayerMode.RopeTeam)
    {
        var runtime = new ExtensionRuntime();
        var game = new Game();
        var host = new FeatureHost(runtime, game, rules: () => MultiplayerModes.RulesFor(mode));
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
        public int LocalPlayerId => 1;
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
        internal readonly Life LifeImpl = new();
        internal readonly FrameTime TimeImpl = new();
        internal readonly State StateImpl = new();
        internal readonly World WorldImpl = new();
        internal readonly Keys InputImpl = new();
        internal readonly Hud HudImpl = new();
        internal readonly Spectator SpectatorImpl = new();
        public IMainMenuApi MainMenu => UnavailableGameApi.Instance.MainMenu;
        public IGameStateApi State => StateImpl;
        public IGameTimeApi Time => TimeImpl;
        public IGameInputApi Input => InputImpl;
        public IGameHudApi Hud => HudImpl;
        public ILifeApi Life => LifeImpl;
        public ISpectatorApi Spectator => SpectatorImpl;
        public IChatApi Chat => ChatImpl;
        public IInventoryApi Inventory => InventoryImpl;
        public IClockApi Clock => ClockImpl;
        public IPlayersApi Players => PlayersImpl;
        public IWeatherApi Weather => UnavailableGameApi.Instance.Weather;
        public IWorldApi World => WorldImpl;
    }

    private sealed class State : IGameStateApi
    {
        internal bool InBivouac;
        public PlayerState LocalPlayerState => PlayerState.InGame;
        public bool IsLocalPlayerInGame => true;
        public bool IsLocalPlayerInBivouac => InBivouac;
        internal bool Summit;
        public bool HasReachedSummit => Summit;
    }

    private sealed class Spectator : ISpectatorApi
    {
        internal int Entered, Left;
        public bool IsActive { get; private set; }
        public bool IsFollowing => FollowedPlayerId >= 0;
        public int FollowedPlayerId { get; private set; } = -1;
        internal readonly HashSet<int> Watchable = new();
        public bool Enter() { IsActive = true; Entered++; return true; }
        public void Leave() { IsActive = false; FollowedPlayerId = -1; Left++; }
        public void FreeLook() => FollowedPlayerId = -1;
        public bool Follow(int playerId)
        {
            if (!Watchable.Contains(playerId)) return false;
            FollowedPlayerId = playerId;
            return true;
        }
        public void Tick(bool acceptInput) { }
    }

    private sealed class World : IWorldApi
    {
        internal WorldPosition? TeleportedTo;
        internal bool TeleportAllowed = true;
        internal bool FreeCamera;
        internal readonly List<WorldPosition> Pings = new();
        public bool IsFreeCameraActive => FreeCamera;
        public bool TryGetAimPoint(out WorldPosition position)
        { position = new WorldPosition(1, 2, 3); return true; }
        public void SpawnPing(int ownerId, WorldPosition position) => Pings.Add(position);
        public void TickPings() { }
        public void DrawPings() { }
        public void ClearPings() { }
        internal readonly Dictionary<int, List<WorldPosition>> Trails = new();
        internal bool TrailsVisible;
        public bool AreTrailsVisible => TrailsVisible;
        public void RecordTrailPoint(int playerId, WorldPosition position)
        {
            if (!Trails.TryGetValue(playerId, out var points))
                Trails[playerId] = points = new List<WorldPosition>();
            points.Add(position);
        }
        public void SetTrailsVisible(bool visible) => TrailsVisible = visible;
        public void ForgetTrail(int playerId) => Trails.Remove(playerId);
        public void ClearTrails() => Trails.Clear();

        internal readonly Dictionary<int, List<WorldPosition>> Marks = new();
        public void SetTrailMarks(int playerId, IReadOnlyList<WorldPosition> positions)
            => Marks[playerId] = new List<WorldPosition>(positions ?? Array.Empty<WorldPosition>());
        public void ClearTrailMarks() => Marks.Clear();
        public bool TryTeleportLocalPlayer(WorldPosition position, float yawDegrees, out string refusedReason)
        {
            refusedReason = TeleportAllowed ? "" : "not walking";
            if (!TeleportAllowed) return false;
            TeleportedTo = position;
            return true;
        }
    }

    private sealed class Keys : IGameInputApi
    {
        private readonly HashSet<GameKey> _pressed = new();
        private readonly HashSet<GameInputAction> _actions = new();
        public bool IsKeyboardCaptured => false;
        public bool IsPauseMenuActive => false;
        public bool WasPressed(GameInputAction action) => _actions.Remove(action);
        public bool WasKeyPressed(GameKey key) => _pressed.Remove(key);
        internal void Press(GameKey key) => _pressed.Add(key);
        internal void Press(GameInputAction action) => _actions.Add(action);
    }

    private sealed class Hud : IGameHudApi
    {
        internal readonly Dictionary<string, string> Messages = new();
        public void ShowMessage(string id, string text, float durationSeconds) => Messages[id] = text;
        public void HideMessage(string id) => Messages.Remove(id);
        internal string StandingsTitle;
        internal readonly List<StandingRow> Standings = new();
        public void ShowStandings(string title, IReadOnlyList<StandingRow> rows)
        {
            StandingsTitle = title;
            Standings.Clear();
            if (rows != null) Standings.AddRange(rows);
        }
        public void HideStandings() { StandingsTitle = null; Standings.Clear(); }
    }

    private sealed class FrameTime : IGameTimeApi
    {
        internal float Now = 1;
        public float UnscaledTime => Now;
        public float UnscaledDeltaTime => 1;
        public bool IsPaused => false;
    }

    /// <summary>
    /// Stands in for Cairn's death machinery: records the hold the feature installs, lets a
    /// test trigger the death the game would have reported, and remembers the outcome.
    /// </summary>
    private sealed class Life : ILifeApi
    {
        private Func<bool> _keepHolding;
        private Action _onWentDown;
        internal Func<int, bool> CanRevive;
        internal Action<int> OnRevive;
        internal bool IsDown;
        internal bool RunEnded;
        internal float RevivedAtRatio = -1;

        public bool IsLocalPlayerDown => IsDown;

        public IGameRegistration HoldBackDeathScreen(Func<bool> keepHolding, Action onWentDown)
        {
            _keepHolding = keepHolding;
            _onWentDown = onWentDown;
            return new Registration();
        }

        public void EndLocalPlayer()
        {
            if (!IsDown) return;
            RunEnded = true;
            IsDown = false;
        }

        internal float Exhausted;
        public void ExhaustLocalPlayer(float amount) => Exhausted += amount;
        public float FallenPartnerStaminaCost(float fallback) => 2f;

        public bool ReviveLocalPlayer(float healthRatio)
        {
            if (!IsDown) return false;
            RevivedAtRatio = healthRatio;
            IsDown = false;
            return true;
        }

        public IGameRegistration AddRevivePrompt(Func<int, bool> canRevive, Action<int> onRevive)
        {
            CanRevive = canRevive;
            OnRevive = onRevive;
            return new Registration();
        }

        /// <summary>The game just decided the local climber died. True when it was held back.</summary>
        internal bool ReportDeath()
        {
            if (!_keepHolding()) return false;
            IsDown = true;
            _onWentDown();
            return true;
        }

        private sealed class Registration : IGameRegistration
        {
            public string Id => "test.life";
            public bool IsActive => true;
            public void Dispose() { }
        }
    }

    private sealed class Clock : IClockApi
    {
        internal bool Frozen;
        public bool TryGetDayTime(out float value) { value = .5f; return true; }
        public bool TryGetLocalSleep(out bool value) { value = true; return true; }
        public bool Freeze(float value) { Frozen = true; return true; }
        public bool Unfreeze() { Frozen = false; return true; }
        // Deliberately leaves Frozen alone: a scene change forgets our ownership of the
        // freeze without releasing it on the game.
        public void OnSceneChanged() { }
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
        private readonly Dictionary<string, Action<string>> _commands = new(StringComparer.OrdinalIgnoreCase);
        public IGameRegistration AddCommand(string name, string usage, string description, Action<string> execute)
        {
            _commands[name] = execute;
            return UnavailableGameApi.Instance.Chat.AddCommand(name, usage, description, execute);
        }

        /// <summary>Runs a command the way the chat router would.</summary>
        internal void Run(string name, string arguments = "")
        {
            Assert.True(_commands.TryGetValue(name, out var execute), $"No /{name} command registered.");
            execute(arguments);
        }
        public void AddRemoteLine(string name, string message) { Name = name; Message = message; }
        public void AddSystemLine(string text) => SystemLines.Add(text);
        public void Tick() { }
        public void Draw() { }
        public void ForceClose() { }
    }

    private sealed class Players : IPlayersApi
    {
        internal float TargetX = 2;
        internal IReadOnlyList<int> InGame = Array.Empty<int>();
        internal float LocalY;
        public IReadOnlyList<int> RemotePlayersInGame => InGame;
        public IReadOnlyList<int> RemoteSleepParticipants => new[] { 2 };
        public bool TryGetLocation(int playerId, out PlayerLocation location)
        {
            location = playerId switch
            {
                1 => new PlayerLocation(0, LocalY, 0, "Test", PlayerState.InGame),
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
        internal bool Falling;
        internal readonly HashSet<int> Roped = new();
        internal int Shaken;
        public bool IsLocalPlayerFalling => Falling;
        public bool IsRopedToLocalPlayer(int playerId) => Roped.Contains(playerId);
        public bool ShakeLocalClimberGrip() { Shaken++; return true; }
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

        internal Action<int> ItemUsed;
        internal readonly List<int> SharedApplied = new();
        public IGameRegistration AddItemUsedListener(Action<int> onUsed)
        { ItemUsed = onUsed; return new Registration(); }
        public bool ApplySharedConsumable(int definitionId)
        { SharedApplied.Add(definitionId); return true; }

        internal int HealingItems = 1;
        public bool HasHealingItem => HealingItems > 0;
        public bool TryConsumeHealingItem(out string itemName)
        {
            itemName = null;
            if (HealingItems <= 0) return false;
            HealingItems--;
            itemName = "herbal tea";
            return true;
        }

        private sealed class Registration : IGameRegistration
        {
            public string Id => "test.share-actions";
            public bool IsActive => true;
            public void Dispose() { }
        }
    }
}
