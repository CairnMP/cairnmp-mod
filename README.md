# CairnMP — `next/feature-framework` branch

> ### ⚠️ Untested branch — do not use this for your regular sessions
>
> **Nothing here has been played yet.** The code builds cleanly and the automated
> tests pass, but a green build proves very little for a mod like this one: the
> parts that matter run inside the game, against IL2CPP objects and a live Steam
> connection, and none of that is covered by a test suite.
>
> These changes are **hot off the keyboard**. They were written in one stretch and
> committed as they went, without a single session played between them. Expect
> rough edges. Expect things that worked on `develop` to be broken here.
>
> **What is most likely to misbehave:**
>
> - **Pings** — rebuilt on top of the new feature framework. They may not appear
>   for other players, appear twice, or not appear at all.
> - **Bivouacs** — the sync suspension was pulled out of the mod core into its own
>   class. The behaviour is meant to be identical, but this is the code path behind
>   the "one save then nothing" bug, so it deserves suspicion.
> - **Anything networked** — the protocol moved to version 8. This branch **cannot
>   play with a client running an older version**, in either direction.
> - **The multiplayer panel** — the old Canvas implementation was removed. Only the
>   in-game-styled panel remains, with the UI Toolkit fallback behind it.
>
> **If you try it anyway** — and you are very welcome to, that is how this gets
> solid — please play with two clients, keep your `MelonLoader/Latest.log`, and
> open an issue with what you did and what happened. A report saying "pings never
> showed up for the host" is worth more than a hundred green builds. Bug reports on
> this branch are genuinely useful; bug reports on `develop` are what keep the mod
> stable for everyone else.
>
> **Where this is going.** This is not a side experiment — it is the direction the
> mod is taking. Once it has been played, tested and fixed, it becomes the base
> everything else is built on. The point of it is simple: **make CairnMP a mod
> people can actually contribute to.** Adding a feature used to mean editing six
> files spread across the protocol, the transport and the mod core, and
> understanding all of them first. On this branch a feature is one file that
> declares what it needs. See [Contributing a feature](#contributing-a-feature).
>
> Stable code lives on [`develop`](../../tree/develop). Use that one to play.

---

A multiplayer mod for the climbing game **Cairn**. It runs inside the game via
MelonLoader (IL2CPP) and connects players over a host-authoritative **Steam relay**
(P2P) — no dedicated server required.

Features include roped climbing (belay), in-game chat and host commands, world
pings, FreeRoam, and full synchronization of players, cosmetics, weather, lamps,
pitons and time of day.

## Contributing a feature

*(New on this branch.)* A multiplayer feature is **one file**. It declares what it
sends over the network, what it does each frame, and what it cleans up — and
nothing else in the codebase has to be touched. No packet ids, no serialization
plumbing, no wiring in the mod core.

```csharp
internal sealed class PingFeature : MultiplayerFeature
{
    public override string Id => "ping";

    private Broadcast<PingPlaced> _placed;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _placed = feature.Broadcast<PingPlaced>("placed", ShowRemotePing);
        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.OnDrawHud(PingMarkerManager.OnGUI);
        feature.OnSessionEnded(PingMarkerManager.ClearAll);
    }

    private static void ShowRemotePing(int fromPlayerId, PingPlaced ping)
        => PingMarkerManager.Spawn(fromPlayerId, ping.Position);
}
```

Drop the file in `Features/`, build, done — a source generator finds it and
registers it for you.

**Pick your channel by what the feature needs:**

| Channel | Meaning | Used by |
|---|---|---|
| `Broadcast<T>` | everyone sees it, any player can send | pings, chat |
| `HostState<T>` | the host owns it, players joining later catch up | weather, time, pitons |
| `HostCommand<T>` | the client asks, the host accepts or refuses | roping up |

**And your phase:** `FeaturePhase.Always` runs every frame including in menus and
bivouacs (input, HUD); `FeaturePhase.Gameplay` runs only while gameplay sync is
active (anything touching the world).

`Features/` is what the mod does; `Framework/` is what you write it with. A feature
that throws is logged and isolated — it cannot take the others down with it.

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
