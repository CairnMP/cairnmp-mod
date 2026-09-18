using System.Collections.Generic;

namespace CairnMultiplayerMod.GameApi;

/// <summary>One line of a standings table: a rank, who it is, and how they are doing.</summary>
internal readonly struct StandingRow
{
    internal StandingRow(int rank, string name, string detail, bool isLocal, bool isOut)
    {
        Rank = rank;
        Name = name ?? "";
        Detail = detail ?? "";
        IsLocal = isLocal;
        IsOut = isOut;
    }

    public int Rank { get; }
    public string Name { get; }
    public string Detail { get; }

    /// <summary>The local player's own line, highlighted so it is found at a glance.</summary>
    public bool IsLocal { get; }

    /// <summary>A climber who is out of the running -- dimmed, but still ranked.</summary>
    public bool IsOut { get; }
}

/// <summary>Simple transient text messages without exposing Unity IMGUI.</summary>
internal interface IGameHudApi
{
    void ShowMessage(string id, string text, float durationSeconds);
    void HideMessage(string id);

    /// <summary>
    /// Shows a standings table until it is hidden. Unlike a message it does not expire: it is
    /// the state of the run, not a notification, and the caller decides when it stops being true.
    /// </summary>
    void ShowStandings(string title, IReadOnlyList<StandingRow> rows);

    void HideStandings();
}
