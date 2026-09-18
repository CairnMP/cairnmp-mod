using System.IO;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class GameModeTests
{
    [Fact]
    public void StartGameCarriesTheLobbyMode()
    {
        var sent = new ServerStartGame
        {
            Difficulty = (int)GameDifficulty.FreeSolo,
            SkipTutorials = true,
            SkipPractice = false,
            AssistEnabled = false,
            Mode = (byte)MultiplayerMode.FreeSolo,
            ExtraConstraints = (int)ClimbConstraints.PermaDeath,
        };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            sent.Serialize(writer);
        stream.Position = 0;
        var received = new ServerStartGame();
        using (var reader = new BinaryReader(stream))
            received.Deserialize(reader);

        Assert.Equal(sent.Difficulty, received.Difficulty);
        Assert.Equal(sent.Mode, received.Mode);
        Assert.Equal(sent.ExtraConstraints, received.ExtraConstraints);
        Assert.Equal(sent.SkipTutorials, received.SkipTutorials);
        Assert.Equal(sent.SkipPractice, received.SkipPractice);
    }

    [Fact]
    public void EveryModeNamesADifficultyTheGameKnows()
    {
        foreach (var rules in MultiplayerModes.Available)
        {
            Assert.NotEqual(GameDifficulty.Invalid, rules.Difficulty);
            Assert.False(string.IsNullOrWhiteSpace(rules.Name));
            Assert.False(string.IsNullOrWhiteSpace(rules.Summary));
        }
    }

    [Fact]
    public void FreeSoloForbidsGettingBackUpButKeepsYouWatching()
    {
        var rules = MultiplayerModes.RulesFor(MultiplayerMode.FreeSolo);

        Assert.False(rules.TeammateRevive);
        Assert.True(rules.SpectateAfterDeath);
        Assert.True(rules.ExtraConstraints.HasFlag(ClimbConstraints.PermaDeath));
        Assert.Equal(GameDifficulty.FreeSolo, rules.Difficulty);
    }

    [Fact]
    public void EveryModeTellsAFallenClimberWhatToExpect()
    {
        foreach (var rules in MultiplayerModes.Available)
        {
            var line = MultiplayerModeText.PromiseLine(rules);
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.NotEqual("Unknown", MultiplayerModeText.DifficultyName(rules.Difficulty));
        }
    }

    [Fact]
    public void AModeWithNoWayBackSaysSoInsteadOfSayingNothing()
    {
        var hopeless = new MultiplayerModeRules(MultiplayerMode.Race, "Sprint", "Go.",
            GameDifficulty.Alpinist, ClimbConstraints.None,
            teammateRevive: false, bivouacRevive: false, spectateAfterDeath: false);

        Assert.Empty(MultiplayerModeText.Promises(hopeless));
        Assert.Equal("No way back once you fall", MultiplayerModeText.PromiseLine(hopeless));
    }

    [Fact]
    public void RopeTeamPromisesBothWaysHome()
    {
        var promises = MultiplayerModeText.Promises(MultiplayerModes.RopeTeam);

        Assert.Contains("Rescue on the face", promises);
        Assert.Contains("Brought back at camp", promises);
    }

    [Fact]
    public void AnUnknownModeReadsAsTheDefaultRatherThanThrowing()
    {
        Assert.Equal(MultiplayerMode.RopeTeam, MultiplayerModes.Parse(200).Mode);
        Assert.Equal(MultiplayerMode.RopeTeam, MultiplayerModes.Parse(-1).Mode);
    }
}
