<!-- bmad:context -->
<!-- Verified 2026-08-12 against 7c5279f. Managed by bmad-project-context; edits inside this block are replaced on refresh. Keep anything you want preserved outside the markers. -->

## CairnMP

Multiplayer mod for the climbing game Cairn, loaded in-process by MelonLoader (IL2CPP)
and connecting players over a host-authoritative Steam relay. C# / .NET 6, four projects
in `CairnMultiplayer.slnx`, xUnit tests. The managed extension API is documented in
`docs/multiplayer-api.md`; BMad planning artifacts land in `_bmad-output/`.

## Policy

- Never introduce code under a licence incompatible with AGPL-3.0, and never commit the proprietary Cairn / Unity / MelonLoader assemblies — they stay in git-ignored `game-refs/`.
- Never hand-edit `Version` or `GameVersion` in `CairnMultiplayerShared/Protocol.cs`, or the `MelonInfo` version in `CairnMultiplayerMod/Bootstrap/Mod.cs` — edit `versions.json` and run `node scripts/sync-versions.js`.

## Where things are

- Mod entry point and MelonLoader lifecycle: `CairnMultiplayerMod/Bootstrap/Mod.cs`
- Wire protocol shared by mod and tests: `CairnMultiplayerShared/` — packet IDs and stability rules are documented in `Protocol.cs` itself.
- Extending the API? Read `docs/multiplayer-api.md` first — negotiation, transaction and abort guarantees.

## Running and verifying

- `dotnet test CairnMultiplayerShared.Tests` runs without `game-refs/`; it depends only on the protocol project. Solution-wide `dotnet test` needs the reference assemblies, contrary to the README's blanket claim.
- Building the mod project is a deploy: `CopyToMods` runs `AfterTargets="Build"` and copies the DLLs into your live Cairn `Mods/` folder. Close the game first if it holds the DLLs.
- Run `bash scripts/check.sh` manually before pushing — its header claims a `.githooks/pre-push` hook calls it, but no such hook exists in this repo.

## Conventions that differ from defaults

- English for all comments, docs and commit messages. A few build and script files still carry French from before `9fb3336`; translate them when you touch them.
- Add every new namespace under `CairnMultiplayerMod/` to `GlobalUsings.cs` — the project relies on project-wide global usings, so an unregistered namespace fails to resolve from other files.
- Conventional Commits, with a body explaining why the change was needed rather than restating what changed.

## Known pitfalls

- Moving or renaming `Bootstrap/Mod.cs` or `CairnMultiplayerShared/Protocol.cs` silently breaks `scripts/sync-versions.js`, which hardcodes both paths and regexes — update it in the same change. It broke this way once already (`d989089`).

<!-- /bmad:context -->
