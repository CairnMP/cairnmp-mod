using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// A command the chat can complete. <see cref="Usage"/> is the string the command
/// already advertises to /help ("/wave &lt;player&gt; &lt;emote&gt;"): the completion
/// engine reads its placeholders to know which argument is a player name, so a feature
/// gets argument completion for free just by naming its arguments.
/// </summary>
internal readonly struct ChatCommandInfo
{
    public ChatCommandInfo(string name, string usage, string description)
    {
        Name = name ?? "";
        Usage = string.IsNullOrWhiteSpace(usage) ? "/" + Name : usage.Trim();
        Description = description?.Trim() ?? "";
    }

    public string Name { get; }
    public string Usage { get; }
    public string Description { get; }
}

/// <summary>One completion candidate: the whole input line it produces, plus its labels.</summary>
internal readonly struct ChatCompletionCandidate
{
    public ChatCompletionCandidate(string input, string label, string detail)
    {
        Input = input ?? "";
        Label = label ?? "";
        Detail = detail ?? "";
    }

    /// <summary>Full replacement for the input line (completion always edits its tail).</summary>
    public string Input { get; }

    /// <summary>Short form shown in the suggestion bar ("/tp", "Alice").</summary>
    public string Label { get; }

    /// <summary>Usage + description, shown when the candidate is the only match.</summary>
    public string Detail { get; }
}

/// <summary>
/// What the engine found for one input line: the candidates to cycle through, and the
/// usage reminder of the command being typed (shown when there is nothing to cycle,
/// for instance while typing an item name).
/// </summary>
internal sealed class ChatCompletionSet
{
    public static readonly ChatCompletionSet Empty =
        new(Array.Empty<ChatCompletionCandidate>(), "");

    public ChatCompletionSet(IReadOnlyList<ChatCompletionCandidate> candidates, string usage)
    {
        Candidates = candidates ?? Array.Empty<ChatCompletionCandidate>();
        Usage = usage ?? "";
    }

    public IReadOnlyList<ChatCompletionCandidate> Candidates { get; }
    public string Usage { get; }
    public int Count => Candidates.Count;
}

/// <summary>
/// Pure completion engine for the chat input (no Unity, no game state, so it is unit
/// tested). It completes command names ("/t" gives "/tp ") and player names in the
/// arguments a command declares as &lt;player&gt;. Anything else - plain messages, free
/// text arguments - returns no candidate: the caller then only shows the usage reminder.
/// </summary>
internal static class ChatCompletion
{
    /// <summary>Placeholder name that means "complete with the connected players".</summary>
    private const string PlayerPlaceholder = "player";

    private static readonly char[] Space = { ' ' };

    /// <summary>
    /// Computes the candidates for <paramref name="input"/> as typed (completion always
    /// applies to the end of the line - the overlay has no movable caret).
    /// </summary>
    public static ChatCompletionSet Complete(string input, IReadOnlyList<ChatCommandInfo> commands,
        IReadOnlyList<string> playerNames)
    {
        var line = input ?? "";
        if (line.Length == 0 || line[0] != '/') return ChatCompletionSet.Empty;

        var body = line.Substring(1);
        var firstSpace = body.IndexOf(' ');
        return firstSpace < 0
            ? CompleteCommandName(body, commands)
            : CompleteArgument(body.Substring(0, firstSpace), body.Substring(firstSpace + 1),
                commands, playerNames);
    }

    /// <summary>"/gi" gives every command starting with "gi", in the order received.</summary>
    private static ChatCompletionSet CompleteCommandName(string typed,
        IReadOnlyList<ChatCommandInfo> commands)
    {
        var candidates = new List<ChatCompletionCandidate>();
        if (commands != null)
        {
            foreach (var command in commands)
            {
                if (command.Name.Length == 0) continue;
                if (typed.Length > 0
                    && !command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) continue;

                // Trailing space when the command takes arguments: the next Tab then
                // completes the first argument instead of re-cycling command names.
                var takesArguments = Placeholders(command.Usage).Count > 0;
                candidates.Add(new ChatCompletionCandidate(
                    "/" + command.Name + (takesArguments ? " " : ""),
                    "/" + command.Name,
                    Describe(command)));
            }
        }
        return new ChatCompletionSet(candidates, "");
    }

    /// <summary>
    /// "/tp Ali" gives the players whose name starts with "Ali". The argument being typed
    /// is matched against the command usage placeholders; only &lt;player&gt; completes.
    /// The last placeholder swallows the rest of the line, so nicknames with spaces work.
    /// </summary>
    private static ChatCompletionSet CompleteArgument(string name, string rest,
        IReadOnlyList<ChatCommandInfo> commands, IReadOnlyList<string> playerNames)
    {
        if (!TryFindCommand(name, commands, out var command)) return ChatCompletionSet.Empty;

        var usage = Describe(command);
        var placeholders = Placeholders(command.Usage);
        var arguments = rest.Split(Space, StringSplitOptions.RemoveEmptyEntries);

        // A trailing space means a new argument is starting, not that the last one is being edited.
        var editingNewArgument = rest.Length == 0 || rest[rest.Length - 1] == ' ';
        var index = editingNewArgument ? arguments.Length : arguments.Length - 1;
        var last = placeholders.Count - 1;
        if (last < 0) return new ChatCompletionSet(null, usage);

        // The last placeholder swallows every extra word: "/tp Jean Mi" is still the
        // <player> argument, being a nickname with a space in it.
        if (index > last)
        {
            if (placeholders[last] != PlayerPlaceholder) return new ChatCompletionSet(null, usage);
            index = last;
        }
        if (placeholders[index] != PlayerPlaceholder) return new ChatCompletionSet(null, usage);

        var isLastArgument = index == last;
        string typed;
        if (isLastArgument)
        {
            // Everything from this argument on, spaces included: "/tp Jean Mi" matches
            // "Jean Michel", and the trailing space of "/tp Jean " is part of the prefix.
            typed = string.Join(" ", arguments, index, arguments.Length - index);
            if (editingNewArgument && typed.Length > 0) typed += " ";
        }
        else
        {
            typed = editingNewArgument ? "" : arguments[index];
        }

        var head = "/" + command.Name;
        for (var i = 0; i < index; i++) head += " " + arguments[i];

        var candidates = new List<ChatCompletionCandidate>();
        if (playerNames != null)
        {
            foreach (var player in playerNames)
            {
                if (string.IsNullOrWhiteSpace(player)) continue;
                if (typed.Length > 0
                    && !player.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) continue;

                candidates.Add(new ChatCompletionCandidate(
                    head + " " + player + (isLastArgument ? "" : " "), player, usage));
            }
        }
        return new ChatCompletionSet(candidates, usage);
    }

    private static bool TryFindCommand(string name, IReadOnlyList<ChatCommandInfo> commands,
        out ChatCommandInfo found)
    {
        found = default;
        if (commands == null) return false;
        foreach (var command in commands)
        {
            if (!string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            found = command;
            return true;
        }
        return false;
    }

    private static string Describe(ChatCommandInfo command)
        => command.Description.Length == 0 ? command.Usage : command.Usage + " - " + command.Description;

    /// <summary>
    /// Argument names read from a usage string: "/wave &lt;player&gt; &lt;emote&gt; [count]"
    /// gives player, emote, count. Anything that is not bracketed is ignored (the leading
    /// "/wave", a literal keyword...).
    /// </summary>
    private static List<string> Placeholders(string usage)
    {
        var placeholders = new List<string>();
        foreach (var token in (usage ?? "").Split(Space, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 3) continue;
            var open = token[0];
            var close = token[token.Length - 1];
            if ((open == '<' && close == '>') || (open == '[' && close == ']'))
                placeholders.Add(token.Substring(1, token.Length - 2).ToLowerInvariant());
        }
        return placeholders;
    }

    /// <summary>
    /// One line of help for the suggestion bar, at most <paramref name="maxChars"/> long:
    /// the full usage when a single candidate matches, otherwise the labels to cycle
    /// through with the selected one bracketed. The window scrolls to keep the selection
    /// visible when the list is longer than the bar.
    /// </summary>
    public static string BuildHint(ChatCompletionSet set, int selectedIndex, int maxChars)
    {
        if (set == null || set.Count == 0) return Truncate(set?.Usage ?? "", maxChars);
        if (set.Count == 1)
        {
            var only = set.Candidates[0];
            return Truncate(only.Detail.Length > 0 ? only.Detail : only.Label, maxChars);
        }

        const string prefix = "Tab: ";
        var budget = Math.Max(8, maxChars - prefix.Length);
        for (var start = 0; start < set.Count; start++)
        {
            var text = "";
            var end = start;
            while (end < set.Count)
            {
                var label = end == selectedIndex
                    ? "[" + set.Candidates[end].Label + "]"
                    : set.Candidates[end].Label;
                var next = text.Length == 0 ? label : text + "  " + label;
                if (end > start && next.Length > budget) break;
                text = next;
                end++;
            }

            // Slide the window right until the selected candidate is inside it.
            if (selectedIndex >= end) continue;

            if (start > 0) text = "< " + text;
            if (end < set.Count) text += " >";
            return prefix + text;
        }
        return prefix + set.Candidates[set.Count - 1].Label;
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 1 || text.Length <= maxChars) return text;
        return text.Substring(0, Math.Max(1, maxChars - 1)) + "~";
    }
}
