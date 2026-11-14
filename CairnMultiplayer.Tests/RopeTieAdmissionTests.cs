#nullable enable

using System.IO;
using System.Text;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class RopeTieAdmissionTests
{
    private const int Host = 4;
    private const int Guest = 7;

    [Fact]
    public void BothClearAllowsTheTie()
    {
        var decision = RopeTieAdmission.Decide(
            Host, Guest, NativePitonReport.Clear, NativePitonReport.Clear);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void TheHostAsPartnerBlocksBeforeAnyoneIsTied()
    {
        var decision = RopeTieAdmission.Decide(
            Guest, Host, NativePitonReport.Clear, NativePitonReport.Holding);
        Assert.False(decision.Allowed);
        Assert.Equal(Host, decision.MustRecallPlayerId);
        Assert.True(decision.PitonOut);
    }

    [Fact]
    public void TheGuestAsPartnerBlocksBeforeAnyoneIsTied()
    {
        var decision = RopeTieAdmission.Decide(
            Host, Guest, NativePitonReport.Clear, NativePitonReport.Holding);
        Assert.False(decision.Allowed);
        Assert.Equal(Guest, decision.MustRecallPlayerId);
        Assert.True(decision.PitonOut);
    }

    [Fact]
    public void AGuestWhoHasNotReportedIsNotClear()
    {
        var decision = RopeTieAdmission.Decide(
            Host, Guest, NativePitonReport.Clear, NativePitonReport.Unknown);
        Assert.False(decision.Allowed);
        Assert.Equal(Guest, decision.MustRecallPlayerId);
        Assert.False(decision.PitonOut);
    }

    [Fact]
    public void AReportIsForgottenWhenThePlayerLeaves()
    {
        var reports = new NativePitonReports();
        reports.Set(Guest, holding: true);
        Assert.Equal(NativePitonReport.Holding, reports.Report(Guest, isLocal: false, localHolding: false));

        reports.Forget(Guest);

        Assert.Equal(NativePitonReport.Unknown, reports.Report(Guest, isLocal: false, localHolding: false));
        var decision = RopeTieAdmission.Decide(
            Host, Guest, NativePitonReport.Clear,
            reports.Report(Guest, isLocal: false, localHolding: false));
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void TheHostReadsItsOwnRopeAndNotAStaleGuestSlot()
    {
        var reports = new NativePitonReports();
        reports.Set(Host, holding: true);
        Assert.Equal(NativePitonReport.Clear, reports.Report(Host, isLocal: true, localHolding: false));
    }

    [Fact]
    public void RopeClipRefusal_RoundTrip()
    {
        var sent = new ServerRopeClipRefusal { MustRecallPlayerId = Guest, PitonOut = true };
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            sent.Serialize(writer);
        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        var got = new ServerRopeClipRefusal();
        got.Deserialize(reader);
        Assert.Equal(Guest, got.MustRecallPlayerId);
        Assert.True(got.PitonOut);
        Assert.Equal(91, (int)PacketId.ServerRopeClipRefusal);
        Assert.Equal(21, (int)PacketId.ClientNativePiton);
    }
}
