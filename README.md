# CairnMP

> **Development branch:** `develop` contains ongoing work. Automated checks do
> not establish in-game stability. Back up your saves and use matching mod/protocol
> versions when testing with other players. Check the release notes before playing.

[Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md) ·
[Code of conduct](CODE_OF_CONDUCT.md)

---

A multiplayer mod for the climbing game **Cairn**. It runs inside the game via
MelonLoader (IL2CPP) and connects players over a host-authoritative **Steam relay**
(P2P) — no dedicated server required.

Features include roped climbing (belay), in-game chat and host commands, world
pings, FreeRoam, and full synchronization of players, cosmetics, weather, lamps,
pitons and time of day.

## Contributing a feature

A multiplayer feature is **one file**. It declares what it
sends over the network, what it does each frame and what it cleans up — nothing
else in the codebase is touched. No packet ids, no serialization plumbing, no
wiring in the mod core.

Here is a complete, working example, start to finish. Save it as
`CairnMultiplayerMod/Features/Wave/WaveFeature.cs`, build, and pressing **H**
shows `<name> waves!` on every other player's screen for three seconds. Nothing
below is hidden or abbreviated — this is the whole feature.

```csharp
using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Features.Wave;

/// What travels over the network when someone waves.
/// IPacket is the same contract the rest of the protocol uses: you write the
/// fields out and read them back, in the same order.
internal sealed class WaveSent : IPacket
{
    public string FromName = "";

    public void Serialize(BinaryWriter writer) => PacketCodec.WriteString(writer, FromName);
    public void Deserialize(BinaryReader reader) => FromName = PacketCodec.ReadString(reader);
}

internal sealed class WaveFeature : MultiplayerFeature
{
    // Names this feature's messages on the wire. Lowercase, and stable: renaming it
    // breaks compatibility with players still on the old version.
    public override string Id => "wave";

    private const float BannerSeconds = 3f;

    private Broadcast<WaveSent> _wave;
    private string _banner;
    private float _hideBannerAt;

    // Called once at startup. Declare here, don't touch the game yet.
    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _wave = feature.Broadcast<WaveSent>("sent", ShowWave);
        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.OnDrawHud(DrawBanner);
        feature.OnSessionEnded(() => _banner = null);
    }

    private void TickInput()
    {
        if (Keyboard.current?.hKey.wasPressedThisFrame != true) return;

        // Goes to every other player. LocalPlayerName comes from the base class.
        _wave.Send(new WaveSent { FromName = LocalPlayerName });
    }

    // Runs on the receiving side. Never fires on the sender — show your own
    // effect locally when you send, if you want one.
    private void ShowWave(int fromPlayerId, WaveSent wave)
    {
        _banner = $"{wave.FromName} waves!";
        _hideBannerAt = Time.unscaledTime + BannerSeconds;
    }

    private void DrawBanner()
    {
        if (_banner == null) return;
        if (Time.unscaledTime >= _hideBannerAt) { _banner = null; return; }

        GUI.Label(new Rect(20f, 20f, 400f, 30f), _banner);
    }
}
```

That is all of it. **You do not register it anywhere** — a source generator finds
every class deriving from `MultiplayerFeature` at build time and builds the list
for you. (Curious what it produced? Look at `obj/generated/` after a build.)

**Pick your channel by what the feature needs:**

| Channel | Meaning | Example in this repo |
|---|---|---|
| `Broadcast<T>` | everyone sees it, any player can send | pings, chat |
| `HostState<T>` | the host owns it, and players joining later catch up automatically | weather, time of day |
| `HostCommand<T>` | the client asks, the host accepts or refuses | roping up |

Use `HostState` whenever a latecomer would otherwise miss something that is still
true — a broadcast is gone the moment it is sent.

**Pick your phase:** `FeaturePhase.Always` runs every frame, including in menus
and bivouacs — for input and HUD. `FeaturePhase.Gameplay` runs only while gameplay
sync is active — for anything touching the world, since during a bivouac the game
drives the pawn itself.

**Where things live:** `Features/` is what the mod does, `Framework/` is what you
write it with. Everything a feature is allowed to do is on `FeatureBuilder` — that
one class is the whole surface to learn.

A feature that throws is logged and isolated: it cannot take the other features,
or the update loop, down with it.

For a real one, read `Features/World/PingFeature.cs` — same shape, with its marker
drawing split into a helper next to it.

## Managed extension API

Other MelonLoader mods can integrate with CairnMP through the host-authoritative
`CairnMultiplayer.Api` surface. It provides compatible-extension negotiation,
typed commands, transactional effects, late-join replicated state and transient
events without exposing Steam or raw packets. See
[`docs/multiplayer-api.md`](docs/multiplayer-api.md).

## Repository layout

| Path | In `.slnx`? | Role |
|---|---|---|
| `CairnMultiplayerMod/` | ✅ | MelonLoader mod loaded into Cairn |
| `CairnMultiplayerMod/Features/` | ✅ | What the mod does — one folder per feature |
| `CairnMultiplayerMod/Framework/` | ✅ | What you write a feature with (channels, lifecycle) |
| `CairnMultiplayerMod.Generators/` | ✅ | Build-time generator listing the features (never ships) |
| `CairnMultiplayerShared/` | ✅ | Shared network protocol (packets, constants) |
| `CairnMultiplayerShared.Tests/` | ✅ | xUnit tests for the shared protocol |
| `CairnMultiplayerMod.Tests/` | ✅ | xUnit tests for the framework and the extension API |
| `game-refs/` | — | Il2Cpp + MelonLoader reference assemblies (**not committed** — provide your own, see below) |

## Reference assemblies

The build references proprietary Cairn / Unity / MelonLoader assemblies that
**cannot be redistributed**, so `game-refs/` is git-ignored. Provide them locally
in one of two ways (resolved by `Directory.Build.props`):

- drop the DLLs into `game-refs/Il2CppAssemblies/` and `game-refs/MelonLoader/`, or
- point `MELON_LOADER_NET6_DIR` at your MelonLoader `net6` folder; the Il2Cpp
  assemblies are otherwise read from your local Cairn install
  (`%LOCALAPPDATA%\CairnMultiplayerData\game\CairnLoader\Il2CppAssemblies`).

## Build & test

```bash
dotnet build CairnMultiplayer.slnx -c Release
dotnet test  CairnMultiplayer.slnx -c Release
```

Requires a .NET 10 SDK, the .NET 6 runtime for the current test targets, and the
reference assemblies described above. See [CONTRIBUTING.md](CONTRIBUTING.md) for
public CI checks that run without game DLLs. Builds do not install the mod;
use `-p:DeployMod=true` with an explicit `CairnDir` to opt into local deployment.

## Package a release

```bash
pwsh scripts/package-mod.ps1     # → dist/cairnmp-mod-{version}.zip
```

## Versions

`versions.json` (`mod`, `protocol`) is the source of truth. Propagate it into the
assembly info and protocol constants with:

```bash
node scripts/sync-versions.js
```

`protocol` is bumped only when the packet layout changes incompatibly — the
handshake rejects clients whose protocol version does not match.

## License

Copyright (C) 2026 Yutho

This program is free software: you can redistribute it and/or modify it under the
terms of the **GNU Affero General Public License v3.0** as published by the Free
Software Foundation. See [`LICENSE`](LICENSE) for the full text.

This is strong copyleft: any derivative — including a competing mod that reuses
this code — **must** be released under the AGPL-3.0 with full source. The Affero
clause also covers **network use**: anyone who lets others interact with a modified
version over a network (e.g. hosting a multiplayer session) must offer them its
source, even without distributing a binary.

The proprietary Cairn / Unity / MelonLoader reference assemblies are **not** part
of this project and are not covered by this license.
