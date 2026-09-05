namespace CairnMultiplayerMod.GameApi;

/// <summary>Simple transient text messages without exposing Unity IMGUI.</summary>
internal interface IGameHudApi
{
    void ShowMessage(string id, string text, float durationSeconds);
    void HideMessage(string id);
}
