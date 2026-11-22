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
    /// <summary>
    /// Save &amp; Quit: warn that placed pitons will not be kept and that the
    /// follower should collect them first, then run <paramref name="onContinue"/>;
    /// cancel leaves Cairn's quit closed.
    /// </summary>
    void ShowQuitPitonSaveWarning(Action onContinue, Action onCancel);
    /// <summary>Close the Save &amp; Quit piton warning without quitting (e.g. pause closed).</summary>
    void DismissQuitPitonSaveWarning();
    void SetStatus(string status, bool connected);
    void SetConnecting(string status);
    void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies);
    void SetCurrentLobbyName(string name);
    void Tick(float dt);
    void OnGUI();
    void DestroyResources();
}
