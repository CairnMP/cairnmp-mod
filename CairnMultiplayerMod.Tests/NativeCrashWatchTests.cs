using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// A native crash never reaches managed code, so these two pieces — the marker left behind and
/// the minidump Windows writes — are the only evidence the mod can ever report. Both are parsed
/// by hand, so both are pinned here.
/// </summary>
public sealed class NativeCrashWatchTests
{
    // Layout mirrors the real thing: header, stream directory, module list, exception stream.
    private static byte[] BuildMinidump(uint exceptionCode, ulong faultAddress,
        ulong moduleBase, uint moduleSize, string moduleName,
        ulong accessOperation = 1, ulong accessAddress = 0xDEAD0000, uint parameterCount = 2)
    {
        var buffer = new byte[4096];
        var nameBytes = Encoding.Unicode.GetBytes(moduleName);

        const uint streamDirectoryRva = 32;
        const uint moduleListRva = 128;
        const uint moduleNameRva = 512;
        const uint exceptionRva = 1024;

        // MINIDUMP_HEADER
        WriteU32(buffer, 0, 0x504D444D);
        WriteU32(buffer, 4, 0xA793);          // version
        WriteU32(buffer, 8, 2);               // stream count
        WriteU32(buffer, 12, streamDirectoryRva);

        // Stream directory: module list (4), exception (6)
        WriteU32(buffer, streamDirectoryRva, 4);
        WriteU32(buffer, streamDirectoryRva + 4, 4 + 108);
        WriteU32(buffer, streamDirectoryRva + 8, moduleListRva);
        WriteU32(buffer, streamDirectoryRva + 12, 6);
        WriteU32(buffer, streamDirectoryRva + 16, 168);
        WriteU32(buffer, streamDirectoryRva + 20, exceptionRva);

        // MINIDUMP_MODULE_LIST: one 108-byte record
        WriteU32(buffer, moduleListRva, 1);
        var module = moduleListRva + 4;
        WriteU64(buffer, module, moduleBase);
        WriteU32(buffer, module + 8, moduleSize);
        WriteU32(buffer, module + 20, moduleNameRva);

        WriteU32(buffer, moduleNameRva, (uint)nameBytes.Length);
        Array.Copy(nameBytes, 0, buffer, moduleNameRva + 4, nameBytes.Length);

        // MINIDUMP_EXCEPTION_STREAM: thread id + alignment, then the exception record
        WriteU32(buffer, exceptionRva, 1234);
        var record = exceptionRva + 8;
        WriteU32(buffer, record, exceptionCode);
        WriteU64(buffer, record + 16, faultAddress);
        WriteU32(buffer, record + 24, parameterCount);
        WriteU64(buffer, record + 32, accessOperation);
        WriteU64(buffer, record + 40, accessAddress);

        return buffer;
    }

    private static void WriteU32(byte[] b, uint o, uint v) => BitConverter.GetBytes(v).CopyTo(b, (int)o);
    private static void WriteU64(byte[] b, uint o, ulong v) => BitConverter.GetBytes(v).CopyTo(b, (int)o);

    [Fact]
    public void AccessViolationResolvesToModuleAndOffset()
    {
        var dump = BuildMinidump(0xC0000005, 0x7FFB_0000_1234, 0x7FFB_0000_0000, 0x10000,
            @"C:\Games\Cairn\UnityPlayer.dll");

        Assert.True(MinidumpReader.TryParse(dump, out var summary, out var error), error);
        Assert.Equal("UnityPlayer.dll", summary.FaultingModule);
        Assert.Equal(0x1234ul, summary.ModuleOffset);
        Assert.Equal("write", summary.AccessKind);
        Assert.True(summary.HasAccessInfo);
        Assert.Contains("access violation", summary.Describe());
        Assert.Contains("UnityPlayer.dll+0x1234", summary.Describe());
    }

    [Fact]
    public void ReadAccessIsDistinguishedFromWrite()
    {
        var dump = BuildMinidump(0xC0000005, 0x1000, 0x1000, 0x10, "GameAssembly.dll",
            accessOperation: 0);

        Assert.True(MinidumpReader.TryParse(dump, out var summary, out _));
        Assert.Equal("read", summary.AccessKind);
    }

    [Fact]
    public void AddressOutsideEveryModuleStillReports()
    {
        var dump = BuildMinidump(0xC0000005, 0x9999_9999_9999, 0x1000, 0x10, "UnityPlayer.dll");

        Assert.True(MinidumpReader.TryParse(dump, out var summary, out _));
        Assert.Null(summary.FaultingModule);
        Assert.Contains("no module", summary.Describe());
    }

    [Fact]
    public void NonAccessViolationCarriesNoAccessDetails()
    {
        var dump = BuildMinidump(0xC00000FD, 0x7FFB_0000_0500, 0x7FFB_0000_0000, 0x10000,
            "UnityPlayer.dll", parameterCount: 0);

        Assert.True(MinidumpReader.TryParse(dump, out var summary, out _));
        Assert.False(summary.HasAccessInfo);
        Assert.Contains("stack overflow", summary.Describe());
    }

    [Fact]
    public void GarbageIsRejectedWithoutThrowing()
    {
        Assert.False(MinidumpReader.TryParse(new byte[] { 1, 2, 3, 4 }, out _, out var error));
        Assert.NotNull(error);

        Assert.False(MinidumpReader.TryParse(null, out _, out _));

        var wrongSignature = new byte[64];
        Assert.False(MinidumpReader.TryParse(wrongSignature, out _, out var signatureError));
        Assert.Contains("signature", signatureError);
    }

    [Fact]
    public void TruncatedStreamDirectoryDoesNotThrow()
    {
        var dump = BuildMinidump(0xC0000005, 0x1000, 0x1000, 0x10, "UnityPlayer.dll");
        Array.Resize(ref dump, 40); // cuts into the stream directory
        Assert.False(MinidumpReader.TryParse(dump, out _, out _));
    }

    [Fact]
    public void ReportNamesTheLastActivityFromTheMarker()
    {
        var marker = string.Join('\n',
            "version=2.2.17",
            "pid=4242",
            "started=2026-09-18T00:16:38.0000000Z",
            "at=2026-09-18T00:19:39.0000000Z",
            "doing=state=InGame scene=2_Kami ghosts=2 waits=0 net=connected");

        var report = NativeCrashWatch.BuildReport(marker, dumpDirectory: null);

        Assert.Contains("native crash", report);
        Assert.Contains("scene=2_Kami", report);
        Assert.Contains("2.2.17", report);
        Assert.Contains("00:19:39", report);
    }

    [Fact]
    public void ReportSurvivesAnEmptyOrCorruptMarker()
    {
        var report = NativeCrashWatch.BuildReport("", dumpDirectory: null);
        Assert.Contains("native crash", report);

        var garbled = NativeCrashWatch.BuildReport("????\n\n=nonsense\n", dumpDirectory: null);
        Assert.Contains("native crash", garbled);
    }

    [Fact]
    public void ReportIncludesTheDumpVerdictWhenOneExists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cairnmp-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dump = BuildMinidump(0xC0000005, 0x7FFB_0000_1A55, 0x7FFB_0000_0000, 0x2000000,
                @"C:\Games\Cairn\UnityPlayer.dll");
            File.WriteAllBytes(Path.Combine(directory, "Cairn.exe.4242.dmp"), dump);

            var marker = string.Join('\n',
                "version=2.2.17", "pid=4242",
                "started=2026-09-18T00:16:38.0000000Z",
                "doing=unloading scene 2_Kami");

            var report = NativeCrashWatch.BuildReport(marker, directory);

            Assert.Contains("UnityPlayer.dll+0x1a55", report);
            Assert.Contains("unloading scene 2_Kami", report);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReportSaysSoWhenNoDumpMatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cairnmp-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var report = NativeCrashWatch.BuildReport("pid=99\nstarted=2026-09-18T00:00:00.0000000Z", directory);
            Assert.Contains("No Windows crash dump", report);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MarkerIsWrittenOnStartAndClearedOnCleanShutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cairnmp-watch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = NativeCrashWatch.InitializeAndCollectPreviousReport(directory);
            Assert.Null(first); // nothing ran before
            Assert.True(File.Exists(Path.Combine(directory, "session-active.txt")));

            NativeCrashWatch.Disarm();
            Assert.False(File.Exists(Path.Combine(directory, "session-active.txt")));

            var second = NativeCrashWatch.InitializeAndCollectPreviousReport(directory);
            Assert.Null(second); // the clean shutdown must not look like a crash
            NativeCrashWatch.Disarm();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMarkerLeftBehindIsReportedOnTheNextLaunch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cairnmp-watch-" + Guid.NewGuid().ToString("N"));
        try
        {
            NativeCrashWatch.InitializeAndCollectPreviousReport(directory);
            NativeCrashWatch.SetBreadcrumb("state=InGame scene=2_Kami ghosts=1 waits=0 net=connected");
            // No Disarm: this is what a process killed mid-flight leaves behind.

            var report = NativeCrashWatch.InitializeAndCollectPreviousReport(directory);

            Assert.NotNull(report);
            Assert.Contains("scene=2_Kami", report);
            NativeCrashWatch.Disarm();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
