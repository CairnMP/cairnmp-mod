using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed class BelayPanelAdapter : IBelayPanelApi
{
    private Action<BelayPanelAction, int> _onAction;
    private BelayPanelView _view = new();
    private readonly BelayPanelKeyGate _toggleKey = new();
    private int _page;
    private bool _firstDrawCompleted;
    private GUIStyle _title, _body, _button, _name;
    private bool _previousCursorVisible;
    private CursorLockMode _previousLock;
    public bool IsOpen { get; private set; }

    public IGameRegistration Register(Action<BelayPanelAction, int> onAction)
    {
        if (_onAction != null) throw new InvalidOperationException("Belay panel already registered.");
        _onAction = onAction ?? throw new ArgumentNullException(nameof(onAction));
        return new Registration(this);
    }

    public void Show()
    {
        if (IsOpen || _onAction == null) return;
        ModLog.Info("[BelayPanel] Opening; phase=" + _view.Phase);
        _previousCursorVisible = Cursor.visible; _previousLock = Cursor.lockState;
        _firstDrawCompleted = false;
        IsOpen = true; InputCaptureState.IsBelayPanelCaptured = true;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
        ModLog.Info("[BelayPanel] Opened; gameplay input captured.");
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false; InputCaptureState.IsBelayPanelCaptured = false;
        Cursor.lockState = _previousLock; Cursor.visible = _previousCursorVisible;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
        ModLog.Info("[BelayPanel] Closed; gameplay input reconciled.");
    }

    public void SetView(BelayPanelView view)
    {
        view ??= new BelayPanelView();
        if (_view.Phase != view.Phase) _page = 0;
        _view = view;
    }

    public void Draw()
    {
        // Like the chat, handle the GUI key event while this UI owns gameplay
        // input. A single event path avoids opening and closing on the same key.
        var keyEvent = Event.current;
        if (_onAction != null && keyEvent != null && keyEvent.keyCode == KeyCode.L
            && keyEvent.type is EventType.KeyDown or EventType.KeyUp)
        {
            if (_toggleKey.Observe(keyEvent.type == EventType.KeyDown))
            {
                var wasOpen = IsOpen;
                Act(BelayPanelAction.Toggle);
                if (IsOpen != wasOpen) keyEvent.Use();
            }
        }
        if (!IsOpen) return;
        var trace = !_firstDrawCompleted;
        if (trace) ModLog.Info("[BelayPanel] First draw started.");
        var previousEnabled = GUI.enabled;
        try
        {
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            _body ??= new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            _name ??= new GUIStyle(_body) { fontStyle = FontStyle.Bold };
            _button ??= new GUIStyle(GUI.skin.button) { fontSize = 16, wordWrap = true };
            if (trace) ModLog.Info("[BelayPanel] Styles ready.");
            var width = Math.Min(600f, Screen.width - 32f);
            var height = Math.Min(500f, Screen.height - 32f);
            var rect = new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height);
            DrawContent(rect);
            _firstDrawCompleted = true;
            if (trace) ModLog.Info("[BelayPanel] First draw completed.");
        }
        finally { GUI.enabled = previousEnabled; }
    }

    private void DrawContent(Rect rect)
    {
        // Explicit rectangles avoid introducing GUILayout's layout-group/option
        // interop into the panel. The same GUI primitives render the existing HUD.
        var view = _view;
        var x = rect.x + 20f;
        var width = rect.width - 40f;
        var half = (width - 10f) * .5f;
        var contentY = rect.y + 146f;
        var footerY = rect.yMax - 78f;
        var available = Math.Max(48f, footerY - 42f - contentY);
        GUI.Box(rect, GUIContent.none);
        GUI.Label(new Rect(x, rect.y + 16f, width, 32f), "Partner belay", _title);
        GUI.Label(new Rect(x, rect.y + 56f, width, 80f), view.Description, _body);
        if (view.Phase == BelayPanelPhase.Partners)
        {
            if (view.Partners.Count == 0)
                GUI.Label(new Rect(x, contentY, width, available), "No partner within 6 m in this area.", _body);
            else
            {
                var pageSize = Math.Max(1, (int)(available / 92f));
                var pageCount = (view.Partners.Count + pageSize - 1) / pageSize;
                _page = Math.Clamp(_page, 0, pageCount - 1);
                var rowHeight = Math.Min(92f, available / pageSize);
                for (var row = 0; row < pageSize; row++)
                {
                    var index = _page * pageSize + row;
                    if (index >= view.Partners.Count) break;
                    var partner = view.Partners[index];
                    var y = contentY + row * rowHeight;
                    GUI.Label(new Rect(x, y, width, 24f), $"{partner.Name} — {partner.Distance:F1} m", _name);
                    var buttonHeight = Math.Min(40f, rowHeight - 30f);
                    if (Button(new Rect(x, y + 26f, half, buttonHeight), "Ask for belay", BelayPanelAction.Request, partner.Id, view.CanRequest)) return;
                    if (Button(new Rect(x + half + 10f, y + 26f, half, buttonHeight), "Offer belay", BelayPanelAction.Offer, partner.Id, view.CanOffer)) return;
                }
                if (pageCount > 1)
                {
                    if (GUI.Button(new Rect(x, footerY - 38f, 100f, 30f), "Previous", _button)) _page = Math.Max(0, _page - 1);
                    GUI.Label(new Rect(x + 110f, footerY - 38f, width - 220f, 30f), $"Page {_page + 1} / {pageCount}", _body);
                    if (GUI.Button(new Rect(x + width - 100f, footerY - 38f, 100f, 30f), "Next", _button)) _page = Math.Min(pageCount - 1, _page + 1);
                }
            }
        }
        else if (view.Phase == BelayPanelPhase.Invitation)
        {
            GUI.Label(new Rect(x, contentY, width, 24f), view.PartnerName, _name);
            var buttonHeight = Math.Min(44f, available - 28f);
            if (Button(new Rect(x, contentY + 28f, half, buttonHeight), "Accept", BelayPanelAction.Accept, enabled: view.CanAccept)) return;
            if (Button(new Rect(x + half + 10f, contentY + 28f, half, buttonHeight), "Decline", BelayPanelAction.Decline)) return;
        }
        else if (view.Phase == BelayPanelPhase.Active)
        {
            GUI.Label(new Rect(x, contentY, width, 24f), view.PartnerName, _name);
            var buttonHeight = Math.Min(44f, available - 28f);
            if (view.LocalBelayer)
            {
                if (Button(new Rect(x, contentY + 28f, half, buttonHeight), view.Braked ? "Release rope" : "Brake rope", BelayPanelAction.ToggleBrake)) return;
                if (Button(new Rect(x + half + 10f, contentY + 28f, half, buttonHeight), "End belay", BelayPanelAction.Cancel)) return;
            }
            else if (Button(new Rect(x, contentY + 28f, width, buttonHeight), "End belay", BelayPanelAction.Cancel)) return;
        }
        else if (Button(new Rect(x, contentY, width, Math.Min(40f, available)), "Cancel invitation", BelayPanelAction.Cancel)) return;
        GUI.Label(new Rect(x, footerY, width, 30f), "L: open/close  •  Space: brake/release while belaying", _body);
        Button(new Rect(x, rect.yMax - 44f, width, 32f), "Close", BelayPanelAction.Close);
    }

    private bool Button(Rect rect, string text, BelayPanelAction action, int target = -1, bool enabled = true)
    {
        var previous = GUI.enabled;
        GUI.enabled = previous && enabled;
        var clicked = GUI.Button(rect, text, _button);
        GUI.enabled = previous;
        if (clicked) Act(action, target);
        return clicked;
    }

    private void Act(BelayPanelAction action, int target = -1) => _onAction?.Invoke(action, target);

    private sealed class Registration : IGameRegistration
    {
        private BelayPanelAdapter _owner;
        internal Registration(BelayPanelAdapter owner) => _owner = owner;
        public string Id => "manual-belay.panel";
        public bool IsActive => _owner != null;
        public void Dispose()
        {
            if (_owner == null) return;
            _owner.Close(); _owner._onAction = null; _owner = null;
        }
    }
}
