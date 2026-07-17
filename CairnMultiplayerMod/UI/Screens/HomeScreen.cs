using System;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CairnMultiplayerMod.UI.Screens;

/// <summary>
/// Home screen of the multiplayer panel. Shows a Player name field followed
/// by 3 stacked cards (Host, Join, Browse). HOST and JOIN expand inline;
/// BROWSE raises an event to navigate to the BrowserScreen.
/// </summary>
internal sealed class HomeScreen
{
    private enum Expanded { None, Host, Join }

    private readonly TMP_FontAsset _font;

    private LobbyTextField _playerNameField;
    private LobbyTextField _lobbyNameField;
    private LobbyTextField _codeField;
    private StepperControl _slotsStepper;
    private SegmentedControl _visibilitySegment;

    // Cards
    private GameObject _hostCard, _joinCard, _browseCard;
    private GameObject _hostCollapsed, _hostExpanded;
    private GameObject _joinCollapsed, _joinExpanded;
    private Image _hostBorder, _joinBorder, _browseBorder;
    private GameObject _hostStripe, _joinStripe;
    private TextMeshProUGUI _hostGlyphCollapsed, _joinGlyphCollapsed;

    // Action button labels (mutable during Connecting)
    private TextMeshProUGUI _createBtnLabel;
    private TextMeshProUGUI _joinBtnLabel;
    private Button _createBtn;
    private Button _joinBtn;
    private Button _browseBtn;

    private Expanded _expanded = Expanded.None;
    private bool _isConnecting;
    private bool _inputsEnabled = true;

    public GameObject Root { get; }

    public event Action<HostConfig>           HostRequested;
    public event Action<string,string>        JoinByCodeRequested;
    public event Action                       BrowseRequested;
    /// <summary>Raised when the player name loses focus (persist to ModConfig).</summary>
    public event Action<string>               PlayerNameChanged;

    public HomeScreen(Transform parent, TMP_FontAsset font, string initialPlayerName, int initialMaxPlayers)
    {
        _font = font;
        Root  = MultiplayerPanelTheme.MakeGo("HomeScreen", parent);
        MultiplayerPanelTheme.FullStretch(Root);

        BuildPlayerNameSection(initialPlayerName);
        BuildHostCard(initialPlayerName, initialMaxPlayers);
        BuildJoinCard();
        BuildBrowseCard();

        UpdateCardsState();
    }

    public void SetInteractable(bool value, bool isConnecting)
    {
        _inputsEnabled = value;
        _isConnecting = isConnecting;
        _playerNameField.SetInteractable(value);
        _lobbyNameField.SetInteractable(value);
        _codeField.SetInteractable(value);
        _slotsStepper.SetInteractable(value);
        _visibilitySegment.SetInteractable(value);
        _joinBtn.interactable   = value;
        _browseBtn.interactable = value;
        if (_createBtn != null)
            _createBtn.interactable = value && !isConnecting;

        if (isConnecting)
        {
            if (_expanded == Expanded.Host && _createBtnLabel != null) _createBtnLabel.text = "Connecting…";
            if (_expanded == Expanded.Join && _joinBtnLabel   != null) _joinBtnLabel.text   = "Connecting…";
        }
        else
        {
            if (_createBtnLabel != null) _createBtnLabel.text = "CREATE LOBBY";
            if (_joinBtnLabel   != null) _joinBtnLabel.text   = "JOIN";
        }
    }

    // ── Construction ─────────────────────────────────────────────────────────

    private void BuildPlayerNameSection(string initial)
    {
        // Header
        var title = MultiplayerPanelTheme.Tmp(Root.transform, "Title", "Play together", _font, 22,
            MultiplayerPanelTheme.TextPrimary, TextAlignmentOptions.TopLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(title.gameObject, new Vector2(0f, 0.91f), new Vector2(1f, 0.99f));

        var label = MultiplayerPanelTheme.Tmp(Root.transform, "PlayerNameLbl", "PLAYER NAME", _font, 10,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.BottomLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(label.gameObject, new Vector2(0f, 0.85f), new Vector2(1f, 0.90f));
        label.characterSpacing = 2f;

        _playerNameField = new LobbyTextField(Root.transform, _font, "Your in-game name", 24);
        MultiplayerPanelTheme.Anchor(_playerNameField.Root, new Vector2(0f, 0.78f), new Vector2(1f, 0.85f));
        _playerNameField.Value = initial ?? "";
        _playerNameField.Blurred += v => PlayerNameChanged?.Invoke(v);
    }

    private void BuildHostCard(string initialPlayerName, int initialMaxPlayers)
    {
        _hostCard = MultiplayerPanelTheme.MakeGo("HostCard", Root.transform);
        MultiplayerPanelTheme.Anchor(_hostCard, new Vector2(0f, 0.55f), new Vector2(1f, 0.76f));
        // Neutral border by default; turns gold only when expanded.
        _hostBorder = MultiplayerPanelTheme.Fill(_hostCard, MultiplayerPanelTheme.Border);

        // OPAQUE Inner — otherwise the border color (gold) bleeds through the alpha and the
        // card shows as one solid gold block. The active color is carried by _hostBorder.
        var hostInner = MultiplayerPanelTheme.MakeGo("Inner", _hostCard.transform);
        MultiplayerPanelTheme.Anchor(hostInner, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        MultiplayerPanelTheme.Fill(hostInner, MultiplayerPanelTheme.CardBg, raycast: true);

        // Accent strip at the top of the card, visible only when the card is expanded.
        _hostStripe = MultiplayerPanelTheme.MakeGo("AccentStripe", hostInner.transform);
        MultiplayerPanelTheme.Anchor(_hostStripe, new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0f, -2f), Vector2.zero);
        MultiplayerPanelTheme.Fill(_hostStripe, MultiplayerPanelTheme.Accent);
        _hostStripe.SetActive(false);

        // Collapsed state (neutral like the other cards)
        _hostCollapsed = BuildCardHeader(hostInner.transform, "▲", "Host a climb",
            "Create a lobby and invite friends", false,
            (UnityAction)(() => Toggle(Expanded.Host)));
        _hostGlyphCollapsed = _hostCollapsed.transform.Find("Glyph")?.GetComponent<TextMeshProUGUI>();

        // Expanded state — hidden initially, holds the inputs
        _hostExpanded = BuildHostExpanded(hostInner.transform, initialPlayerName, initialMaxPlayers);
        _hostExpanded.SetActive(false);
    }

    private GameObject BuildHostExpanded(Transform parent, string initialPlayerName, int initialMaxPlayers)
    {
        var go = MultiplayerPanelTheme.MakeGo("Expanded", parent);
        MultiplayerPanelTheme.FullStretch(go);

        // Vertical layout with disjoint zones (top to bottom):
        //   Header (chevron + title)
        //   LOBBY NAME: label + input
        //   SLOTS / VISIBILITY: labels + controls (row)
        //   CREATE LOBBY (primary)

        // Header (title + chevron)
        var header = BuildCardHeader(go.transform, "▴", "Host a climb", null, true,
            (UnityAction)(() => Toggle(Expanded.None)));
        MultiplayerPanelTheme.Anchor(header, new Vector2(0f, 0.88f), new Vector2(1f, 1f));

        // LOBBY NAME (label then input, no overlap)
        var lblName = MultiplayerPanelTheme.Tmp(go.transform, "LbName", "LOBBY NAME", _font, 10,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.BottomLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(lblName.gameObject, new Vector2(0.06f, 0.78f), new Vector2(0.94f, 0.83f));
        lblName.characterSpacing = 2f;

        _lobbyNameField = new LobbyTextField(go.transform, _font, "Yutho's ascent", 32);
        MultiplayerPanelTheme.Anchor(_lobbyNameField.Root, new Vector2(0.06f, 0.68f), new Vector2(0.94f, 0.77f));
        _lobbyNameField.Value = $"{initialPlayerName ?? "My"}'s climb";

        // SLOTS label + control
        var lblSlots = MultiplayerPanelTheme.Tmp(go.transform, "LbSlots", "SLOTS", _font, 10,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.BottomLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(lblSlots.gameObject, new Vector2(0.06f, 0.56f), new Vector2(0.48f, 0.61f));
        lblSlots.characterSpacing = 2f;

        _slotsStepper = new StepperControl(go.transform, _font, 2, 8, initialMaxPlayers);
        MultiplayerPanelTheme.Anchor(_slotsStepper.Root, new Vector2(0.06f, 0.46f), new Vector2(0.48f, 0.55f));

        // VISIBILITY label + control
        var lblVis = MultiplayerPanelTheme.Tmp(go.transform, "LbVis", "VISIBILITY", _font, 10,
            MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.BottomLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(lblVis.gameObject, new Vector2(0.52f, 0.56f), new Vector2(0.94f, 0.61f));
        lblVis.characterSpacing = 2f;

        _visibilitySegment = new SegmentedControl(go.transform, _font,
            new[] { "Public", "Friends", "Private" }, 0);
        MultiplayerPanelTheme.Anchor(_visibilitySegment.Root, new Vector2(0.52f, 0.46f), new Vector2(0.94f, 0.55f));

        // Save note: each player lands in the game's native save menu on start
        // and picks new/existing themselves, just like in solo.
        var saveNote = MultiplayerPanelTheme.Tmp(go.transform, "SaveNote",
            "Everyone picks new or existing save in Cairn's menu when the host starts.",
            _font, 11, MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Italic);
        MultiplayerPanelTheme.Anchor(saveNote.gameObject, new Vector2(0.06f, 0.20f), new Vector2(0.94f, 0.40f));

        // CREATE LOBBY button (primary)
        var (btnGo, label, btn) = BuildPrimaryButton(go.transform, "CREATE LOBBY",
            (UnityAction)OnCreateClicked);
        MultiplayerPanelTheme.Anchor(btnGo, new Vector2(0.06f, 0.06f), new Vector2(0.94f, 0.16f));
        _createBtn      = btn;
        _createBtnLabel = label;

        return go;
    }

    private void BuildJoinCard()
    {
        _joinCard = MultiplayerPanelTheme.MakeGo("JoinCard", Root.transform);
        MultiplayerPanelTheme.Anchor(_joinCard, new Vector2(0f, 0.43f), new Vector2(1f, 0.53f));
        _joinBorder = MultiplayerPanelTheme.Fill(_joinCard, MultiplayerPanelTheme.Border);

        var inner = MultiplayerPanelTheme.MakeGo("Inner", _joinCard.transform);
        MultiplayerPanelTheme.Anchor(inner, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        MultiplayerPanelTheme.Fill(inner, MultiplayerPanelTheme.CardBg, raycast: true);

        // Accent strip — visible only when expanded.
        _joinStripe = MultiplayerPanelTheme.MakeGo("AccentStripe", inner.transform);
        MultiplayerPanelTheme.Anchor(_joinStripe, new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0f, -2f), Vector2.zero);
        MultiplayerPanelTheme.Fill(_joinStripe, MultiplayerPanelTheme.Accent);
        _joinStripe.SetActive(false);

        _joinCollapsed = BuildCardHeader(inner.transform, "→", "Join with code",
            "A friend shared a lobby code", false,
            (UnityAction)(() => Toggle(Expanded.Join)));
        _joinGlyphCollapsed = _joinCollapsed.transform.Find("Glyph")?.GetComponent<TextMeshProUGUI>();

        _joinExpanded = BuildJoinExpanded(inner.transform);
        _joinExpanded.SetActive(false);
    }

    private GameObject BuildJoinExpanded(Transform parent)
    {
        var go = MultiplayerPanelTheme.MakeGo("Expanded", parent);
        MultiplayerPanelTheme.FullStretch(go);

        var header = BuildCardHeader(go.transform, "→", "Join with code", null, false,
            (UnityAction)(() => Toggle(Expanded.None)));
        MultiplayerPanelTheme.Anchor(header, new Vector2(0f, 0.55f), new Vector2(1f, 1f));

        _codeField = new LobbyTextField(go.transform, _font, "XXXX-XXXX", 9);
        _codeField.SetCharacterValidation(TMP_InputField.CharacterValidation.None);
        MultiplayerPanelTheme.Anchor(_codeField.Root, new Vector2(0.05f, 0.10f), new Vector2(0.65f, 0.50f));

        var (btnGo, label, btn) = BuildPrimaryButton(go.transform, "JOIN",
            (UnityAction)OnJoinClicked);
        MultiplayerPanelTheme.Anchor(btnGo, new Vector2(0.68f, 0.10f), new Vector2(0.95f, 0.50f));
        _joinBtn      = btn;
        _joinBtnLabel = label;

        return go;
    }

    private void BuildBrowseCard()
    {
        _browseCard = MultiplayerPanelTheme.MakeGo("BrowseCard", Root.transform);
        MultiplayerPanelTheme.Anchor(_browseCard, new Vector2(0f, 0.30f), new Vector2(1f, 0.40f));
        _browseBorder = MultiplayerPanelTheme.Fill(_browseCard, MultiplayerPanelTheme.Border);

        var inner = MultiplayerPanelTheme.MakeGo("Inner", _browseCard.transform);
        MultiplayerPanelTheme.Anchor(inner, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
        MultiplayerPanelTheme.Fill(inner, MultiplayerPanelTheme.CardBg, raycast: true);

        _browseBtn = inner.AddComponent<Button>();
        _browseBtn.targetGraphic = inner.GetComponent<Image>();
        _browseBtn.onClick.AddListener((UnityAction)(() => BrowseRequested?.Invoke()));

        BuildCardHeader(inner.transform, "◇", "Browse public lobbies",
            "Find an open expedition", false, null);
    }

    // ── Helpers UI ───────────────────────────────────────────────────────────

    private GameObject BuildCardHeader(Transform parent, string glyph, string title, string subtitle,
        bool active, UnityAction onClick)
    {
        var header = MultiplayerPanelTheme.MakeGo("Header", parent);
        MultiplayerPanelTheme.FullStretch(header);

        // If onClick is provided, attach a Button. Otherwise the click is handled by the parent (browse card).
        if (onClick != null)
        {
            var btnImg = header.AddComponent<Image>();
            btnImg.color         = new Color(0, 0, 0, 0);
            btnImg.raycastTarget = true;
            var btn = header.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.onClick.AddListener(onClick);
        }

        var glyphTmp = MultiplayerPanelTheme.Tmp(header.transform, "Glyph", glyph, _font, 18,
            active ? MultiplayerPanelTheme.Accent : MultiplayerPanelTheme.TextMuted,
            TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(glyphTmp.gameObject, new Vector2(0.04f, 0f), new Vector2(0.10f, 1f));

        var titleTmp = MultiplayerPanelTheme.Tmp(header.transform, "Title", title, _font, 14,
            active ? MultiplayerPanelTheme.TextPrimary : MultiplayerPanelTheme.TextPrimary,
            TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        MultiplayerPanelTheme.Anchor(titleTmp.gameObject,
            string.IsNullOrEmpty(subtitle) ? new Vector2(0.10f, 0f) : new Vector2(0.10f, 0.50f),
            new Vector2(0.95f, 1f));

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subTmp = MultiplayerPanelTheme.Tmp(header.transform, "Sub", subtitle, _font, 11,
                MultiplayerPanelTheme.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
            MultiplayerPanelTheme.Anchor(subTmp.gameObject, new Vector2(0.10f, 0f), new Vector2(0.95f, 0.50f));
        }

        return header;
    }

    private (GameObject go, TextMeshProUGUI label, Button btn) BuildPrimaryButton(
        Transform parent, string text, UnityAction onClick)
    {
        var go = MultiplayerPanelTheme.MakeGo("PrimaryBtn", parent);
        var img = go.AddComponent<Image>();
        img.color         = MultiplayerPanelTheme.AccentSolid;
        img.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);
        var colors = btn.colors;
        colors.normalColor      = Color.white;
        colors.highlightedColor = new Color(1f, 1f, 1f, 0.92f);
        colors.pressedColor     = new Color(1f, 1f, 1f, 0.78f);
        colors.disabledColor    = new Color(0.6f, 0.6f, 0.6f, 1f);
        btn.colors = colors;

        var label = MultiplayerPanelTheme.Tmp(go.transform, "Label", text, _font, 12,
            new Color(0.10f, 0.08f, 0.05f, 1f), TextAlignmentOptions.Center, FontStyles.Bold);
        label.characterSpacing = 4f;

        return (go, label, btn);
    }

    // ── Logic ──────────────────────────────────────────────────────────────────

    private void Toggle(Expanded target)
    {
        _expanded = (_expanded == target) ? Expanded.None : target;
        UpdateCardsState();
    }

    private void UpdateCardsState()
    {
        bool hostExpanded = _expanded == Expanded.Host;
        bool joinExpanded = _expanded == Expanded.Join;
        bool anyExpanded  = hostExpanded || joinExpanded;

        // Toggle header/expanded visibility
        if (_hostCollapsed != null) _hostCollapsed.SetActive(!hostExpanded);
        if (_hostExpanded  != null) _hostExpanded.SetActive(hostExpanded);
        if (_joinCollapsed != null) _joinCollapsed.SetActive(!joinExpanded);
        if (_joinExpanded  != null) _joinExpanded.SetActive(joinExpanded);

        // Accent stripes: visible only on the expanded card.
        if (_hostStripe != null) _hostStripe.SetActive(hostExpanded);
        if (_joinStripe != null) _joinStripe.SetActive(joinExpanded);

        // Glyphs: gold accent only when the card is expanded, neutral otherwise.
        if (_hostGlyphCollapsed != null)
            _hostGlyphCollapsed.color = MultiplayerPanelTheme.TextMuted;
        if (_joinGlyphCollapsed != null)
            _joinGlyphCollapsed.color = MultiplayerPanelTheme.TextMuted;

        // Dynamic card heights: we adjust the vertical anchors
        // Layout with a single expanded card takes ~58% of the height, the others compact down
        if (hostExpanded)
        {
            MultiplayerPanelTheme.Anchor(_hostCard,   new Vector2(0f, 0.13f), new Vector2(1f, 0.76f));
            MultiplayerPanelTheme.Anchor(_joinCard,   new Vector2(0f, 0.065f), new Vector2(1f, 0.115f));
            MultiplayerPanelTheme.Anchor(_browseCard, new Vector2(0f, 0.005f), new Vector2(1f, 0.055f));
        }
        else if (joinExpanded)
        {
            MultiplayerPanelTheme.Anchor(_hostCard,   new Vector2(0f, 0.62f), new Vector2(1f, 0.76f));
            MultiplayerPanelTheme.Anchor(_joinCard,   new Vector2(0f, 0.20f), new Vector2(1f, 0.60f));
            MultiplayerPanelTheme.Anchor(_browseCard, new Vector2(0f, 0.10f), new Vector2(1f, 0.18f));
        }
        else
        {
            MultiplayerPanelTheme.Anchor(_hostCard,   new Vector2(0f, 0.55f), new Vector2(1f, 0.76f));
            MultiplayerPanelTheme.Anchor(_joinCard,   new Vector2(0f, 0.43f), new Vector2(1f, 0.53f));
            MultiplayerPanelTheme.Anchor(_browseCard, new Vector2(0f, 0.31f), new Vector2(1f, 0.41f));
        }

        // Borders: neutral by default, gold only on the expanded card.
        if (anyExpanded)
        {
            _hostBorder.color   = hostExpanded ? MultiplayerPanelTheme.BorderFocus : MultiplayerPanelTheme.BorderSubtle;
            _joinBorder.color   = joinExpanded ? MultiplayerPanelTheme.BorderFocus : MultiplayerPanelTheme.BorderSubtle;
            _browseBorder.color = MultiplayerPanelTheme.BorderSubtle;
        }
        else
        {
            _hostBorder.color   = MultiplayerPanelTheme.Border;
            _joinBorder.color   = MultiplayerPanelTheme.Border;
            _browseBorder.color = MultiplayerPanelTheme.Border;
        }
    }

    private void OnCreateClicked()
    {
        if (_isConnecting) return;
        var name = _playerNameField.Value?.Trim() ?? "";
        var lobbyName = _lobbyNameField.Value?.Trim() ?? "";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(lobbyName)) return;

        var visibility = _visibilitySegment.SelectedIndex switch
        {
            0 => LobbyVisibility.Public,
            1 => LobbyVisibility.FriendsOnly,
            _ => LobbyVisibility.Private,
        };

        HostRequested?.Invoke(new HostConfig
        {
            PlayerName = name,
            LobbyName  = lobbyName,
            MaxPlayers = _slotsStepper.Value,
            Visibility = visibility,
        });
    }

    private void OnJoinClicked()
    {
        if (_isConnecting) return;
        var name = _playerNameField.Value?.Trim() ?? "";
        var code = _codeField.Value?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(code)) return;
        JoinByCodeRequested?.Invoke(name, code);
    }
}
