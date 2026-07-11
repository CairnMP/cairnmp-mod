using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using CairnMultiplayerMod.Core;
using CairnMultiplayerMod.UI.Screens;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.UI;

/// <summary>
/// Multiplayer panel: orchestrates the Home / Browser / Connected screens,
/// routes events to Mod.cs and receives state pushes (status, lobbies).
///
/// The panel never writes to NetworkManager directly — everything goes through
/// the public events, consumed on the Mod.cs side. This lets us swap the network
/// layer (Steam Relay) without touching the UI.
/// </summary>
public sealed class MultiplayerPanel : IMultiplayerPanel
{
    private enum Screen { Home, Browser, Connected }

    private GameObject _panelRoot;
    private GameObject _canvasRoot;
    private GameObject _card;
    private TextMeshProUGUI _statusTmp;

    private HomeScreen _homeScreen;
    private BrowserScreen _browserScreen;
    private ConnectedScreen _connectedScreen;

    private Screen _currentScreen = Screen.Home;
    private bool _visible;
    private bool _isConnected;
    private bool _isConnecting;
    private string _statusText = "";
    private string _lobbyName  = "";

    // ── Current configuration ─────────────────────────────────────────────────

    private const float CW = 720f;
    private const float CH = 820f;

    // ── Public events ─────────────────────────────────────────────────────────

    public event Action<HostConfig>           OnHostRequested;
    public event Action<string,string>        OnJoinByCodeRequested;   // (playerName, code)
    public event Action                       OnBrowseRequested;
    public event Action<ulong>                OnJoinByLobbyIdRequested;
    public event Action                       OnDisconnectRequested;
    public event Action                       OnStartRequested;
    public event Action                       OnPanelClosed;

    public bool IsVisible => _visible;

    // ── Public API ────────────────────────────────────────────────────────────

    public void Show()
    {
        _visible = true;
        EnsureCanvas();
        if (_panelRoot != null) _panelRoot.SetActive(true);
        UpdateScreenVisibility();
        UpdateStatus();
    }

    public void Hide()
    {
        _visible = false;
        if (_panelRoot != null) _panelRoot.SetActive(false);
        OnPanelClosed?.Invoke();
    }

    public void Toggle() { if (_visible) Hide(); else Show(); }

    public void SetStatus(string status, bool connected)
    {
        _statusText   = status ?? "";
        _isConnected  = connected;
        _isConnecting = false;
        if (connected) _currentScreen = Screen.Connected;
        else if (_currentScreen == Screen.Connected) _currentScreen = Screen.Home;

        UpdateScreenVisibility();
        UpdateStatus();
        UpdateInteractable();
    }

    public void SetConnecting(string status)
    {
        _statusText   = status ?? "";
        _isConnecting = true;
        UpdateStatus();
        UpdateInteractable();
    }

    /// <summary>Pushes a new list of lobbies into the browser. Called by Mod.cs.</summary>
    public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies)
    {
        if (_browserScreen == null) return;
        _browserScreen.SetLobbies(lobbies);
        _browserScreen.SetRefreshing(false);
    }

    /// <summary>Stores the name of the currently connected lobby to show it as the title.</summary>
    public void SetCurrentLobbyName(string name)
    {
        _lobbyName = name ?? "";
    }

    /// <summary>Per-frame tick from Mod.OnUpdate — refreshes the player list while connected.</summary>
    public void Tick(float dt)
    {
        if (!_visible) return;
        if (_currentScreen == Screen.Connected && _connectedScreen != null)
        {
            _connectedScreen.Refresh(_lobbyName, Mod.Instance?.Lobby,
                ModConfig.PlayerName.Value);
        }
    }

    /// <summary>No-op: keyboard input is handled by TMP_InputField, IMGUI is no longer needed.</summary>
    public void OnGUI() { }

    public void DestroyResources()
    {
        _visible = false;

        if (_canvasRoot != null)
            Object.Destroy(_canvasRoot);

        _canvasRoot = null;
        _panelRoot = null;
        _card = null;
        _statusTmp = null;
        _homeScreen = null;
        _browserScreen = null;
        _connectedScreen = null;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    private void EnsureCanvas()
    {
        if (_panelRoot != null) return;
        try { BuildCanvas(); }
        catch (Exception ex) { Mod.Log.Error($"[MultiplayerPanel] Build failed: {ex}"); }
    }

    private void BuildCanvas()
    {
        var font = MainMenuMultiplayerButton.CapturedFont;

        var canvasGo = new GameObject("MP_Canvas");
        _canvasRoot = canvasGo;
        Object.DontDestroyOnLoad(canvasGo);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        _panelRoot = MultiplayerPanelTheme.MakeGo("PanelRoot", canvasGo.transform);
        MultiplayerPanelTheme.FullStretch(_panelRoot);

        // Clickable dark overlay (does not close — only the close button does).
        var overlay = MultiplayerPanelTheme.MakeGo("Overlay", _panelRoot.transform);
        MultiplayerPanelTheme.FullStretch(overlay);
        MultiplayerPanelTheme.Fill(overlay, MultiplayerPanelTheme.Overlay, raycast: true);

        // Centered card
        _card = MultiplayerPanelTheme.MakeGo("Card", _panelRoot.transform);
        var cardRT = _card.AddComponent<RectTransform>();
        cardRT.anchorMin = cardRT.anchorMax = cardRT.pivot = new Vector2(0.5f, 0.5f);
        cardRT.sizeDelta = new Vector2(CW, CH);
        cardRT.anchoredPosition = Vector2.zero;
        MultiplayerPanelTheme.Fill(_card, MultiplayerPanelTheme.CardBg, raycast: true);
        MultiplayerPanelTheme.DrawBorder(_card.transform, MultiplayerPanelTheme.Border);

        // Header: title + close button
        var titleEyebrow = MultiplayerPanelTheme.Tmp(_card.transform, "Eyebrow", "CAIRN MULTIPLAYER",
            font, 10, MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.TopLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(titleEyebrow.gameObject, new Vector2(0.06f, 0.94f), new Vector2(0.94f, 0.99f));
        titleEyebrow.characterSpacing = 6f;

        BuildCloseButton(font);

        // Screens container (inner area below the header, above the status footer)
        var screensRoot = MultiplayerPanelTheme.MakeGo("Screens", _card.transform);
        MultiplayerPanelTheme.Anchor(screensRoot, new Vector2(0.06f, 0.10f), new Vector2(0.94f, 0.93f));

        // Build the 3 screens (only one active at a time)
        _homeScreen      = new HomeScreen(screensRoot.transform, font,
            ModConfig.PlayerName.Value, Mathf.Clamp(ModConfig.MaxPlayers.Value, 2, 8));
        _browserScreen   = new BrowserScreen(screensRoot.transform, font);
        _connectedScreen = new ConnectedScreen(screensRoot.transform, font);

        WireScreenEvents();

        _browserScreen.Root.SetActive(false);
        _connectedScreen.Root.SetActive(false);

        // Footer: status
        var sep = MultiplayerPanelTheme.MakeGo("Sep", _card.transform);
        MultiplayerPanelTheme.Anchor(sep, new Vector2(0.06f, 0.085f), new Vector2(0.94f, 0.087f));
        MultiplayerPanelTheme.Fill(sep, MultiplayerPanelTheme.BorderSubtle);

        _statusTmp = MultiplayerPanelTheme.Tmp(_card.transform, "Status", "",
            font, 11, MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.Center, FontStyles.Italic);
        MultiplayerPanelTheme.Anchor(_statusTmp.gameObject, new Vector2(0.06f, 0.02f), new Vector2(0.94f, 0.075f));

        Mod.Log.Msg($"[MultiplayerPanel] Canvas built ({CW}x{CH}).");
    }

    private void BuildCloseButton(TMP_FontAsset font)
    {
        var go = MultiplayerPanelTheme.MakeGo("CloseBtn", _card.transform);
        MultiplayerPanelTheme.Anchor(go, new Vector2(0.92f, 0.94f), new Vector2(0.99f, 0.99f));
        var img = go.AddComponent<Image>();
        img.color         = new Color(0, 0, 0, 0);
        img.raycastTarget = true;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener((UnityAction)Hide);

        MultiplayerPanelTheme.Tmp(go.transform, "X", "✕", font, 14,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.Center, FontStyles.Normal);
    }

    private void WireScreenEvents()
    {
        _homeScreen.HostRequested        += cfg => OnHostRequested?.Invoke(cfg);
        _homeScreen.JoinByCodeRequested  += (n, c) => OnJoinByCodeRequested?.Invoke(n, c);
        _homeScreen.BrowseRequested      += () =>
        {
            _currentScreen = Screen.Browser;
            UpdateScreenVisibility();
            _browserScreen.SetRefreshing(true);
            OnBrowseRequested?.Invoke();
        };
        _homeScreen.PlayerNameChanged    += name =>
        {
            if (!string.IsNullOrWhiteSpace(name))
                ModConfig.PlayerName.Value = name.Trim();
        };

        _browserScreen.BackRequested     += () => { _currentScreen = Screen.Home; UpdateScreenVisibility(); };
        _browserScreen.RefreshRequested  += () =>
        {
            _browserScreen.SetRefreshing(true);
            OnBrowseRequested?.Invoke();
        };
        _browserScreen.JoinByLobbyIdRequested += id => OnJoinByLobbyIdRequested?.Invoke(id);

        _connectedScreen.DisconnectRequested    += () => OnDisconnectRequested?.Invoke();
        _connectedScreen.StartRequested         += () => OnStartRequested?.Invoke();
        _connectedScreen.CopyCodeRequested      += () =>
        {
            var code = Mod.Instance?.Lobby?.CurrentRoomCode;
            if (string.IsNullOrEmpty(code)) return;
            try
            {
                GUIUtility.systemCopyBuffer = code;
                _connectedScreen.SetCopyFeedback(true);
                _statusText = $"Copied {code}";
                UpdateStatus();
            }
            catch (Exception ex) { Mod.Log.Warning($"[MultiplayerPanel] Copy failed: {ex.Message}"); }
        };
    }

    // ── State updates ─────────────────────────────────────────────────────────

    private void UpdateScreenVisibility()
    {
        if (_homeScreen == null) return;
        _homeScreen.Root.SetActive(_currentScreen == Screen.Home);
        _browserScreen.Root.SetActive(_currentScreen == Screen.Browser);
        _connectedScreen.Root.SetActive(_currentScreen == Screen.Connected);
    }

    private void UpdateInteractable()
    {
        if (_homeScreen == null) return;
        _homeScreen.SetInteractable(!_isConnecting && !_isConnected, _isConnecting);
    }

    private void UpdateStatus()
    {
        if (_statusTmp == null) return;
        if (_isConnecting)
        {
            _statusTmp.color = MultiplayerPanelTheme.StatusWarn;
            _statusTmp.text  = string.IsNullOrEmpty(_statusText) ? "● Connecting…" : $"● {_statusText}";
        }
        else if (_isConnected)
        {
            _statusTmp.color = MultiplayerPanelTheme.StatusOk;
            _statusTmp.text  = string.IsNullOrEmpty(_statusText) ? "● Connected" : $"● {_statusText}";
        }
        else
        {
            bool isError = !string.IsNullOrEmpty(_statusText) &&
                           (_statusText.StartsWith("Failed") || _statusText.StartsWith("Rejected"));
            _statusTmp.color = isError ? MultiplayerPanelTheme.StatusError : MultiplayerPanelTheme.TextMuted;
            _statusTmp.text  = string.IsNullOrEmpty(_statusText) ? "○ Disconnected" : _statusText;
        }
    }
}
