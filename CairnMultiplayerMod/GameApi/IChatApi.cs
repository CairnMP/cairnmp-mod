using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Safe access to the multiplayer chat overlay and its host commands.</summary>
internal interface IChatApi
{
    bool IsTyping { get; }

    IGameRegistration Configure(Action<string> send, Func<bool> isHost, Func<bool> canType);
    /// <summary>
    /// Registers a "/name" command. <paramref name="usage"/> is shown by /help and drives
    /// the Tab completion: name the arguments between angle brackets ("/wave
    /// &lt;player&gt; &lt;emote&gt;") and every &lt;player&gt; is completed with the
    /// connected players, at no further cost to the feature.
    /// </summary>
    IGameRegistration AddCommand(string name, string usage, string description, Action<string> execute);
    void AddRemoteLine(string fromName, string message);
    void AddSystemLine(string text);
    void Tick();
    void Draw();
    void ForceClose();
}
