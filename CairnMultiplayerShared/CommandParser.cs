using System;

namespace CairnMultiplayer.Shared;

/// <summary>
/// Result of parsing a chat line. <see cref="IsCommand"/> is true when the line
/// starts with '/'. The name is normalized to lowercase for case-insensitive
/// matching. <see cref="ArgsText"/> keeps all the text after the name (useful for
/// targeting a player whose nickname contains spaces).
/// </summary>
public readonly struct ParsedCommand
{
    public bool IsCommand { get; }
    public string Name { get; }
    public string[] Args { get; }
    public string ArgsText { get; }

    public ParsedCommand(bool isCommand, string name, string[] args, string argsText)
    {
        IsCommand = isCommand;
        Name = name;
        Args = args;
        ArgsText = argsText;
    }
}

/// <summary>
/// Pure parser (no Unity/game dependency, so testable with xUnit) that turns chat
/// lines into "/name args" commands.
/// </summary>
public static class CommandParser
{
    private static readonly char[] Whitespace = { ' ', '\t' };

    /// <summary>
    /// Parses a chat line. If it does not start with '/', returns
    /// <c>IsCommand=false</c> (a normal message). Otherwise, splits "/name arg1 arg2 ...":
    /// the name (lowercase, without the '/'), the args separated by spaces, and the raw
    /// text after the name.
    /// </summary>
    public static ParsedCommand Parse(string input)
    {
        var line = (input ?? "").Trim();
        if (line.Length == 0 || line[0] != '/')
            return new ParsedCommand(false, "", Array.Empty<string>(), "");

        // Strip the leading '/'.
        var body = line.Substring(1).Trim();
        if (body.Length == 0)
            return new ParsedCommand(true, "", Array.Empty<string>(), "");

        var parts = body.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();

        var args = parts.Length > 1 ? new string[parts.Length - 1] : Array.Empty<string>();
        for (int i = 1; i < parts.Length; i++)
            args[i - 1] = parts[i];

        // Text after the command name, with edge whitespace trimmed but internal
        // spaces preserved (multi-word nicknames).
        var nameLen = parts[0].Length;
        var argsText = body.Length > nameLen ? body.Substring(nameLen).Trim() : "";

        return new ParsedCommand(true, name, args, argsText);
    }
}
