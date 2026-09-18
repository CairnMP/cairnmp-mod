using System.Collections.Generic;

namespace CairnMultiplayer.Shared;

/// <summary>
/// The rule sets a lobby can be played under. The wire value is stable: renaming an entry
/// is free, renumbering one breaks compatibility with older clients.
/// </summary>
public enum MultiplayerMode : byte
{
    /// <summary>Everyone on the same rope, teammates can pick each other up. The default.</summary>
    RopeTeam = 0,

    /// <summary>No rope, no second chance: death is final and the climber becomes a spectator.</summary>
    FreeSolo = 1,

    /// <summary>A timed ascent, scored stage by stage.</summary>
    Race = 2,
}

/// <summary>
/// Mirror of Cairn's own <c>GamemodeConstraints</c> flags — same names, same values, so the
/// mod can carry a run's constraints over the wire and hand them straight to the game.
/// </summary>
[System.Flags]
public enum ClimbConstraints
{
    None = 0,
    PermaDeath = 1,
    NoStory = 8,
    NoClimbot = 16,
    MeaninglessDeath = 64,
    NoBoosts = 128,
    NoPhysiologicalNeeds = 256,
    NoTutorials = 512,
    CanWarp = 1024,
    NoAssistMode = 2048,
}

/// <summary>What a mode decides. The game owns the climbing rules; these are the ones only a
/// session can answer — what happens to a player who dies, and who may undo it.</summary>
public readonly struct MultiplayerModeRules
{
    public MultiplayerModeRules(MultiplayerMode mode, string name, string summary,
        GameDifficulty difficulty, ClimbConstraints extraConstraints,
        bool teammateRevive, bool bivouacRevive, bool spectateAfterDeath)
    {
        Mode = mode;
        Name = name ?? "";
        Summary = summary ?? "";
        Difficulty = difficulty;
        ExtraConstraints = extraConstraints;
        TeammateRevive = teammateRevive;
        BivouacRevive = bivouacRevive;
        SpectateAfterDeath = spectateAfterDeath;
    }

    public MultiplayerMode Mode { get; }

    /// <summary>Fallback label. The panel prefers the game's own localised mode name.</summary>
    public string Name { get; }
    public string Summary { get; }

    /// <summary>The native difficulty every player launches with.</summary>
    public GameDifficulty Difficulty { get; }

    /// <summary>Added on top of the constraints the chosen difficulty already carries.</summary>
    public ClimbConstraints ExtraConstraints { get; }

    /// <summary>A teammate can put a downed climber back on their feet where they fell.</summary>
    public bool TeammateRevive { get; }

    /// <summary>The dead can be brought back at a bivouac by whoever is still standing.</summary>
    public bool BivouacRevive { get; }

    /// <summary>A player with no way back watches the climb instead of ending their session.</summary>
    public bool SpectateAfterDeath { get; }
}

/// <summary>The modes CairnMP ships, and the only place their rules are written down.</summary>
public static class MultiplayerModes
{
    public static readonly MultiplayerModeRules RopeTeam = new(
        MultiplayerMode.RopeTeam,
        "Rope team",
        "Climb roped together. A downed partner can be brought back on the spot.",
        GameDifficulty.Alpinist,
        ClimbConstraints.None,
        teammateRevive: true,
        bivouacRevive: true,
        spectateAfterDeath: false);

    public static readonly MultiplayerModeRules FreeSolo = new(
        MultiplayerMode.FreeSolo,
        "Free solo",
        "No rope, no safety net. Death is final — you keep watching, you do not climb again.",
        GameDifficulty.FreeSolo,
        ClimbConstraints.PermaDeath,
        teammateRevive: false,
        bivouacRevive: true,
        spectateAfterDeath: true);

    public static readonly MultiplayerModeRules Race = new(
        MultiplayerMode.Race,
        "Race",
        "Climb higher than anyone else. Top out first and it is yours outright.",
        GameDifficulty.Alpinist,
        ClimbConstraints.None,
        teammateRevive: false,
        bivouacRevive: false,
        spectateAfterDeath: true);

    private static readonly MultiplayerModeRules[] All = { RopeTeam, FreeSolo, Race };

    public static IReadOnlyList<MultiplayerModeRules> Available => All;

    public static MultiplayerModeRules RulesFor(MultiplayerMode mode)
    {
        foreach (var rules in All)
            if (rules.Mode == mode) return rules;
        return RopeTeam;
    }

    /// <summary>Reads a mode off the wire, falling back to the default rather than throwing:
    /// a lobby advertised by a newer client must stay joinable.</summary>
    public static MultiplayerModeRules Parse(int wireValue)
        => wireValue >= 0 && wireValue <= byte.MaxValue
            ? RulesFor((MultiplayerMode)(byte)wireValue)
            : RopeTeam;
}

/// <summary>
/// How a mode is put into words. It lives here rather than in a panel because both the
/// in-game panel and its fallback have to say the same thing about the same lobby.
/// </summary>
public static class MultiplayerModeText
{
    public static string DifficultyName(GameDifficulty difficulty) => difficulty switch
    {
        GameDifficulty.Alpinist => "Alpinist",
        GameDifficulty.Explorer => "Explorer",
        GameDifficulty.FreeSolo => "Free solo",
        GameDifficulty.FreeRoam => "Free roam",
        _ => "Unknown",
    };

    /// <summary>What the mode promises a climber who falls, in reading order.</summary>
    public static IReadOnlyList<string> Promises(MultiplayerModeRules rules)
    {
        var promises = new List<string>(3);
        if (rules.TeammateRevive) promises.Add("Rescue on the face");
        if (rules.BivouacRevive) promises.Add("Brought back at camp");
        if (rules.SpectateAfterDeath) promises.Add("Watch after death");
        return promises;
    }

    /// <summary>
    /// The promises on one line. A mode that makes none says so plainly: an empty line would
    /// read as missing information rather than as the point of the mode.
    /// </summary>
    public static string PromiseLine(MultiplayerModeRules rules, string separator = "   -   ")
    {
        var promises = Promises(rules);
        return promises.Count == 0
            ? "No way back once you fall"
            : string.Join(separator, promises);
    }
}
