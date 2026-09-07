using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Composes the safe game services exposed to features.</summary>
internal sealed class GameApiFacade : IGameApi
{
    internal GameApiFacade(IMainMenuApi mainMenu, IGameStateApi state, IGameTimeApi time,
        IGameInputApi input, IGameHudApi hud, IChatApi chat, IInventoryApi inventory,
        IClockApi clock, IPlayersApi players,
        IWeatherApi weather, IWorldApi world, IVoiceApi voice = null)
    {
        MainMenu = mainMenu ?? throw new ArgumentNullException(nameof(mainMenu));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Time = time ?? throw new ArgumentNullException(nameof(time));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Hud = hud ?? throw new ArgumentNullException(nameof(hud));
        Chat = chat ?? throw new ArgumentNullException(nameof(chat));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Players = players ?? throw new ArgumentNullException(nameof(players));
        Weather = weather ?? throw new ArgumentNullException(nameof(weather));
        World = world ?? throw new ArgumentNullException(nameof(world));
        Voice = voice ?? UnavailableVoiceApi.Instance;
    }

    public IMainMenuApi MainMenu { get; }
    public IGameStateApi State { get; }
    public IGameTimeApi Time { get; }
    public IGameInputApi Input { get; }
    public IGameHudApi Hud { get; }
    public IChatApi Chat { get; }
    public IInventoryApi Inventory { get; }
    public IClockApi Clock { get; }
    public IPlayersApi Players { get; }
    public IWeatherApi Weather { get; }
    public IWorldApi World { get; }
    public IVoiceApi Voice { get; }
}
