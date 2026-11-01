using System;
using CairnMultiplayerMod.GameApi;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed class BelayPanelAdapter : IBelayPanelApi
{
    private Action<BelayPanelAction, int> _onAction;
    private BelayPanelView _view = new();
    private Vector2 _scroll;
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
        _previousCursorVisible = Cursor.visible; _previousLock = Cursor.lockState;
        IsOpen = true; InputCaptureState.IsBelayPanelCaptured = true;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false; InputCaptureState.IsBelayPanelCaptured = false;
        Cursor.lockState = _previousLock; Cursor.visible = _previousCursorVisible;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
    }

    public void SetView(BelayPanelView view) => _view = view ?? new BelayPanelView();

    public void Draw()
    {
        if (!IsOpen) return;
        _title ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        _body ??= new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
        _name ??= new GUIStyle(_body) { fontStyle = FontStyle.Bold };
        _button ??= new GUIStyle(GUI.skin.button) { fontSize = 16, wordWrap = true };
        var width = Math.Min(600f, Screen.width - 32f);
        var height = Math.Min(470f, Screen.height - 32f);
        var rect = new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height);
        var oldColor = GUI.color;
        GUI.color = new Color(.08f, .1f, .1f, .97f); GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = oldColor;
        GUILayout.BeginArea(new Rect(rect.x + 20, rect.y + 16, rect.width - 40, rect.height - 32));
        GUILayout.Label("Partner belay", _title);
        GUILayout.Label(_view.Description, _body, GUILayout.MinHeight(52));
        GUILayout.Space(8);
        _scroll = GUILayout.BeginScrollView(_scroll);
        if (_view.Phase == BelayPanelPhase.Partners)
        {
            if (_view.Partners.Count == 0) GUILayout.Label("No partner within 6 m in this area.", _body);
            foreach (var partner in _view.Partners)
            {
                GUILayout.Label($"{partner.Name} — {partner.Distance:F1} m", _name);
                GUILayout.BeginHorizontal();
                GUI.enabled = _view.CanRequest;
                if (GUILayout.Button("Ask for belay", _button, GUILayout.Height(40))) Act(BelayPanelAction.Request, partner.Id);
                GUI.enabled = _view.CanOffer;
                if (GUILayout.Button("Offer belay", _button, GUILayout.Height(40))) Act(BelayPanelAction.Offer, partner.Id);
                GUI.enabled = true;
                GUILayout.EndHorizontal(); GUILayout.Space(12);
            }
        }
        else if (_view.Phase == BelayPanelPhase.Invitation)
        {
            GUILayout.Label(_view.PartnerName, _name);
            GUILayout.BeginHorizontal();
            GUI.enabled = _view.CanAccept;
            if (GUILayout.Button("Accept", _button, GUILayout.Height(44))) Act(BelayPanelAction.Accept);
            GUI.enabled = true;
            if (GUILayout.Button("Decline", _button, GUILayout.Height(44))) Act(BelayPanelAction.Decline);
            GUILayout.EndHorizontal();
        }
        else if (_view.Phase == BelayPanelPhase.Active)
        {
            GUILayout.Label(_view.PartnerName, _name);
            if (_view.LocalBelayer && GUILayout.Button(_view.Braked ? "Release rope" : "Brake rope", _button, GUILayout.Height(44)))
                Act(BelayPanelAction.ToggleBrake);
            if (GUILayout.Button("End belay", _button, GUILayout.Height(40))) Act(BelayPanelAction.Cancel);
        }
        else if (GUILayout.Button("Cancel invitation", _button, GUILayout.Height(40))) Act(BelayPanelAction.Cancel);
        GUILayout.EndScrollView();
        GUILayout.Label("L: open/close  •  Space: brake/release while belaying", _body);
        if (GUILayout.Button("Close", _button, GUILayout.Height(34))) Act(BelayPanelAction.Close);
        GUILayout.EndArea();
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
