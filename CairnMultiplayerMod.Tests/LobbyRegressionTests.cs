using System.IO;
using CairnMultiplayerMod.Internal.Networking;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class LobbyRegressionTests
{
    [Fact]
    public void ResultSurvivesGameDispatcherConsumptionAndIsDeliveredOnlyOnce()
    {
        var capture = new LobbyListCallCapture();
        var steam = new SingleUseResults();
        capture.Begin(42);
        capture.Capture(steam); // prefix, before the game's RunFrame
        Assert.False(steam.Read(42, out _, out _)); // game dispatcher already has no result
        capture.Capture(steam); // another pump must not overwrite the captured result
        Assert.True(capture.TryTake(out var count, out var error));
        Assert.Equal(2u, count);
        Assert.Null(error);
        Assert.False(capture.TryTake(out _, out _));
        Assert.Equal(1, steam.Advances);
    }

    [Fact]
    public void ReadingOnlyAfterGameDispatcherReproducesTheReportedFailure()
    {
        var capture = new LobbyListCallCapture();
        var steam = new SingleUseResults();
        capture.Begin(42);
        steam.Advance();
        Assert.True(steam.Read(42, out _, out _)); // vanilla dispatcher consumes first
        capture.Capture(steam);
        Assert.True(capture.TryTake(out _, out var error));
        Assert.Contains("could not read lobby call 42", error);
    }

    [Fact]
    public void CancelledResultCannotCompleteAReplacementSearch()
    {
        var capture = new LobbyListCallCapture();
        capture.Begin(42);
        capture.Capture(new SingleUseResults());
        capture.Cancel();
        capture.Begin(43);
        Assert.False(capture.TryTake(out _, out _));
        var next = new SingleUseResults { ExpectedCall = 43, Completed = false };
        capture.Capture(next);
        Assert.False(capture.TryTake(out _, out _));
        next.Completed = true;
        capture.Capture(next);
        Assert.True(capture.TryTake(out _, out var error));
        Assert.Null(error);
    }

    private sealed class SingleUseResults : ILobbyListResultReader
    {
        internal ulong ExpectedCall = 42;
        internal bool Completed = true;
        internal int Advances;
        private bool _consumed;
        public void Advance() => Advances++;
        public bool IsCompleted(ulong call, out bool failed)
        { Assert.Equal(ExpectedCall, call); failed = false; return Completed; }
        public bool Read(ulong call, out uint count, out bool failed)
        {
            Assert.Equal(ExpectedCall, call);
            Assert.True(Advances > 0);
            count = 2; failed = false;
            if (_consumed) return false;
            _consumed = true;
            return true;
        }
    }

    [Theory]
    [InlineData(0u, 50, 0)]
    [InlineData(1u, 50, 1)]
    [InlineData(10u, 10, 10)]
    [InlineData(50u, 50, 50)]
    public void LobbySearchNeverReadsPastTheReportedResultCount(uint count, int limit, int expected)
        => Assert.Equal(expected, LobbyListResultCount.Validate(count, limit));

    [Theory]
    [InlineData(unchecked((uint)-1649481872), 50)] // recorded in the affected session
    [InlineData(unchecked((uint)-61349200), 50)]
    [InlineData(uint.MaxValue, 50)]
    [InlineData(51u, 50)]
    [InlineData(11u, 10)]
    public void CorruptLobbyCountsFailBeforeAnyNativeSlotIsRead(uint count, int limit)
        => Assert.Throws<InvalidDataException>(() => LobbyListResultCount.Validate(count, limit));
}
