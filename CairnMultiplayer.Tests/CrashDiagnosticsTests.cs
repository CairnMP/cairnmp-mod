using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CairnMultiplayerMod.Internal.Diagnostics;
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

    // Without a stable element, an empty fingerprint is safer than grouping unrelated crashes.
    [Fact]
    public void ReturnsEmptyWhenNothingIsStable()
    {
        Assert.Equal(string.Empty, CrashFingerprint.Build(null, null, null, null));
    }

    [Fact]
    public void StaysCompactForArchiveNamesAndSupportTools()
    {
        var huge = new string('x', 400);
        Assert.True(CrashFingerprint.Build("exception", huge, huge, null).Length <= 255);
    }
}

public sealed class CrashArchiveBuilderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "cairnmp-crash-bundle-" + Guid.NewGuid().ToString("N"));

    public CrashArchiveBuilderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }

    [Fact]
    public void CreatesALocalBundleWithReportPrivacyNoticeAndLogs()
    {
        var logPath = Path.Combine(_directory, "Latest.log");
        File.WriteAllText(logPath, "loader output", Encoding.UTF8);
        var output = Path.Combine(_directory, "Crashes");

        var archivePath = CrashArchiveBuilder.Create(output, Incident(), new[] { logPath });

        Assert.True(File.Exists(archivePath));
        using var archive = ZipFile.OpenRead(archivePath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("crash-report.txt", names);
        Assert.Contains("README.txt", names);
        Assert.Contains("logs/Latest.log", names);
        Assert.Contains("not uploaded or transmitted", ReadEntry(archive, "README.txt"));
        Assert.Contains("Mod.OnUpdate", ReadEntry(archive, "crash-report.txt"));
        Assert.Equal("loader output", ReadEntry(archive, "logs/Latest.log"));
    }

    [Fact]
    public void ReadsALogThatIsStillOpenAndDisambiguatesDuplicateNames()
    {
        var firstDirectory = Path.Combine(_directory, "first");
        var secondDirectory = Path.Combine(_directory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(firstDirectory, "Player.log");
        var second = Path.Combine(secondDirectory, "Player.log");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        using var heldOpen = new FileStream(first, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var archivePath = CrashArchiveBuilder.Create(
            Path.Combine(_directory, "Crashes"), Incident(), new[] { first, second, first });

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.NotNull(archive.GetEntry("logs/Player.log"));
        Assert.NotNull(archive.GetEntry("logs/Player-2.log"));
        Assert.Equal(2, archive.Entries.Count(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal)));
    }

    [Fact]
    public void RecordsAnOptionalLogThatCouldNotBeCollected()
    {
        var logPath = Path.Combine(_directory, "locked.log");
        File.WriteAllText(logPath, "unavailable");
        using var heldOpen = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var archivePath = CrashArchiveBuilder.Create(
            Path.Combine(_directory, "Crashes"), Incident(), new[] { logPath });

        using var archive = ZipFile.OpenRead(archivePath);
        var collectionErrors = ReadEntry(archive, "logs/collection-errors.txt");
        Assert.Contains("locked.log", collectionErrors);
        Assert.Contains("IOException", collectionErrors);
    }

    [Fact]
    public void KeepsOnlyTheBoundedTailOfAnOversizedLog()
    {
        var logPath = Path.Combine(_directory, "oversized.log");
        using (var file = new FileStream(logPath, FileMode.Create, FileAccess.Write))
        {
            file.SetLength(CrashArchiveBuilder.MaxLogBytes + 128);
            file.Seek(-4, SeekOrigin.End);
            file.Write(Encoding.UTF8.GetBytes("tail"));
        }

        var archivePath = CrashArchiveBuilder.Create(
            Path.Combine(_directory, "Crashes"), Incident(), new[] { logPath });

        using var archive = ZipFile.OpenRead(archivePath);
        var content = ReadEntry(archive, "logs/oversized.log");
        Assert.StartsWith("[truncated by CairnMP", content);
        Assert.EndsWith("tail", content);
    }

    private static CrashIncident Incident() => new()
    {
        OccurredAtUtc = new DateTimeOffset(2026, 9, 4, 12, 30, 0, TimeSpan.Zero),
        ModVersion = "1.1.0",
        Context = "Mod.OnUpdate",
        ExceptionType = typeof(InvalidOperationException).FullName,
        Message = "fatal test",
        StackTrace = "at CairnMultiplayerMod.Bootstrap.Mod.OnUpdate()",
        Fingerprint = "mod:fatal:Mod.OnUpdate",
    };

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public sealed class DiagnosticRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "cairnmp-retention-" + Guid.NewGuid().ToString("N"));

    public DiagnosticRetentionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }

    [Fact]
    public void KeepsNewestFilesWithinCountAndAlwaysKeepsProtectedFile()
    {
        var files = Enumerable.Range(1, 5)
            .Select(index => Write($"crash-{index}.zip", index, DateTime.UtcNow.AddMinutes(index)))
            .ToArray();

        DiagnosticRetention.Prune(_directory, "*.zip", maxFiles: 2, maxTotalBytes: 100, files[0]);

        Assert.True(File.Exists(files[0]));
        Assert.True(File.Exists(files[4]));
        Assert.False(File.Exists(files[1]));
        Assert.False(File.Exists(files[2]));
        Assert.False(File.Exists(files[3]));
    }

    [Fact]
    public void ProtectedFileConsumesTheTotalByteBudget()
    {
        var older = Write("crash-old.zip", 6, DateTime.UtcNow.AddMinutes(-1));
        var current = Write("crash-current.zip", 6, DateTime.UtcNow);

        DiagnosticRetention.Prune(_directory, "*.zip", maxFiles: 10, maxTotalBytes: 10, current);

        Assert.True(File.Exists(current));
        Assert.False(File.Exists(older));
    }

    private string Write(string name, int bytes, DateTime timestamp)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, timestamp);
        return path;
    }
}
