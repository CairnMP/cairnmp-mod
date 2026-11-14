#nullable enable

using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class EmptyUntieRulesTests
{
    [Fact]
    public void AChosenTieWithNoAnchorStaysArmedAfterUntie()
    {
        Reset();
        TeamAnchorSession.Bind(() => true);
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.True(TeamAnchorSession.RulesArmed);
        Assert.False(TeamAnchorSession.AllowsPlacement);
    }

    [Fact]
    public void AnEchoedUnclipDoesNothingWhenThePairNeverOpened()
    {
        Reset();
        TeamAnchorSession.Bind(() => true);
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);
        Assert.True(TeamAnchorSession.RulesArmed);

        TeamAnchorSession.OnClip(4, 7, 4, false);

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.Equal(0, TeamAnchorSession.Count);
        Assert.True(TeamAnchorSession.RulesArmed);
    }

    private static void Reset()
    {
        RopeLinkState.Clear();
        TeamAnchorSession.Clear();
        TeamAnchorSession.DisarmRules();
        TeamAnchorSession.DiscardRelease();
        TeamAnchorSession.Bind(() => false);
        TopAnchorSession.Reset();
        PartnerFallSession.Reset();
    }
}
