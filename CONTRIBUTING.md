# Contributing to CairnMP

Use the SDK pinned in `global.json`. Open `CairnMultiplayer.sln` in Rider for C# navigation
and semantic refactoring. Read [the architecture](docs/architecture.md) before changing
layer dependencies, and [the feature example](README.md#contributing-a-feature) to add
gameplay behavior.

## Run checks

Without a game installation:

```bash
dotnet restore CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj --locked-mode
dotnet test CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj -c Release --no-restore
node scripts/sync-versions.js --check
```

For changes to the mod, provide your own [reference assemblies](README.md#reference-assemblies)
and the .NET 6 runtime, then run `bash scripts/check.sh` (Git Bash on Windows). It restores
locked dependencies, checks versions and runs the complete solution's tests in Release.
Normal builds never deploy. Only `-p:DeployMod=true` installs a development build.

Test gameplay changes with two clients as well: automated checks cannot exercise live Steam
sessions or native IL2CPP behavior. Describe the scenario, expected behavior and result in
your pull request, including any checks you could not run.

## Keep changes easy to review

Put feature declarations directly in `CairnMultiplayerMod/Features/`. Use `FeatureBuilder`
and `GameApi`; CMP003 prevents features from reaching into internal implementation code.
Keep domain folders for groups of at least three files, subject to the architectural
boundaries documented above. Use descriptive filenames for smaller groups.

Use `git mv` for tracked file moves and Rider's semantic refactorings for namespaces and
symbols. Update source-path architecture checks in the same commit as the corresponding
move. Keep packet dispatch separate from deserialization and game integration.

Preserve published extension namespaces, DLL names, preference categories, Harmony IDs and
wire identifiers. Dependency changes must update the affected `packages.lock.json` files.
Edit `versions.json` and run `node scripts/sync-versions.js` to synchronize version declarations.

Stage explicit paths with `git add -- <paths>`; do not use `git add -A`. Working audits belong
in ignored `docs/local/`. Never commit proprietary assemblies, game saves or player logs.
For bug reports, include mod versions, reproduction steps, host/client roles and relevant
log excerpts after reviewing them for private information.

The [managed extension example](examples/ManagedExtensionExample/) illustrates the public
API; it is currently a source example, not a project included in the solution.
