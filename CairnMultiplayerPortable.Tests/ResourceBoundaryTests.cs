using CairnMultiplayerMod.Internal.Networking.Authoritative;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

public sealed class ResourceBoundaryTests
{
    [Fact]
    public void CreationBudgetIsIndependentPerPeerAndRefills()
    {
        double now = 0;
        var budget = new PeerBudget(2, 1, () => now);
        Assert.True(budget.Take(1));
        Assert.True(budget.Take(1));
        Assert.False(budget.Take(1));
        Assert.True(budget.Take(2));
        now = 1;
        Assert.True(budget.Take(1));
        Assert.False(budget.Take(1));
    }

    [Fact]
    public void RopeRejectsUnknownTargetsSelfLinksAndOccupiedEndpoints()
    {
        var links = new[] { (1, 2) };
        bool Admitted(int id) => id is >= 1 and <= 4;
        Assert.False(RopeRequests.IsAllowed(3, 99, true, links, Admitted));
        Assert.False(RopeRequests.IsAllowed(3, 3, true, links, Admitted));
        Assert.False(RopeRequests.IsAllowed(3, 2, true, links, Admitted));
        Assert.False(RopeRequests.IsAllowed(1, 4, true, links, Admitted));
        Assert.True(RopeRequests.IsAllowed(3, 4, true, links, Admitted));
        Assert.True(RopeRequests.IsAllowed(1, 2, false, links, id => id == 1));
        Assert.False(RopeRequests.IsAllowed(3, 4, false, links, Admitted));
    }

    [Fact]
    public void ArchiveCopyStopsAtSnapshotLengthEvenIfSourceHasMoreBytes()
    {
        using var source = new MemoryStream(new byte[100000]);
        using var destination = new MemoryStream();
        CrashArchiveBuilder.CopyBounded(source, destination, 1234);
        Assert.Equal(1234, destination.Length);
        Assert.Equal(1234, source.Position);
    }

    [Fact]
    public void SessionLogRotatesDuringTheSessionAndPreservesLatestMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cairn-log-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "CairnMP-session-test.log");
            for (var i = 0; i < 55; i++) BoundedSessionLog.Append(path, new string('x', 500000));
            BoundedSessionLog.Append(path, "last-message");
            var files = new DirectoryInfo(directory).GetFiles();
            Assert.InRange(files.Length, 1, 10);
            Assert.All(files, file => Assert.True(file.Length <= BoundedSessionLog.MaxFileBytes));
            Assert.EndsWith("last-message", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
