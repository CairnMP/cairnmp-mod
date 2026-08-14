using System;
using System.IO;
using System.Text;
using CairnMultiplayerMod.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class CrashFingerprintTests
{
    private const string Stack = """
        System.NullReferenceException: Object reference not set to an instance of an object.
           at Il2CppInterop.Runtime.Injection.ClassInjector.Invoke(IntPtr ptr)
           at CairnMultiplayerMod.Bootstrap.Mod.TickPlayerSync() in C:\src\Mod.cs:line 631
           at MelonLoader.MelonMod.OnUpdate()
        """;

    [Fact]
    public void PrefersTheFirstFrameInsideTheMod()
    {
        Assert.Equal(
            "CairnMultiplayerMod.Bootstrap.Mod.TickPlayerSync",
            CrashFingerprint.ExtractSignificantFrame(Stack));
    }

    [Fact]
    public void FallsBackToTheFirstFrameWhenNoneBelongsToTheMod()
    {
        const string foreign = "   at UnityEngine.Object.Destroy(Object o)\n   at Il2Cpp.Foo.Bar()";
        Assert.Equal("UnityEngine.Object.Destroy", CrashFingerprint.ExtractSignificantFrame(foreign));
    }

    [Fact]
    public void ReturnsEmptyForAStackWithoutFrames()
    {
        Assert.Equal(string.Empty, CrashFingerprint.ExtractSignificantFrame(null));
        Assert.Equal(string.Empty, CrashFingerprint.ExtractSignificantFrame("no frames here"));
    }

    [Fact]
    public void BuildsAReadableKeyFromTheFailingSite()
    {
        var fingerprint = CrashFingerprint.Build(
            "exception", "Mod.TickPlayerSync", "System.NullReferenceException", Stack);

        Assert.Equal(
            "mod:exception:Mod.TickPlayerSync:System.NullReferenceException:CairnMultiplayerMod.Bootstrap.Mod.TickPlayerSync",
            fingerprint);
    }

    // Deux occurrences du même bug ne diffèrent que par les frames profondes et
    // le message : elles doivent partager la même clé de groupement.
    [Fact]
    public void GroupsTwoOccurrencesOfTheSameBug()
    {
        var first = CrashFingerprint.Build("exception", "Mod.MainQueue", "System.InvalidOperationException",
            "   at CairnMultiplayerMod.Bootstrap.Mod.Drain() in C:\\Users\\alice\\Mod.cs:line 12\n   at A.B()");
        var second = CrashFingerprint.Build("exception", "Mod.MainQueue", "System.InvalidOperationException",
            "   at CairnMultiplayerMod.Bootstrap.Mod.Drain() in D:\\Users\\bob\\Mod.cs:line 12\n   at C.D()");

        Assert.Equal(first, second);
    }

    [Fact]
    public void DistinguishesDifferentFailingSites()
    {
        var tick = CrashFingerprint.Build("exception", null, "System.Exception", "   at CairnMultiplayerMod.Mod.Tick()");
        var render = CrashFingerprint.Build("exception", null, "System.Exception", "   at CairnMultiplayerMod.Mod.Render()");
        Assert.NotEqual(tick, render);
    }

    [Fact]
    public void SkipsEmptySegmentsAndTheSeparator()
    {
        var fingerprint = CrashFingerprint.Build("exception", "  ", "System.Exception", null);
        Assert.Equal("mod:exception:System.Exception", fingerprint);

        var withSeparator = CrashFingerprint.Build("exception", "Weird: label", "T", null);
        Assert.Equal("mod:exception:Weird__label:T", withSeparator);
    }

    // Sans rien de stable à quoi s'accrocher, mieux vaut laisser l'API dériver
    // la signature que renvoyer une clé qui regrouperait tous les crashes.
    [Fact]
    public void ReturnsEmptyWhenNothingIsStable()
    {
        Assert.Equal(string.Empty, CrashFingerprint.Build(null, null, null, null));
    }

    [Fact]
    public void StaysWithinTheApiColumnLimit()
    {
        var huge = new string('x', 400);
        Assert.True(CrashFingerprint.Build("exception", huge, huge, null).Length <= 255);
    }
}

public sealed class GameLogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairnmp-logtail-" + Guid.NewGuid().ToString("N"));

    public GameLogTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteLog(string content)
    {
        var path = Path.Combine(_dir, "Latest.log");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void ReadsASmallLogWhole()
    {
        var path = WriteLog("line one\nline two\n");
        Assert.Equal("line one\nline two\n", GameLogTail.ReadFrom(path));
    }

    [Fact]
    public void KeepsTheTailAndFlagsTheTruncation()
    {
        var path = WriteLog(string.Concat(new string('a', 500), "\nthe interesting part\n"));

        var tail = GameLogTail.ReadFrom(path, maxBytes: 64);

        Assert.StartsWith("[truncated", tail);
        Assert.Contains("the interesting part", tail);
        Assert.DoesNotContain(new string('a', 500), tail);
    }

    // Le loader garde Latest.log ouvert en écriture pendant toute la partie.
    [Fact]
    public void ReadsWhileTheFileIsHeldOpenForWriting()
    {
        var path = WriteLog("held open\n");
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        Assert.Equal("held open\n", GameLogTail.ReadFrom(path));
    }

    [Fact]
    public void ReturnsEmptyRatherThanThrowing()
    {
        Assert.Equal(string.Empty, GameLogTail.ReadFrom(Path.Combine(_dir, "missing.log")));
        Assert.Equal(string.Empty, GameLogTail.ReadFrom(null));
        Assert.Equal(string.Empty, GameLogTail.ReadFrom(WriteLog(""), maxBytes: 0));
        Assert.Equal(string.Empty, GameLogTail.ReadFrom(WriteLog("")));
    }
}
