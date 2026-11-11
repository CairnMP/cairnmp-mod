using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class LinkPoseTests
{
    [Fact]
    public void AFrameRootDoesNotOpenAClipThePresencePointsRefuse()
    {
        var presser = Window();
        var host = Window();
        // The bodies' presence points are 2 m apart. The capture roots are 0.4 m apart.
        Stand(presser, selfX: 0f, partnerX: 2f);
        Stand(host, selfX: 2f, partnerX: 0f);
        MoveGhost(presser, 0.4f);
        MoveGhost(host, 0f);

        Assert.True(presser.TryRefuseOutOfRangeClip(presser.LocalPlayerId, Partner, out var presserRefusal));
        Assert.Equal("2.0m apart", presserRefusal);
        Assert.True(host.TryRefuseOutOfRangeClip(Partner, host.LocalPlayerId, out var hostRefusal));
        Assert.Equal("2.0m apart", hostRefusal);
    }

    [Fact]
    public void MatchingPresencePointsAgreeAfterTheGhostRootsMove()
    {
        var presser = Window();
        var host = Window();
        Stand(presser, selfX: 0f, partnerX: 0.8f);
        Stand(host, selfX: 0.8f, partnerX: 0f);
        MoveGhost(presser, 5f);
        MoveGhost(host, -4f);

        Assert.False(presser.TryRefuseOutOfRangeClip(presser.LocalPlayerId, Partner, out _));
        Assert.False(host.TryRefuseOutOfRangeClip(Partner, host.LocalPlayerId, out _));
        Assert.True(presser.TryGetLinkPosition(Partner, out var x, out var y, out var z));
        Assert.Equal(0.8f, x);
        Assert.Equal(0f, y);
        Assert.Equal(0f, z);
        Assert.True(RopeRequests.WithinLinkRange(0f, 0f, 0f, x, y, z));
    }

    [Fact]
    public void AGhostRootWithoutPresenceIsNotAClipPoint()
    {
        var host = Window();
        host.SendPlayerState(0f, 0f, 0f, 0f, "Kami", PlayerState.InGame);
        host.ApplyRemotePresence(Partner, 0.2f, 0f, 0f, 0f, "Kami", PlayerState.Loading);
        var partner = host.RemotePlayers[Partner];
        partner.State = PlayerState.InGame;
        host.NoteRemoteFrameRoot(partner, 0.2f, 0f, 0f);

        Assert.True(host.TryRefuseOutOfRangeClip(Partner, host.LocalPlayerId, out var refusal));
        Assert.Equal($"no presence pose for {Partner}", refusal);
    }

    private const int Partner = 7;

    private static NetworkManager Window()
        => new NetworkManager(() => new List<(int a, int b)>());

    private static void Stand(NetworkManager window, float selfX, float partnerX)
    {
        window.SendPlayerState(selfX, 0f, 0f, 0f, "Kami", PlayerState.InGame);
        window.ApplyRemotePresence(Partner, partnerX, 0f, 0f, 0f, "Kami", PlayerState.InGame);
    }

    private static void MoveGhost(NetworkManager window, float x)
        => window.NoteRemoteFrameRoot(window.RemotePlayers[Partner], x, 0f, 0f);
}
