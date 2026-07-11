using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Networking;
using UnityEngine;

namespace CairnMultiplayerMod.Core.Chat;

/// <summary>
/// In-game IMGUI (OnGUI) chat: an overlay of recent messages at the bottom left + an
/// input line. Enter opens/sends, Escape cancels. While typing, the game's gameplay
/// inputs are frozen (<see cref="CairnGameApi.SetGameplayInputDisabled"/>) so that
/// typing doesn't drive the climber. Reuses the existing chat plumbing
/// (NetworkManager.SendChat / OnChatReceived); commands go through the router.
/// </summary>
internal sealed class ChatController
{
    private struct ChatLine
    {
        public string Text;
        public float ShownAt;
        public bool System;
    }

    private const int MaxLines = 50;
    private const int MaxVisibleLines = 12;
    private const float FadeAfterSeconds = 8f;
    private const int MaxInputLength = 200;

    private static readonly Color TextColor = Color.white;
    private static readonly Color SystemColor = new Color(1f, 0.85f, 0.4f, 1f);

    // Base font size (at scale 1, ~1080p). Raised from the IMGUI default (~13) for
    // better readability. Then scaled by UiScale.
    private const int BaseFontSize = 18;

    // UI scale proportional to the resolution (ref 1080p): the IMGUI chat reasons in
    // raw pixels, so without this it looks tiny at 4K and too big at 720p. Floored at
    // the 720p ratio (~0.667) so it doesn't become unreadable on small screens, capped
    // at 2.5 to stay reasonable on very large ones.
    private static float UiScale => Mathf.Clamp(Screen.height / 1080f, 720f / 1080f, 2.5f);
    private static int ScaledFontSize => Mathf.RoundToInt(BaseFontSize * UiScale);

    // Cached label style, fontSize refreshed every frame (resolution can change).
    private GUIStyle _labelStyle;

    private readonly NetworkManager _network;
    private readonly CommandRouter _router;
    private readonly Func<bool> _canChat;

    private readonly List<ChatLine> _lines = new();
    // Last known nicknames by id, to name the player in the "X left" message
    // (OnPlayerLeft only gives the id, the RemotePlayer is already removed by then).
    private readonly Dictionary<int, string> _knownNames = new();
    private bool _isOpen;
    private string _input = "";

    // History of sent messages (recall via up/down arrows, like a terminal).
    // _historyIndex == -1: we're editing the current draft (_draft saves its value when
    // we go back up through the history so we can return to it with Down).
    private const int MaxHistory = 50;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _draft = "";

    public ChatController(NetworkManager network, CommandRouter router, Func<bool> canChat)
    {
        _network = network;
        _router = router;
        _canChat = canChat;
        _network.OnChatReceived += OnChatReceived;
        _network.OnPlayerJoined += OnPlayerJoined;
        _network.OnPlayerLeft += OnPlayerLeft;
    }

    public bool IsTyping => _isOpen;

    private void OnChatReceived(int fromId, string fromName, string message)
    {
        // Our own message is already displayed locally by Submit (optimistic echo):
        // we ignore the network echo sent back by the host so we don't show it twice.
        if (fromId == _network.LocalPlayerId) return;
        AddLine($"{fromName}: {message}", system: false);
    }

    private void OnPlayerJoined(int id, string name)
    {
        // Ignore ourselves and fake players (debug mirror = negative id).
        if (id < 0 || id == _network.LocalPlayerId) return;
        var display = string.IsNullOrWhiteSpace(name) ? $"Player{id}" : name;
        bool isNew = !_knownNames.ContainsKey(id);
        _knownNames[id] = display;
        // OnPlayerJoined can re-fire on a presence update: we announce only once.
        if (isNew) AddSystemLine($"{display} joined the session.");
    }

    private void OnPlayerLeft(int id)
    {
        if (id < 0 || id == _network.LocalPlayerId) return;
        var display = _knownNames.TryGetValue(id, out var n) ? n : $"Player{id}";
        _knownNames.Remove(id);
        AddSystemLine($"{display} left the session.");
    }

    /// <summary>Adds a local system line (command feedback) — not broadcast.</summary>
    public void AddSystemLine(string text) => AddLine(text, system: true);

    private void AddLine(string text, bool system)
    {
        _lines.Add(new ChatLine { Text = text, ShownAt = Time.unscaledTime, System = system });
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
    }

    /// <summary>Called every frame from Mod.OnUpdate (Unity thread).</summary>
    public void Update()
    {
        if (_isOpen && !_canChat())
        {
            Close();
            return;
        }
        // Reconcile EVERY frame: input blocked IFF the chat is open. _isOpen is the single
        // source of truth. If one frame fails to resolve the InputManager, the next one
        // retries -> a closed chat ALWAYS returns input (no more permanent block).
        CairnGameApi.ReconcileGameplayInput(_isOpen);
    }

    /// <summary>Forced close (panic failsafe): doesn't touch the network, just the UI state.
    /// Unblocking inputs is done by the caller via CairnGameApi.ForceClearInputBlock.</summary>
    public void ForceClose()
    {
        _isOpen = false;
        _input = "";
    }

    /// <summary>Called from Mod.OnGUI.</summary>
    public void OnGUI()
    {
        var e = Event.current;

        if (_isOpen)
        {
            if (e.type == EventType.KeyDown)
                HandleTypingKey(e);
        }
        else if (e.type == EventType.KeyDown
                 && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                 && _canChat())
        {
            Open();
            e.Use();
        }

        DrawLog();
        if (_isOpen) DrawInput();
    }

    /// <summary>
    /// Manual text entry from the IMGUI keyboard events. We CANNOT use GUI.TextField:
    /// DoTextField is stripped from the IL2CPP build (Method unstripping failed). So we
    /// build the string ourselves (Enter/Escape/Backspace + characters).
    /// </summary>
    private void HandleTypingKey(Event e)
    {
        switch (e.keyCode)
        {
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                Submit();
                e.Use();
                return;
            case KeyCode.Escape:
                Close();
                e.Use();
                return;
            case KeyCode.Backspace:
                if (_input.Length > 0)
                    _input = _input.Substring(0, _input.Length - 1);
                e.Use();
                return;
            case KeyCode.UpArrow:
                RecallOlder();
                e.Use();
                return;
            case KeyCode.DownArrow:
                RecallNewer();
                e.Use();
                return;
        }

        // Printable character (e.character carries the typed character, accents included).
        char c = e.character;
        if (c != '\0' && !char.IsControl(c) && _input.Length < MaxInputLength)
        {
            _input += c;
            e.Use();
        }
    }

    /// <summary>Up arrow: recalls an older message from the history.</summary>
    private void RecallOlder()
    {
        if (_history.Count == 0) return;
        if (_historyIndex == -1)
        {
            _draft = _input;                  // save the current draft
            _historyIndex = _history.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        _input = _history[_historyIndex];
    }

    /// <summary>Down arrow: moves back toward more recent messages, then the current draft.</summary>
    private void RecallNewer()
    {
        if (_historyIndex == -1) return;      // already on the current draft
        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            _input = _history[_historyIndex];
        }
        else
        {
            _historyIndex = -1;
            _input = _draft;                  // back to the draft
        }
    }

    /// <summary>Pushes a sent message onto the history (no consecutive duplicate).</summary>
    private void PushHistory(string text)
    {
        if (_history.Count == 0 || _history[_history.Count - 1] != text)
        {
            _history.Add(text);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
        _historyIndex = -1;
        _draft = "";
    }

    private void Open()
    {
        _isOpen = true;
        _input = "";
        _historyIndex = -1;
        _draft = "";
        CairnGameApi.ReconcileGameplayInput(true);   // immediate (the per-frame reconcile follows)
    }

    private void Close()
    {
        _isOpen = false;
        _input = "";
        CairnGameApi.ReconcileGameplayInput(false);  // immediate; per-frame reconcile = safety net
    }

    private void Submit()
    {
        var text = (_input ?? "").Trim();
        // try/finally is CRUCIAL: if a command throws, Close() MUST still run, otherwise
        // the chat stays open and the input freeze is never restored (player stuck). We
        // always close, come what may.
        try
        {
            if (text.Length > 0)
            {
                // Record everything sent (messages AND commands) into the history for arrow recall.
                PushHistory(text);

                // A command? the router consumes it. Otherwise, a normal message: immediate
                // local echo (we always see ourselves, even solo or before a peer arrives) +
                // network broadcast.
                if (!_router.TryHandle(text))
                {
                    var name = string.IsNullOrWhiteSpace(ModConfig.PlayerName.Value)
                        ? "You" : ModConfig.PlayerName.Value;
                    AddLine($"{name}: {text}", system: false);
                    _network.SendChat(text);
                }
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[Chat] command/send failed: {ex.Message}");
            AddSystemLine("Command failed.");
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Label style at the current scale (fontSize refreshed every frame).</summary>
    private GUIStyle EnsureLabelStyle()
    {
        _labelStyle ??= new GUIStyle(GUI.skin.label);
        _labelStyle.fontSize = ScaledFontSize;
        return _labelStyle;
    }

    private void DrawLog()
    {
        float now = Time.unscaledTime;
        float scale = UiScale;
        var style = EnsureLabelStyle();
        float lineHeight = 22f * scale;
        float width = 520f * scale;
        float x = Screen.width - width - 16f * scale;   // anchored at the bottom RIGHT
        float bottom = Screen.height - (_isOpen ? 84f : 60f) * scale;

        int shown = 0;
        for (int i = _lines.Count - 1; i >= 0 && shown < MaxVisibleLines; i--)
        {
            var line = _lines[i];
            // When the chat is closed, we hide messages older than the fade.
            if (!_isOpen && now - line.ShownAt > FadeAfterSeconds) continue;

            float y = bottom - (shown + 1) * lineHeight;
            DrawShadowLabel(new Rect(x, y, width, lineHeight), line.Text,
                line.System ? SystemColor : TextColor, style);
            shown++;
        }
    }

    private void DrawInput()
    {
        float scale = UiScale;
        var style = EnsureLabelStyle();
        float width = 520f * scale;
        float x = Screen.width - width - 16f * scale;   // anchored at the bottom RIGHT
        float height = (BaseFontSize + 10) * scale;
        float y = Screen.height - height - 28f * scale;

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.Box(new Rect(x - 2f * scale, y - 2f * scale, width + 4f * scale, height + 4f * scale), GUIContent.none);
        GUI.color = Color.white;

        // Manual rendering via GUI.Label (GUI.TextField is stripped under IL2CPP). The caret
        // blinks at ~2 Hz to signal active input.
        bool caretOn = ((int)(Time.unscaledTime * 2f) & 1) == 0;
        GUI.Label(new Rect(x + 4f * scale, y + 2f * scale, width - 8f * scale, height),
            "> " + (_input ?? "") + (caretOn ? "_" : ""), style);
        GUI.color = prev;
    }

    private static void DrawShadowLabel(Rect rect, string text, Color color, GUIStyle style)
    {
        var prev = GUI.color;
        var shadow = rect;
        shadow.x += 1f;
        shadow.y += 1f;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.Label(shadow, text, style);
        GUI.color = color;
        GUI.Label(rect, text, style);
        GUI.color = prev;
    }
}
