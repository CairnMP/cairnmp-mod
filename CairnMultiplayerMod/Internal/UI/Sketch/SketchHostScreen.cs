using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using CairnMultiplayerMod.Internal.Networking;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;

namespace CairnMultiplayerMod.Internal.UI.Sketch;

/// <summary>
/// Assembles the "Host" screen (create a lobby) in the camera menu's sketch style, from the game's
/// real sprites (GameUiAssetLibrary) and the building blocks in SketchUiKit. "Variant A" layout:
/// row of icon tabs (Host/Join/Browse), ‹ HOST › title, Lobby name / Slots / Visibility rows,
/// large CREATE LOBBY button, status footer. Built under a supplied parent; does not manage the Canvas.
/// </summary>
internal sealed class SketchHostScreen
{
    public GameObject Root { get; }

    /// <summary>Raised when the user confirms creation (preview: just a log).</summary>
    public event Action<HostConfig> CreateRequested;

    private const int MinSlots = 1;
    private const int MaxSlots = 8;
    private static readonly string[] VisibilityLabels = { "Public", "Friends", "Private" };
    private static readonly LobbyVisibility[] VisibilityValues =
        { LobbyVisibility.Public, LobbyVisibility.FriendsOnly, LobbyVisibility.Private };

    private TMP_InputField _nameInput;
    private int _slots = MaxSlots;
    private int _visIndex;
    private TextMeshProUGUI _slotsLabel;
    private TextMeshProUGUI _visLabel;

    public SketchHostScreen(Transform parent, string defaultLobbyName)
    {
        Root = SketchUiKit.Make("SketchHostScreen", parent);
        SketchUiKit.Stretch(Root);

        // Centered panel, sized close to the photo-mode's native frame (~828x632 ratio).
        var panel = SketchUiKit.Make("Panel", Root.transform);
        SketchUiKit.Box(panel, Vector2.zero, new Vector2(720f, 600f));

        // Sketch frame (overflows the content; mountain ornament at top-left). Values: SketchLayout.
        var frame = SketchUiKit.Make("Frame", panel.transform);
        SketchUiKit.Rect(frame, Vector2.zero, Vector2.one,
            new Vector2(-SketchLayout.FrameL, -SketchLayout.FrameB),
            new Vector2(SketchLayout.FrameR, SketchLayout.FrameT));
        SketchUiKit.Sliced(frame, GameUiAssetLibrary.Frame, SketchUiKit.FrameTint);

        // Content area (navy): aligned inside the frame's visible line. Values: SketchLayout.
        var content = SketchUiKit.Make("Content", panel.transform);
        SketchUiKit.Rect(content, Vector2.zero, Vector2.one,
            new Vector2(SketchLayout.ContentL, SketchLayout.ContentB),
            new Vector2(-SketchLayout.ContentR, -SketchLayout.ContentT));

        BuildTitlePanel(content.transform);
        BuildBodyPanel(content.transform, defaultLobbyName);
    }

    // ── Title panel: icon tabs + ‹ HOST › title ──────────────────────────────
    private void BuildTitlePanel(Transform content)
    {
        var title = SketchUiKit.Make("TitlePanel", content);
        SketchUiKit.StretchTop(title, 138f);
        SketchUiKit.Sliced(title, GameUiAssetLibrary.PanelTitle, SketchUiKit.PanelTint);

        // Tabs (repurposed photo-mode icons): Host / Join / Browse.
        Tab(title.transform, GameUiAssetLibrary.IconMountain, new Vector2(-104f, 38f), active: true, "Host");
        Tab(title.transform, GameUiAssetLibrary.IconLens, new Vector2(0f, 38f), active: false, "Join");
        Tab(title.transform, GameUiAssetLibrary.IconCamera, new Vector2(104f, 38f), active: false, "Browse");

        // Title + arrows.
        LabelCell(title.transform, "Title", new Vector2(0f, -38f), new Vector2(320f, 50f),
            "HOST", 34f, SketchUiKit.TextCream, TextAlignmentOptions.Center, logo: true);
        Arrow(title.transform, new Vector2(-92f, -38f), left: true);
        Arrow(title.transform, new Vector2(92f, -38f), left: false);
    }

    private void Tab(Transform parent, string iconSprite, Vector2 pos, bool active, string label)
    {
        var cell = SketchUiKit.Make($"Tab_{label}", parent);
        SketchUiKit.Box(cell, pos, new Vector2(58f, 58f));
        SketchUiKit.Simple(cell, iconSprite, active ? SketchUiKit.IconActive : SketchUiKit.IconIdle);
        // Clickable (preview: only Host is active; Join/Browse to come).
        SketchUiKit.MakeButton(cell, (UnityAction)(() => ModLog.Debug($"[Sketch] tab '{label}' clicked")));
    }

    private void Arrow(Transform parent, Vector2 pos, bool left)
    {
        var go = SketchUiKit.Make(left ? "ArrowLeft" : "ArrowRight", parent);
        var rt = SketchUiKit.Box(go, pos, new Vector2(26f, 36f));
        SketchUiKit.Simple(go, GameUiAssetLibrary.Arrow, SketchUiKit.TextCream);
        // The native sprite points left (‹). Keep ‹ on the left, mirror it for › on the right.
        if (!left) rt.localScale = new Vector3(-1f, 1f, 1f);
    }

    // ── Body panel: option rows + Create + footer ─────────────────────────────
    private void BuildBodyPanel(Transform content, string defaultLobbyName)
    {
        var body = SketchUiKit.Make("BodyPanel", content);
        SketchUiKit.StretchFill(body, topInset: 142f, bottomInset: 0f);   // below the title panel (138 + margin)
        SketchUiKit.Sliced(body, GameUiAssetLibrary.PanelBody, SketchUiKit.PanelTint);

        // Full-width strip rows (like the camera menu), distributed to fill the body.
        var nameCell = Row(body.transform, "Lobby name", 128f);
        _nameInput = SketchUiKit.NativeField(nameCell, "Lobby name", 32);
        _nameInput.text = defaultLobbyName ?? "";

        var slotsCell = Row(body.transform, "Slots", 66f);
        _slotsLabel = Stepper(slotsCell, _slots.ToString(),
            () => AdjustSlots(-1), () => AdjustSlots(+1));

        var visCell = Row(body.transform, "Visibility", 4f);
        _visLabel = Stepper(visCell, VisibilityLabels[_visIndex],
            () => CycleVisibility(-1), () => CycleVisibility(+1));

        // CREATE LOBBY button.
        var createGo = SketchUiKit.Make("CreateButton", body.transform);
        SketchUiKit.Box(createGo, new Vector2(0f, -78f), new Vector2(420f, 70f));
        SketchUiKit.Sliced(createGo, GameUiAssetLibrary.Button, Color.white, raycast: true);
        LabelCell(createGo.transform, "Label", Vector2.zero, new Vector2(420f, 70f),
            "CREATE LOBBY", 22f, SketchUiKit.TextCream, TextAlignmentOptions.Center, logo: true);
        SketchUiKit.MakeButton(createGo, (UnityAction)OnCreateClicked);

        // Status footer.
        LabelCell(body.transform, "Footer", new Vector2(0f, -136f), new Vector2(560f, 26f),
            "Disconnected", 14f, SketchUiKit.TextDim, TextAlignmentOptions.Center);
    }

    /// <summary>Full-width strip row: background + label (left) + control cell (right).
    /// Returns the Transform of the control cell (fixed width, anchored right).</summary>
    private Transform Row(Transform body, string label, float y)
    {
        var row = SketchUiKit.Make($"Row_{label}", body);
        var rt = row.GetComponent<RectTransform>() ?? row.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(-40f, 52f);   // full width minus side padding
        rt.anchoredPosition = new Vector2(0f, y);
        SketchUiKit.Sliced(row, GameUiAssetLibrary.RowBg, SketchUiKit.RowTint);

        // Label on the left (anchored, padded).
        var labelGo = SketchUiKit.Make("Label", row.transform);
        var lrt = labelGo.GetComponent<RectTransform>() ?? labelGo.AddComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0f, 0f);
        lrt.anchorMax = new Vector2(0.5f, 1f);
        lrt.offsetMin = new Vector2(22f, 0f);
        lrt.offsetMax = Vector2.zero;
        SketchUiKit.Label(labelGo.transform, "Text", label, 19f, SketchUiKit.TextCream, TextAlignmentOptions.Left);

        // Control cell on the right (fixed width).
        var cell = SketchUiKit.Make("Cell", row.transform);
        var crt = cell.GetComponent<RectTransform>() ?? cell.AddComponent<RectTransform>();
        crt.anchorMin = new Vector2(1f, 0.5f);
        crt.anchorMax = new Vector2(1f, 0.5f);
        crt.pivot = new Vector2(1f, 0.5f);
        crt.sizeDelta = new Vector2(250f, 42f);
        crt.anchoredPosition = new Vector2(-16f, 0f);
        return cell.transform;
    }

    /// <summary>‹ value › control: left arrow + centered value + right arrow.</summary>
    private TextMeshProUGUI Stepper(Transform cell, string value, Action onLeft, Action onRight)
    {
        // Left arrow ‹ (native sprite as-is) = decrement.
        var leftGo = SketchUiKit.Make("Left", cell);
        SketchUiKit.Box(leftGo, new Vector2(-100f, 0f), new Vector2(26f, 34f));
        SketchUiKit.Simple(leftGo, GameUiAssetLibrary.Arrow, SketchUiKit.TextCream, raycast: true);
        SketchUiKit.MakeButton(leftGo, (UnityAction)(() => onLeft()));

        var valTmp = LabelCell(cell, "Value", Vector2.zero, new Vector2(130f, 40f),
            value, 20f, SketchUiKit.TextCream, TextAlignmentOptions.Center);

        // Right arrow › (mirrored) = increment.
        var rightGo = SketchUiKit.Make("Right", cell);
        var rightRt = SketchUiKit.Box(rightGo, new Vector2(100f, 0f), new Vector2(26f, 34f));
        SketchUiKit.Simple(rightGo, GameUiAssetLibrary.Arrow, SketchUiKit.TextCream, raycast: true);
        rightRt.localScale = new Vector3(-1f, 1f, 1f);
        SketchUiKit.MakeButton(rightGo, (UnityAction)(() => onRight()));

        return valTmp;
    }

    private TextMeshProUGUI LabelCell(Transform parent, string name, Vector2 pos, Vector2 size,
        string text, float fontSize, Color color, TextAlignmentOptions align, bool logo = false)
    {
        var cell = SketchUiKit.Make(name, parent);
        SketchUiKit.Box(cell, pos, size);
        return SketchUiKit.Label(cell.transform, "Text", text, fontSize, color, align, logo);
    }

    // ── Logic ─────────────────────────────────────────────────────────────────────
    private void AdjustSlots(int delta)
    {
        _slots = Mathf.Clamp(_slots + delta, MinSlots, MaxSlots);
        if (_slotsLabel != null) _slotsLabel.text = _slots.ToString();
    }

    private void CycleVisibility(int delta)
    {
        int n = VisibilityLabels.Length;
        _visIndex = ((_visIndex + delta) % n + n) % n;
        if (_visLabel != null) _visLabel.text = VisibilityLabels[_visIndex];
    }

    private void OnCreateClicked()
    {
        var config = new HostConfig
        {
            PlayerName = "Player",
            LobbyName = _nameInput != null ? _nameInput.text : "",
            MaxPlayers = _slots,
            Visibility = VisibilityValues[_visIndex],
        };
        ModLog.Info($"[Sketch] CREATE LOBBY: name='{config.LobbyName}' slots={config.MaxPlayers} vis={config.Visibility}");
        CreateRequested?.Invoke(config);
    }
}
