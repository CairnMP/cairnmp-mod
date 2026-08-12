# CairnMP

A multiplayer mod for the climbing game **Cairn**. It runs inside the game via
MelonLoader (IL2CPP) and connects players over a host-authoritative **Steam relay**
(P2P) — no dedicated server required.

Features include roped climbing (belay), in-game chat and host commands, world
pings, FreeRoam, and full synchronization of players, cosmetics, weather, lamps,
pitons and time of day.

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
| `CairnMultiplayerShared/` | ✅ | Shared network protocol (packets, constants) |
| `CairnMultiplayerShared.Tests/` | ✅ | xUnit tests for the shared protocol |
| `CairnMultiplayerMod.Tests/` | ✅ | xUnit tests for the mod (needs the reference assemblies) |
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

Requires the reference assemblies described above to be present locally.

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
