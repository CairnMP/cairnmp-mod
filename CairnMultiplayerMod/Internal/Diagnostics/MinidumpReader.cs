using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// What a minidump says about the crash that produced it.
/// </summary>
internal sealed class MinidumpSummary
{
    public uint ExceptionCode { get; init; }
    public ulong ExceptionAddress { get; init; }
    public string FaultingModule { get; init; }
    public ulong ModuleOffset { get; init; }
    public string AccessKind { get; init; }
    public ulong AccessAddress { get; init; }
    public bool HasAccessInfo { get; init; }

    public string Describe()
    {
        var where = FaultingModule != null
            ? $"{FaultingModule}+0x{ModuleOffset:x}"
            : $"0x{ExceptionAddress:x} (no module)";
        var what = DescribeCode(ExceptionCode);
        var access = HasAccessInfo
            ? $", {AccessKind} at 0x{AccessAddress:x}"
            : string.Empty;
        return $"{what} in {where}{access}";
    }

    private static string DescribeCode(uint code) => code switch
    {
        0xC0000005 => "access violation (0xC0000005)",
        0xC0000094 => "integer divide by zero (0xC0000094)",
        0xC00000FD => "stack overflow (0xC00000FD)",
        0xC0000374 => "heap corruption (0xC0000374)",
        0xC000001D => "illegal instruction (0xC000001D)",
        0x80000003 => "breakpoint (0x80000003)",
        0xE0434352 => "managed exception (0xE0434352)",
        _ => $"native exception (0x{code:X8})",
    };
}

/// <summary>
/// Minimal reader for the Windows minidumps that WER drops in %LOCALAPPDATA%\CrashDumps.
/// It reads only the exception record and the module list, which is enough to name the
/// module and offset a native crash died in — the one thing the mod's own logs can never
/// capture, because an access violation never reaches managed code.
/// </summary>
internal static class MinidumpReader
{
    private const uint Signature = 0x504D444D; // 'MDMP'
    private const uint StreamModuleList = 4;
    private const uint StreamException = 6;

    // MINIDUMP_MODULE is a fixed 108-byte record; the fields we need sit at these offsets.
    private const int ModuleRecordSize = 108;
    private const int ModuleBaseOffset = 0;
    private const int ModuleSizeOffset = 8;
    private const int ModuleNameRvaOffset = 20;

    public static bool TryRead(string path, out MinidumpSummary summary, out string error)
    {
        summary = null;
        error = null;
        try
        {
            var data = File.ReadAllBytes(path);
            return TryParse(data, out summary, out error);
        }
        catch (Exception exception)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    internal static bool TryParse(byte[] data, out MinidumpSummary summary, out string error)
    {
        summary = null;
        error = null;

        if (data == null || data.Length < 32)
        {
            error = "file too small to be a minidump";
            return false;
        }

        if (ReadU32(data, 0) != Signature)
        {
            error = "not a minidump (bad signature)";
            return false;
        }

        var streamCount = ReadU32(data, 8);
        var streamDirectoryRva = ReadU32(data, 12);

        uint exceptionRva = 0;
        uint moduleListRva = 0;
        for (uint i = 0; i < streamCount; i++)
        {
            var entry = streamDirectoryRva + i * 12;
            if (entry + 12 > (uint)data.Length) break;

            var type = ReadU32(data, entry);
            var rva = ReadU32(data, entry + 8);
            if (type == StreamException) exceptionRva = rva;
            else if (type == StreamModuleList) moduleListRva = rva;
        }

        if (exceptionRva == 0)
        {
            error = "the dump carries no exception record";
            return false;
        }

        // MINIDUMP_EXCEPTION_STREAM: thread id (4) + alignment (4), then MINIDUMP_EXCEPTION.
        var record = exceptionRva + 8;
        if (record + 40 > (uint)data.Length)
        {
            error = "truncated exception record";
            return false;
        }

        var code = ReadU32(data, record);
        var address = ReadU64(data, record + 16);
        var parameterCount = ReadU32(data, record + 24);

        string accessKind = null;
        ulong accessAddress = 0;
        var hasAccessInfo = false;
        // For an access violation the first two parameters are the operation and the address.
        if (code == 0xC0000005 && parameterCount >= 2 && record + 32 + 16 <= (uint)data.Length)
        {
            var operation = ReadU64(data, record + 32);
            accessAddress = ReadU64(data, record + 40);
            accessKind = operation switch
            {
                0 => "read",
                1 => "write",
                8 => "execute (DEP)",
                _ => $"operation {operation}",
            };
            hasAccessInfo = true;
        }

        ResolveModule(data, moduleListRva, address, out var moduleName, out var moduleOffset);

        summary = new MinidumpSummary
        {
            ExceptionCode = code,
            ExceptionAddress = address,
            FaultingModule = moduleName,
            ModuleOffset = moduleOffset,
            AccessKind = accessKind,
            AccessAddress = accessAddress,
            HasAccessInfo = hasAccessInfo,
        };
        return true;
    }

    private static void ResolveModule(byte[] data, uint moduleListRva, ulong address,
        out string moduleName, out ulong moduleOffset)
    {
        moduleName = null;
        moduleOffset = 0;
        if (moduleListRva == 0 || moduleListRva + 4 > (uint)data.Length) return;

        var count = ReadU32(data, moduleListRva);
        var cursor = moduleListRva + 4;
        for (uint i = 0; i < count; i++)
        {
            if (cursor + ModuleRecordSize > (uint)data.Length) return;

            var baseAddress = ReadU64(data, cursor + ModuleBaseOffset);
            var size = ReadU32(data, cursor + ModuleSizeOffset);
            if (address >= baseAddress && address < baseAddress + size)
            {
                moduleName = ShortName(ReadMinidumpString(data, ReadU32(data, cursor + ModuleNameRvaOffset)));
                moduleOffset = address - baseAddress;
                return;
            }
            cursor += ModuleRecordSize;
        }
    }

    private static string ShortName(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;
        var slash = fullPath.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < fullPath.Length ? fullPath.Substring(slash + 1) : fullPath;
    }

    private static string ReadMinidumpString(byte[] data, uint rva)
    {
        if (rva == 0 || rva + 4 > (uint)data.Length) return null;
        var byteCount = ReadU32(data, rva);
        if (byteCount > 1024 || rva + 4 + byteCount > (uint)data.Length) return null;
        return Encoding.Unicode.GetString(data, (int)(rva + 4), (int)byteCount);
    }

    private static uint ReadU32(byte[] data, uint offset) => BitConverter.ToUInt32(data, (int)offset);
    private static ulong ReadU64(byte[] data, uint offset) => BitConverter.ToUInt64(data, (int)offset);

    /// <summary>
    /// The most recent dump Windows wrote for this game, newer than <paramref name="notBefore"/>.
    /// A dump named after <paramref name="processId"/> always wins, since that is an exact match.
    /// </summary>
    public static string FindDumpForProcess(string directory, int processId, DateTime notBefore)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

            var exact = Path.Combine(directory, $"Cairn.exe.{processId}.dmp");
            if (File.Exists(exact)) return exact;

            string newest = null;
            var newestTime = notBefore;
            foreach (var candidate in Directory.EnumerateFiles(directory, "Cairn.exe.*.dmp"))
            {
                var written = File.GetLastWriteTimeUtc(candidate);
                if (written <= newestTime) continue;
                newestTime = written;
                newest = candidate;
            }
            return newest;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"CairnMP could not look for a crash dump: {exception}");
            return null;
        }
    }

    public static string DefaultDumpDirectory
    {
        get
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "CrashDumps");
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"CairnMP could not resolve the crash-dump directory: {exception}");
                return null;
            }
        }
    }
}
