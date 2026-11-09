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
    private readonly BelayPanelKeyGate _stationKey = new();
    private readonly BelayPanelNavigation _navigation = new();
    private int _page;
    private bool _firstDrawCompleted;
    private int _cursorRepairsLogged;
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
        _cursorRepairsLogged = 0;
        IsOpen = true; InputCaptureState.IsBelayPanelCaptured = true;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
        MaintainCursor();
        ModLog.Info("[BelayPanel] Opened; gameplay input captured.");
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false; InputCaptureState.IsBelayPanelCaptured = false;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
        Cursor.lockState = _previousLock; Cursor.visible = _previousCursorVisible;
        ModLog.Info("[BelayPanel] Closed; gameplay input reconciled.");
    }

    public void SetView(BelayPanelView view)
    {
        view ??= new BelayPanelView();
        if (_view.Phase != view.Phase) _page = 0;
        _view = view;
        _navigation.Update(view);
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
        if (_onAction != null && keyEvent != null && keyEvent.keyCode == KeyCode.K
            && keyEvent.type is EventType.KeyDown or EventType.KeyUp)
        {
            var eligible = IsOpen && !_view.AwaitingGameplay && (_view.Phase == BelayPanelPhase.Partners
                || (_view.Phase == BelayPanelPhase.Active && _view.CanSwap)) && Application.isFocused && !keyEvent.control && !keyEvent.alt
                && !keyEvent.command && !keyEvent.shift;
            if (_stationKey.Observe(keyEvent.type == EventType.KeyDown, eligible))
            {
                var action = _view.Phase == BelayPanelPhase.Active ? BelayPanelAction.ProposeSwap
                    : _view.StationDefined ? BelayPanelAction.RemoveStation : BelayPanelAction.DefineStation;
                ModLog.Info("[BelayPanel] Station shortcut; action=" + action);
                Act(action);
                keyEvent.Use();
            }
            else if (IsOpen) keyEvent.Use();
        }
        ObserveShortcut(keyEvent);
        if (!IsOpen) return;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
        MaintainCursor();
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
        finally
        {
            GUI.enabled = previousEnabled;
            if (keyEvent != null && keyEvent.type is EventType.MouseDown or EventType.MouseUp
                or EventType.MouseDrag or EventType.ScrollWheel) keyEvent.Use();
        }
    }

    private void ObserveShortcut(Event keyEvent)
    {
        if (_onAction == null || keyEvent == null || keyEvent.type is not (EventType.KeyDown or EventType.KeyUp)) return;
        BelayPanelShortcut? key = keyEvent.keyCode switch
        {
            KeyCode.UpArrow => BelayPanelShortcut.Previous,
            KeyCode.DownArrow => BelayPanelShortcut.Next,
            KeyCode.O => BelayPanelShortcut.Offer,
            KeyCode.I => BelayPanelShortcut.Request,
            KeyCode.Return or KeyCode.KeypadEnter => BelayPanelShortcut.Accept,
            KeyCode.Backspace => BelayPanelShortcut.Decline,
            _ => null
        };
        if (!key.HasValue) return;
        var eligible = IsOpen && Application.isFocused && !keyEvent.control && !keyEvent.alt
            && !keyEvent.command && !keyEvent.shift;
        if (_navigation.Observe(key.Value, keyEvent.type == EventType.KeyDown, eligible, _view, out var action, out var target))
        {
            ModLog.Info("[BelayPanel] Keyboard action=" + action + ", target=" + target);
            Act(action, target);
        }
        if (IsOpen || eligible) keyEvent.Use();
    }

    private void MaintainCursor()
    {
        if (!IsOpen || !Application.isFocused) return;
        var previousLock = Cursor.lockState;
        var previousVisible = Cursor.visible;
        if (previousLock == CursorLockMode.None && previousVisible) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (_cursorRepairsLogged < 3)
        {
            _cursorRepairsLogged++;
            ModLog.Info("[BelayPanel] Cursor released; previousLock=" + previousLock + ", previousVisible=" + previousVisible);
        }
    }

    private void DrawContent(Rect rect)
    {
        // Explicit rectangles avoid introducing GUILayout's layout-group/option
        // interop into the panel. The same GUI primitives render the existing HUD.
        var view = _view;
        var x = rect.x + 20f;
        var width = rect.width - 40f;
        var half = (width - 10f) * .5f;
        var stationRow = !view.AwaitingGameplay && (view.StationDefined || view.Phase == BelayPanelPhase.Partners);
        var extendedStatus = view.Phase is BelayPanelPhase.Active or BelayPanelPhase.SwapRequest
            or BelayPanelPhase.SwapInvitation or BelayPanelPhase.SwapPreparing;
        var statusExtra = extendedStatus ? 32f : 0f;
        var contentY = rect.y + (stationRow ? 190f : 146f) + statusExtra;
        var footerY = rect.yMax - 78f;
        var available = Math.Max(48f, footerY - 42f - contentY);
        GUI.Box(rect, GUIContent.none);
        GUI.Label(new Rect(x, rect.y + 16f, width, 32f), "Partner belay", _title);
        GUI.Label(new Rect(x, rect.y + 56f, width, 80f + statusExtra), view.Description, _body);
        if (stationRow)
        {
            GUI.Label(new Rect(x, rect.y + 140f + statusExtra, half, 40f),
                view.StationDefined ? "Station: your current piton anchor" : "Use your existing piton anchor", _body);
            if (Button(new Rect(x + half + 10f, rect.y + 140f + statusExtra, half, 40f),
                view.Phase != BelayPanelPhase.Partners ? "Station locked during invitation/belay" : view.StationDefined ? "Remove station (K)" : "Define station (K)",
                view.StationDefined ? BelayPanelAction.RemoveStation : BelayPanelAction.DefineStation,
                enabled: view.Phase == BelayPanelPhase.Partners)) return;
        }
        if (view.Phase == BelayPanelPhase.Partners)
        {
            if (view.Partners.Count == 0)
                GUI.Label(new Rect(x, contentY, width, available), "No eligible partner yet. See the status above.", _body);
            else
            {
                var pageSize = Math.Max(1, (int)(available / 92f));
                var pageCount = (view.Partners.Count + pageSize - 1) / pageSize;
                _page = Math.Clamp(_page, 0, pageCount - 1);
                for (var i = 0; i < view.Partners.Count; i++)
                    if (view.Partners[i].Id == _navigation.SelectedPartner) _page = i / pageSize;
                var rowHeight = Math.Min(92f, available / pageSize);
                for (var row = 0; row < pageSize; row++)
                {
                    var index = _page * pageSize + row;
                    if (index >= view.Partners.Count) break;
                    var partner = view.Partners[index];
                    var y = contentY + row * rowHeight;
                    var selected = partner.Id == _navigation.SelectedPartner ? "> " : "";
                    var distance = float.IsFinite(partner.Distance) ? $"{partner.Distance:F1} m" : "distance checked after dialogue";
                    GUI.Label(new Rect(x, y, width, 24f), $"{selected}{partner.Name} — {distance}", _name);
                    var buttonHeight = Math.Min(40f, rowHeight - 30f);
                    if (Button(new Rect(x, y + 26f, half, buttonHeight), "Ask for belay (I)", BelayPanelAction.Request, partner.Id, view.CanRequest && partner.CanRequest)) return;
                    if (Button(new Rect(x + half + 10f, y + 26f, half, buttonHeight), "Offer belay (O)", BelayPanelAction.Offer, partner.Id, view.CanOffer && partner.CanOffer)) return;
                }
                if (pageCount > 1)
                {
                    if (GUI.Button(new Rect(x, footerY - 38f, 100f, 30f), "Previous", _button))
                        _navigation.Select(view.Partners[Math.Max(0, _page - 1) * pageSize].Id);
                    GUI.Label(new Rect(x + 110f, footerY - 38f, width - 220f, 30f), $"Page {_page + 1} / {pageCount}", _body);
                    if (GUI.Button(new Rect(x + width - 100f, footerY - 38f, 100f, 30f), "Next", _button))
                        _navigation.Select(view.Partners[Math.Min(pageCount - 1, _page + 1) * pageSize].Id);
                }
            }
        }
        else if (view.Phase == BelayPanelPhase.Invitation)
        {
            GUI.Label(new Rect(x, contentY, width, 24f), view.PartnerName, _name);
            var buttonHeight = Math.Min(44f, available - 28f);
            if (Button(new Rect(x, contentY + 28f, half, buttonHeight), "Accept (Enter)", BelayPanelAction.Accept, enabled: view.CanAccept)) return;
            if (Button(new Rect(x + half + 10f, contentY + 28f, half, buttonHeight), "Decline (Backspace)", BelayPanelAction.Decline)) return;
        }
        else if (view.Phase is BelayPanelPhase.SwapInvitation or BelayPanelPhase.SwapPreparing or BelayPanelPhase.SwapRequest)
        {
            GUI.Label(new Rect(x, contentY, width, 24f), view.PartnerName, _name);
            var buttonHeight = Math.Min(44f, available - 28f);
            if ((view.Phase == BelayPanelPhase.SwapInvitation && view.LocalBelayer)
                || (view.Phase == BelayPanelPhase.SwapRequest && !view.LocalBelayer))
            {
                var acceptLabel = view.Phase == BelayPanelPhase.SwapRequest ? "Propose my station (Enter)" : "Accept roles (Enter)";
                if (Button(new Rect(x, contentY + 28f, half, buttonHeight), acceptLabel, BelayPanelAction.AcceptSwap, enabled: view.CanAccept)) return;
                if (Button(new Rect(x + half + 10f, contentY + 28f, half, buttonHeight), "Keep roles (Backspace)", BelayPanelAction.CancelSwap)) return;
            }
            else if (Button(new Rect(x, contentY + 28f, width, buttonHeight), "Cancel exchange (Backspace)", BelayPanelAction.CancelSwap)) return;
        }
        else if (view.Phase == BelayPanelPhase.Recovering)
        {
            GUI.Label(new Rect(x, contentY, width, 24f), view.PartnerName, _name);
            if (Button(new Rect(x, contentY + 28f, width, Math.Min(44f, available - 28f)),
                "End belay / cancel resume (Backspace)", BelayPanelAction.Cancel)) return;
        }
        else if (view.Phase == BelayPanelPhase.Active)
        {
            GUI.Label(new Rect(x, contentY, width, 24f), view.PartnerName, _name);
            var buttonHeight = Math.Min(44f, available - 28f);
            if (view.LocalBelayer)
            {
                if (Button(new Rect(x, contentY + 28f, width, buttonHeight), "Request role exchange (K)", BelayPanelAction.ProposeSwap, enabled: view.CanSwap)) return;
                if (Button(new Rect(x, contentY + 36f + buttonHeight, half, buttonHeight), view.Braked ? "Release rope" : "Brake rope", BelayPanelAction.ToggleBrake)) return;
                if (Button(new Rect(x + half + 10f, contentY + 36f + buttonHeight, half, buttonHeight), "End belay", BelayPanelAction.Cancel)) return;
            }
            else
            {
                if (Button(new Rect(x, contentY + 28f, half, buttonHeight), "Define station / swap roles (K)", BelayPanelAction.ProposeSwap, enabled: view.CanSwap)) return;
                if (Button(new Rect(x + half + 10f, contentY + 28f, half, buttonHeight), "End belay", BelayPanelAction.Cancel)) return;
            }
        }
        else if (Button(new Rect(x, contentY, width, Math.Min(40f, available)),
            view.AwaitingGameplay ? "Cancel queued invitation (Backspace)" : "Cancel invitation (Backspace)", BelayPanelAction.Cancel)) return;
        GUI.Label(new Rect(x, footerY, width, 34f), view.Phase == BelayPanelPhase.Partners
            ? view.AwaitingGameplay ? "Up/Down: partner | I/O: queue invitation | L: close"
                : "Up/Down: partner | I: ask | O: offer | K: station | L: close"
            : view.Phase == BelayPanelPhase.Invitation ? "Enter: accept | Backspace: decline | L: close"
            : view.Phase is BelayPanelPhase.SwapInvitation or BelayPanelPhase.SwapPreparing or BelayPanelPhase.SwapRequest ? "Enter: confirm when ready | Backspace: keep current roles"
            : view.Phase == BelayPanelPhase.Recovering ? "Automatic resume waits for both players | Backspace: end | L: close"
            : view.AwaitingGameplay ? "Invitation queued, not sent | Backspace: cancel | L: close"
            : "K: role exchange | L: open/close | Space: brake with panel closed", _body);
        Button(new Rect(x, rect.yMax - 44f, width, 32f), view.Phase == BelayPanelPhase.SwapPreparing
            ? "Remain anchored while preparing" : "Close", BelayPanelAction.Close, enabled: view.Phase != BelayPanelPhase.SwapPreparing);
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
