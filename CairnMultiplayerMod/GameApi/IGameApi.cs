namespace CairnMultiplayerMod.GameApi;

/// <summary>
/// Safe entry point from a feature into Cairn. Its contracts deliberately expose no
/// Unity, IL2CPP, Steam or Harmony type; the fragile integration stays under Internal.
/// </summary>
internal interface IGameApi
{
    IMainMenuApi MainMenu { get; }
    IGameStateApi State { get; }
    IGameTimeApi Time { get; }
    IGameInputApi Input { get; }
    IGameHudApi Hud { get; }
    IChatApi Chat { get; }
    IClockApi Clock { get; }
    IPlayersApi Players { get; }
    IWeatherApi Weather { get; }
    IWorldApi World { get; }
}
