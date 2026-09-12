<div align="center">

# CairnMP

**Host-authoritative multiplayer for the climbing game _Cairn_.**

[![CI](https://github.com/CairnMP/cairnmp-mod/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/CairnMP/cairnmp-mod/actions/workflows/ci.yml)
[![CodeQL](https://github.com/CairnMP/cairnmp-mod/actions/workflows/codeql.yml/badge.svg?branch=develop)](https://github.com/CairnMP/cairnmp-mod/actions/workflows/codeql.yml)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](LICENSE)

[Features](#features) · [Player guide](#player-guide) · [Build](#build-and-test) ·
[Contribute](CONTRIBUTING.md) · [Security](SECURITY.md) · [Changelog](CHANGELOG.md)

</div>

---

CairnMP runs inside Cairn through MelonLoader (IL2CPP) and connects players over
a Steam relay (P2P). The lobby owner is authoritative, so no dedicated server is
required.

Public lobby hosts also publish a small presence record to the CairnMP platform.
The mod refreshes it every 30 seconds and removes it when the host leaves; the
platform expires missing heartbeats after 90 seconds. This powers community
websites without moving matchmaking or gameplay traffic away from Steam.

No shared API key is embedded in the mod. Each lobby receives a short-lived,
random registration token, while community integrations use separate read-only
keys created in the CairnMP developer console. Developers can override the API
base URL for local testing with `CAIRNMP_API_URL`.

> [!IMPORTANT]
> Every player in a lobby must use a compatible CairnMP build. The current wire
> protocol is **13**; lobby admission may also require the exact mod version.

## Features

| Area | Included functionality |
| --- | --- |
| Climbing | Roped climbing and belay synchronization |
| Players | Position, animation, cosmetics, lamps, hand and finger poses |
| World | Weather, time of day, pitons, shared ground items, and world pings |
| Communication | In-game chat, completion, host commands, and experimental proximity voice |
| Sessions | Steam relay hosting, late-join state, and host-authoritative validation |
| Modes | Playable FreeRoam and synchronized bivouac behavior |
| Reliability | Local crash reports, bounded diagnostics, and isolated feature failures |

## Player guide

### Chat

| Key | Action |
| --- | --- |
| `Enter` | Open chat and send a message |
| `↑` / `↓` | Browse previously sent messages |
| `Tab` | Complete a command or player argument |
| `Shift` + `Tab` | Cycle completion backward |
| `Escape` | Close chat |
| `F10` | Force-close chat if needed |

Your climber remains stationary while you type. The suggestion bar lists matching
commands and players, including nicknames with spaces, and displays command usage.
Run `/help` for the complete command list.

Feature-provided commands participate automatically when their usage string marks
an argument as `<player>`.

### Share items

Select an ordinary consumable in Cairn’s backpack. The native action bar adds:

- **`G` — Give nearest:** transfer one unit to the nearest eligible player;
- **`X` — Drop:** place one unit into the shared world.

| Rule | Limit |
| --- | ---: |
| Give distance | 3.5 m |
| Pickup distance | 1.6 m |
| Shared ground items | 32 per session |
| Quantity moved per action | 1 |

The give action is dimmed when nobody is eligible. Both players must be active in
the same area. The prompts are disabled while an item is being dragged or the bag
is busy.

Dropped items are owned by the host, synchronized to every player, and picked up
with **`E`**. If the recipient’s backpack is full, the item returns to the ground
instead of being destroyed or duplicated.

> [!NOTE]
> Quest items, containers, charms, unique objects, and equipment with individual
> state cannot be shared. Items can only leave the selected backpack stack; there
> is no chat command for giving or dropping.

### Proximity voice

Experimental proximity voice is available under **Settings → CairnMP**. It
includes voice activation, push-to-talk, microphone selection, individual player
mutes, volume control, and a local microphone test.

> [!WARNING]
> Voice currently supports Windows through WASAPI. Two-player quality validation
> is still in progress. See the [complete voice guide](docs/proximity-voice.md).

## Documentation

| Guide | Audience | Purpose |
| --- | --- | --- |
| [Contributing](CONTRIBUTING.md) | Contributors | Issues, setup, checks, and pull requests |
| [Architecture](docs/architecture.md) | Developers | Layer boundaries, packet flow, and conventions |
| [Managed extension API](docs/multiplayer-api.md) | Mod authors | Host-authoritative integration API |
| [Proximity voice](docs/proximity-voice.md) | Players and testers | Settings, implementation, and validation |
| [Security policy](SECURITY.md) | Everyone | Private reporting and security scope |
| [Changelog](CHANGELOG.md) | Everyone | Version history and compatibility notes |

## Repository layout

| Path | Role |
| --- | --- |
| `CairnMultiplayerMod/Features/` | One-file multiplayer feature declarations |
| `CairnMultiplayerMod/Api/` | Public `CairnMultiplayer.Api` contracts |
| `CairnMultiplayerMod/Bootstrap/` | Startup and composition root |
| `CairnMultiplayerMod/Framework/` | Feature lifecycle and typed channels |
| `CairnMultiplayerMod/GameApi/` | Safe façade for game operations |
| `CairnMultiplayerMod/Internal/` | Cairn, Unity, IL2CPP, Steam, Harmony, and UI implementations |
| `CairnMultiplayerMod.Generators/` | Build-time feature discovery generator |
| `CairnMultiplayerShared/` | Shared packets, constants, and validation |
| `CairnMultiplayer.Tests/` | Portable protocol, architecture, generator, and authority tests |
| `CairnMultiplayerMod.Tests/` | Tests that require game references |
| `examples/` | Managed extension source example |
| `scripts/` | Checks, reference generation, version sync, and packaging |
| `game-refs/` | Local proprietary references; ignored by Git |

See the [architecture guide](docs/architecture.md) for dependency rules and the
inbound packet flow.

## Development

### Requirements

- .NET SDK **10.0.400**, pinned by `global.json`;
- .NET 6 runtime for native mod tests;
- JetBrains Rider for the shared run/debug profiles;
- local Cairn, Unity, and MelonLoader references for the complete mod build.

### Open the solution

Open `CairnMultiplayer.sln` from the repository root in **JetBrains Rider**. It
loads the mod, shared protocol, source generator, and both test projects. The main
Markdown guides appear in the `Documentation` solution folder.

If game types are unresolved, generate local IL2CPP references:

```powershell
pwsh scripts/generate-il2cpp-refs.ps1
```

Generated references remain local and must never be committed.

### Run or debug CairnMP

Select **CairnMP** in Rider’s run-configuration menu. The shared profiles build in
Debug, deploy the DLLs and PDBs, then start `Cairn.exe` with
`--cairnloader.debug`.

| Profile or action | Result |
| --- | --- |
| **Run** (`Shift+F10`) | Build, deploy, and launch Cairn |
| **Debug** (`Shift+F9`) | Launch with the CoreCLR debugger |
| **CairnMP - Build Debug** | Build and deploy without launching |

Although Cairn uses IL2CPP, CairnLoader hosts the mod in **.NET 6**. Keep Rider’s
runtime set to **.NET / .NET Core** (`RUNTIME_TYPE=coreclr`), not Auto, .NET
Framework, or Unity/Mono. CoreCLR debugging has been verified at
`Mod.OnInitializeMelon`, including local values; it does not restore the original
C# sources of the IL2CPP game.

Close Cairn before rebuilding or switching between Run and Debug, and keep Steam
open for multiplayer. The shared profiles assume the standard install path under
`%LOCALAPPDATA%\CairnMultiplayerData\game` and the SDK at
`C:\Program Files\dotnet\dotnet.exe`. For a custom installation, update both the
game profile and the `CairnDir` MSBuild property so deployment and launch use the
same location.

## Reference assemblies

The build depends on proprietary Cairn, Unity, and MelonLoader assemblies that
cannot be redistributed. `game-refs/` is therefore ignored by Git.

Choose one setup:

1. place the assemblies under `game-refs/Il2CppAssemblies/` and
   `game-refs/MelonLoader/`; or
2. point `MELON_LOADER_NET6_DIR` at the MelonLoader `net6` directory and let
   `Directory.Build.props` read IL2CPP assemblies from the local Cairn install.

The default IL2CPP location is:

```text
%LOCALAPPDATA%\CairnMultiplayerData\game\CairnLoader\Il2CppAssemblies
```

## Build and test

### Portable suite

From a fresh clone without Cairn installed:

```bash
dotnet restore CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj --locked-mode
dotnet test CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj -c Release --no-restore
```

### Complete solution

With game references available:

```bash
dotnet restore CairnMultiplayer.sln --locked-mode
dotnet build CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
dotnet test CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
```

Normal builds never deploy. Pass `-p:DeployMod=true` explicitly only for a local
development installation.

GitHub Actions runs the portable protocol, architecture, generator, authority,
and diagnostic checks with locked dependencies. The full mod suite stays local
because the proprietary reference assemblies cannot be redistributed.

## Add a feature

A multiplayer feature is a single file. It declares its network messages,
per-frame work, and cleanup. A source generator discovers it automatically—there
is no packet-ID registry or central wiring to edit.

<details>
<summary><strong>View a complete <code>WaveFeature</code> example</strong></summary>

Save this as `CairnMultiplayerMod/Features/WaveFeature.cs`. Pressing **H** displays
`<name> waves!` for three seconds on every other player’s screen.

```csharp
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

internal sealed class WaveSent : IPacket
{
    public string FromName = "";

    public void Serialize(BinaryWriter writer) =>
        PacketCodec.WriteString(writer, FromName);

    public void Deserialize(BinaryReader reader) =>
        FromName = PacketCodec.ReadString(reader);
}

internal sealed class WaveFeature : MultiplayerFeature
{
    public override string Id => "wave";

    private const float BannerSeconds = 3f;
    private Broadcast<WaveSent> _wave;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _wave = feature.Broadcast<WaveSent>("sent", ShowWave);
        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.OnSessionEnded(() => Game.Hud.HideMessage("wave"));
    }

    private void TickInput()
    {
        if (KeyboardCaptured || !Game.Input.WasKeyPressed(GameKey.H))
            return;

        var message = new WaveSent { FromName = LocalPlayerName };
        ShowWave(LocalPlayerId, message);
        _wave.Send(message);
    }

    private void ShowWave(int fromPlayerId, WaveSent wave)
    {
        Game.Hud.ShowMessage("wave", $"{wave.FromName} waves!", BannerSeconds);
    }
}
```

</details>

The generated list is available under `obj/generated/` after a build.

### Choose a channel

| Channel | Use it for | Example |
| --- | --- | --- |
| `Broadcast<T>` | A transient message any player may send | Chat or pings |
| `HostState<T>` | Host-owned state replayed to late joiners | Weather or time |
| `HostCommand<T>` | A client request the host validates | Sleep or appearance changes |
| `HostEvent<T>` | A transient result emitted only by the host | Item delivery results |
| `PerPlayerState<T>` | Host-owned state keyed by player | Appearance |
| `Stream<T>` | Rate-limited transient updates | Finger poses |

Use `HostState<T>` whenever a late joiner must learn a value that is still true.
A broadcast disappears once sent.

### Choose a phase

| Phase | Runs | Appropriate work |
| --- | --- | --- |
| `FeaturePhase.Always` | Menus, bivouacs, and gameplay | Input and HUD |
| `FeaturePhase.Gameplay` | Active gameplay synchronization only | World interaction |

Features use `GameApi` for Cairn operations and `FeatureBuilder` for networking.
Unity, IL2CPP, Steamworks, MelonLoader, and Harmony remain under `Internal`.
Diagnostic `CMP003` rejects feature code that crosses this boundary.

Feature exceptions are logged and isolated so one feature cannot stop the update
loop or unrelated features. For a real implementation, read
[`PingFeature.cs`](CairnMultiplayerMod/Features/PingFeature.cs).

## Managed extension API

Other MelonLoader mods can integrate through the host-authoritative
`CairnMultiplayer.Api` surface. It provides compatibility negotiation, typed
commands, transactions, late-join state, and transient events without exposing
Steam or raw packets.

Read the [managed extension API guide](docs/multiplayer-api.md) and the
[complete source example](examples/ManagedExtensionExample/).

## Diagnostics and privacy

CairnMP never uploads diagnostics. Recoverable problems are deduplicated into
local session logs.

If a fatal mod error occurs, CairnMP stops multiplayer and installed patches,
then creates:

```text
UserData/CairnMultiplayer/Crashes/CairnMP-crash-*.zip
```

Cairn displays the path before closing on request or after 30 seconds. Storage is
bounded to 10 session logs (20 MiB) and 5 crash archives (100 MiB). Oversized
source logs are tailed inside the archive.

## Package a release

```powershell
pwsh scripts/package-mod.ps1
```

Output:

```text
../CairnMP-packages/cairnmp-mod-{version}.zip
../CairnMP-packages/cairnmp-mod-{version}-debug.zip
```

Building `CairnMultiplayer.sln` also refreshes this archive automatically. The
package directory is outside the repository, so generated ZIP files never enter
Git and do not require a `.gitignore` rule. Release and Debug builds use separate
filenames. Set `PackageOnSolutionBuild=false` to disable this hook for a particular
build.

## Versioning

`versions.json` is the source of truth for the mod and protocol versions. After
editing it, synchronize generated declarations:

```bash
node scripts/sync-versions.js
```

Bump `protocol` whenever packet layouts, feature contract IDs, or their meanings
change incompatibly. The handshake rejects mismatched protocol versions. A change
of lobby owner ends the session; reconnect through a new lobby.

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md) for issue quality, environment setup,
verification, and pull-request conventions. Participation is governed by the
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

Copyright © 2026 Yutho

CairnMP is free software licensed under the
[GNU Affero General Public License v3.0](LICENSE). Derivative works that reuse
this code must follow the AGPL-3.0 terms, including the network-use source offer.

Proprietary Cairn, Unity, and MelonLoader reference assemblies are not part of
this project and are not covered by its license.
