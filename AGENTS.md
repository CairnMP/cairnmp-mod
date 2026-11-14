# AGENTS.md

Instructions for AI coding agents working in the CairnMP repository: Claude Code,
GitHub Copilot, Codex, Cursor, Gemini, and any similar tool. `CLAUDE.md` imports
this file, so the rules below are the single source of truth for every agent.

Human contributors can read it too. It condenses the parts of
[README.md](README.md), [CONTRIBUTING.md](CONTRIBUTING.md) and
[SECURITY.md](SECURITY.md) an agent needs, and adds the rules that only apply to
AI-assisted work. When this file and CONTRIBUTING.md or SECURITY.md disagree,
those two win; point out the conflict so this file can be fixed.

> [!IMPORTANT]
> **AI assists. It does not replace human judgment, expertise, or
> responsibility.** CairnMP runs inside other people's games, next to their
> story saves, with peers they do not control. A small, plausible-looking change
> can crash Cairn, corrupt a save, desynchronize a lobby, or open a hole to
> malicious peers. Treat every suggestion you make as a draft that a human must
> understand, test, and own before it is merged.

---

## Contents

1. [Ground rules for AI-assisted work](#1-ground-rules-for-ai-assisted-work)
2. [Project at a glance](#2-project-at-a-glance)
3. [Repository map](#3-repository-map)
4. [Architecture and layer rules](#4-architecture-and-layer-rules)
5. [Code patterns under `Internal/Game`](#5-code-patterns-under-internalgame)
6. [Adding or changing a feature](#6-adding-or-changing-a-feature)
7. [Networking, authority and untrusted input](#7-networking-authority-and-untrusted-input)
8. [Compatibility contracts](#8-compatibility-contracts)
9. [Working with Cairn, IL2CPP, Unity and MelonLoader](#9-working-with-cairn-il2cpp-unity-and-melonloader)
10. [Diagnostics, privacy and network egress](#10-diagnostics-privacy-and-network-egress)
11. [Build, test and verify](#11-build-test-and-verify)
12. [Code quality and style](#12-code-quality-and-style)
13. [Documentation and changelog](#13-documentation-and-changelog)
14. [Git, commits and pull requests](#14-git-commits-and-pull-requests)
15. [Dependencies, CI and supply chain](#15-dependencies-ci-and-supply-chain)
16. [Never do this](#16-never-do-this)
17. [Ask before doing this](#17-ask-before-doing-this)
18. [Definition of done](#18-definition-of-done)
19. [How to report back](#19-how-to-report-back)
20. [Maintaining this file](#20-maintaining-this-file)

---

## 1. Ground rules for AI-assisted work

1. **A human is accountable for every line.** The person who runs the agent
   submits the change and answers for it in review. The maintainer decides what
   is merged. Write code that person can explain line by line.
2. **Propose, don't decide.** Design, scope, compatibility breaks, new
   dependencies, and anything players will notice are human decisions. Lay out
   the options and the trade-offs, then let the human choose.
3. **Never claim what you did not verify.** "Builds", "tests pass" and "works"
   each need the command that proves it, run in this session, with its result.
   An agent cannot test in game: say what a human still has to check, and never
   tick a validation box that was not done.
4. **Read before you write.** Read the code you change, its callers, its tests,
   and the Harmony patches on the same game types. Cairn is closed source and
   reverse-engineered: much of this code records facts learned by playing, and
   its comments explain them. If you don't understand why code is the way it
   is, ask. Don't "simplify" it.
5. **Make the smallest correct change.** One problem per change. No drive-by
   refactors, renames, reformatting, or "while I was here" cleanups. Keep the
   diff easy to review: reviews are rigorous, and an unrelated line costs the
   reviewer time.
6. **Working is not enough.** Passing tests is where review starts. Code must
   also be correct, readable and balanced: no spaghetti, no copy-paste, no
   comment noise, no over-engineering. See
   [section 12](#12-code-quality-and-style).
7. **Stop and ask when unsure.** A question costs minutes. A wrong guess about
   rope physics, save gating, or packet authority can cost players their climb.
   [Section 17](#17-ask-before-doing-this) lists what always needs a question
   first.
8. **Be transparent.** Say when a pull request contains substantial
   AI-generated code or text, and which parts. Don't hide uncertainty: mark
   guesses as guesses, and unverified IL2CPP member names as unverified.
9. **No outward actions on your own.** Don't push, open or merge pull requests,
   comment on issues, create tags, publish releases, or post anywhere unless the
   human asks for that specific action in this session.

## 2. Project at a glance

CairnMP is a **host-authoritative multiplayer mod** for the climbing game
*Cairn* (The Game Bakers). It runs through **MelonLoader** on the game's
**IL2CPP** build and connects players over the **Steam relay** (P2P). The lobby
owner is authoritative; there is no dedicated server.

| Item | Value |
| --- | --- |
| Mod and shared protocol target | `net6.0` (MelonLoader hosts mods in .NET 6) |
| Language version | C# 12 (`Directory.Build.props`) |
| SDK | .NET 10.0.400, pinned by `global.json` |
| Source generator / analyzer | `netstandard2.0` (Roslyn requirement) |
| Portable tests | `net10.0`, xUnit |
| Mod tests | `net6.0`, xUnit, need the game references |
| Versions | `versions.json`: mod `2.6.0`, protocol `19` at the time of writing |
| Base branch | `develop` (CI also runs on `production`) |
| License | AGPL-3.0; the game, Unity and MelonLoader assemblies are proprietary |
| Owner and reviewer | `@yutho-o` (see `.github/CODEOWNERS`) |

The SDK is .NET 10, but the mod and `CairnMultiplayerShared` **run on .NET 6**.
Don't use APIs, language features or packages that need a newer runtime in
those projects.

## 3. Repository map

| Path | Role |
| --- | --- |
| `CairnMultiplayerMod/Features/` | One-file multiplayer features (`*Feature.cs` only) |
| `CairnMultiplayerMod/Framework/` | Feature lifecycle, `FeatureBuilder`, typed channels, `FeatureLog` |
| `CairnMultiplayerMod/GameApi/` | Safe interfaces (`I*Api`) and the façade features use |
| `CairnMultiplayerMod/Api/` | Public `CairnMultiplayer.Api` for third-party mods |
| `CairnMultiplayerMod/Bootstrap/` | `Mod` (MelonMod) partials: composition root and lifecycle |
| `CairnMultiplayerMod/Internal/Game/` | Cairn/Unity/IL2CPP integration, Harmony patches, adapters |
| `CairnMultiplayerMod/Internal/Networking/` | Steam lobby, transport, packet dispatch, host authority |
| `CairnMultiplayerMod/Internal/Diagnostics/` | Local logs, crash archives, performance capture |
| `CairnMultiplayerMod/Internal/Extensions/` | Runtime behind the public extension API |
| `CairnMultiplayerMod/Internal/UI/` | Multiplayer panel (UI Toolkit and sketch UI) |
| `CairnMultiplayerShared/` | Packets, `Protocol`, `PacketValidation`, codecs; no game references |
| `CairnMultiplayerMod.Generators/` | `FeatureRegistryGenerator` and the `CMP00x` analyzers |
| `CairnMultiplayer.Tests/` | Portable tests: protocol, architecture, generator, authority, diagnostics |
| `CairnMultiplayerMod.Tests/` | Tests that compile against the mod, so they need the game references |
| `examples/ManagedExtensionExample/` | Source example of the public API; not in the solution |
| `scripts/` | Checks, version sync, packaging, release tooling, two-client launchers |
| `.github/` | CI, CodeQL, release workflow, issue and PR templates, Dependabot |
| `.run/` | Shared Rider run configurations |
| `game-refs/` | Local proprietary references. **Ignored by Git. Never commit.** |
| `docs/` | **Ignored by Git.** The guides live in a separate docs repository. Scratch notes go in `docs/local/`. |

Links to `docs/*.md` in README.md and CONTRIBUTING.md do not resolve in a fresh
clone. Don't create files under `docs/` expecting them to be committed.

## 4. Architecture and layer rules

Dependencies point inwards: features know only the framework and the safe game
API; engine details stay under `Internal`; `Bootstrap` wires everything.

| Layer | May depend on | Must not depend on |
| --- | --- | --- |
| `Features/` | `Framework`, `GameApi`, `CairnMultiplayer.Shared` | `Internal`, `Bootstrap`, `CairnMultiplayer.Api`, `UnityEngine`, `Il2Cpp*`, `MelonLoader`, `HarmonyLib`, `Steamworks`, `TMPro` |
| `GameApi/` | BCL, `CairnMultiplayer.Shared` | Engine `using`s, `CairnMultiplayerMod.Internal`, `Mod.Instance`, `Mod.Log` |
| `Framework/` | `GameApi`, `CairnMultiplayer.Api`, `Internal.Extensions`, `Internal.Networking`, Shared | `Bootstrap`, `Mod.Instance`, `Mod.Log`, `Mod.LogDebug` |
| `Api/` (public) | `Internal.Extensions`, Shared | Any engine `using` |
| `Internal/Networking/` | Shared, `Internal.Diagnostics`, `Internal.Extensions`, `CairnMultiplayer.Api`, Steamworks and IL2CPP interop | `Internal.Game`, `Internal.UI`, `Framework`, `GameApi`, `using Unity*`, `Mod.` |
| `Internal/Diagnostics/` | Local files only | `System.Net.*`, `HttpClient`, `WebClient`, `WebRequest`, `Environment.MachineName`, `Environment.UserName` |
| `Internal/**` | Anything it needs | **No `public` types**: everything is `internal` |
| `Bootstrap/` | Everything | — (only the composition root may wire layers together) |
| `CairnMultiplayerShared/` | BCL only | Any game, Unity or loader reference |

Also enforced:

- `Internal/Networking/NetworkManager.PacketDispatch.cs` only routes. It must
  not deserialize (`.Deserialize(`), touch `Internal.Game`, or use Unity.
- `Internal/Game/Voice/VoiceAdapter.cs` talks to `VoiceAudioBackend`, never to
  the Windows capture or output classes directly.

### What enforces it

| Check | Where | Runs in CI |
| --- | --- | --- |
| `CMP001` feature needs a parameterless, non-generic constructor | `FeatureRegistryGenerator.cs` | Yes (generator tests) |
| `CMP002` feature must be a top-level class | `FeatureRegistryGenerator.cs` | Yes (generator tests) |
| `CMP003` feature references a forbidden namespace | `FeatureDependencyAnalyzer.cs` | On every mod build |
| Layer, privacy and patch-lifecycle rules | `CairnMultiplayer.Tests/ArchitectureBoundaryTests.cs` | Yes |

`ArchitectureBoundaryTests` also pins **specific gameplay decisions** by
asserting on source text: the cooperative rope stays a tether, the team rope
stops at the coil through Cairn's own check, the Climbot rope stays off, a staff
unclip tells Climbot to stop securing and the next lead does not clip onto that
belay, the Rope Team save gate refuses quick saves, the tied leader passes Cairn's
rope-head check, interop uses generated IL2CPP APIs, and remote weather uses
Cairn's own cleanup. If your change breaks one of these, you are changing a
design decision. **Stop and ask.** Never edit those assertions just to make the
suite pass. Moving a file legitimately means updating its source path in the
same commit.

## 5. Code patterns under `Internal/Game`

Gameplay code is split by responsibility, and the file suffix tells you which
one. Follow the split when you add code.

| Suffix | Responsibility | Rules |
| --- | --- | --- |
| `*Policy` | Pure decisions (when to show a prompt, whether a save is allowed, rope length limits) | `internal static`, no Unity, IL2CPP or Harmony. Takes plain values, returns plain values. Covered by `CairnMultiplayerMod.Tests/<Name>Tests.cs`. **Put new rules here first.** |
| `*Patch` | Harmony hooks into Cairn | See [Harmony patches](#harmony-patches) below |
| `*Interop` | Reading and driving Cairn objects through IL2CPP | Use the generated `Il2Cpp*` APIs (`PawnManager.Instance`, `lifeline.AddPiton(...)`). Never manual field offsets, `GetIl2CppField`, `il2cpp_field_get_offset` or `il2cpp_runtime_invoke` |
| `*Session`, `*State` | Runtime state for one climb or session | Clear it when the session ends and on scene changes |
| `*Adapter` | Implements a `GameApi` interface over the interop | e.g. `InventoryAdapter : IInventoryApi`. Features only see the interface |
| `*Feature` | Feature declaration | Only in `Features/` |

A good change usually keeps the logic in a tested `*Policy` and leaves the
`*Patch` or `*Interop` as a thin layer that gathers inputs and applies the
result.

### Harmony patches

Copy the shape of an existing patch such as `Roping/RopeClipPromptPatch.cs`:

- Each patch class owns its own `Harmony` instance with a unique ID of the form
  `CairnMultiplayerMod.<Name>`.
- It exposes idempotent `Install()` and `Uninstall()` methods.
- It resolves every target with `AccessTools` and throws
  `MissingMethodException` when one is missing.
- If installation fails, it calls `UnpatchSelf()`, logs a `ModLog.Warning`, and
  lets the game run without that hook. A game update can rename any method;
  that must cost a feature, never the game.
- It is registered in `Internal/Game/GamePatchRegistry.cs` with its uninstall
  path. `EveryRegisteredGamePatchHasAnUninstallPath` enforces this.
- Prefixes that return `false` skip Cairn's own code. Use them only when a
  postfix cannot do the job, and explain why in a comment.
- Patches on per-frame methods (`Update`, `LateUpdate`, `FixedUpdate`) cost
  every frame. Keep them allocation-free and return early.

### Scene-bound IL2CPP references

Cairn destroys and recreates many objects on scene loads and after a death.
Holding an old pointer can crash natively. Any cache of scene-bound IL2CPP
objects must reset through `Internal/Game/SceneCache.cs`.

## 6. Adding or changing a feature

A feature is **one file** in `CairnMultiplayerMod/Features/`. The source
generator finds it; there is no registry or packet ID to edit. Read
[`PingFeature.cs`](CairnMultiplayerMod/Features/PingFeature.cs) and the
[README example](README.md#add-a-feature) first.

- The file name ends in `Feature.cs`. The class is `internal sealed`,
  top-level, non-generic, derives from `MultiplayerFeature`, and has a
  parameterless constructor.
- Packet classes for the feature live in the same file and implement
  `IPacket` (`Serialize` and `Deserialize` in the same field order).
- Use `Game.*` (the `IGameApi` façade) for the game, `FeatureBuilder` for the
  network, and `LogInfo` for logs. Never reach into `Internal`. `CMP003`
  rejects the build.
- **`Id` and channel names are wire identifiers.** Renaming them is a protocol
  break (see [section 8](#8-compatibility-contracts)).
- If a feature needs a game capability that `GameApi` lacks, add an interface
  member and implement it in an `Internal/Game` adapter. That is a larger
  change: say so.

| Channel | Use it for |
| --- | --- |
| `Broadcast<T>` | Transient message any player may send (chat, pings). Never echoed back to the sender. |
| `HostState<T>` | Host-owned state replayed to late joiners (weather, time) |
| `HostCommand<T>` | Client request the host validates before acting |
| `HostEvent<T>` | Transient result only the host emits |
| `PerPlayerState<T>` | Host-owned state keyed by player |
| `Stream<T>` | Rate-limited transient updates (finger poses) |

If a late joiner must learn a value that is still true, use `HostState<T>`: a
broadcast is gone once sent.

| Phase | Runs | Appropriate work |
| --- | --- | --- |
| `FeaturePhase.Always` | Menus, bivouacs and gameplay | Input and HUD |
| `FeaturePhase.Gameplay` | Active gameplay only | World interaction |

Feature exceptions are logged and isolated, so one feature cannot stop the
others. That is a safety net, not error handling: handle the failures you can
foresee.

## 7. Networking, authority and untrusted input

These rules come from [SECURITY.md](SECURITY.md). Breaking one is a security bug.

- **Every peer and every payload is untrusted.** The Steam relay does not make
  a message trustworthy. A modified client can send anything.
- **The host is authoritative.** A client asks (`HostCommand<T>`); the host
  checks the sender, its role, the session state and the values, then applies
  the result and broadcasts it. Never let a client message change shared state
  directly.
- **Validate before use:** lengths, counts, enum ranges, finite floats, world
  bounds (`PacketValidation`), and the session state the message assumes.
- **Bound everything:** cap counts before you allocate, cap queues, cap parsing
  work. `Protocol.MaxFrameVectorCount` is the model: serialization throws above
  the cap and validation rejects it.
- **Rate-limit per peer** with `PeerBudget` for anything a peer can trigger
  repeatedly.
- **Never deserialize into arbitrary types.** No `BinaryFormatter`, no
  `Type.GetType` or reflection driven by payload data.
- **`PacketId` values are append-only.** Never renumber or reuse one; retired
  IDs stay as gaps so older clients cannot misread them. `PlayerState` values
  are stable for the same reason.
- Keep dispatch, deserialization and game application in separate places.
- Add or extend tests in `CairnMultiplayer.Tests` (`PacketValidationTests`,
  `PacketCodecTests`, `PitonAuthorityTests`, and so on) for every new packet or
  rule.

When you touch packet handling, authority, rate limits, or file I/O, list the
security implications in your report, even when you think there are none.

## 8. Compatibility contracts

Changing any of these breaks players, saves, or third-party mods. Don't change
them without the maintainer's explicit agreement, a version decision, and a
changelog entry.

| Contract | Where |
| --- | --- |
| Wire protocol version | `versions.json` → `Protocol.Version` |
| Packet IDs, field order and types | `CairnMultiplayerShared/Protocol.cs`, `Packets.cs`, feature packet classes |
| Feature `Id` and channel names | Every `*Feature.cs` |
| `PlayerState` values | `CairnMultiplayerShared/Protocol.cs` |
| Extension negotiation | `CairnMultiplayerShared/Extensions/`, `ExtensionPackets.cs` |
| Public managed API | `CairnMultiplayer.Api` namespace in `CairnMultiplayerMod/Api/`; `examples/ManagedExtensionExample/` must still compile against it |
| DLL and mod names | `CairnMultiplayerMod.dll`, `CairnMultiplayerShared.dll`; `MelonInfo` name `Cairn Multiplayer Mod` |
| Preference categories and entries | `Player`, `Room`, `Keybinds`, `Debug`, `CairnMP.Controller`, `CairnMP.Voice` (players' `MelonPreferences.cfg`) |
| Harmony IDs | `CairnMultiplayerMod.<Name>` |
| Save behavior | `BivouacSave*`, `SavegamePitonGuardPatch`, story launch and difficulty code: players' story saves are at stake |
| Diagnostics locations and bounds | `UserData/CairnMultiplayer/Crashes/`, 10 session logs (20 MiB), 5 crash archives (100 MiB) |

**Protocol bump rule:** bump `protocol` in `versions.json` whenever packet
layouts, feature contract IDs, or their meanings change incompatibly, then run
`node scripts/sync-versions.js`. The handshake rejects mismatched versions, so a
bump forces every lobby member to update. Say clearly when a change needs one;
let the maintainer make the call.

## 9. Working with Cairn, IL2CPP, Unity and MelonLoader

- **Main thread only.** Unity and IL2CPP objects may only be touched on Unity's
  main thread. Steam callbacks, audio threads and workers must hand results
  back through the existing queues (network results complete from
  `NetworkManager.Update`). Voice work must never stall the main thread.
- **Objects die.** A Unity object can be destroyed while your C# reference
  lives on. Re-resolve after scene loads, check before use, and never cache
  scene objects without a `SceneCache` reset.
- **Scene transitions are fragile.** Don't spawn prefabs while a scene is
  loading: it can crash Cairn's Addressables pipeline. Remote players are shown
  only when both sides are `InGame`.
- **Per-frame code is hot.** In `Update`-like paths and per-frame patches: no
  allocations, LINQ, string formatting or `Find*` calls; cache what you can;
  throttle logs (`FeatureLog.ErrorThrottled`). Performance regressions show up
  as stutter for every player.
- **Game updates break things.** Method names, signatures and fields can change
  with any Cairn patch. Fail soft (see [Harmony patches](#harmony-patches)) and
  log one clear warning.
- **Logging.** `ModLog` in `Internal`, `LogInfo` and `FeatureLog` in features
  and the framework. Use a short tag (`[Rope]`, `[Voice]`). Chatty logs go
  behind `Debug.VerboseLogging`.
- **Don't invent game APIs.** Without the game references you cannot see
  Cairn's types. If you cannot check that an `Il2Cpp*` type or member exists,
  write that it is unverified. Never guess a member name and present it as
  fact.
- **Don't copy decompiled game code** into the repository, comments, issues or
  pull requests. Name the types and methods you rely on and describe their
  behavior in your own words.
- **Rope, pitons, belay, bivouacs and saves are the most fragile areas.** They
  mix Cairn's native physics (Obi rope), the save system and network
  authority, and they are guarded by the architecture assertions above. Plan
  changes there with the human before writing code.

## 10. Diagnostics, privacy and network egress

- CairnMP **never uploads diagnostics**. Crash archives and session logs stay on
  the player's disk, within fixed bounds.
- `Internal/Diagnostics` must not use the network or create machine
  identifiers. The architecture tests enforce it.
- The only HTTP traffic is the public-lobby presence record sent to the CairnMP
  platform (overridable with `CAIRNMP_API_URL`). Don't add telemetry,
  analytics, update checks or any other outbound call.
- Never embed API keys, tokens or secrets. Lobbies use short-lived registration
  tokens; keep it that way.
- Don't log chat contents, tokens, IP addresses or other private data.
- The Goldberg launchers point `CAIRNMP_API_URL` at a closed local port so
  emulated Steam IDs never reach the public lobby list. Don't change that.

## 11. Build, test and verify

### Portable checks: any OS, no game needed

These are what CI runs. Run them for every code change:

```bash
dotnet restore CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj --locked-mode
dotnet test CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj -c Release --no-restore -p:DeployMod=false
node scripts/sync-versions.js --check
node --test "scripts/*.test.js"
```

`CairnMultiplayer.Tests` compiles some production files directly (see the
`<Compile Include=... Link=...>` items in its `.csproj`): `ChatCompletion`,
`PitonAuthority`, `IAuthoritativeSink`, `PeerBudget`, `RopeRequests`,
`CrashArchiveBuilder`, `CrashFingerprint`, `DiagnosticRetention`,
`BoundedSessionLog`, `PerformanceCapture` and `PerformanceReport`. Those files
must stay free of Unity, IL2CPP, MelonLoader and Harmony, or CI breaks.

### Complete solution: needs the game references

The mod and `CairnMultiplayerMod.Tests` compile against proprietary assemblies
that are not in the repository. They come from `game-refs/Il2CppAssemblies/`
and `game-refs/MelonLoader/`, or from `MELON_LOADER_NET6_DIR` plus a local Cairn
install (see `Directory.Build.props`). You also need the .NET 6 runtime.

```bash
bash scripts/check.sh   # restore (locked), version check, full Release test
```

or step by step:

```bash
dotnet restore CairnMultiplayer.sln --locked-mode
dotnet build CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
dotnet test CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
```

- **If the references are missing, the mod does not compile.** Say so plainly:
  "mod build and mod tests not run: no game references". Never present the
  portable suite as proof that the mod builds.
- Building the solution also runs `scripts/package-mod.ps1` (needs `pwsh`) and
  writes ZIPs to `../CairnMP-packages/`, outside the repository. Pass
  `-p:PackageOnSolutionBuild=false` when you don't want that.
- `-p:DeployMod=true` copies the build into the player's game `Mods` folder
  (and `CAIRN_DIR_2`). **Don't run it unless asked**: it replaces the mod the
  human plays with.
- Default paths in `Directory.Build.props` use `%LOCALAPPDATA%` (Windows). On
  Linux, use `game-refs/` or the environment variables.

### Which tests to run and write

| You changed | Run | Add or update |
| --- | --- | --- |
| `CairnMultiplayerShared/` (packets, validation, codecs) | Portable | `PacketCodecTests`, `PacketValidationTests`, `QuaternionCodecTests`… |
| Host authority, rate limits | Portable | `PitonAuthorityTests`, `ServerTeleportTests`… |
| Generator or analyzers | Portable | `GeneratorTests` |
| Layering, file moves | Portable | `ArchitectureBoundaryTests` (paths only) |
| Diagnostics | Portable | `CrashDiagnosticsTests`, `ResourceBoundaryTests`, `PerformanceDiagnosticsTests` |
| A `*Policy` or framework code | Complete solution | `CairnMultiplayerMod.Tests/<Name>Tests.cs` |
| Release or performance scripts | `node --test "scripts/*.test.js"` | The matching `*.test.js` |
| Patches, interop, UI, voice, transport | Complete solution **and** in game | A `*Policy` test for any logic you can extract |
| Versions | `node scripts/sync-versions.js --check` | — |

A bug fix should come with a test that fails without the fix whenever the logic
can be tested without the game.

### In-game validation: humans only

Automated tests cannot exercise Steam sessions or native IL2CPP behavior, and
an agent cannot play the game. For any gameplay change, hand the human a test
plan:

- the scenario and the steps;
- host and client roles, and which side to watch;
- the expected result, and what a failure looks like;
- log lines worth checking (`MelonLoader/Latest.log`, the session logs);
- regressions to look for in neighboring behavior.

Two clients on one PC: the Goldberg emulator scripts (`scripts/setup-goldberg.*`
and `scripts/start-goldberg.*`) for everyday gameplay, two Steam accounts for
lobby, admission, relay and transport changes. See
[CONTRIBUTING.md](CONTRIBUTING.md#two-clients-on-one-pc). Mute voice when both
clients share a room.

## 12. Code quality and style

### Code quality

"It works" is where review starts, not where it ends. A change that passes the
tests but is hard to follow, duplicated or tangled goes back for another round.
Write code the next contributor can read once and trust.

- **Correct first, then clear.** Handle the real cases, including the edge
  cases this mod meets every day: destroyed objects, empty lists, host versus
  client, late joiners, scene changes, a peer leaving mid-action. Don't add code
  for cases that cannot happen.
- **Balanced: not clever, not over-engineered.** Pick the simplest design that
  fits the existing architecture. No interface, factory, generic or setting for
  a single use, and no one-line wrapper that only renames a call. No
  200-line method that does everything either.
- **No spaghetti.** One job per method and per class. Keep nesting shallow with
  guard clauses and early returns. Don't thread flags through several methods
  to change their behavior, and don't add a boolean parameter that switches a
  method between two different jobs: split it. Give each piece of state one
  owner instead of static fields changed from several places.
- **Put logic where it belongs.** Decisions in a `*Policy`, game access in a
  `*Interop`, hooks in a `*Patch`, wiring in `Bootstrap`
  ([section 5](#5-code-patterns-under-internalgame)). Don't put a gameplay rule
  in a Harmony patch or a network handler because it is the nearest place.
- **Don't duplicate.** Look for an existing helper, policy, codec or constant
  before writing one (`PacketCodec`, `PacketValidation`, `Protocol`, the
  existing `*Policy` classes). Two copies of the same rule drift apart.
- **Name things for what they mean**, in the game's words: leader, follower,
  piton, belay, bivouac. No `data`, `temp`, `helper`, `flag` or `Manager2`. A
  good name saves a comment.
- **No magic numbers.** A tuning value gets a named constant with its unit next
  to the code that uses it (`LeadSeconds`, `PingCooldownSeconds`). Wire limits
  shared by both sides go in `Protocol`.
- **Handle errors on purpose.** Don't wrap code in `try`/`catch` to make an
  exception disappear. Catch what you can handle, and log it. The fail-soft
  [Harmony patch](#harmony-patches) pattern is the exception, and it still logs.
- **Fix the cause, not the symptom.** A delay, a retry loop or an extra null
  check that hides a bug is not a fix. If you cannot find the root cause, say
  so and describe what you observed.
- **Leave no debris:** no dead code, unused parameters or `using`s, leftover
  debug logs, or experiments behind switches nobody asked for.
- **Rework your first draft.** Once it works, reread the diff as a reviewer
  would: remove what is not needed, merge what is duplicated, untangle what is
  tangled. Then run the tests again.

### Comments

Fewer, better comments. If code needs a comment to explain *what* it does,
rewrite the code first.

- Comment only what the code cannot say: why it is done this way, a fact about
  Cairn learned in game, an ordering or threading constraint, the reason for a
  workaround. One or two short sentences are usually enough.
- Don't paraphrase the code (`// increment the counter`,
  `// return true if valid`), and don't comment every line or every method.
- Don't narrate the change or its history (`// fixed`, `// added for #12`,
  `// changed from X to Y`, `// new logic`). That goes in the commit message.
- Put an XML `<summary>` on types and members whose purpose or contract the
  name does not make obvious, not on every member.
- Write in English, in plain present tense, describing behavior, like the
  existing comments.
- When you change code, update or delete the comments that described it. A
  wrong comment is worse than none.
- No commented-out code, no `TODO` without an issue, no banners saying code was
  AI-generated.

### Formatting

Formatting comes from `.editorconfig` and `.gitattributes`:

- UTF-8, final newline, no trailing whitespace (except Markdown).
- LF line endings, **except CRLF for `*.ps1`, `*.bat` and `*.cmd`**.
- 4 spaces for C#; 2 spaces for YAML, JSON, `.csproj` and `.props`.
- `System` usings first, no blank lines between using groups.

### Conventions

- File-scoped namespaces that mirror the folder
  (`CairnMultiplayerMod.Internal.Game.Roping`).
- `internal` by default, `sealed` classes, `static` for stateless helpers and
  policies. Nothing under `Internal` is `public`.
- Private fields `_camelCase`; constants and static readonly fields
  `PascalCase`.
- Nullable reference types: **disabled** in the mod project (pure files may opt
  in with `#nullable enable`), **enabled** in `CairnMultiplayerShared`.
  `CS8600`–`CS8604` are errors.
- Expression-bodied members and early returns are common; follow the style of
  the file you are in.
- Create a domain folder only for a group of at least three files.
- Move tracked files with `git mv`, rename symbols with a semantic refactoring,
  and update source paths in architecture tests in the same commit.
- Tests use xUnit `[Fact]` and `[Theory]` in a `<Subject>Tests` class, with
  method names that read as a sentence about behavior
  (`TheTipShowsInTheLastSecond`).
- Don't reformat, reorder or "modernize" code you are not otherwise changing.

## 13. Documentation and changelog

- **User-visible change → `CHANGELOG.md`**, under `## [Unreleased]` at the top
  of the release list (create the heading if it is missing). Use the categories
  **Added**, **Changed**, **Fixed**, **Removed**.
- Write entries for players, as the existing ones are: short, plain sentences
  about what a player sees, prefixed with the mode when it applies
  (`Rope Team: …`). No class names or implementation detail.
- Never invent pull request numbers or contributor handles. Leave credits to the
  human.
- Never turn `[Unreleased]` into a version heading, bump `mod` in
  `versions.json`, or create a tag. Releases are the maintainer's job (see
  [README.md](README.md#publish-a-release)).
- Update README tables (features, controls, shortcuts, limits) when behavior
  they describe changes. Developer-facing changes go in CONTRIBUTING.md.
- Markdown follows `.markdownlint-cli2.jsonc`.

## 14. Git, commits and pull requests

- Branch from `develop`, one focused change per branch: `fix/…`, `feat/…`,
  `docs/…`, `refactor/…`, `chore/…`.
- **Commit only when asked.** Never amend, rebase or force-push a shared
  branch, and never skip hooks (`--no-verify`).
- **Stage explicit paths:** `git add -- <paths>`. Never `git add -A` or
  `git add .`. Check `git diff --staged` before committing.
- Commit messages follow Conventional Commits, as the history does:
  `type(scope): summary`, lowercase, imperative, no trailing period. Types:
  `feat`, `fix`, `refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`.
  Scopes used so far include `rope`, `belay`, `pitons`, `ui`, `voice`,
  `diagnostics`, `framework`, `multiplayer`, `game`, `freeroam`, `netplay`,
  `debug`, `deps`, `readme`, `changelog`. `release(mod)` is reserved for the
  maintainer. The body says what changes for the player and why.
- Fill in `.github/PULL_REQUEST_TEMPLATE.md` honestly. A check you did not run
  stays **Not run**, and "Not tested" says why (for example, no game
  references).
- Use `Fixes #123` only when the pull request fully resolves the issue,
  otherwise `Related to #123`. Open a **draft** while implementation or
  validation is incomplete.
- Disclose AI assistance in the description: which tool, and what it produced
  (code, tests, documentation, review).
- Expect a thorough review that may take time. Every change must be understood,
  tested, and free of side effects before it is merged. Answer review questions
  with explanations, not with another regenerated diff.

## 15. Dependencies, CI and supply chain

- Restores use `--locked-mode`. Any package change must update the
  `packages.lock.json` of every affected project.
- **No new NuGet or npm dependency without the maintainer's approval.** The mod
  ships its runtime dependencies (`Concentus`, `NAudio`), lists them in
  `ModDeployFiles` and `THIRD-PARTY-NOTICES.txt`, and their licenses must be
  compatible with AGPL-3.0.
- Major version upgrades are reviewed separately (see `.github/dependabot.yml`).
- GitHub Actions are pinned to full commit SHAs with a version comment, with the
  minimum token permissions, on GitHub-hosted runners.
- Never run pull-request code with repository secrets, and never reuse
  untrusted artifacts in a privileged job.
- Don't edit `.github/workflows/release.yml`, the MelonLoader URL and SHA-256
  (pinned in both the workflow and the launcher), or anything that touches
  secrets without the maintainer.

## 16. Never do this

- Commit, paste or upload `game-refs/`, Cairn, Unity or MelonLoader assemblies,
  decompiled game code, saves, player logs, crash archives, `.env` files, keys,
  certificates or other credentials.
- Commit the Goldberg emulator or the game copies its scripts create, or share
  them in any way.
- Run `-p:DeployMod=true`, start the game, or change files outside the
  repository (the game install, `CairnMultiplayerData`, Steam, the user
  profile) unless asked.
- Push, open or merge pull requests, comment, tag, publish a release, or post to
  Discord or GitHub unless the human asks for that specific action.
- Weaken validation, host authority, rate limits or bounds to make something
  work.
- Delete, skip or weaken tests, or rewrite architecture assertions, to get a
  green run.
- Silence analyzers or warnings (`#pragma warning disable CMP003`, `NoWarn`)
  instead of fixing the cause.
- Add telemetry, analytics, or any new outbound network call.
- Change a [compatibility contract](#8-compatibility-contracts) silently.
- Present an unverified IL2CPP member, test result or in-game behavior as
  verified.
- Test against public lobbies or sessions you don't own.

## 17. Ask before doing this

- Bumping `protocol` or `mod` in `versions.json`.
- Changing anything in [section 8](#8-compatibility-contracts).
- Touching rope, piton, belay, bivouac, save or death and revive logic beyond
  a narrow, agreed fix.
- Adding a Harmony patch, especially on a per-frame method or a prefix that
  skips Cairn's code.
- Adding a `GameApi` member, a public `CairnMultiplayer.Api` type, or a new
  channel kind.
- Adding, removing or upgrading a dependency, or changing a target framework.
- Changing CI, release, packaging or the two-client scripts.
- Any change that a failing architecture assertion is warning you about.
- Moving or renaming files or namespaces beyond the change at hand.
- Any change larger than the task as the human described it.

## 18. Definition of done

Before you hand work back, check each item:

- [ ] The change does what was asked, and nothing else.
- [ ] You read the code around it, including callers and related patches.
- [ ] The portable suite passes, and you ran it in this session.
- [ ] The complete solution builds and its tests pass, **or** you stated that
      it could not be run and why.
- [ ] New logic has tests; a fix has a test that fails without it when the
      logic can be tested without the game.
- [ ] You reread the diff as a reviewer would. The code is clear and balanced:
      no duplication, tangled control flow, magic numbers, dead code or
      comment noise.
- [ ] No layer rule, `CMP` diagnostic or architecture assertion was worked
      around.
- [ ] Untrusted input is validated, bounded and rate-limited where relevant.
- [ ] Compatibility contracts are untouched, or the break was agreed and is
      documented.
- [ ] `CHANGELOG.md`, README and CONTRIBUTING are updated where needed.
- [ ] `git diff` contains no unrelated, generated, private or proprietary
      files, and line endings follow `.gitattributes`.
- [ ] You wrote an in-game test plan for anything players can see or feel.

## 19. How to report back

End every task with a short report the human can paste into a pull request:

```markdown
### What changed
<one or two sentences on the result, then the files touched and why>

### Verification
| Check | Command | Result |
| --- | --- | --- |
| Portable tests | dotnet test CairnMultiplayer.Tests/... | Passed (N tests) |
| Complete solution | bash scripts/check.sh | Not run: no game references |

### Not verified
<what could not be checked, IL2CPP members you could not confirm, open questions>

### Risks and side effects
<compatibility, save, performance, security, host/client differences>

### In-game test plan
<scenario, host/client roles, expected result, what to watch in the logs>
```

## 20. Maintaining this file

- Keep this file in step with the code. When a rule changes, update it in the
  same pull request.
- Keep shared rules here. Put tool-specific notes in that tool's file
  (`CLAUDE.md` for Claude Code), and personal notes in ignored local files.
- If an instruction here is wrong or out of date, say so instead of working
  around it silently.
