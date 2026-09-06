using System;
using System.Text;
using System.Text.RegularExpressions;

namespace CairnMultiplayerMod.Internal.Diagnostics;

/// <summary>
/// Builds a readable local key for a fatal-error archive.
///
/// A fingerprint made of the failing site (context, exception type, first frame
/// inside our own code) gives support tools a stable value without collecting a
/// machine or player identifier.
/// </summary>
internal static class CrashFingerprint
{
    /// <summary>Namespace marker identifying a frame belonging to the mod.</summary>
    private const string OwnCodeMarker = "CairnMultiplayer";

    /// <summary>Matches `at Namespace.Type.Method(` in a .NET stack trace.</summary>
    private static readonly Regex FramePattern = new(
        @"\bat\s+([A-Za-z_][A-Za-z0-9_.`+<>\[\]]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Keeps the fingerprint compact for filenames and support tools.</summary>
    private const int MaxLength = 255;

    /// <summary>
    /// Builds a fingerprint of the form `mod:kind:context:ExceptionType:Frame`.
    /// Empty segments are dropped. Returns an empty string when there is
    /// nothing stable to key on.
    /// </summary>
    public static string Build(string kind, string contextLabel, string exceptionType, string stackTrace)
    {
        var frame = ExtractSignificantFrame(stackTrace);

        var sb = new StringBuilder("mod");
        AppendSegment(sb, kind);
        AppendSegment(sb, contextLabel);
        AppendSegment(sb, exceptionType);
        AppendSegment(sb, frame);

        // "mod" alone carries no information: every report would collapse into
        // one group.
        var result = sb.ToString();
        if (result == "mod") return string.Empty;

        return result.Length <= MaxLength ? result : result.Substring(0, MaxLength);
    }

    /// <summary>
    /// Returns the first stack frame belonging to the mod, falling back to the
    /// first frame of any kind. Frames from the runtime or from Unity vary
    /// between builds and would fragment grouping.
    /// </summary>
    public static string ExtractSignificantFrame(string stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) return string.Empty;

        string firstFrame = null;
        foreach (Match match in FramePattern.Matches(stackTrace))
        {
            var frame = match.Groups[1].Value;
            firstFrame ??= frame;
            if (frame.IndexOf(OwnCodeMarker, StringComparison.Ordinal) >= 0)
                return frame;
        }
        return firstFrame ?? string.Empty;
    }

    private static void AppendSegment(StringBuilder sb, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(':').Append(Sanitize(value));
    }

    /// <summary>
    /// Keeps the fingerprint printable and free of the separator, so it stays
    /// readable in the console instead of showing up as an opaque hash.
    /// </summary>
    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (c == ':' || char.IsWhiteSpace(c)) sb.Append('_');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}
