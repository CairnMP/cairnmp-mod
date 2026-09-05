# Changelog

All notable changes to CairnMP are documented here.
This project adheres to [Semantic Versioning](https://semver.org/).

## Unreleased — audit corrections, 2026-09-05

- Protocol 13: feature-scoped contract identifiers and explicit bivouac presence. Older clients are incompatible.
- Fix distant rope release, maintain rope safety during chat, and include awake campers in sleep consensus.
- Deliver asynchronous feature callbacks on the game thread; release subscriptions, pending starts and clock state on shutdown.
- Correct panel close state and clean up the primary UI before fallback.
- End sessions on host ownership changes; refresh hand poses for late joiners.
- Validate rope endpoints, bound piton creation and rope churn, attribute chat to authenticated players, and prevent repeated admission snapshots.
- Bound session logs, deduplication, recurring feature errors and archive copies; preserve native save failures instead of suppressing them.
- Pin SDK/C# and NuGet dependencies, add portable security/generator regressions, disable implicit deployment, and test release packaging with an explicit DLL list.
- See `docs/corrections-audit-2026-09-05.md` for evidence and required native validation.

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
