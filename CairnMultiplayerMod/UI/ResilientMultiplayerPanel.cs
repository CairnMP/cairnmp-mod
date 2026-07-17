using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.UI;

internal sealed class ResilientMultiplayerPanel : IMultiplayerPanel
{
    private readonly Func<IMultiplayerPanel> _fallbackFactory;
    private IMultiplayerPanel _active;
    private bool _usingFallback;
    private bool _visible;
    private bool _connecting;
    private bool _connected;
    private string _status = "";
    private string _lobbyName = "";
    private IReadOnlyList<LobbyEntry> _lastLobbies = Array.Empty<LobbyEntry>();

    public event Action<HostConfig> OnHostRequested;
    public event Action<string, string> OnJoinByCodeRequested;
    public event Action OnBrowseRequested;
    public event Action<ulong> OnJoinByLobbyIdRequested;
    public event Action OnDisconnectRequested;
    public event Action OnStartRequested;
    public event Action OnPanelClosed;

    public bool IsVisible => _visible;

    public ResilientMultiplayerPanel(IMultiplayerPanel primary, Func<IMultiplayerPanel> fallbackFactory)
    {
        _active = primary;
        _fallbackFactory = fallbackFactory;
        Wire(primary);
    }

    public void Show()
    {
        _visible = true;
        Try(panel => panel.Show());
    }

    public void Hide()
    {
        _visible = false;
        Try(panel => panel.Hide());
    }

    public void Toggle()
    {
        if (_visible) Hide();
        else Show();
    }

    public void SetStatus(string status, bool connected)
    {
        _status = status ?? "";
        _connected = connected;
        _connecting = false;
        Try(panel => panel.SetStatus(_status, connected));
    }

    public void SetConnecting(string status)
    {
        _status = status ?? "";
        _connecting = true;
        Try(panel => panel.SetConnecting(_status));
    }

    public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies)
    {
        _lastLobbies = lobbies ?? Array.Empty<LobbyEntry>();
        Try(panel => panel.SetBrowserLobbies(_lastLobbies));
    }

    public void SetCurrentLobbyName(string name)
    {
        _lobbyName = name ?? "";
        Try(panel => panel.SetCurrentLobbyName(_lobbyName));
    }

    public void Tick(float dt)
    {
        Try(panel => panel.Tick(dt));
    }

    public void OnGUI()
    {
        Try(panel => panel.OnGUI());
    }

    public void DestroyResources()
    {
        _visible = false;
        Try(panel => panel.DestroyResources());
    }

    private void Try(Action<IMultiplayerPanel> action)
    {
        if (_usingFallback)
        {
            action(_active);
            return;
        }

        try
        {
            action(_active);
        }
        catch (Exception ex)
        {
            var fallback = SwitchToFallback(ex);
            action(fallback);
        }
    }

    private IMultiplayerPanel SwitchToFallback(Exception ex)
    {
        if (_usingFallback)
            return _active;

        Mod.Log.Warning($"[CairnMP] UI Toolkit panel failed at runtime, switching to legacy Canvas UI: {ex.Message}");
        _usingFallback = true;
        _active = _fallbackFactory();
        Wire(_active);

        _active.SetCurrentLobbyName(_lobbyName);
        _active.SetBrowserLobbies(_lastLobbies);
        if (_connecting) _active.SetConnecting(_status);
        else _active.SetStatus(_status, _connected);
        if (_visible) _active.Show();

        return _active;
    }

    private void Wire(IMultiplayerPanel panel)
    {
        panel.OnHostRequested += cfg => OnHostRequested?.Invoke(cfg);
        panel.OnJoinByCodeRequested += (name, code) => OnJoinByCodeRequested?.Invoke(name, code);
        panel.OnBrowseRequested += () => OnBrowseRequested?.Invoke();
        panel.OnJoinByLobbyIdRequested += id => OnJoinByLobbyIdRequested?.Invoke(id);
        panel.OnDisconnectRequested += () => OnDisconnectRequested?.Invoke();
        panel.OnStartRequested += () => OnStartRequested?.Invoke();
        panel.OnPanelClosed += () => OnPanelClosed?.Invoke();
    }
}
