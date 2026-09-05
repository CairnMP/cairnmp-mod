using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

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
        if (Text.Length > 200 || Text.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new InvalidDataException("Invalid chat message.");
    }
}

/// <summary>
/// In-game chat and host commands. The overlay and native input integration live behind
/// GameApi; this feature only owns the network behavior and lifecycle declarations.
/// </summary>
internal sealed class ChatFeature : MultiplayerFeature
{
    public override string Id => "chat";

    private Broadcast<ChatMessage> _lines;
    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _lines = feature.Broadcast<ChatMessage>("line", ShowRemoteLine);

        // The router checks the host role when dispatching; feedback shows up as local
        // system lines. Typing is only allowed in game and while the game is not paused —
        // Cairn pauses with timeScale=0, and an overlay left open there would keep the
        // input freeze on after unpausing.
        Game.Chat.Configure(Send, () => IsHost, () => IsConnected && CanTypeNow());

        feature.EveryFrame(Game.Chat.Tick, FeaturePhase.Always);
        feature.EveryFrame(TickPanicKey, FeaturePhase.Always);
        feature.OnDrawHud(Game.Chat.Draw);

        feature.OnPlayerJoined((_, name) => Game.Chat.AddSystemLine($"{name} joined the session."));
        feature.OnPlayerLeft((_, name) => Game.Chat.AddSystemLine($"{name} left the session."));
    }

    private bool CanTypeNow() => Game.State.IsLocalPlayerInGame && !Game.Time.IsPaused;

    /// <summary>
    /// Panic failsafe (F10): closes the overlay whatever the state, so a chat stuck open
    /// can never keep the player's input frozen. The mod core clears the game-side input
    /// block on the same key; the two are deliberately independent.
    /// </summary>
    private void TickPanicKey()
    {
        if (!Game.Input.WasPressed(GameInputAction.Panic)) return;
        if (!Game.Chat.IsTyping) return;

        Game.Chat.ForceClose();
        LogInfo("Panic: overlay force-closed (F10)");
    }

    private void Send(string text) => _lines.Send(new ChatMessage { FromName = LocalPlayerName, Text = text });

    // Only remote lines land here — a broadcast never echoes to its sender, so the local
    // echo written when submitting is the only copy we show for our own messages.
    private void ShowRemoteLine(int fromPlayerId, ChatMessage message)
        => Game.Chat.AddRemoteLine(GetPlayerName(fromPlayerId), message.Text);
}
