using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.UI;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PanelRegressionTests
{
    [Fact]
    public void NativeCloseUpdatesWrapperAndToggleReopens()
    {
        var primary = new Panel();
        var wrapper = new ResilientMultiplayerPanel(primary, () => new Panel());
        wrapper.Show();
        primary.Close();
        Assert.False(wrapper.IsVisible);
        wrapper.Toggle();
        Assert.True(wrapper.IsVisible);
        Assert.True(primary.IsVisible);
    }

    [Fact]
    public void FallbackDestroysAndUnsubscribesPrimaryWhilePreservingState()
    {
        var primary = new Panel();
        var fallback = new Panel();
        var wrapper = new ResilientMultiplayerPanel(primary, () => fallback);
        wrapper.Show();
        wrapper.SetCurrentLobbyName("Expedition");
        primary.FailTick = true;
        wrapper.Tick(1);
        Assert.True(primary.Destroyed);
        Assert.True(fallback.IsVisible);
        Assert.Equal("Expedition", fallback.LobbyName);
        primary.Close();
        Assert.True(wrapper.IsVisible);
        fallback.Close();
        Assert.False(wrapper.IsVisible);
    }

    private sealed class Panel : IMultiplayerPanel
    {
        public event Action<HostConfig> OnHostRequested { add {} remove {} }
        public event Action<string> OnJoinByCodeRequested { add {} remove {} }
        public event Action OnBrowseRequested { add {} remove {} }
        public event Action<ulong> OnJoinByLobbyIdRequested { add {} remove {} }
        public event Action OnDisconnectRequested { add {} remove {} }
        public event Action OnStartRequested { add {} remove {} }
        public event Action OnPanelClosed;
        public bool IsVisible { get; private set; }
        internal bool FailTick, Destroyed;
        internal string LobbyName;
        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void Toggle() => IsVisible = !IsVisible;
        public void SetStatus(string status, bool connected) {}
        public void SetConnecting(string status) {}
        public void SetBrowserLobbies(IReadOnlyList<LobbyEntry> lobbies) {}
        public void SetCurrentLobbyName(string name) => LobbyName = name;
        public void Tick(float dt) { if (FailTick) throw new InvalidOperationException("UI unavailable"); }
        public void OnGUI() {}
        public void DestroyResources() { Destroyed = true; IsVisible = false; }
        internal void Close() { IsVisible = false; OnPanelClosed?.Invoke(); }
    }
}
