using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Networking;
using UnityEngine;

namespace CairnMultiplayerMod.Core.Chat;

/// <summary>
/// Chat in-game en IMGUI (OnGUI) : overlay des messages recents en bas a gauche +
/// ligne de saisie. Entree ouvre/envoie, Echap annule. Pendant la saisie, les inputs
/// gameplay du jeu sont geles (<see cref="CairnGameApi.SetGameplayInputDisabled"/>)
/// pour que taper ne pilote pas le grimpeur. Reutilise la plomberie chat existante
/// (NetworkManager.SendChat / OnChatReceived) ; les commandes passent par le router.
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

    // Taille de police de base (a l'echelle 1, ~1080p). Montee depuis le defaut IMGUI
    // (~13) pour une meilleure lisibilite. Scalee ensuite par UiScale.
    private const int BaseFontSize = 18;

    // Echelle UI proportionnelle a la resolution (ref 1080p) : le chat IMGUI raisonne en
    // pixels bruts, donc sans ca il parait minuscule en 4K et trop gros en 720p. Plancher
    // a la proportion 720p (~0.667) pour ne pas devenir illisible sur les petits ecrans,
    // plafond a 2.5 pour rester raisonnable sur les tres grands.
    private static float UiScale => Mathf.Clamp(Screen.height / 1080f, 720f / 1080f, 2.5f);
    private static int ScaledFontSize => Mathf.RoundToInt(BaseFontSize * UiScale);

    // Style de label cache, fontSize rafraichie chaque frame (resolution peut changer).
    private GUIStyle _labelStyle;

    private readonly NetworkManager _network;
    private readonly CommandRouter _router;
    private readonly Func<bool> _canChat;

    private readonly List<ChatLine> _lines = new();
    // Derniers pseudos connus par id, pour nommer le joueur dans le message "X left"
    // (OnPlayerLeft ne donne que l'id, le RemotePlayer est deja retire a ce moment).
    private readonly Dictionary<int, string> _knownNames = new();
    private bool _isOpen;
    private string _input = "";

    // Historique des messages envoyes (rappel via fleches haut/bas, comme un terminal).
    // _historyIndex == -1 : on edite le brouillon courant (_draft sauvegarde sa valeur quand
    // on remonte dans l'historique pour pouvoir y revenir avec Bas).
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
        // Notre propre message est deja affiche localement par Submit (echo optimiste) :
        // on ignore l'echo reseau renvoye par l'hote pour ne pas l'afficher deux fois.
        if (fromId == _network.LocalPlayerId) return;
        AddLine($"{fromName}: {message}", system: false);
    }

    private void OnPlayerJoined(int id, string name)
    {
        // Ignore soi-meme et les faux joueurs (debug mirror = id negatif).
        if (id < 0 || id == _network.LocalPlayerId) return;
        var display = string.IsNullOrWhiteSpace(name) ? $"Player{id}" : name;
        bool isNew = !_knownNames.ContainsKey(id);
        _knownNames[id] = display;
        // OnPlayerJoined peut re-emettre sur maj de presence : on n'annonce qu'une fois.
        if (isNew) AddSystemLine($"{display} joined the session.");
    }

    private void OnPlayerLeft(int id)
    {
        if (id < 0 || id == _network.LocalPlayerId) return;
        var display = _knownNames.TryGetValue(id, out var n) ? n : $"Player{id}";
        _knownNames.Remove(id);
        AddSystemLine($"{display} left the session.");
    }

    /// <summary>Ajoute une ligne systeme locale (feedback de commande) — non diffusee.</summary>
    public void AddSystemLine(string text) => AddLine(text, system: true);

    private void AddLine(string text, bool system)
    {
        _lines.Add(new ChatLine { Text = text, ShownAt = Time.unscaledTime, System = system });
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
    }

    /// <summary>Appele chaque frame depuis Mod.OnUpdate (thread Unity).</summary>
    public void Update()
    {
        if (_isOpen && !_canChat())
        {
            Close();
            return;
        }
        // Reconcile CHAQUE frame : input bloque SSI le chat est ouvert. _isOpen est l'unique
        // source de verite. Si une frame echoue a resoudre l'InputManager, la suivante
        // reessaie -> un chat ferme rend TOUJOURS l'input (plus de blocage permanent).
        CairnGameApi.ReconcileGameplayInput(_isOpen);
    }

    /// <summary>Fermeture forcee (failsafe panique) : ne touche pas au reseau, juste l'etat UI.
    /// Le deblocage des inputs est fait par l'appelant via CairnGameApi.ForceClearInputBlock.</summary>
    public void ForceClose()
    {
        _isOpen = false;
        _input = "";
    }

    /// <summary>Appele depuis Mod.OnGUI.</summary>
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
    /// Saisie de texte manuelle depuis les events clavier IMGUI. On NE peut PAS utiliser
    /// GUI.TextField : DoTextField est strippe du build IL2CPP (Method unstripping failed).
    /// On construit donc la string nous-memes (Entree/Echap/Backspace + caracteres).
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

        // Caractere imprimable (e.character porte le caractere tape, accents inclus).
        char c = e.character;
        if (c != '\0' && !char.IsControl(c) && _input.Length < MaxInputLength)
        {
            _input += c;
            e.Use();
        }
    }

    /// <summary>Fleche haut : rappelle un message plus ancien de l'historique.</summary>
    private void RecallOlder()
    {
        if (_history.Count == 0) return;
        if (_historyIndex == -1)
        {
            _draft = _input;                  // sauvegarde le brouillon en cours
            _historyIndex = _history.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        _input = _history[_historyIndex];
    }

    /// <summary>Fleche bas : revient vers les messages plus recents, puis le brouillon courant.</summary>
    private void RecallNewer()
    {
        if (_historyIndex == -1) return;      // deja sur le brouillon courant
        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            _input = _history[_historyIndex];
        }
        else
        {
            _historyIndex = -1;
            _input = _draft;                  // retour au brouillon
        }
    }

    /// <summary>Empile un message envoye dans l'historique (sans doublon consecutif).</summary>
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
        CairnGameApi.ReconcileGameplayInput(true);   // immediat (le reconcile/frame suit)
    }

    private void Close()
    {
        _isOpen = false;
        _input = "";
        CairnGameApi.ReconcileGameplayInput(false);  // immediat ; reconcile/frame = filet
    }

    private void Submit()
    {
        var text = (_input ?? "").Trim();
        // try/finally CRUCIAL : si une commande leve une exception, Close() DOIT quand meme
        // s'executer, sinon le chat reste ouvert et le gel d'input ne se restaure jamais
        // (joueur bloque). On ferme toujours, advienne que pourra.
        try
        {
            if (text.Length > 0)
            {
                // Historise tout ce qui est envoye (messages ET commandes) pour le rappel fleches.
                PushHistory(text);

                // Commande ? le router la consomme. Sinon, message normal : echo local
                // immediat (on se voit toujours, meme solo ou avant qu'un pair arrive) +
                // diffusion reseau.
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

    /// <summary>Style de label a l'echelle courante (fontSize rafraichie chaque frame).</summary>
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
        float x = Screen.width - width - 16f * scale;   // ancre en bas a DROITE
        float bottom = Screen.height - (_isOpen ? 84f : 60f) * scale;

        int shown = 0;
        for (int i = _lines.Count - 1; i >= 0 && shown < MaxVisibleLines; i--)
        {
            var line = _lines[i];
            // Quand le chat est ferme, on masque les messages plus vieux que le fondu.
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
        float x = Screen.width - width - 16f * scale;   // ancre en bas a DROITE
        float height = (BaseFontSize + 10) * scale;
        float y = Screen.height - height - 28f * scale;

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.Box(new Rect(x - 2f * scale, y - 2f * scale, width + 4f * scale, height + 4f * scale), GUIContent.none);
        GUI.color = Color.white;

        // Rendu manuel via GUI.Label (GUI.TextField est strippe en IL2CPP). Le caret
        // clignote ~2 Hz pour signaler la saisie active.
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
