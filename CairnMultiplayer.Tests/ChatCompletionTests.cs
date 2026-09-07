#nullable enable

using System.Collections.Generic;
using System.Linq;
using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerShared.Tests;

/// <summary>
/// Chat input completion: command names and player names. The engine is pure (no Unity),
/// so the whole Tab behaviour is covered here rather than in game.
/// </summary>
public class ChatCompletionTests
{
    // Same shape and order as what CommandRouter.AvailableCommands() hands over: the
    // built-ins, plus /roll and /wave standing in for commands added by a feature.
    private static readonly ChatCommandInfo[] Commands =
    {
        new("bring", "/bring <player>", "teleport a player to you"),
        new("help", "/help", "show this help"),
        new("roll", "/roll [sides]", "roll a die"),
        new("tp", "/tp <player>", "teleport yourself to a player"),
        new("wave", "/wave <player> <emote>", "wave at a player"),
    };

    private static readonly string[] Players = { "Alice", "Alina", "Bob", "Jean Michel" };

    private static ChatCompletionSet Complete(string input)
        => ChatCompletion.Complete(input, Commands, Players);

    private static string[] Inputs(ChatCompletionSet set)
        => set.Candidates.Select(candidate => candidate.Input).ToArray();

    private static string[] Labels(ChatCompletionSet set)
        => set.Candidates.Select(candidate => candidate.Label).ToArray();

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("hey Ali")]
    [InlineData(" /tp")]           // a leading space is not a command line
    public void PlainMessagesCompleteToNothing(string input)
    {
        var set = Complete(input);

        Assert.Equal(0, set.Count);
        Assert.Equal("", set.Usage);
    }

    [Fact]
    public void LoneSlashListsEveryAvailableCommand()
    {
        var set = Complete("/");

        Assert.Equal(new[] { "/bring", "/help", "/roll", "/tp", "/wave" }, Labels(set));
    }

    [Fact]
    public void CommandNamesFilterOnWhatIsTyped()
    {
        Assert.Equal(new[] { "/tp " }, Inputs(Complete("/t")));
        Assert.Equal(new[] { "/wave " }, Inputs(Complete("/WA")));
        Assert.Equal(0, Complete("/zz").Count);
    }

    /// <summary>
    /// A command that takes arguments completes with a trailing space, so the next Tab
    /// moves on to its first argument instead of cycling through command names again.
    /// </summary>
    [Fact]
    public void OnlyCommandsWithArgumentsCompleteWithATrailingSpace()
    {
        Assert.Equal(new[] { "/bring " }, Inputs(Complete("/b")));
        Assert.Equal(new[] { "/roll " }, Inputs(Complete("/r")));
        Assert.Equal(new[] { "/help" }, Inputs(Complete("/he")));
    }

    [Fact]
    public void SingleCandidateCarriesItsUsageAndDescription()
    {
        var set = Complete("/t");

        Assert.Equal("/tp <player> - teleport yourself to a player", set.Candidates[0].Detail);
    }

    [Fact]
    public void PlayerArgumentListsTheRoster()
    {
        var set = Complete("/tp ");

        Assert.Equal(Players, Labels(set));
        Assert.Equal(new[] { "/tp Alice", "/tp Alina", "/tp Bob", "/tp Jean Michel" }, Inputs(set));
    }

    [Fact]
    public void PlayerArgumentFiltersCaseInsensitively()
    {
        var set = Complete("/bring aL");

        Assert.Equal(new[] { "/bring Alice", "/bring Alina" }, Inputs(set));
    }

    /// <summary>The last argument swallows the rest of the line: nicknames may contain spaces.</summary>
    [Theory]
    [InlineData("/tp Jean Mi")]
    [InlineData("/tp Jean ")]
    [InlineData("/tp jean mich")]
    public void LastPlayerArgumentMatchesNicknamesWithSpaces(string input)
    {
        var set = Complete(input);

        Assert.Equal(new[] { "/tp Jean Michel" }, Inputs(set));
    }

    [Fact]
    public void PlayerArgumentFollowedByOthersKeepsTheLineGoing()
    {
        var set = Complete("/wave Ali");

        // Trailing space: the emote is still to be typed.
        Assert.Equal(new[] { "/wave Alice ", "/wave Alina " }, Inputs(set));
    }

    /// <summary>
    /// Arguments other than &lt;player&gt; have nothing to complete with, but the usage
    /// stays on screen to say what is expected there.
    /// </summary>
    [Fact]
    public void NonPlayerArgumentsOnlyShowTheUsage()
    {
        var set = Complete("/wave Alice ha");

        Assert.Equal(0, set.Count);
        Assert.Equal("/wave <player> <emote> - wave at a player", set.Usage);

        // An optional argument is a placeholder too, and it is not a player either.
        var roll = Complete("/roll 2");

        Assert.Equal(0, roll.Count);
        Assert.Equal("/roll [sides] - roll a die", roll.Usage);
    }

    [Fact]
    public void ArgumentsBeyondTheUsageCompleteToNothing()
    {
        var set = Complete("/wave Alice hello there ");

        Assert.Equal(0, set.Count);
        Assert.NotEqual("", set.Usage);
    }

    [Fact]
    public void ArgumentsOfACommandWithoutAnyCompleteToNothing()
    {
        var set = Complete("/help foo");

        Assert.Equal(0, set.Count);
        Assert.Equal("/help - show this help", set.Usage);
    }

    [Fact]
    public void UnknownCommandArgumentsCompleteToNothing()
    {
        var set = Complete("/zz Ali");

        Assert.Equal(0, set.Count);
        Assert.Equal("", set.Usage);
    }

    /// <summary>Without a host, /tp and /bring are not offered (the router filters them out).</summary>
    [Fact]
    public void OnlyTheGivenCommandsAreOffered()
    {
        var guestCommands = new[] { new ChatCommandInfo("help", "/help", "show this help") };

        var set = ChatCompletion.Complete("/", guestCommands, Players);

        Assert.Equal(new[] { "/help" }, Labels(set));
    }

    [Fact]
    public void EmptyOrMissingRosterIsHandled()
    {
        Assert.Equal(0, ChatCompletion.Complete("/tp ", Commands, null).Count);
        Assert.Equal(0, ChatCompletion.Complete("/tp ", Commands, new List<string>()).Count);
        Assert.Equal(0, ChatCompletion.Complete("/", null, Players).Count);
    }

    [Fact]
    public void HintShowsTheUsageWhenASingleCandidateMatches()
    {
        var set = Complete("/t");

        Assert.Equal("/tp <player> - teleport yourself to a player",
            ChatCompletion.BuildHint(set, -1, 80));
    }

    [Fact]
    public void HintListsCandidatesAndBracketsTheSelectedOne()
    {
        var set = Complete("/tp ");

        Assert.Equal("Tab: Alice  Alina  Bob  Jean Michel", ChatCompletion.BuildHint(set, -1, 80));
        Assert.Equal("Tab: Alice  [Alina]  Bob  Jean Michel", ChatCompletion.BuildHint(set, 1, 80));
    }

    /// <summary>When the list is wider than the bar, the window follows the selection.</summary>
    [Fact]
    public void HintScrollsToKeepTheSelectionVisible()
    {
        var set = Complete("/tp ");

        var start = ChatCompletion.BuildHint(set, 0, 24);
        Assert.Contains("[Alice]", start);
        Assert.EndsWith(">", start);

        var end = ChatCompletion.BuildHint(set, 3, 24);
        Assert.Contains("[Jean Michel]", end);
        Assert.StartsWith("Tab: <", end);
    }

    [Fact]
    public void HintFallsBackToTheUsageWhenThereIsNothingToCycle()
    {
        Assert.Equal("/wave <player> <emote> - wave at a player",
            ChatCompletion.BuildHint(Complete("/wave Alice ha"), -1, 80));
        Assert.Equal("", ChatCompletion.BuildHint(ChatCompletionSet.Empty, -1, 80));
        Assert.Equal("", ChatCompletion.BuildHint(null, -1, 80));
    }

    [Fact]
    public void HintNeverOverflowsTheBar()
    {
        const int maxChars = 30;
        foreach (var input in new[] { "/", "/t", "/tp ", "/wave Alice ha" })
        {
            var hint = ChatCompletion.BuildHint(Complete(input), 0, maxChars);
            Assert.True(hint.Length <= maxChars + 4, $"{input} produced {hint.Length} chars: {hint}");
        }
    }
}
