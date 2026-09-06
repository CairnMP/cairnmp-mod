namespace CairnMultiplayerMod.GameApi;

/// <summary>Audio and native settings live behind this Unity-free feature boundary.</summary>
internal interface IVoiceApi
{
    void Tick(bool connected);
    bool TryCapture(out uint burst, out uint sequence, out byte[] opus);
    void Receive(int playerId, uint burst, uint sequence, byte[] opus);
    void RemovePlayer(int playerId);
    void Reset();
}

internal sealed class UnavailableVoiceApi : IVoiceApi
{
    internal static readonly UnavailableVoiceApi Instance = new();
    public void Tick(bool connected) { }
    public bool TryCapture(out uint burst, out uint sequence, out byte[] opus) { burst = sequence = 0; opus = null; return false; }
    public void Receive(int playerId, uint burst, uint sequence, byte[] opus) { }
    public void RemovePlayer(int playerId) { }
    public void Reset() { }
}
