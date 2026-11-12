# Contributing to CairnMP

Thank you for helping improve CairnMP. Bug reports, feature proposals,
documentation fixes, tests, and code contributions are welcome in **English or
French**.

> [!IMPORTANT]
> Follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report vulnerabilities
> privately through the [Security Policy](SECURITY.md)—never in a public issue.

## Contents

- [Before you start](#before-you-start)
- [Report a bug](#report-a-bug)
- [Suggest an improvement](#suggest-an-improvement)
- [Set up development](#set-up-development)
- [Two clients on one PC](#two-clients-on-one-pc)
- [Run the checks](#run-the-checks)
- [Prepare a pull request](#prepare-a-pull-request)
- [Keep changes reviewable](#keep-changes-reviewable)

## Before you start

| Your contribution | What you need |
| --- | --- |
| Bug report or feature proposal | No local build required |
| Documentation-only change | Repository checkout |
| Portable protocol or architecture change | .NET SDK pinned by `global.json` |
| Mod or gameplay change | Local Cairn references and .NET 6 runtime |
| Native gameplay validation | Two clients and distinct Steam identities ([one copy of Cairn is enough](#two-clients-on-one-pc)) |

For a substantial feature, open an issue before investing in implementation.
This confirms scope and avoids duplicated work.

## Report a bug

1. Search [open and closed issues](https://github.com/CairnMP/cairnmp-mod/issues).
2. If the problem already exists, add new reproduction details there.
3. Otherwise, [open a new issue](https://github.com/CairnMP/cairnmp-mod/issues/new/choose)
   and keep it focused on one problem.
4. Use a specific title, such as `Client pings disappear after leaving a
   bivouac`.
5. Describe the reproduction steps, expected result, and actual result.

Include as much of this context as you can:

- CairnMP version and release channel on every client;
- Cairn and MelonLoader versions;
- player count and whether the affected player was host or client;
- operating system and other installed mods;
- frequency, if the issue is intermittent;
- relevant log excerpts, screenshots, or a short recording.

> [!CAUTION]
> Review attachments for private chat, identifiers, usernames, and local paths.
> Never upload game DLLs, saves containing private data, or the entire game
> directory. Logs help, but are not mandatory.

## Suggest an improvement

Describe the player or contributor problem first, then give an example of the
desired behavior. Mention any workaround you currently use and identify who
benefits from the change.

## Set up development

1. Install the SDK pinned in [`global.json`](global.json).
2. Open `CairnMultiplayer.sln` in JetBrains Rider.
3. Read the [architecture guide](docs/architecture.md) before changing layer
   dependencies.
4. Read the [feature example](README.md#add-a-feature) before adding gameplay
   behavior.
5. For mod changes, provide the local
   [reference assemblies](README.md#reference-assemblies).

The [managed extension example](examples/ManagedExtensionExample/) demonstrates
the public API. It is a source example, not a project in the solution.

### Run the game from Rider

The solution ships two run configurations in [`.run/`](.run):

- **CairnMP - Build Debug** builds the mod project with `-p:DeployMod=true`,
  which copies the mod and its symbols into the game's `Mods` folder.
- **CairnMP** launches the game and runs that build first, so pressing Run
  always starts Cairn with the code currently in the editor.

A normal build never deploys, so a solution build leaves whatever the game's
`Mods` folder already held. When the game behaves like an older build, check
that the last thing you ran was the **CairnMP** configuration.

Set the `CAIRN_DIR_2` environment variable to a second game folder to have the
same build deploy there too. [Two clients on one PC](#two-clients-on-one-pc)
is how that folder is created and signed in.

## Two clients on one PC

CairnMP treats each climber as one Steam account. Two windows on one account
are the same person and cannot join each other. Steam Families will not run
two copies of Cairn at the same time on one purchase. Two setups get around
this:

| Setup | You need | It tests |
| --- | --- | --- |
| [Goldberg emulator](#one-copy-with-the-goldberg-emulator) | One copy of Cairn, on Windows or Linux | Gameplay between two windows, over the emulator's local network |
| [Two Steam accounts](#two-steam-accounts) | A second copy on a second account, on Windows | The Steam lobby and relay path that players use |

Start with the emulator for everyday work. Use two Steam accounts before a
release, or when a change touches lobbies, admission or the Steam transport.

### One copy, with the Goldberg emulator

[gbe_fork](https://github.com/Detanup01/gbe_fork), the maintained fork of the
[Goldberg emulator](https://mr_goldberg.gitlab.io/goldberg_emulator/), stands
in for Steam inside two copies of your game. Each copy gets its own Steam name
and ID, and the two find each other over `127.0.0.1`. Steam does not need to
be running.

> [!IMPORTANT]
> This is a development tool for people who own Cairn on Steam. The setup stops
> unless Cairn is installed through Steam on the PC, and it never changes that
> install. Do not share the copies it makes. Players always use the Steam build.

The scripts use gbe_fork because the original Goldberg stops at Steamworks SDK
1.57 and lacks `SteamInternal_SteamAPI_Init`, which CairnMP calls. They pin
one gbe_fork release and check its SHA-256 before they install anything.

1. Install CairnMP in your usual game folder and start the game once, so
   MelonLoader generates its assemblies. Then quit Cairn.
2. Run the setup:

   | System | Command |
   | --- | --- |
   | Windows | `pwsh ./scripts/setup-goldberg.ps1` |
   | Linux, with Proton | `bash scripts/setup-goldberg.sh` |

   It makes `client1` (`CairnDev1`) and `client2` (`CairnDev2`) under
   `%LOCALAPPDATA%\CairnMultiplayerData\goldberg` on Windows, or
   `~/.local/share/CairnMultiplayerData/goldberg` on Linux. It looks for the
   game in `CAIRN_DIR`, then the launcher folder (Windows only), then your
   Steam libraries. To choose the folder, pass it as `-Source` on Windows or
   as the first argument on Linux. Files under `Cairn_Data` are hard links
   when the disk allows it, so the two copies take little extra space. The
   folder must be new or empty the first time.
3. Start both windows with `pwsh ./scripts/start-goldberg.ps1` or
   `bash scripts/start-goldberg.sh`. Before it starts a client, the script
   copies the source game's `Mods` folder into it, so a deploy from Rider
   reaches it. A client that is already open is left alone. To start one
   again, close its window, then pass its number: `-Clients 2` on Windows,
   `2` on Linux.

Host in one window and join from the other. Mute voice. Two games in one room
feed each other's microphones.

- The emulator carries lobbies and packets over the local network, not the
  Steam relay. Lobby admission, relay and NAT behavior still need two Steam
  accounts.
- Hosting does not reach the CairnMP platform. The start scripts point
  `CAIRNMP_API_URL` at a closed local port, so emulated Steam IDs never
  appear on the public lobby list.
- On Windows both clients share your Windows profile, and with it Cairn's
  story saves. Pick a different save slot in each window. On Linux each client
  has its own Proton prefix and so its own saves. The first start of a prefix
  takes about a minute.
- Linux starts the newest `Proton N.N` in your Steam libraries, or Proton
  Experimental when there is none. Set `PROTON` to a Proton folder to choose
  another.
- The setup unpacks the emulator with 7-Zip on Windows, and with `bsdtar` or
  7-Zip on Linux. Without 7-Zip it tries Windows' own `tar`, which cannot read
  the archive on Windows 10.
- Run the setup again after a game, MelonLoader or launcher update. Each client
  keeps its `UserData`, `Logs` and emulator saves (`gse-saves`).

### Two Steam accounts

The second account needs its own copy of the game.

1. Create a local Windows user named `cairnmp2` with a password and no
   Microsoft account (Settings, Accounts, Other users). The second Steam login
   and the second story save live in that profile.
2. Buy Cairn on a second Steam account.
3. Quit Cairn, then run `pwsh ./scripts/clone-cairn.ps1`. It copies the game
   you already have (about 12 GB) to `C:\Users\Public\CairnMP\instance2` and
   sets `CAIRN_DIR_2`. Open a new terminal before the next deploy so the
   deploy sees that variable. Client 1 stays in its current folder.
4. Open your normal Steam and sign into the main account.
5. Run `pwsh ./scripts/start-second-client.ps1`. The first run asks for the
   `cairnmp2` password and saves it for later runs. Sign the new Steam window
   into the second account. When that window is signed in, press Enter. The
   script starts the cloned game. Do not press Play in that Steam window.
   Closing the game leaves that Steam window up. The next run reuses one
   second Steam, and closes a pile of them, or helpers whose Steam has already
   exited, before it opens another. The normal Steam window is left alone.
6. Run `pwsh ./scripts/start-local-client.ps1` for client 1.

Host in one window and join from the other. The lobby should show two Steam
names. Click between the windows to move each climber. Both scripts tell the
mod to keep simulating while a window is in the background, and each game
writes its Unity log under its own `Logs\Player.log`. Mute voice. Two games
in one room feed each other's microphones.

This second Steam launch is a community workaround. If a Steam update breaks
it, sign into `cairnmp2` with Fast User Switching and run the game from that
desktop. That path shows one desktop at a time.

## Run the checks

### Portable checks

These commands work without Cairn installed:

```bash
dotnet restore CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj --locked-mode
dotnet test CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj -c Release --no-restore
node scripts/sync-versions.js --check
```

### Complete solution

For mod changes, provide your own game references and the .NET 6 runtime, then run
the project check script from Git Bash on Windows:

```bash
bash scripts/check.sh
```

The script restores locked dependencies, checks version synchronization, and
runs the complete solution in Release mode. Normal builds never deploy. Only
`-p:DeployMod=true` installs a development build.

### Gameplay checks

Automated tests cannot exercise live Steam sessions or native IL2CPP behavior.
Test gameplay changes with two clients when possible, then document:

- the scenario;
- expected behavior;
- observed result;
- host and client roles;
- checks you could not run.

## Prepare a pull request

1. Fork the repository if you do not have write access.
2. Branch from the target branch—normally `develop`—with one focused change.
   Examples: `fix/bivouac-pings` or `docs/setup`.
3. Implement the change, update documentation, and add regression coverage when
   practical.
4. Run the applicable checks and record their results.
5. Review your own diff for unrelated or private files.
6. Commit explicit paths, push your branch, and open a pull request against the
   intended base branch.
7. Complete the pull-request template, including compatibility impact and
   verification.

The shared branches are protected on GitHub. Submit changes through a pull
request and satisfy the checks and review requirements reported by GitHub before
merging.

Use `Fixes #123` only when the pull request fully resolves that issue; otherwise
use `Related to #123`. Open a **draft pull request** if implementation or
validation is incomplete.

A good title describes the result—`Fix client pings after bivouac`—rather than the
activity—`Various fixes`.

## Keep changes reviewable

### Source structure

- Put feature declarations directly in `CairnMultiplayerMod/Features/`.
- Use `FeatureBuilder` and `GameApi`; diagnostic `CMP003` prevents features from
  reaching internal implementation code.
- Create domain folders only for groups of at least three files.
- Keep packet dispatch separate from deserialization and game integration.
- Use `git mv` for tracked file moves and semantic refactoring for symbols and
  namespaces.
- Update source-path architecture tests in the same commit as a move.

### Compatibility contracts

Preserve published extension namespaces, DLL names, preference categories,
Harmony IDs, and wire identifiers unless the change intentionally includes a
documented compatibility break.

When versions change:

1. edit `versions.json`;
2. run `node scripts/sync-versions.js`;
3. commit every synchronized declaration.

Dependency updates must include the affected `packages.lock.json` files.

### Repository safety

- Pin GitHub Actions to full commit SHAs and grant minimum token permissions.
- Use GitHub-hosted runners for public contributions.
- Never run pull-request code with repository secrets.
- Stage explicit paths with `git add -- <paths>`; avoid `git add -A`.
- Keep working audits under ignored `docs/local/`.
- Never commit proprietary assemblies, game saves, player logs, or credentials.
- Never commit the Steam emulator or the game copies the Goldberg scripts make.
  The scripts keep both outside the repository.

---

Questions are welcome through the repository’s
[issue templates](https://github.com/CairnMP/cairnmp-mod/issues/new/choose).
