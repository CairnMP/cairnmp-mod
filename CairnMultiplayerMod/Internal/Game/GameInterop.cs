using System;

namespace CairnMultiplayerMod.Internal.Game;

internal static class GameInterop
{
    internal static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var index = text.IndexOfAny(new[] { '\r', '\n' });
        return index >= 0 ? text.Substring(0, index) : text;
    }
}
