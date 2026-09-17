using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Extensions;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// Crossing a scene boundary is not leaving the session, and every feature that forces a
/// native state has to tell them apart.
///
/// A scene reset fires constantly — gameplay boundaries, zone streaming, entering and leaving
/// a bivouac, several times within seconds. Handing the forced state back to the game on each
/// one lets the local state play out until the next host packet takes over again: the weather
/// flipping and flipping back, the sky jumping. Leaving the session is the only moment the
/// game should get its state back.
/// </summary>
public sealed class SceneResetSemanticsTests
{
    [Fact]
    public void WeatherKeepsTheHostStateAcrossASceneBoundary()
    {
        var game = new RecordingGameApi();
        var host = NewHost(game, new WeatherFeature());

        host.NotifySceneReset();

        Assert.Equal(1, game.Weather.SceneChanges);
        Assert.Equal(0, game.Weather.Resets);
    }

    [Fact]
    public void WeatherIsHandedBackWhenTheSessionEnds()
    {
        var game = new RecordingGameApi();
        var host = NewHost(game, new WeatherFeature());

        host.NotifySessionEnded();

        Assert.Equal(1, game.Weather.Resets);
        Assert.Equal(0, game.Weather.SceneChanges);
    }

    [Fact]
    public void TheDayNightCycleIsNotUnfrozenByASceneBoundary()
    {
        var game = new RecordingGameApi();
        var host = NewHost(game, new ClockFeature());

        host.NotifySceneReset();

        Assert.Equal(1, game.Clock.SceneChanges);
        Assert.Equal(0, game.Clock.Resets);
    }

    [Fact]
    public void TheDayNightCycleIsHandedBackWhenTheSessionEnds()
    {
        var game = new RecordingGameApi();
        var host = NewHost(game, new ClockFeature());

        host.NotifySessionEnded();

        Assert.Equal(1, game.Clock.Resets);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void RepeatedBoundariesNeverRelinquishForcedState(int boundaries)
    {
        var game = new RecordingGameApi();
        var host = NewHost(game, new WeatherFeature(), new ClockFeature());

        // A gameplay boundary can bounce several times in a few seconds; each bounce used to
        // cost one visible weather flip and one sky jump.
        for (var i = 0; i < boundaries; i++) host.NotifySceneReset();

        Assert.Equal(boundaries, game.Weather.SceneChanges);
        Assert.Equal(boundaries, game.Clock.SceneChanges);
        Assert.Equal(0, game.Weather.Resets);
        Assert.Equal(0, game.Clock.Resets);
    }

    [Fact]
    public void FeaturesThatOnlyRearmLocalCapturesMayShareOneHandler()
    {
        // Appearance and hand poses force nothing on the game: their reset only clears
        // capture caches and re-arms sending, which is right for both events. This test
        // exists so a future change there is a deliberate one.
        var game = new RecordingGameApi();
        var host = NewHost(game, new AppearanceFeature(), new HandPoseFeature());

        host.NotifySceneReset();
        host.NotifySessionEnded();

        Assert.Equal(0, game.Weather.Resets);
        Assert.Equal(0, game.Clock.Resets);
    }

    private static FeatureHost NewHost(IGameApi game, params MultiplayerFeature[] features)
    {
        var host = new FeatureHost(new ExtensionRuntime(), game);
        host.RegisterAll(features, new Version(1, 0, 0));
        return host;
    }

    private sealed class RecordingWeatherApi : IWeatherApi
    {
        internal int SceneChanges { get; private set; }
        internal int Resets { get; private set; }

        public bool TryCapture(out WeatherSyncData weather) { weather = default; return false; }
        public bool IsValid(WeatherSyncData weather) => false;
        public void ApplyRemote(WeatherSyncData weather) { }
        public void TickRemote() { }
        public void OnSceneChanged() => SceneChanges++;
        public void Reset() => Resets++;
    }

    private sealed class RecordingClockApi : IClockApi
    {
        internal int SceneChanges { get; private set; }
        internal int Resets { get; private set; }

        public bool TryGetDayTime(out float dayTime) { dayTime = 0f; return false; }
        public bool TryGetLocalSleep(out bool asleep) { asleep = false; return false; }
        public bool Freeze(float dayTime) => false;
        public bool Unfreeze() => false;
        public void OnSceneChanged() => SceneChanges++;
        public void Reset() => Resets++;
        public void LogDiagnosticsOnce() { }
    }

    private sealed class RecordingGameApi : IGameApi
    {
        internal RecordingWeatherApi Weather { get; } = new();
        internal RecordingClockApi Clock { get; } = new();

        IWeatherApi IGameApi.Weather => Weather;
        IClockApi IGameApi.Clock => Clock;

        public IMainMenuApi MainMenu => UnavailableGameApi.Instance.MainMenu;
        public IGameStateApi State => UnavailableGameApi.Instance.State;
        public IGameTimeApi Time => UnavailableGameApi.Instance.Time;
        public IGameInputApi Input => UnavailableGameApi.Instance.Input;
        public IGameHudApi Hud => UnavailableGameApi.Instance.Hud;
        public IChatApi Chat => UnavailableGameApi.Instance.Chat;
        public IInventoryApi Inventory => UnavailableGameApi.Instance.Inventory;
        public IPlayersApi Players => UnavailableGameApi.Instance.Players;
        public IWorldApi World => UnavailableGameApi.Instance.World;
    }
}
