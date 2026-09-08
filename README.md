# CairnMP 

[Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md) ·
[Code of conduct](CODE_OF_CONDUCT.md)

---

A multiplayer mod for the climbing game **Cairn**. It runs inside the game via
MelonLoader (IL2CPP) and connects players over a host-authoritative **Steam relay**
(P2P) — no dedicated server required.

Features include roped climbing (belay), in-game chat and host commands, nearby
item sharing, world pings, FreeRoam, and full synchronization of players,
cosmetics, weather, lamps, pitons and time of day.

Experimental [proximity voice](docs/proximity-voice.md) is available under
**Settings → CairnMP**, with open-mic detection, push-to-talk, microphone selection
and a local microphone test. Two-player voice validation is still pending.

### Chat

Press **Enter** to talk to the session, **↑ / ↓** to browse what you already sent, and
**Escape** to close (**F10** force-closes it if anything ever goes wrong). Your climber
stays put while you type.

**Tab** completes what is being typed and **Shift+Tab** cycles backwards: first the
command name, then the players for the arguments a command declares as `<player>`
(nicknames with spaces included). A bar above the input line lists the candidates and
shows the usage of the command in progress, so nothing has to be memorised — `/help`
still prints the full list. A command registered by a feature is completed like the
built-in ones as soon as its usage string names its arguments.

### Share items

Item sharing is intentionally local and conservative. Select an ordinary consumable in
Cairn's backpack: the native action bar adds **G — Give nearest** and **X — Drop**. Each
press moves exactly one unit. The give hint stays dimmed when no eligible recipient is nearby.
Both hints use Cairn's
bottom-left input legend and are disabled while dragging an item or while the bag is busy.
Giving requires both players to be active in the same area
and within **3.5 metres**. Dropped items are owned by the host, visible to every player and
can be picked up with **E** from within 1.6 metres. A full backpack returns the item to the
ground instead of destroying or duplicating it.

Quest items, containers, charms, unique objects and equipment with individual state are
excluded. At most 32 shared items may exist on the ground in a session.

The backpack is the only way to give or drop: the actions follow the selected stack, and
there is no chat command for it. An item therefore always leaves the bag through the same
path, on a stack you have in front of you.

## Contributing a feature

*(New on this branch.)* A multiplayer feature is **one file**. It declares what it
sends over the network, what it does each frame and what it cleans up — nothing
else in the codebase is touched. No packet ids, no serialization plumbing, no
wiring in the mod core.

Here is a complete, working example, start to finish. Save it as
`CairnMultiplayerMod/Features/WaveFeature.cs`, build, and pressing **H**
shows `<name> waves!` on every other player's screen for three seconds. Nothing
below is hidden or abbreviated — this is the whole feature.

```csharp
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

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

    // Called once at startup. Declare here, don't touch the game yet.
    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _wave = feature.Broadcast<WaveSent>("sent", ShowWave);
        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.OnSessionEnded(() => Game.Hud.HideMessage("wave"));
    }

    private void TickInput()
    {
        if (KeyboardCaptured || !Game.Input.WasKeyPressed(GameKey.H)) return;

        // Goes to every other player. LocalPlayerName comes from the base class.
        ShowWave(LocalPlayerId, new WaveSent { FromName = LocalPlayerName });
        _wave.Send(new WaveSent { FromName = LocalPlayerName });
    }

    // Runs on the receiving side. Never fires on the sender — show your own
    // effect locally when you send, if you want one.
    private void ShowWave(int fromPlayerId, WaveSent wave)
    {
        Game.Hud.ShowMessage("wave", $"{wave.FromName} waves!", BannerSeconds);
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
| `HostCommand<T>` | the client asks, the host accepts or refuses | sleep requests, appearance changes |
| `HostEvent<T>` | only the host emits a transient committed result | item delivery and receipt results |
| `PerPlayerState<T>` | the host owns a value per player; late joiners receive the current values | appearance |
| `Stream<T>` | transient updates with a bounded send rate | finger poses |

Use `HostState` whenever a latecomer would otherwise miss something that is still
true — a broadcast is gone the moment it is sent.

**Pick your phase:** `FeaturePhase.Always` runs every frame, including in menus
and bivouacs — for input and HUD. `FeaturePhase.Gameplay` runs only while gameplay
sync is active — for anything touching the world, since during a bivouac the game
drives the pawn itself.

**Where things live:** `Features/` is what the mod does, `Framework/` is what you
write it with, and `GameApi/` is the safe façade toward Cairn. Unity, IL2CPP,
Steamworks and Harmony stay under `Internal/`; build diagnostic `CMP003` rejects a
feature that bypasses that boundary.

A feature that throws is logged and isolated: it cannot take the other features,
or the update loop, down with it.

For a real one, read [`Features/PingFeature.cs`](CairnMultiplayerMod/Features/PingFeature.cs): input, world queries and HUD
markers all go through `GameApi`, while its networking remains declared locally.

## Managed extension API

Other MelonLoader mods can integrate with CairnMP through the host-authoritative
`CairnMultiplayer.Api` surface. It provides compatible-extension negotiation,
typed commands, transactional effects, late-join replicated state and transient
events without exposing Steam or raw packets. See
[`docs/multiplayer-api.md`](docs/multiplayer-api.md).

## Repository layout

| Path | In `.sln`? | Role |
|---|---|---|
| `CairnMultiplayerMod/` | ✅ | MelonLoader mod loaded into Cairn |
| `CairnMultiplayerMod/Features/` | ✅ | What the mod does — one file per feature |
| `CairnMultiplayerMod/Api/` | ✅ | Public managed extension contracts; namespace `CairnMultiplayer.Api` |
| `CairnMultiplayerMod/Bootstrap/` | ✅ | Composition and startup wiring |
| `CairnMultiplayerMod/Framework/` | ✅ | What you write a feature with (channels, lifecycle) |
| `CairnMultiplayerMod/GameApi/` | ✅ | Safe, documented façade available to features |
| `CairnMultiplayerMod/Internal/` | ✅ | Cairn, Unity, IL2CPP, Steam, Harmony and UI implementations |
| `CairnMultiplayerMod.Generators/` | ✅ | Build-time generator listing the features (never ships) |
| `CairnMultiplayerShared/` | ✅ | Shared network protocol (packets, constants) |
| `CairnMultiplayer.Tests/` | ✅ | Portable protocol, architecture, generator, authority and diagnostics tests; no game DLLs required |
| `CairnMultiplayerMod.Tests/` | ✅ | Framework, gameplay, panel and extension tests requiring game references |
| `docs/` | — | Published architecture and API documentation; `docs/local/` is ignored |
| `examples/` | — | Managed extension source example |
| `scripts/` | — | Local checks, reference generation, version synchronization and packaging |
| `game-refs/` | — | Il2Cpp + MelonLoader reference assemblies (**not committed** — provide your own, see below) |

The dependency rules and inbound packet flow are documented in
[`docs/architecture.md`](docs/architecture.md). Start with [CONTRIBUTING.md](CONTRIBUTING.md)
for setup, verification and contribution conventions.

## JetBrains Rider

Open `CairnMultiplayer.sln` from the repository root in JetBrains Rider. It loads
the mod, shared protocol, source generator and both test projects together, and
exposes the main documentation files under the `Documentation` solution folder.
Use Rider rather than IntelliJ IDEA: Rider is JetBrains' C#/.NET IDE and provides
the syntax highlighting, code completion, navigation, refactoring and test runner
needed by this project.

If game types appear unresolved in the editor, generate the local reference
assemblies first with `pwsh scripts/generate-il2cpp-refs.ps1`. These assemblies
remain local and must not be committed.

### Run and debug the installed game

Select **CairnMP** in Rider's run configuration selector. The shared profiles in
`.run/` build the mod in **Debug**, deploy its DLLs and matching PDBs to the local
development installation, then launch `Cairn.exe` with `--cairnloader.debug`.

- **Run** (`Shift+F10`, default Rider keymap): launch the game.
- **Debug** (`Shift+F9`): launch with the **CoreCLR** debugger for the managed mod.
- **CairnMP - Build Debug**: only compile and deploy, without launching the game.

The game uses IL2CPP, but CairnLoader hosts the mod in **.NET 6**. Keep the profile
runtime set to **.NET / .NET Core** (`RUNTIME_TYPE=coreclr`), not Auto, .NET Framework
or Unity/Mono. The default runtime can start the game without binding C# breakpoints.
CoreCLR debugging was verified at `Mod.OnInitializeMelon`, including local values.
This does not restore the original C# sources of the IL2CPP game itself.

Close the running game before rebuilding or switching between Run and Debug, and
open Steam for multiplayer. The profiles assume the standard CairnMP installation
under the current user's `AppData/Local/CairnMultiplayerData/game`, and the .NET SDK
at `C:/Program Files/dotnet/dotnet.exe`. For a custom installation, update both the
game profile's executable/working directory and the build profile's `CairnDir`
MSBuild property so deployment and launch target the same directory.

## Reference assemblies

The build references proprietary Cairn / Unity / MelonLoader assemblies that
**cannot be redistributed**, so `game-refs/` is git-ignored. Provide them locally
in one of two ways (resolved by `Directory.Build.props`):

- drop the DLLs into `game-refs/Il2CppAssemblies/` and `game-refs/MelonLoader/`, or
- point `MELON_LOADER_NET6_DIR` at your MelonLoader `net6` folder; the Il2Cpp
  assemblies are otherwise read from your local Cairn install
  (`%LOCALAPPDATA%\CairnMultiplayerData\game\CairnLoader\Il2CppAssemblies`).

## Build & test

From a fresh clone without Cairn installed, run the portable suite:

```bash
dotnet restore CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj --locked-mode
dotnet test CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj -c Release --no-restore
```

With the game references available, build and test the complete solution:

```bash
dotnet restore CairnMultiplayer.sln --locked-mode
dotnet build CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
dotnet test CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
```

Requires SDK 10.0.400 (global.json), the reference assemblies described above, and the .NET 6 runtime for the native mod tests. Portable tests run on .NET 10. Normal builds never deploy; use -p:DeployMod=true explicitly for a local development install (dev channel).

GitHub Actions always runs the portable shared-protocol tests, the source-level
architecture tests, generator regression tests and source-linked authority/diagnostic tests with locked NuGet dependencies. The full mod suite remains a local
verification because Cairn's proprietary reference assemblies cannot be redistributed.

A change of lobby owner ends the session; reconnect through a new lobby. All players must use protocol 13.

## Local crash reports

CairnMP never uploads diagnostic data. Recoverable problems are deduplicated into local
session logs. If a fatal mod error occurs, multiplayer and all installed patches stop,
the available logs are compressed in
`UserData/CairnMultiplayer/Crashes/CairnMP-crash-*.zip`, and Cairn shows the exact path
before closing cleanly on request or after 30 seconds. At most 10 session logs (20 MiB)
and 5 crash archives (100 MiB) are retained; oversized source logs are tailed inside the
archive so a runaway log cannot make crash handling unbounded.

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

`protocol` is bumped when packet layouts, feature contract identifiers or their meanings change incompatibly — the
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
