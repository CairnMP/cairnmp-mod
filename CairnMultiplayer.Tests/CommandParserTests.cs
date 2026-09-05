#nullable enable

using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class CommandParserTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a command")]
    public void Parse_NonSlashLine_IsNotCommand(string input)
    {
        var parsed = CommandParser.Parse(input);
        Assert.False(parsed.IsCommand);
    }

    [Fact]
    public void Parse_SimpleCommand_NoArgs()
    {
        var parsed = CommandParser.Parse("/help");
        Assert.True(parsed.IsCommand);
        Assert.Equal("help", parsed.Name);
        Assert.Empty(parsed.Args);
        Assert.Equal("", parsed.ArgsText);
    }

    [Fact]
    public void Parse_LowercasesName()
    {
        var parsed = CommandParser.Parse("/BRING Bob");
        Assert.Equal("bring", parsed.Name);
        Assert.Equal(new[] { "Bob" }, parsed.Args);
    }

    [Fact]
    public void Parse_SingleArg()
    {
        var parsed = CommandParser.Parse("/tp Bob");
        Assert.Equal("tp", parsed.Name);
        Assert.Equal(new[] { "Bob" }, parsed.Args);
        Assert.Equal("Bob", parsed.ArgsText);
    }

    [Fact]
    public void Parse_CollapsesMultipleSpaces_InArgs()
    {
        var parsed = CommandParser.Parse("/tp   John    Doe");
        Assert.Equal("tp", parsed.Name);
        Assert.Equal(new[] { "John", "Doe" }, parsed.Args);
    }

    [Fact]
    public void Parse_ArgsText_PreservesMultiWordTarget()
    {
        var parsed = CommandParser.Parse("/tp John Doe");
        Assert.Equal("John Doe", parsed.ArgsText);
    }

    [Theory]
    [InlineData("  /help  ", "help")]
    [InlineData("/HeLp", "help")]
    public void Parse_TrimsAndNormalizes(string input, string expectedName)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsCommand);
        Assert.Equal(expectedName, parsed.Name);
    }

    [Fact]
    public void Parse_BareSlash_IsCommandWithEmptyName()
    {
        var parsed = CommandParser.Parse("/");
        Assert.True(parsed.IsCommand);
        Assert.Equal("", parsed.Name);
        Assert.Empty(parsed.Args);
    }
}
