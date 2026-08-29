using System;
using System.IO;
using System.Text;
using MelonLoader.Utils;

namespace CairnMultiplayerMod.Diagnostics;

/// <summary>
/// Reads the tail of CairnLoader's Latest.log to attach it to a crash report.
///
/// A stack trace alone rarely explains an Il2Cpp failure — what the loader
/// printed in the seconds before matters just as much. Without this, triaging a
/// report means asking the player for their log file.
/// </summary>
public static class GameLogTail
{
    /// <summary>
    /// Kept well under the API's 512 KB cap: the interesting part of a log is
    /// always its end, and the payload travels while the game is dying.
    /// </summary>
    public const int DefaultMaxBytes = 128 * 1024;

    /// <summary>Reads the tail of the running game's loader log.</summary>
    public static string Read(int maxBytes = DefaultMaxBytes)
    {
        try
        {
            return ReadFrom(Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Latest.log"), maxBytes);
        }
        catch
        {
            // MelonEnvironment may be unavailable very early in startup.
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads the last <paramref name="maxBytes"/> bytes of a log file. Returns
    /// an empty string rather than throwing: a crash reporter must never be the
    /// thing that crashes.
    /// </summary>
    public static string ReadFrom(string path, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrEmpty(path) || maxBytes <= 0) return string.Empty;

        try
        {
            if (!File.Exists(path)) return string.Empty;

            // The loader holds the file open for writing — share everything, or
            // the read fails exactly when it is needed.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            if (length == 0) return string.Empty;

            var take = (int)Math.Min(length, maxBytes);
            stream.Seek(length - take, SeekOrigin.Begin);

            var buffer = new byte[take];
            var read = stream.Read(buffer, 0, take);
            var text = new UTF8Encoding(false, false).GetString(buffer, 0, read);

            return length > take ? TrimPartialFirstLine(text) : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Drops the truncated line the byte-offset seek landed in the middle of,
    /// and says so — otherwise the first log line reads as corrupt.
    /// </summary>
    private static string TrimPartialFirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        var body = newline >= 0 && newline + 1 < text.Length ? text.Substring(newline + 1) : text;
        return "[truncated — showing the tail of the log]\n" + body;
    }
}
