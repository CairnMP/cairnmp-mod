# Changelog

All notable changes to CairnMP are documented here.
This project adheres to [Semantic Versioning](https://semver.org/).

## [2.2.0] — 2026-09-07 (beta)

> This beta uses protocol 13. Every player in a lobby must run CairnMP
> 2.2.0; other mod versions cannot join the same lobby.

### Added

- Chat completion: **Tab** completes the command being typed, then the player names of
  the arguments it declares (**Shift+Tab** cycles backwards). A suggestion bar shows
  the available candidates and the usage of the command in progress.
- Native backpack actions for giving one unit of the selected consumable to the nearest
  eligible player with **G**, or dropping it into the shared world with **X**.
- Host-authoritative ground items that are synchronized for late joiners, limited to the
  local scene and pickup range, and restored when the recipient's backpack is full.
- A native-style **E** interaction carousel when several shared items occupy the same
  place, using the inventory artwork for each item.

### Changed

- Chat rows and the input field now reserve enough vertical space for font descenders
  and shadows, improving readability at every supported UI scale.
- Item sharing remains deliberately conservative: quest items, equipment, containers,
  charms and unique stateful objects cannot be transferred, and only 32 ground items
  may exist in a session.

### Fixed

- Fixed dropped items being invisible or impossible to recover after leaving the
  backpack.
- Fixed missing ground-item icons, oversized world visuals and square interaction-key
  prompts that did not match Cairn's interface.
- Fixed a native crash when a second dropped item activated the interaction carousel.
- Fixed the lower edge of chat letters and their shadow being clipped.

### Beta notes

- Item sharing needs broader two-player and late-join testing across streamed areas.
- All players must use this exact beta because lobby admission checks the full mod
  version even though the network protocol remains 13.

## [2.1.0] — 2026-09-06 (beta)

> This beta uses protocol 13. Every player in a lobby must run CairnMP 2.1.0;
> clients from the 1.x releases are not compatible.

### Added

- Experimental proximity voice chat with voice activation enabled by default,
  push-to-talk, microphone mute, distance attenuation and stereo positioning.
- A dedicated **CairnMP** page in the game's settings for choosing the input
  device, adjusting voice sensitivity and volume, changing the push-to-talk key,
  testing the microphone locally and muting individual players.
- A feature framework with scoped network contracts and a real-time stream channel,
  making multiplayer features easier to isolate and extend.
- Shared Rider profiles for building, deploying, running and debugging the installed
  game in one action.

### Changed

- Chat, pings, weather and time, sleeping, player appearance and hand poses now use
  the new feature framework.
- The multiplayer menu has been polished to better match Cairn's native navigation,
  animations and visual language.
- Voice device discovery now runs outside the game loop to avoid frame hitches while
  playing or opening the settings page.
- SDK, C# and NuGet dependencies are pinned, deployment is explicit, and release
  packaging is covered by automated checks.

### Fixed

- Fixed releasing distant ropes, losing rope safety while chatting, and omitting
  awake campers from the sleep consensus.
- Fixed sessions remaining active after host ownership changes and hand poses not
  refreshing for players who join late.
- Fixed invalid rope endpoints, unbounded piton or rope churn, unauthenticated chat
  attribution and repeated admission snapshots.
- Fixed asynchronous feature callbacks reaching game objects from the wrong thread,
  plus subscriptions and pending state surviving shutdown.
- Fixed multiplayer panels retaining an incorrect close state or leaving remnants
  of the primary UI after switching to the fallback interface.
- Bounded session logs, duplicate tracking, recurring feature errors and archive
  copies, while preserving native save failures instead of silently suppressing them.

### Beta limitations

- Proximity voice chat currently supports Windows through WASAPI. Linux/Proton and
  macOS are not supported by this beta.
- Voice chat does not yet include echo cancellation, noise suppression or wall
  occlusion. Headphones are recommended when using voice activation.
- The settings interface and local microphone path have been validated in game;
  two-player voice quality and the complete multiplayer path still require broader
  community testing.
- See `docs/corrections-audit-2026-09-05.md` and `docs/proximity-voice.md` for the
  detailed validation notes.

## [1.1.0] — 2026-08-02

Network protocol unchanged (still version 7), so `1.1.0` and `1.0.0` clients
can still play together.

### Added
- **Extension API** — other mods can now register their own state, events and
  commands and have them synchronized across a session, without touching
  CairnMP's own networking. Handlers run in a transaction: a rejected or
  throwing command rolls back every managed effect, and a misbehaving
  extension is isolated behind its own circuit breaker instead of taking the
  session down. See `docs/multiplayer-api.md`.
- **`VerboseLogging` preference** — the verbose log switch is now a proper
  MelonPreferences entry, so it can be toggled from the config file instead of
  requiring a rebuild. Thanks [@nullbrik](https://github.com/nullbrik).

### Fixed
- The **Multiplayer button** no longer shows "Story" while opening the
  multiplayer panel.
- **Roping** — a partner unclipping now cleans up the native belay properly,
  and remote piton references are cleared on reset instead of pointing at
  stale objects.
- Extension sessions are finalized when a session ends, and Steam diagnostics
  no longer report a stale state.

### Changed
- **Crash diagnostics are now local-only.** CairnMP no longer uploads errors or
  creates a machine identifier. A fatal mod error stops multiplayer safely,
  bundles the available CairnMP, MelonLoader and Unity logs under
  `UserData/CairnMultiplayer/Crashes`, displays that path, then closes Cairn
  cleanly. The player alone decides whether to share the ZIP. Session logs and
  archives now have count/size retention limits, and oversized logs are tailed.
- Game-facing features now use the safe `GameApi` façade; Unity, IL2CPP, Steam
  and Harmony implementations live under `Internal`, with source-level boundary
  tests enforcing the dependency direction.
- Runtime services no longer reach through `Mod.Instance`: state, networking and
  Steam dependencies are supplied explicitly by the bootstrap composition root.
- Harmony patches, exception hooks, Steam callbacks and pending operations now
  have symmetric shutdown paths. Every Melon callback is protected by the fatal
  error boundary.
- Portable protocol and architecture checks now run in GitHub Actions without
  requiring redistributable game assemblies.

## [1.0.0] — 2026-07-11

First stable release. Consolidates the entire `0.1.20 → 0.1.37` beta line into a
single supported version. No protocol change from `0.1.37` (network protocol
stays at version 6).

### Added
- **Rope up with a partner (belay)** — press **E** near a climber to clip a rope
  between you; on a wall you're truly belayed (fall → hang, rappel with **S**,
  climb back with **W**) instead of dying.
- **In-game chat** — press **Enter** to talk to the session; browse history with
  **↑ / ↓**. Your climber stays put while typing.
- **Host commands** — `/tp <player>`, `/bring <player>`, `/help`.
- **Ping system** — drop a colored, everyone-visible marker from the Display Route
  free camera (distance, off-screen arrow, 15s fade).
- **Playable FreeRoam mode** — unlocked and launchable from the menu; chosen
  difficulty respected.
- **Hide player names (photo mode)** — press **N** to toggle floating names.
- **Native-style multiplayer panel** with automatic fallback to the old UI.
- **Save options from the lobby** — opens Cairn's normal save menu when hosting.

### Synchronized
- Weather, time of day (host-authoritative), player lamps, pitons (with
  host-assigned authoritative IDs), hand/finger poses, and cosmetics (outfits,
  hoods, glow sticks, glowing gloves).
- Smoother remote avatars via native interpolation and official-palette colors.
- Joining mid-session now receives existing world state (pitons, lamps, weather).

### Fixed
- Multiplayer saving is fully reliable: correct slot when hosting, multiple saves
  per session, bivouac saves no longer silently break, and a bad piton can never
  abort the save.
- Bivouac desync (players invisible to each other) auto-detected and restored.
- Roped climbing fully synced (no double ropes / desync); ropes attach at the harness.
- Remote players no longer clip through walls or keep stale positions on transitions.
- Teleport safeguards (walking target only, zone preloaded, clear errors).
- "Skip Tutorial" bypasses the tutorial with no empty/frozen menu.
- Remote avatar crashes (`KeyNotFoundException`) and loading/transition instability.

## [0.1.37] — 2026-07-09 (beta)

### Fixed
- **Multiplayer saves no longer fail after clipping into another player's piton.**
  Clipping onto a remote player's piton could throw `NullReferenceException` in
  `Piton.WriteToSavegame` and abort the **entire** save (`Save FAILED`), losing
  progress — and it kept failing *even after recalling every piton*. Two layers now
  prevent this:
  - remote pitons are properly initialized on spawn (their climbing setting is filled
    in), so clipping into them no longer corrupts native state;
  - a save guard ensures that a single bad piton can never abort the whole save — the
    save skips it and completes. This covers the crash regardless of its root cause,
    including the "even after recall" case.
- **Pitons placed by different players at the same time no longer collide.** The host
  now assigns authoritative piton IDs, instead of each client numbering its own pitons
  from 1 (which made two players' pitons overwrite each other).

### Improved
- **Joining mid-session now syncs existing world state.** A player who joins an
  in-progress game now correctly receives already-placed pitons, other players' lamp
  states, and the current weather.

### Changed (internal)
- Host-side piton authority is now owned by a transport-agnostic core
  (`PitonAuthority`) instead of being handled inline in the Steam transport. No
  gameplay change; groundwork for future sync work.
- Removed the unused standalone LiteNetLib server — multiplayer is Steam relay only.

### Notes for testers
- Both players must run the **same** `CairnMultiplayerMod.dll` (0.1.37) for the save
  fix to protect both sides.
- If the save guard ever triggers, it now logs a visible warning
  (`[SaveGuard] …`) — previous diagnostics were hidden unless verbose logging was on.
- Tested against Cairn **1.36**.
