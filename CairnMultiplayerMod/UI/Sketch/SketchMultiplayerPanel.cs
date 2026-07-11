using System;
using System.Collections.Generic;
using System.Text;
using CairnMultiplayerMod.Core;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.UI.Sketch;

/// <summary>
/// Multiplayer panel in the native "sketch" style (photo-mode sprites via GameUiAssetLibrary).
/// Implements IMultiplayerPanel: 4 screens (Host / Join / Browse / Connected) inside a shared frame
/// (icon tabs + title). Networking goes through the same events as the old panel.
/// </summary>
internal sealed class SketchMultiplayerPanel : IMultiplayerPanel
{
    private enum Screen { Host = 0, Join = 1, Browse = 2, Connected = 3 }

    private const int MinSlots = 2, MaxSlots = 8, MaxBrowseRows = 6;
    private static readonly string[] VisLabels = { "Public", "Friends", "Private" };
    private static readonly LobbyVisibility[] VisValues =
        { LobbyVisibility.Public, LobbyVisibility.FriendsOnly, LobbyVisibility.Private };

    // ── State ────────────────────────────────────────────────────────────────────────
    private GameObject _canvasGo;
    private GameObject _contentGo;       // container whose children (title + body) are rebuilt per screen
    private Screen _current = Screen.Host;
    private bool _visible, _isConnected, _isConnecting;
    private string _statusText = "Disconnected";
    private string _lobbyName = "";
    private IReadOnlyList<LobbyEntry> _lobbies;

    // Dynamic refs (rebuilt on every screen change; reset to null otherwise).
    private TextMeshProUGUI _statusLabel;
    private TMP_InputField _codeInput;
    private string _autoLobbyName = "";
    private int _slots = MaxSlots, _visIndex;
    private TextMeshProUGUI _slotsLabel, _visLabel, _createLabel, _joinLabel, _refreshLabel;
    private Button _createBtn, _joinBtn;
    private Transform _browseList;
    private GameObject _browseEmpty;
    private TextMeshProUGUI _connCodeLabel, _connPlayersLabel, _connCountLabel, _connCopyLabel;
    private GameObject _connStart, _connStartHint;

    // ── IMultiplayerPanel events ─────────────────────────────────────────────────
    public event Action<HostConfig> OnHostRequested;
    public event Action<string, string> OnJoinByCodeRequested;
    public event Action OnBrowseRequested;
    public event Action<ulong> OnJoinByLobbyIdRequested;
    public event Action OnDisconnectRequested;
    public event Action OnStartRequested;
    public event Action OnPanelClosed;

    public bool IsVisible => _visible;

    // ── Lifecycle ─────────────────────────────────────────────────────────────────
    public void Show()
    {
        _visible = true;
        EnsureBuilt();
        if (_canvasGo != null) _canvasGo.SetActive(true);
    }

    public void Hide()
    {
        _visible = false;
        DestroyResources();
        OnPanelClosed?.Invoke();
    }

    public void Toggle() { if (_visible) Hide(); else Show(); }

    private void EnsureBuilt()
    {
        if (_canvasGo != null) return;
        GameUiAssetLibrary.EnsureHarvested(force: true);

        _canvasGo = new GameObject("CairnMP_SketchPanel");
        Object.DontDestroyOnLoad(_canvasGo);

        var canvas = _canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 210;                       // above the MainMenu (200)

        var scaler = _canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasGo.AddComponent<GraphicRaycaster>();

        // Dark scrim (blocks clicks behind it).
        var dim = SketchUiKit.Make("Dim", _canvasGo.transform);
        MultiplayerPanelTheme.FullStretch(dim);
        SketchUiKit.FillColor(dim, new Color(0f, 0f, 0f, 0.55f), raycast: true);

        // Centered panel + frame + content area (values: SketchLayout).
        var panel = SketchUiKit.Make("Panel", _canvasGo.transform);
        SketchUiKit.Box(panel, Vector2.zero, new Vector2(720f, 600f));

        var frame = SketchUiKit.Make("Frame", panel.transform);
        SketchUiKit.Rect(frame, Vector2.zero, Vector2.one,
            new Vector2(-SketchLayout.FrameL, -SketchLayout.FrameB),
            new Vector2(SketchLayout.FrameR, SketchLayout.FrameT));
        SketchUiKit.Sliced(frame, GameUiAssetLibrary.Frame, SketchUiKit.FrameTint);

        _contentGo = SketchUiKit.Make("Content", panel.transform);
        SketchUiKit.Rect(_contentGo, Vector2.zero, Vector2.one,
            new Vector2(SketchLayout.ContentL, SketchLayout.ContentB),
            new Vector2(-SketchLayout.ContentR, -SketchLayout.ContentT));

        // Close button (panel's top-right corner).
        var close = SketchUiKit.Make("Close", panel.transform);
        SketchUiKit.Box(close, new Vector2(300f, 246f), new Vector2(38f, 38f));
        SketchUiKit.LabelBox(close.transform, "X", Vector2.zero, new Vector2(40f, 40f),
            "X", 22f, SketchUiKit.TextCream, TextAlignmentOptions.Center);
        SketchUiKit.MakeButton(close, (UnityAction)Hide);

        SwitchTo(_isConnected ? Screen.Connected : Screen.Host);
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────
    private void SwitchTo(Screen screen)
    {
        _current = screen;
        ClearScreenRefs();
        DestroyChildren(_contentGo.transform);

        BuildTitle(screen);
        var body = BuildBodyPanel();

        switch (screen)
        {
            case Screen.Host:      BuildHost(body); break;
            case Screen.Join:      BuildJoin(body); break;
            case Screen.Browse:    BuildBrowse(body); break;
            case Screen.Connected: BuildConnected(body); break;
        }

        UpdateStatusLabel();
        if (screen == Screen.Browse) OnBrowseRequested?.Invoke();
    }

    private void CycleTab(int dir)
    {
        if (_current == Screen.Connected) return;
        int idx = (((int)_current + dir) % 3 + 3) % 3;
        SwitchTo((Screen)idx);
    }

    private void BuildTitle(Screen screen)
    {
        var title = SketchUiKit.Make("TitlePanel", _contentGo.transform);
        SketchUiKit.StretchTop(title, 138f);
        SketchUiKit.Sliced(title, GameUiAssetLibrary.PanelTitle, SketchUiKit.PanelTint);

        if (screen == Screen.Connected)
        {
            // No tabs when connected: the title shows the lobby name.
            var name = string.IsNullOrEmpty(_lobbyName) ? "LOBBY" : _lobbyName;
            SketchUiKit.LabelBox(title.transform, "Title", new Vector2(0f, 0f), new Vector2(520f, 60f),
                name, 32f, SketchUiKit.TextCream, TextAlignmentOptions.Center, logo: true);
            return;
        }

        int active = (int)screen;
        SketchUiKit.Tab(title.transform, GameUiAssetLibrary.IconMountain, new Vector2(-104f, 38f), 54f,
            active == 0, (UnityAction)(() => SwitchTo(Screen.Host)));     // Host = mountain
        SketchUiKit.Tab(title.transform, GameUiAssetLibrary.IconFriend, new Vector2(0f, 38f), 54f,
            active == 1, (UnityAction)(() => SwitchTo(Screen.Join)));     // Join = friend
        SketchUiKit.Tab(title.transform, GameUiAssetLibrary.IconPlayer, new Vector2(104f, 38f), 54f,
            active == 2, (UnityAction)(() => SwitchTo(Screen.Browse)));   // Browse = players

        string label = screen == Screen.Host ? "HOST" : screen == Screen.Join ? "JOIN" : "BROWSE";
        SketchUiKit.LabelBox(title.transform, "Title", new Vector2(0f, -38f), new Vector2(320f, 50f),
            label, 34f, SketchUiKit.TextCream, TextAlignmentOptions.Center, logo: true);
        SketchUiKit.ArrowButton(title.transform, new Vector2(-92f, -38f), new Vector2(26f, 36f), true,
            (UnityAction)(() => CycleTab(-1)));
        SketchUiKit.ArrowButton(title.transform, new Vector2(92f, -38f), new Vector2(26f, 36f), false,
            (UnityAction)(() => CycleTab(+1)));
    }

    private GameObject BuildBodyPanel()
    {
        var body = SketchUiKit.Make("BodyPanel", _contentGo.transform);
        SketchUiKit.StretchFill(body, topInset: 142f, bottomInset: 0f);
        SketchUiKit.Sliced(body, GameUiAssetLibrary.PanelBody, SketchUiKit.PanelTint);
        return body;
    }

    // ── Host screen ───────────────────────────────────────────────────────────────────
    private void BuildHost(GameObject body)
    {
        // Automatic lobby name (Steam name): read-only display, no input anymore.
        _autoLobbyName = $"{GetLocalPlayerName()}'s climb";
        var nameCell = SketchUiKit.RowStrip(body.transform, "Lobby name", 128f, 300f);
        SketchUiKit.Label(nameCell, "Value", _autoLobbyName, 16f, SketchUiKit.TextCream, TextAlignmentOptions.Right);

        _slots = Mathf.Clamp(ModConfig.MaxPlayers?.Value ?? MaxSlots, MinSlots, MaxSlots);
        var slotsCell = SketchUiKit.RowStrip(body.transform, "Slots", 66f, 250f);
        _slotsLabel = SketchUiKit.Stepper(slotsCell, _slots.ToString(),
            (UnityAction)(() => AdjustSlots(-1)), (UnityAction)(() => AdjustSlots(+1)));

        var visCell = SketchUiKit.RowStrip(body.transform, "Visibility", 4f, 250f);
        _visLabel = SketchUiKit.Stepper(visCell, VisLabels[_visIndex],
            (UnityAction)(() => CycleVisibility(-1)), (UnityAction)(() => CycleVisibility(+1)));

        _createBtn = PrimaryButton(body.transform, new Vector2(0f, -78f), new Vector2(420f, 70f),
            "CREATE LOBBY", out _createLabel, (UnityAction)OnCreateClicked);

        _statusLabel = SketchUiKit.LabelBox(body.transform, "Footer", new Vector2(0f, -136f),
            new Vector2(560f, 26f), _statusText, 14f, SketchUiKit.TextDim, TextAlignmentOptions.Center);

        UpdateInteractable();
    }

    // ── Join screen ───────────────────────────────────────────────────────────────────
    private void BuildJoin(GameObject body)
    {
        var codeCell = SketchUiKit.RowStrip(body.transform, "Lobby code", 118f, 250f);
        _codeInput = SketchUiKit.NativeField(codeCell, "XXXX-XXXX", 9);

        SketchUiKit.LabelBox(body.transform, "Hint", new Vector2(0f, 58f), new Vector2(540f, 24f),
            "Enter a friend's lobby code to join.", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Center);

        _joinBtn = PrimaryButton(body.transform, new Vector2(0f, -28f), new Vector2(420f, 70f),
            "JOIN", out _joinLabel, (UnityAction)OnJoinClicked);

        _statusLabel = SketchUiKit.LabelBox(body.transform, "Footer", new Vector2(0f, -120f),
            new Vector2(560f, 26f), _statusText, 14f, SketchUiKit.TextDim, TextAlignmentOptions.Center);

        UpdateInteractable();
    }

    // ── Browse screen ─────────────────────────────────────────────────────────────────
    private void BuildBrowse(GameObject body)
    {
        SketchUiKit.LabelBox(body.transform, "BrowseTitle", new Vector2(-150f, 152f), new Vector2(280f, 28f),
            "Public lobbies", 18f, SketchUiKit.TextCream, TextAlignmentOptions.Left);
        PrimaryButton(body.transform, new Vector2(200f, 152f), new Vector2(150f, 44f),
            "REFRESH", out _refreshLabel, (UnityAction)(() => OnBrowseRequested?.Invoke()));

        _browseList = body.transform;   // rows are placed directly, at fixed positions
        _browseEmpty = SketchUiKit.Make("Empty", body.transform);
        SketchUiKit.Box(_browseEmpty, new Vector2(0f, 0f), new Vector2(540f, 80f));
        SketchUiKit.Label(_browseEmpty.transform, "T", "No public lobbies right now.\nRefresh or host one.",
            15f, SketchUiKit.TextDim, TextAlignmentOptions.Center);

        _statusLabel = SketchUiKit.LabelBox(body.transform, "Footer", new Vector2(0f, -170f),
            new Vector2(560f, 26f), _statusText, 14f, SketchUiKit.TextDim, TextAlignmentOptions.Center);

        RenderBrowseRows();
    }

    private void RenderBrowseRows()
    {
        if (_browseList == null) return;
        // Clean up the old rows.
        var toKill = new List<GameObject>();
        for (int i = 0; i < _browseList.childCount; i++)
        {
            var ch = _browseList.GetChild(i);
            if (ch != null && ch.name.StartsWith("LobbyRow")) toKill.Add(ch.gameObject);
        }
        foreach (var go in toKill) Object.Destroy(go);

        int count = _lobbies?.Count ?? 0;
        if (_browseEmpty != null) _browseEmpty.SetActive(count == 0);

        int shown = Mathf.Min(count, MaxBrowseRows);
        for (int i = 0; i < shown; i++)
        {
            var entry = _lobbies[i];
            ulong id = entry.LobbyId;
            var row = SketchUiKit.Make("LobbyRow", _browseList);
            var rt = row.GetComponent<RectTransform>() ?? row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(-40f, 46f);
            rt.anchoredPosition = new Vector2(0f, 120f - i * 50f);
            SketchUiKit.Sliced(row, GameUiAssetLibrary.RowBg, SketchUiKit.RowTint, raycast: true);

            SketchUiKit.LabelBox(row.transform, "Name", new Vector2(-150f, 9f), new Vector2(300f, 22f),
                entry.Name, 16f, SketchUiKit.TextCream, TextAlignmentOptions.Left);
            SketchUiKit.LabelBox(row.transform, "Host", new Vector2(-150f, -11f), new Vector2(300f, 18f),
                $"Hosted by {entry.HostName}", 12f, SketchUiKit.TextDim, TextAlignmentOptions.Left);
            SketchUiKit.LabelBox(row.transform, "Count", new Vector2(220f, 0f), new Vector2(90f, 24f),
                $"{entry.PlayerCount}/{entry.MaxPlayers}", 16f, SketchUiKit.Accent, TextAlignmentOptions.Right);
            SketchUiKit.MakeButton(row, (UnityAction)(() => OnJoinByLobbyIdRequested?.Invoke(id)));
        }

        if (count > MaxBrowseRows)
            Mod.LogDebug($"[Sketch] browse: {count} lobbies, showing {MaxBrowseRows} (no scroll in v1).");
    }

    // ── Connected screen ───────────────────────────────────────────────────────────────
    private void BuildConnected(GameObject body)
    {
        _connCodeLabel = SketchUiKit.LabelBox(body.transform, "Code", new Vector2(-40f, 132f), new Vector2(340f, 60f),
            "----", 40f, SketchUiKit.Accent, TextAlignmentOptions.Center, logo: true);
        PrimaryButton(body.transform, new Vector2(210f, 132f), new Vector2(120f, 48f),
            "COPY", out _connCopyLabel, (UnityAction)OnCopyClicked);

        SketchUiKit.LabelBox(body.transform, "PlayersHdr", new Vector2(-180f, 76f), new Vector2(200f, 24f),
            "PLAYERS", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Left);
        _connCountLabel = SketchUiKit.LabelBox(body.transform, "Count", new Vector2(200f, 76f), new Vector2(120f, 24f),
            "", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Right);

        _connPlayersLabel = SketchUiKit.LabelBox(body.transform, "Players", new Vector2(0f, 8f), new Vector2(540f, 90f),
            "", 16f, SketchUiKit.TextCream, TextAlignmentOptions.TopLeft);

        _connStart = PrimaryButton(body.transform, new Vector2(0f, -86f), new Vector2(360f, 64f),
            "START CLIMB", out _, (UnityAction)(() => OnStartRequested?.Invoke())).gameObject;
        _connStartHint = SketchUiKit.LabelBox(body.transform, "StartHint", new Vector2(0f, -86f), new Vector2(420f, 30f),
            "Waiting for host to start", 15f, SketchUiKit.TextDim, TextAlignmentOptions.Center).gameObject;

        PrimaryButton(body.transform, new Vector2(0f, -150f), new Vector2(300f, 52f),
            "LEAVE LOBBY", out var leaveLabel, (UnityAction)(() => OnDisconnectRequested?.Invoke()));
        leaveLabel.color = new Color(0.82f, 0.38f, 0.33f, 1f);

        RefreshConnected();
    }

    private void RefreshConnected()
    {
        var lobby = Mod.Instance?.Lobby;
        string code = lobby?.CurrentRoomCode;
        if (_connCodeLabel != null) _connCodeLabel.text = string.IsNullOrEmpty(code) ? "----" : code;

        bool isHost = lobby?.IsHost ?? false;
        if (_connStart != null) _connStart.SetActive(isHost);
        if (_connStartHint != null) _connStartHint.SetActive(!isHost);

        var members = lobby?.Members;
        int count = members?.Count ?? 0;
        if (_connCountLabel != null)
        {
            int max = lobby?.MaxMembers ?? 0;
            _connCountLabel.text = max > 0 ? $"{count} / {max}" : count.ToString();
        }
        if (_connPlayersLabel != null)
        {
            var sb = new StringBuilder();
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var m = members[i];
                    if (m == null) continue;
                    string tag = m.IsSelf && m.IsHost ? " (you, host)" : m.IsSelf ? " (you)" : m.IsHost ? " (host)" : "";
                    sb.Append(m.IsSelf ? "* " : "- ").Append(string.IsNullOrEmpty(m.Name) ? "?" : m.Name).Append(tag).Append('\n');
                }
            }
            else
            {
                sb.Append("* ").Append(GetLocalPlayerName());
            }
            _connPlayersLabel.text = sb.ToString();
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────────────
    private void OnCreateClicked()
    {
        if (_isConnecting || _isConnected) return;
        var cfg = new HostConfig
        {
            PlayerName = GetLocalPlayerName(),
            LobbyName = !string.IsNullOrEmpty(_autoLobbyName) ? _autoLobbyName : $"{GetLocalPlayerName()}'s climb",
            MaxPlayers = _slots,
            Visibility = VisValues[_visIndex],
        };
        OnHostRequested?.Invoke(cfg);
    }

    private void OnJoinClicked()
    {
        if (_isConnecting || _isConnected) return;
        string code = _codeInput != null ? _codeInput.text?.Trim().ToUpperInvariant() : "";
        if (string.IsNullOrEmpty(code)) return;
        OnJoinByCodeRequested?.Invoke(GetLocalPlayerName(), code);
    }

    private void OnCopyClicked()
    {
        var code = Mod.Instance?.Lobby?.CurrentRoomCode;
        if (string.IsNullOrEmpty(code)) return;
        GUIUtility.systemCopyBuffer = code;
        if (_connCopyLabel != null) _connCopyLabel.text = "COPIED";
    }

    private void AdjustSlots(int d)
    {
        _slots = Mathf.Clamp(_slots + d, MinSlots, MaxSlots);
        if (_slotsLabel != null) _slotsLabel.text = _slots.ToString();
    }

    private void CycleVisibility(int d)
    {
        _visIndex = ((_visIndex + d) % VisLabels.Length + VisLabels.Length) % VisLabels.Length;
        if (_visLabel != null) _visLabel.text = VisLabels[_visIndex];
    }

    // ── IMultiplayerPanel: state pushed by Mod.cs ────────────────────────────────────
    public void SetStatus(string status, bool connected)
    {
        _statusText = status ?? "";
        _isConnected = connected;
        _isConnecting = false;

        if (connected && _current != Screen.Connected) SwitchTo(Screen.Connected);
        else if (!connected && _current == Screen.Connected) SwitchTo(Screen.Host);

        UpdateStatusLabel();
        UpdateInteractable();
    }

    public void SetConnecting(string status)
    {
        _statusText = status ?? "";
        _isConnecting = true;
        UpdateStatusLabel();
        UpdateInteractable();
    }

    public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies)
    {
        _lobbies = lobbies;
        if (_refreshLabel != null) _refreshLabel.text = "REFRESH";
        if (_current == Screen.Browse) RenderBrowseRows();
    }

    public void SetCurrentLobbyName(string name)
    {
        _lobbyName = name ?? "";
    }

    public void Tick(float dt)
    {
        if (!_visible) return;
        if (_current == Screen.Connected) RefreshConnected();
    }

    public void OnGUI() { }   // input handled by TMP_InputField

    public void DestroyResources()
    {
        _visible = false;
        if (_canvasGo != null) Object.Destroy(_canvasGo);
        _canvasGo = null;
        _contentGo = null;
        ClearScreenRefs();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────
    private void UpdateStatusLabel()
    {
        if (_statusLabel == null) return;
        Color c = _isConnected ? new Color(0.38f, 0.76f, 0.45f, 1f)
            : _isConnecting ? new Color(0.86f, 0.73f, 0.29f, 1f)
            : (_statusText.StartsWith("Failed") || _statusText.StartsWith("Rejected"))
                ? new Color(0.82f, 0.38f, 0.33f, 1f) : SketchUiKit.TextDim;
        _statusLabel.color = c;
        _statusLabel.text = string.IsNullOrEmpty(_statusText) ? "Disconnected" : _statusText;
    }

    private void UpdateInteractable()
    {
        bool enabled = !_isConnecting && !_isConnected;
        if (_codeInput != null) _codeInput.interactable = enabled;
        if (_createBtn != null) _createBtn.interactable = enabled;
        if (_joinBtn != null) _joinBtn.interactable = enabled;
        if (_createLabel != null) _createLabel.text = _isConnecting ? "CONNECTING..." : "CREATE LOBBY";
        if (_joinLabel != null) _joinLabel.text = _isConnecting ? "CONNECTING..." : "JOIN";
    }

    private Button PrimaryButton(Transform parent, Vector2 pos, Vector2 size, string text,
        out TextMeshProUGUI label, UnityAction onClick)
    {
        var go = SketchUiKit.Make("Button", parent);
        SketchUiKit.Box(go, pos, size);
        SketchUiKit.Sliced(go, GameUiAssetLibrary.Button, Color.white, raycast: true);
        label = SketchUiKit.LabelBox(go.transform, "Label", Vector2.zero, size,
            text, 20f, SketchUiKit.ButtonText, TextAlignmentOptions.Center, logo: true);
        return SketchUiKit.MakeButton(go, onClick);
    }

    private void ClearScreenRefs()
    {
        _statusLabel = null; _codeInput = null;
        _slotsLabel = null; _visLabel = null; _createLabel = null; _joinLabel = null; _refreshLabel = null;
        _createBtn = null; _joinBtn = null; _browseList = null; _browseEmpty = null;
        _connCodeLabel = null; _connPlayersLabel = null; _connCountLabel = null; _connCopyLabel = null;
        _connStart = null; _connStartHint = null;
    }

    private static void DestroyChildren(Transform parent)
    {
        if (parent == null) return;
        var kids = new List<GameObject>();
        for (int i = 0; i < parent.childCount; i++) kids.Add(parent.GetChild(i).gameObject);
        foreach (var go in kids) Object.Destroy(go);
    }

    private static string GetLocalPlayerName()
    {
        var n = Mod.Instance?.Lobby?.LocalPersonaName;
        if (string.IsNullOrEmpty(n)) n = ModConfig.PlayerName?.Value;
        return string.IsNullOrEmpty(n) ? "Player" : n;
    }
}
