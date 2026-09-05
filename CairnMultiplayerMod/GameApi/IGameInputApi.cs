namespace CairnMultiplayerMod.GameApi;

internal enum GameInputAction
{
    PrimaryPointer,
    PingController,
    Panic,
}

internal enum GameKey
{
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

/// <summary>Edge-triggered input queries safe for feature callbacks.</summary>
internal interface IGameInputApi
{
    bool IsKeyboardCaptured { get; }
    bool WasPressed(GameInputAction action);
    bool WasKeyPressed(GameKey key);
}
