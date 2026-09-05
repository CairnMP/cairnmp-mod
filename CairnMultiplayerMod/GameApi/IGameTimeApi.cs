namespace CairnMultiplayerMod.GameApi;

/// <summary>Frame timing without exposing UnityEngine.Time.</summary>
internal interface IGameTimeApi
{
    float UnscaledTime { get; }
    float UnscaledDeltaTime { get; }
    bool IsPaused { get; }
}
