using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Safe access to the multiplayer chat overlay and its host commands.</summary>
internal interface IChatApi
{
    bool IsTyping { get; }

    IGameRegistration Configure(Action<string> send, Func<bool> isHost, Func<bool> canType);
    void AddRemoteLine(string fromName, string message);
    void AddSystemLine(string text);
    void Tick();
    void Draw();
    void ForceClose();
}
