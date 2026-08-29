using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Features.Chat;

/// <summary>A chat line sent by a player.</summary>
internal sealed class ChatMessage : IPacket
{
    public string FromName = "";
    public string Text = "";

    public void Serialize(BinaryWriter writer)
    {
        PacketCodec.WriteString(writer, FromName);
        PacketCodec.WriteString(writer, Text);
    }

    public void Deserialize(BinaryReader reader)
    {
        FromName = PacketCodec.ReadString(reader);
        Text = PacketCodec.ReadString(reader);
    }
}

/// <summary>
/// In-game chat and host commands. Owns the overlay (<see cref="ChatController"/>, which
/// holds the IMGUI rendering and the typing state) and carries its lines over the network.
/// </summary>
internal sealed class ChatFeature : MultiplayerFeature
{
    public override string Id => "chat";

    private Broadcast<ChatMessage> _lines;
    private ChatController _chat;

    /// <summary>The overlay, for the panic failsafe wired in the mod core.</summary>
    internal ChatController Controller => _chat;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _lines = feature.Broadcast<ChatMessage>("line", ShowRemoteLine);

        // The router checks the host role when dispatching; feedback shows up as local
        // system lines. Typing is only allowed in game and while the game is not paused —
        // Cairn pauses with timeScale=0, and an overlay left open there would keep the
        // input freeze on after unpausing.
        var router = new CommandRouter(Mod.Instance.Network, () => IsHost, line => _chat.AddSystemLine(line));
        _chat = new ChatController(router, Send, () => IsConnected && CanTypeNow());

        feature.EveryFrame(_chat.Update, FeaturePhase.Always);
        feature.EveryFrame(TickPanicKey, FeaturePhase.Always);
        feature.OnDrawHud(_chat.OnGUI);

        feature.OnPlayerJoined((_, name) => _chat.AddSystemLine($"{name} joined the session."));
        feature.OnPlayerLeft((_, name) => _chat.AddSystemLine($"{name} left the session."));
    }

    private static bool CanTypeNow()
        => Mod.Instance.LocalState == PlayerState.InGame && UnityEngine.Time.timeScale > 0f;

    /// <summary>
    /// Panic failsafe (F10): closes the overlay whatever the state, so a chat stuck open
    /// can never keep the player's input frozen. The mod core clears the game-side input
    /// block on the same key; the two are deliberately independent.
    /// </summary>
    private void TickPanicKey()
    {
        if (UnityEngine.InputSystem.Keyboard.current?.f10Key.wasPressedThisFrame != true) return;
        if (!_chat.IsTyping) return;

        _chat.ForceClose();
        Mod.Log.Msg("[Chat] Panic: overlay force-closed (F10)");
    }

    private void Send(string text) => _lines.Send(new ChatMessage { FromName = LocalPlayerName, Text = text });

    // Only remote lines land here — a broadcast never echoes to its sender, so the local
    // echo written when submitting is the only copy we show for our own messages.
    private void ShowRemoteLine(int fromPlayerId, ChatMessage message)
        => _chat.AddRemoteLine(message.FromName, message.Text);
}
