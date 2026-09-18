# Changelog

All notable CairnMP changes are documented here. Releases follow
[Semantic Versioning](https://semver.org/) and use the categories **Added**,
**Changed**, **Fixed**, and **Removed** where applicable.

> [!IMPORTANT]
> Beta releases may require every lobby member to run the exact same mod version,
> even when the wire-protocol number is unchanged. Read the compatibility note
> for the release you install.

## Releases

| Version | Date | Channel | Highlights |
| --- | --- | --- | --- |
| [2.3.0](#230--2026-09-18-beta) | 2026-09-18 | Beta | Game modes, downed climbers and a rebuilt panel |
| [2.2.17](#2217--2026-09-18-stable) | 2026-09-18 | Stable | Streaming crash mitigation and realistic voice occlusion |
| [2.2.16](#2216--2026-09-17-stable) | 2026-09-17 | Stable | Controller support and conflict-free player ropes |
| [2.2.15](#2215--2026-09-17-beta) | 2026-09-17 | Beta | Stable harness ropes and cross-platform spatial voice |
| [2.2.14](#2214--2026-09-16-beta) | 2026-09-16 | Beta | Free Roam button initialization fix |
| [2.2.13](#2213--2026-09-16-beta) | 2026-09-16 | Beta | Free Roam multiplayer launch fix |
| [2.2.12](#2212--2026-09-16-beta) | 2026-09-16 | Beta | RE-verified native integration |
| [2.2.11](#2211--2026-09-16-beta) | 2026-09-16 | Beta | Native cooperative-rope lifecycle fixes |
| [2.2.10](#2210--2026-09-15-beta) | 2026-09-15 | Beta | Profiler-guided performance and rope fixes |
| [2.2.9](#229--2026-09-13-beta) | 2026-09-13 | Beta | Smooth voice distance and direct harness ropes |
| [2.2.8](#228--2026-09-13-beta) | 2026-09-13 | Beta | Louder, clearer proximity voice |
| [2.2.7](#227--2026-09-13-beta) | 2026-09-13 | Beta | Steam lobby-result retrieval regression |
| [2.2.6](#226--2026-09-13-beta) | 2026-09-13 | Beta | Lobby browser, voice and rope fixes |
| [2.2.5](#225--2026-09-12-beta) | 2026-09-12 | Beta | Website lobby joins |
| [2.2.4](#224--2026-09-12-beta) | 2026-09-12 | Beta | Vanilla Story-mode isolation |
| [2.2.3](#223--2026-09-11-beta) | 2026-09-11 | Beta | Pause-menu audio fix |
| [2.2.2](#222--2026-09-11-beta) | 2026-09-11 | Beta | Proximity voice settings |
| [2.2.1](#221--2026-09-08-beta) | 2026-09-08 | Beta | Remote pose reliability |
| [2.2.0](#220--2026-09-07-beta) | 2026-09-07 | Beta | Item sharing and chat completion |
| [2.1.0](#210--2026-09-06-beta) | 2026-09-06 | Beta | Voice chat and feature framework |
| [1.1.0](#110--2026-08-02) | 2026-08-02 | Stable | Managed extension API and diagnostics |
| [1.0.0](#100--2026-07-11) | 2026-07-11 | Stable | First stable release |
| [0.1.37](#0137--2026-07-09-beta) | 2026-07-09 | Beta | Multiplayer save and piton fixes |

---

## [2.3.0] — 2026-09-18 (beta)

> This beta uses protocol 14. Every player in a lobby must run CairnMP 2.3.0;
> earlier versions cannot join, and this is the first protocol change since 2.2.0.

### Added

- Game modes. A lobby is now created under **Rope team**, **Free solo** or **Race**.
  The mode is advertised in the lobby browser, and it sets the difficulty and
  constraints every player launches with, read from Cairn's own difficulty table
  rather than from values of our own.
- Downed climbers. Dying with a teammate still standing no longer ends the run: the
  body stays on the face and a partner can put the climber back on their feet, at the
  cost of a healing item, through Cairn's own revive prompt on the ghost.
- Bivouac recall. Whoever is still standing can call the fallen back to camp with
  **R**, bringing them in on half health.
- Spectator seat. In a mode with no way back, a dead climber keeps watching: free
  flight, or locked onto a teammate with **F** and **C**. Spectators can still place
  pings to guide the living.
- Race standings. A live table ranking climbers by the height they have gained since
  the start, decided outright by the first to top out.
- Rope shake. When a roped partner comes off the wall, the climbers tied to them lose
  every hold they were merely holding; a firm hold rides it out.
- Dead weight. A fallen rope member costs endurance to carry, at the rate the game's
  own netplay tweakables set.
- Shared rations. Eating or drinking on the rope feeds the whole rope team, paid for
  by the climber who opened it.
- Trail marks. **B** leaves a permanent mark on the face for everyone, including late
  joiners; aiming at one of your own takes it back.
- Climb trails. **T** or `/trails` draws the route every climber actually took. It
  costs nothing on the wire: positions are already replicated.
- Ascent recap. `/recap` shows the rope team's session: height gained, falls, and how
  often each climber went down.

### Changed

- The multiplayer panel was rebuilt around one screen per intent — home, game mode,
  new climb, join, browse, lobby — with the mode restated wherever it still matters.
- The panel answers the pointer: cards slide and grow a golden edge under the cursor,
  titles light up, buttons press in. Controller focus gets the same feedback, and
  hovering moves that focus so the two never light up different elements.
- Controller navigation is now written explicitly per screen instead of relying on
  Unity's geometric guesswork, **Escape** steps back, and **Enter** submits the
  lobby code.
- The lobby browser states each lobby's game mode next to its host.

### Compatibility

- Version **2.3.0** raises the protocol from **13** to **14**: `ServerStartGame` now
  carries the lobby's mode and its extra constraints. All lobby members must use this
  exact version.
- The public managed-extension API is unchanged; this release is a minor version
  because it only adds to it.

### Beta notes

- Nothing here has been verified in a live two-client session yet. The mechanics most
  likely to need tuning are the rope shake, which may prove too punishing, and dead
  weight, whose native rate may be harsh once several partners are down.
- Two native paths cannot be proven without playing: that the revive prompt appears on
  our ghosts, and that the imposed difficulty survives Cairn's own new-game flow.
  Both log what they do when `VerboseLogging` is on.

---

## [2.2.17] — 2026-09-18 (stable)

### Fixed

- Controller pings now use `View/Share + RB/R1` instead of bare `RB/R1`, so
  Free Roam's native **Pick a destination** action remains exclusive to `RB/R1`.
- Voice playback now detects terrain between players. Rock walls progressively
  reduce volume, muffle high frequencies, and add a subtle reflected tail.
- Voice and settings integrations no longer keep using scene-owned native Unity
  objects during loading or after disconnecting, reducing transition crashes.
- Additive world streaming no longer flips the multiplayer state between `InGame`
  and `Loading`; native synchronization pauses safely without hiding players.

### Compatibility

- Version **2.2.17** retains protocol **13**. All lobby members must use the same
  mod version.
- This is a stable hotfix for version 2.2.16.

---

## [2.2.16] — 2026-09-17 (stable)

### Added

- Added a controller layout for the multiplayer panel, cooperative ropes,
  shared items, player names, and voice push-to-talk. CairnMP shortcuts use
  View/Share as a modifier and the multiplayer panel now preserves controller
  navigation while native background menus stay blocked. Holding the modifier
  temporarily captures gameplay input so face-button shortcuts cannot also fire
  Cairn actions.

### Fixed

- Moved cooperative player-rope attach/detach from E to the dedicated L key, so
  native interactions such as placing or grabbing a piton cannot trigger both
  actions at once.
- Starting a multiplayer game now opens Cairn's native save selection first,
  allowing each player to resume an existing save or choose a new slot instead
  of being sent directly into new-game creation.

### Compatibility

- Version **2.2.16** retains protocol **13**. All lobby members must use the same
  mod version.
- This is the stable promotion of the 2.2 beta series, including multiplayer
  Free Roam, shared items, proximity voice, native cooperative ropes, bivouac
  synchronization, lobby browsing and the managed extension API improvements.

---

## [2.2.15] — 2026-09-17 (beta)

### Added

- Proximity voice now uses OpenAL capture and streaming output on native Linux
  and macOS, while retaining WASAPI on Windows and Proton. All platforms keep
  the same Opus stream, spatial processing, device selection and local test.
- Cairn's authored acoustic rooms now extend voice range from 40 to 70 metres
  when both players share a cave, room, gym or shelter. These zones add a short,
  bounded reflection, while a small interaural delay improves player direction.
- Architecture coverage prevents voice orchestration from depending directly on
  a platform-specific capture or output implementation.

### Changed

- Outdoor voice range is now 40 metres with a smoother distance curve. The
  settings description and voice guide document the platform and zone behavior.

### Fixed

- Direct player-to-player ropes now start from the live harness separation plus
  bounded slack instead of spawning fully paid out at their maximum length.
- Runtime-created rope renderers now receive Cairn's rope-part references and an
  immediate synchronization, preventing invalid segments from stretching toward
  the horizon while walking or climbing.
- Aava's harness outfit is shown while the cooperative rope is attached and is
  restored safely afterward without hiding a harness earned by placing a piton.

### Compatibility and verification

- Version **2.2.15** retains protocol **13**. All lobby members must use the same
  mod version.
- The Release suite passes **283/283** managed tests. Physical Linux and macOS
  microphone/output validation remains required; Linux native uses the system
  `libopenal.so.1`.

---

## [2.2.14] — 2026-09-16 (beta)

### Fixed

- Free Roam is now enabled before Cairn performs its one-time difficulty-button
  initialization. Multiplayer launches can therefore display and select the
  native Free Roam mode instead of showing only Explorer, Alpinist and Free Solo.
- The 2.2.13 difficulty-selection transition is retained; this update fixes the
  earlier initialization race that left Free Roam absent from that screen.

### Compatibility and verification

- Version **2.2.14** retains protocol **13**. All lobby members must use the same
  mod version.
- Runtime debugging confirmed that the initialization hook runs before the main
  menu scene activation signal, unhides one Free Roam mode out of four, and makes
  Cairn create the native Free Roam difficulty UI.

---

## [2.2.13] — 2026-09-16 (beta)

### Fixed

- Starting a multiplayer lobby now opens Cairn's native difficulty selection
  before save selection. Free Roam is therefore available again when creating a
  new multiplayer game.

### Compatibility and verification

- Version **2.2.13** retains protocol **13**. All lobby members must use the same
  mod version.
- The launch transition is protected by a regression test. Two-account in-game
  validation remains required before publishing the beta.

---

## [2.2.12] — 2026-09-16 (beta)

### Changed

- Game integration now uses the generated Cairn APIs confirmed by Cpp2IL for
  managers, local-player state, lamps, remote animation frames, pitons and the
  main-menu transition. Manual IL2CPP offsets, runtime overload discovery and
  broad object scans were removed from these paths.
- Remote pitons now use the native `AddPiton` overload that receives the local
  climbing controller, so Cairn assigns their climbing setting during creation.

### Fixed

- Opening Cairn's pause menu while the multiplayer chat is active now closes the
  chat and releases its input capture before the native pause context is pushed.
  This prevents lost game audio and restores bivouac and world interactions after
  leaving the menu.
- The multiplayer pause menu now keeps the local player in the network gameplay
  state. Cairn's world time and physics continue, and pose snapshots remain visible
  to other players instead of freezing or hiding the paused player remotely.
- Disconnecting now releases NetPlay-owned weather and wind through Cairn's
  native cleanup sequence, and day/night synchronization uses the native freeze
  lifecycle so visual setups refresh correctly.

### Compatibility and verification

- Version **2.2.12** retains protocol **13**. All lobby members must use the same
  mod version.
- The migrated native paths are protected by architecture tests and the full
  managed suite. Two-account in-game acceptance remains required for native
  physics, save, weather and animation validation.

---

## [2.2.11] — 2026-09-16 (beta)

### Fixed

- Cooperative ropes now initialize their native rope-part collection explicitly,
  preventing a newly created `LogicalRope` from failing before its first segment
  can be registered.
- Rope length changes now use Cairn's deferred `RequestSetLength` path, keeping
  Obi simulation updates on the native fixed-update lifecycle.
- While two players are attached, Cairn's lifeline selects the cooperative rope
  for fall-distance and suspension logic. Personal-rope piton operations still
  run against the personal rope, which is restored when the link ends.
### Compatibility and verification

- Version **2.2.11** retains protocol **13**. All lobby members must use the same
  mod version.
- The rope lifecycle was checked against Cpp2IL output for Cairn's shared-rope
  mode, then covered by the managed architecture tests. Two-account in-game
  acceptance remains required for final physics validation.

---

## [2.2.10] — 2026-09-15 (beta)

### Changed

- Inventory and photo-mode UI discovery now caches native interface objects instead
  of repeatedly scanning every loaded object while those interfaces are hidden. The
  caches refresh after scene resets and retain a bounded recovery search for UI that
  appears later, removing the two periodic scans identified by profiling.
- Per-frame item-sharing checks now read the local player id directly instead of
  building a full player record and querying the Steam persona name. This removes
  the 17 ms native call identified in a UI-freeze profile.

### Fixed

- Cooperative ropes no longer replace the local lifeline's personal rope. This
  keeps Cairn's off-belay and rappel interactions responsive and prevents stale
  extra rope registrations after a partner detaches or leaves.

### Compatibility and verification

- Version **2.2.10** retains protocol **13**. All lobby members must use the same
  mod version.
- Automated tests cover the cached UI discovery, direct local-player-id access,
  and cooperative-rope ownership and cleanup paths.

---

## [2.2.9] — 2026-09-13 (beta)

### Changed

- Remote voice volume and direction now transition smoothly on the audio thread.
  A gentle distance-dependent low-pass filter softens distant speech, and a
  one-second decoder grace period avoids repeated resets around 30 m.
- Cooperative ropes now use a dedicated native rope attached directly to both
  harnesses, with no synthetic piton. Personal piton operations use the personal
  rope, which is restored when the cooperative attachment ends.

### Fixed

- Piton discovery now compares native identities and drains batched additions
  and removals, excluding remote spawns even when allocation precedes an error.
- Direct rope initialization is bounded, partial attachments are cleaned up,
  and destroyed endpoints or teleports stop the native simulation safely.
- Inactive remote harness physics follows its animated skeleton attach marker
  before cooperative rope simulation.

### Compatibility and verification

- Version **2.2.9** retains protocol **13** and the `voice.opus-v2` stream.
  All lobby members must use the same mod version.
- Automated tests cover distance DSP, native-binding lifecycle and piton identity
  discovery. Native calls were checked against the installed game's metadata and
  disassembly. Two-account in-game listening, fall arrest, alignment and save/load
  verification remain pending; see [direct-rope verification](docs/direct-rope.md).

---

## [2.2.8] — 2026-09-13 (beta)

### Added

- Added optional microphone enhancement, enabled by default, with an 80 Hz
  high-pass filter, automatic gain, voice compression, and a -1 dBFS limiter.
- The local microphone test now reports raw and processed levels plus automatic
  gain, making threshold and input-quality setup easier.

### Changed

- Voice capture and Opus playback now use 48 kHz mono, 20 ms frames, 32 kbit/s
  variable bitrate, voice tuning, and codec complexity 8.
- Proximity volume stays full through 5 m, fades gently to 55% at 20 m and 40%
  at 25 m, then reaches silence at 30 m. Centered voices no longer lose 3 dB.
- Incoming voice volume can now be adjusted from 0% to 300%; existing 100%
  preferences retain the same neutral value.
- The negotiated voice stream is now `voice.opus-v2`. Final mixed voice output
  is limited to prevent clipping when several players speak simultaneously.

### Compatibility

- The wire protocol remains **13**, but every lobby member must use CairnMP
  2.2.8 because the negotiated voice stream and codec configuration changed.

### Verification

- All 241 automated tests pass. Coverage validates microphone processing, exact proximity points,
  stereo compensation, 48 kHz Opus frames, the 400-byte payload limit, and the
  final multi-speaker limiter. Two-player audible quality remains an in-game
  release check.

---

## [2.2.7] — 2026-09-13 (beta)

### Fixed

- Fixed the 2.2.6 lobby-search regression reporting "Steam could not retrieve the
  lobby search results." Searches now capture their result through manual dispatch
  before Steamworks.NET consumes it, then process the copied result during the
  mod update. No generic lobby-count delegate is used.
- Search failures now distinguish completion failures from result-read failures
  and include the affected API call identifier.

### Compatibility

- The wire protocol remains **13**. Every lobby member must use CairnMP 2.2.7.

### Verification

- All 226 automated tests pass, including single-consumption ordering and cancelled
  search coverage. A read-only probe against the installed Steam DLL successfully
  retrieved an empty lobby result through the new native call sequence. In-game
  verification of the dispatcher hook remains pending.

---

## [2.2.6] — 2026-09-13 (beta)

### Fixed

- Lobby searches now read the result of their specific Steam API call, reject
  invalid result counts, and update the browser on Unity's main thread.
- Proximity voice uses recent network positions when a remote harness is
  unavailable and preserves the newest captured audio after slow frames or
  large microphone batches. Settings refresh failures no longer interrupt capture.
- Shared ropes wait for native initialization before attaching, reject invalid
  or unreachable anchors, and release links after a refused attachment. Moving
  an anchor preserves the quickdraw geometry; unsafe player states prevent attachment.

### Compatibility

- The wire protocol remains **13**. Every lobby member must use CairnMP 2.2.6.

### Verification

- All 223 automated tests pass. Two-player in-game verification of the reported
  crashes and bidirectional voice remains pending.

---

## [2.2.5] — 2026-09-12 (beta)

### Added

- Community websites can now send players to a public CairnMP lobby through the
  launcher’s `cairnmp://join/<SteamLobbyID>` link. Steam carries the join request
  into Cairn, including when the game is not already running.

### Compatibility

- The wire protocol remains **13**. As with every beta release, lobby admission
  checks the full mod version, so all players in a lobby must use CairnMP 2.2.5.

---

## [2.2.4] — 2026-09-12 (beta)

### Changed

- Selecting the native **Story** mode now leaves any active lobby and disables
  CairnMP gameplay features until a new multiplayer handshake succeeds.
- Feature ticks, overlays, inventory integration, photo-mode additions and local
  multiplayer shortcuts stay dormant during vanilla Story play.
- The experimental FreeRoam unlock is now limited to multiplayer mode and its menu
  state is restored when returning to Story.

### Fixed

- Fixed local multiplayer behavior such as pings remaining available after entering
  a single-player Story game.

### Removed

- Removed the obsolete repository-security setup guide now that branch protection
  is managed and enforced directly through GitHub.

## [2.2.3] — 2026-09-11 (beta)

### Fixed

- Fixed opening the pause menu with Start or Escape muting all in-game audio until
  entering a bivouac during a multiplayer session.

## [2.2.2] — 2026-09-11 (beta)

### Added

- Added a settings system for proximity voice chat.

## [2.2.1] — 2026-09-08 (beta)

> This beta still uses protocol 13, but lobby admission checks the full mod
> version: every player must run CairnMP 2.2.1.

### Fixed

- Fixed remote climbers freezing in a T-pose, most often reported while two players
  climb at the same time. Pose frames captured during a mode transition — a secured
  fall, an abseil, moving between wall and ground — were discarded instead of sent,
  and the partner stopped being animated within half a second.
- Fixed a climber being replicated as walking with free hands whenever the native
  capture was briefly unavailable. The same wrong state also opened the teleport
  check, which is only supposed to accept a partner standing on the ground.
- Fixed pose frames being truncated to 128 bones while the receiving side expected
  every bone, a mismatch that left a remote player unanimated.
- Fixed a ghost staying frozen wherever its last accepted frame left it when a pose
  was refused; it now follows its owner's position, and the log tells the two causes
  apart.

### Changed

- Completed the Roslyn 5.9 dependency update, which had left the lock files behind:
  release checks could no longer restore and a third of the test suite did not build.

### Beta notes

- These pose fixes come from reading the game's own capture and replication code;
  they still need two-player validation while roped and climbing simultaneously.

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
- See the [proximity voice guide](docs/proximity-voice.md) for implementation and
  validation notes.

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
