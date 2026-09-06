using System.Collections.Generic;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Safe capture and application operations for local and remote climbers.</summary>
internal interface IPlayersApi
{
    IReadOnlyList<int> RemotePlayersInGame { get; }
    IReadOnlyList<int> RemoteSleepParticipants { get; }

    bool TryCaptureHandPose(out byte[] packed);
    bool TryCaptureAppearance(out int packed);
    bool TryCaptureCosmetics(out byte flags);
    void SetRemoteHandPose(int playerId, byte[] packed);
    void SetRemoteAppearance(int playerId, int packed);
    void SetRemoteCosmetics(int playerId, byte flags);
    void ResetHandPoseCaches();
    void ResetAppearanceCaches();
}
