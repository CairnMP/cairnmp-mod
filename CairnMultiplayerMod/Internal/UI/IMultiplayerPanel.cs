using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Networking;

namespace CairnMultiplayerMod.Internal.UI;

internal interface IMultiplayerPanel
{
    event Action<HostConfig> OnHostRequested;
    event Action<string> OnJoinByCodeRequested;
    event Action OnBrowseRequested;
    event Action<ulong> OnJoinByLobbyIdRequested;
    event Action OnDisconnectRequested;
    event Action OnStartRequested;
    event Action OnPanelClosed;

    bool IsVisible { get; }

    void Show();
    void Hide();
    void Toggle();
    void SetStatus(string status, bool connected);
    void SetConnecting(string status);
    void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies);
    void SetCurrentLobbyName(string name);
    void Tick(float dt);
    void OnGUI();
    void DestroyResources();
}
