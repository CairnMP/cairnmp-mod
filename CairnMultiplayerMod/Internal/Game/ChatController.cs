using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

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
    private static readonly Color HintColor = new Color(0.7f, 0.78f, 0.86f, 1f);

    // Hint length in characters. The panel keeps a constant width/font-size ratio
    // (520/18) at every resolution, so a fixed character budget is enough and we avoid
    // GUIStyle.CalcSize, which IL2CPP is free to strip like it stripped DoTextField.
    private const int MaxHintChars = 56;

    // Shown as soon as the overlay opens: the completion is only discoverable if
    // something says it exists.
    private const string EmptyInputHint = "Type / for commands, Tab to complete";

    private const int BaseFontSize = 18;

    // UI scale proportional to the resolution (ref 1080p): the IMGUI chat reasons in
    // raw pixels, so without this it looks tiny at 4K and too big at 720p. Floored at
    // the 720p ratio (~0.667) so it doesn't become unreadable on small screens, capped
    // at 2.5 to stay reasonable on very large ones.
    private static float UiScale => Mathf.Clamp(Screen.height / 1080f, 720f / 1080f, 2.5f);
    private static int ScaledFontSize => Mathf.RoundToInt(BaseFontSize * UiScale);

    private GUIStyle _labelStyle;

    private readonly CommandRouter _router;
    private readonly Action<string> _send;
    private readonly Func<bool> _canChat;

    private readonly List<ChatLine> _lines = new();
    private bool _isOpen;
    private string _input = "";

    private const int MaxHistory = 50;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _draft = "";

    private ChatCompletionSet _completions = ChatCompletionSet.Empty;
    private int _completionIndex = -1;
    private bool _completionsDirty = true;
    private string _hint = "";

    public ChatController(CommandRouter router, Action<string> send, Func<bool> canChat)
    {
        _router = router;
        _send = send;
        _canChat = canChat;
    }

    public bool IsTyping => _isOpen;

    public void AddRemoteLine(string fromName, string message)
        => AddLine($"{fromName}: {message}", system: false);

    public void AddSystemLine(string text) => AddLine(text, system: true);

    private void AddLine(string text, bool system)
    {
        _lines.Add(new ChatLine { Text = text, ShownAt = Time.unscaledTime, System = system });
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
    }

    public void Update()
    {
        if (_isOpen && !_canChat())
        {
            Close();
            return;
        }
        // Reconcile every frame so a temporarily missing InputManager does not strand input.
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
        InputCaptureState.IsKeyboardCaptured = _isOpen;
    }

    /// <summary>Native menu transitions require the chat-owned block to be released immediately.</summary>
    public void ForceClose()
    {
        Close();
    }

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

        if (_isOpen) EnsureCompletions();

        DrawLog();
        if (!_isOpen) return;
        DrawInput();
        DrawHint();
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
                InvalidateCompletions();
                e.Use();
                return;
            case KeyCode.Tab:
                CycleCompletion(e.shift ? -1 : 1);
                e.Use();
                return;
            case KeyCode.UpArrow:
                RecallOlder();
                InvalidateCompletions();
                e.Use();
                return;
            case KeyCode.DownArrow:
                RecallNewer();
                InvalidateCompletions();
                e.Use();
                return;
        }

        // Printable character (e.character carries the typed character, accents included).
        // Tab also arrives as a character event: char.IsControl filters it out, so it is
        // never appended to the input.
        char c = e.character;
        if (c != '\0' && !char.IsControl(c) && _input.Length < MaxInputLength)
        {
            _input += c;
            InvalidateCompletions();
            e.Use();
        }
    }

    private void CycleCompletion(int direction)
    {
        EnsureCompletions();
        if (_completions.Count == 0) return;

        _completionIndex = _completionIndex < 0
            ? (direction > 0 ? 0 : _completions.Count - 1)
            : (_completionIndex + direction + _completions.Count) % _completions.Count;

        var candidate = _completions.Candidates[_completionIndex].Input;
        if (candidate.Length > MaxInputLength) return;

        _input = candidate;
        _historyIndex = -1;          // we left the history recall
        _draft = "";
        if (_completions.Count == 1) InvalidateCompletions();
        else RefreshHint();
    }

    private void InvalidateCompletions()
    {
        _completionsDirty = true;
        _completionIndex = -1;
    }

    /// <summary>
    /// Recomputes the candidates for the current line if needed. Called from OnGUI and
    /// from Tab, so it must never throw: a failure here would leave the overlay open and
    /// the player's inputs frozen.
    /// </summary>
    private void EnsureCompletions()
    {
        if (!_completionsDirty) return;
        _completionsDirty = false;
        _completionIndex = -1;
        try
        {
            _completions = _router.GetCompletions(_input);
        }
        catch (Exception ex)
        {
            _completions = ChatCompletionSet.Empty;
            ModLog.Warning($"[Chat] completion failed: {ex.Message}");
        }
        RefreshHint();
    }

    private void RefreshHint()
    {
        if (!_isOpen) { _hint = ""; return; }
        _hint = _input.Length == 0
            ? EmptyInputHint
            : ChatCompletion.BuildHint(_completions, _completionIndex, MaxHintChars);
    }

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
        InvalidateCompletions();
        InputCaptureState.IsKeyboardCaptured = true;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
    }

    private void Close()
    {
        _isOpen = false;
        _input = "";
        InvalidateCompletions();
        _hint = "";
        InputCaptureState.IsKeyboardCaptured = false;
        InputInterop.ReconcileGameplayInput(InputCaptureState.WantsGameplayBlocked);
    }

    private void Submit()
    {
        var text = (_input ?? "").Trim();
        // A command failure must not leave the chat's input block active.
        try
        {
            if (text.Length > 0)
                DispatchSubmittedText(text);
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Chat] command/send failed: {ex.Message}");
            AddSystemLine("Command failed.");
        }
        finally
        {
            Close();
        }
    }

    private void DispatchSubmittedText(string text)
    {
        PushHistory(text);
        if (_router.TryHandle(text)) return;

        var name = string.IsNullOrWhiteSpace(ModConfig.PlayerName.Value)
            ? "You"
            : ModConfig.PlayerName.Value;
        AddLine($"{name}: {text}", system: false);
        _send(text);
    }

    private GUIStyle EnsureLabelStyle()
    {
        _labelStyle ??= new GUIStyle(GUI.skin.label);
        _labelStyle.richText = false;
        _labelStyle.fontSize = ScaledFontSize;
        _labelStyle.alignment = TextAnchor.MiddleLeft;
        // Some fonts draw their descenders (g, j, p, q, y) a little outside their
        // advertised metrics. Let IMGUI render these pixels instead of clipping them.
        _labelStyle.clipping = TextClipping.Overflow;
        return _labelStyle;
    }

    private void DrawLog()
    {
        float now = Time.unscaledTime;
        float scale = UiScale;
        var style = EnsureLabelStyle();
        // Leave room for descenders and the one-pixel drop shadow.
        float lineHeight = (BaseFontSize + 8f) * scale;
        float width = PanelWidth(scale);
        float x = PanelX(scale);                        // anchored at the bottom RIGHT
        float bottom = Screen.height - (_isOpen ? 84f : 60f) * scale - HintBand(scale);

        int shown = 0;
        for (int i = _lines.Count - 1; i >= 0 && shown < MaxVisibleLines; i--)
        {
            var line = _lines[i];
            if (!_isOpen && now - line.ShownAt > FadeAfterSeconds) continue;

            float y = bottom - (shown + 1) * lineHeight;
            DrawShadowLabel(new Rect(x, y, width, lineHeight), line.Text,
                line.System ? SystemColor : TextColor, style);
            shown++;
        }
    }

    private static float PanelWidth(float scale) => 520f * scale;
    private static float PanelX(float scale) => Screen.width - PanelWidth(scale) - 16f * scale;
    private static float InputHeight(float scale) => (BaseFontSize + 14f) * scale;
    private static float InputTop(float scale) => Screen.height - InputHeight(scale) - 28f * scale;
    private static float HintHeight(float scale) => (BaseFontSize + 8f) * scale;

    private float HintBand(float scale)
        => _isOpen && _hint.Length > 0 ? HintHeight(scale) + 2f * scale : 0f;

    /// <summary>
    /// Suggestion bar drawn just above the input: candidates to cycle through with Tab,
    /// or the usage of the command being typed. A single GUI.Label, no text measuring:
    /// GUIStyle.CalcSize is exactly the kind of method IL2CPP strips.
    /// </summary>
    private void DrawHint()
    {
        if (_hint.Length == 0) return;

        float scale = UiScale;
        float height = HintHeight(scale);
        float y = InputTop(scale) - height - 2f * scale;
        DrawShadowLabel(new Rect(PanelX(scale) + 4f * scale, y, PanelWidth(scale) - 8f * scale, height),
            _hint, HintColor, EnsureLabelStyle());
    }

    private void DrawInput()
    {
        float scale = UiScale;
        var style = EnsureLabelStyle();
        float width = PanelWidth(scale);
        float x = PanelX(scale);                        // anchored at the bottom RIGHT
        float height = InputHeight(scale);
        float y = InputTop(scale);

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.Box(new Rect(x - 2f * scale, y - 2f * scale, width + 4f * scale, height + 4f * scale), GUIContent.none);
        GUI.color = Color.white;

        // GUI.TextField is stripped under IL2CPP, so input is rendered manually.
        bool caretOn = ((int)(Time.unscaledTime * 2f) & 1) == 0;
        GUI.Label(new Rect(x + 4f * scale, y, width - 8f * scale, height),
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
