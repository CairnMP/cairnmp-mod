using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayPanelNavigationTests
{
    [Fact]
    public void PendingRecoveryCanBeEndedWithoutAcceptingOrSelectingAnotherPartner()
    {
        var navigation = new BelayPanelNavigation();
        var recovery = new BelayPanelView { Phase = BelayPanelPhase.Recovering, CanAccept = true,
            CanOffer = true, CanRequest = true, Partners = new[] { new BelayPartnerRow(9, "Other", 1) } };
        foreach (var key in new[] { BelayPanelShortcut.Accept, BelayPanelShortcut.Offer, BelayPanelShortcut.Request,
            BelayPanelShortcut.Next, BelayPanelShortcut.Previous })
            Assert.False(navigation.Observe(key, true, true, recovery, out _, out _));
        Assert.True(navigation.Observe(BelayPanelShortcut.Decline, true, true, recovery, out var action, out var target));
        Assert.Equal(BelayPanelAction.Cancel, action);
        Assert.Equal(-1, target);
        Assert.False(navigation.Observe(BelayPanelShortcut.Decline, true, true, recovery, out _, out _));
    }

    [Fact]
    public void SwapShortcutsAcceptOrCancelOnlyTheRoleProposal()
    {
        var navigation = new BelayPanelNavigation();
        var invitation = new BelayPanelView { Phase = BelayPanelPhase.SwapInvitation, CanAccept = true };
        Assert.True(navigation.Observe(BelayPanelShortcut.Accept, true, true, invitation, out var action, out _));
        Assert.Equal(BelayPanelAction.AcceptSwap, action);
        Assert.True(navigation.Observe(BelayPanelShortcut.Decline, true, true,
            new() { Phase = BelayPanelPhase.SwapPreparing }, out action, out _));
        Assert.Equal(BelayPanelAction.CancelSwap, action);
        navigation.Observe(BelayPanelShortcut.Accept, false, true, invitation, out _, out _);
        Assert.False(navigation.Observe(BelayPanelShortcut.Accept, true, true,
            new() { Phase = BelayPanelPhase.SwapPreparing }, out _, out _));
    }
    private static BelayPanelView Partners(params BelayPartnerRow[] rows)
        => new() { Phase = BelayPanelPhase.Partners, Partners = rows, CanOffer = true, CanRequest = true };

    [Fact]
    public void ReorderingPartnersByDistanceDoesNotRedirectTheInvitation()
    {
        var navigation = new BelayPanelNavigation();
        navigation.Update(Partners(new(2, "A", 1), new(3, "B", 2)));
        var refreshed = Partners(new(3, "B", .5f), new(2, "A", 1));
        navigation.Update(refreshed);
        Assert.True(navigation.Observe(BelayPanelShortcut.Offer, true, true, refreshed, out var action, out var target));
        Assert.Equal(BelayPanelAction.Offer, action); Assert.Equal(2, target);
    }

    [Fact]
    public void NavigationSelectsAnotherPartnerAndSuppressesRepeatedKeyDowns()
    {
        var navigation = new BelayPanelNavigation();
        var view = Partners(new(2, "A", 1), new(3, "B", 2));
        navigation.Update(view);
        navigation.Observe(BelayPanelShortcut.Next, true, true, view, out _, out _);
        navigation.Observe(BelayPanelShortcut.Next, true, true, view, out _, out _);
        Assert.Equal(3, navigation.SelectedPartner);
        Assert.True(navigation.Observe(BelayPanelShortcut.Request, true, true, view, out var action, out var target));
        Assert.Equal(BelayPanelAction.Request, action); Assert.Equal(3, target);
        Assert.False(navigation.Observe(BelayPanelShortcut.Request, true, true, view, out _, out _));
    }

    [Fact]
    public void AcceptHeldBeforeTheInvitationCannotAcceptItWithoutARelease()
    {
        var navigation = new BelayPanelNavigation();
        var invitation = new BelayPanelView { Phase = BelayPanelPhase.Invitation, CanAccept = true };
        Assert.False(navigation.Observe(BelayPanelShortcut.Accept, true, false, Partners(), out _, out _));
        Assert.False(navigation.Observe(BelayPanelShortcut.Accept, true, true, invitation, out _, out _));
        navigation.Observe(BelayPanelShortcut.Accept, false, true, invitation, out _, out _);
        Assert.True(navigation.Observe(BelayPanelShortcut.Accept, true, true, invitation, out var action, out _));
        Assert.Equal(BelayPanelAction.Accept, action);
    }

    [Fact]
    public void DeclineDoesNotBecomeAnActiveSessionEndAfterTheViewChanges()
    {
        var navigation = new BelayPanelNavigation();
        Assert.True(navigation.Observe(BelayPanelShortcut.Decline, true, true,
            new() { Phase = BelayPanelPhase.Invitation }, out var action, out _));
        Assert.Equal(BelayPanelAction.Decline, action);
        navigation.Observe(BelayPanelShortcut.Decline, false, true, new() { Phase = BelayPanelPhase.Active }, out _, out _);
        Assert.False(navigation.Observe(BelayPanelShortcut.Decline, true, true,
            new() { Phase = BelayPanelPhase.Active }, out _, out _));
    }

    [Fact]
    public void UnavailablePartnerActionsCannotBeSentByKeyboard()
    {
        var navigation = new BelayPanelNavigation();
        var view = Partners(new BelayPartnerRow(2, "A", 50, CanRequest: false));
        Assert.False(navigation.Observe(BelayPanelShortcut.Request, true, true, view, out _, out _));
        Assert.True(navigation.Observe(BelayPanelShortcut.Offer, true, true, view, out _, out _));
        navigation.Update(Partners());
        navigation.Observe(BelayPanelShortcut.Offer, false, true, Partners(), out _, out _);
        Assert.False(navigation.Observe(BelayPanelShortcut.Offer, true, true, Partners(), out _, out _));
    }
}
