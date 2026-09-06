# Contributing to CairnMP

Bug reports, feature suggestions, documentation fixes and code contributions are welcome.
You can write issues and pull requests in English or French. You do not need to build the
mod to report a problem.

## Report a bug or suggest an improvement

1. Search [existing issues](https://github.com/CairnMP/cairnmp-mod/issues), including closed
   ones. Add new reproduction details to an existing report when it describes the same bug.
2. Select [New issue](https://github.com/CairnMP/cairnmp-mod/issues/new/choose) and choose the
   bug report, feature request or question template. Keep one topic per issue.
3. Give it a specific title, such as `Client pings disappear after leaving a bivouac`.
   Describe the steps, expected result and actual result. If you cannot reproduce it
   consistently, explain what happened and how often you have seen it.
4. Include the CairnMP version/channel on each client, game and loader versions if known,
   number of players, host/client role, operating system and other installed mods.
5. Attach relevant log excerpts, screenshots or a short recording if available. Review
   attachments for personal paths, identifiers and private chat before sharing them.
   Do not upload game DLLs or your entire game folder. Logs are helpful, not mandatory.

For a feature request, explain the player problem and an example of the desired behavior.
Mention any workaround you use today. For larger changes, discuss the approach in an issue
before spending time on an implementation.

## Open a pull request

1. Fork the repository if you do not have write access, then create a branch in your fork
   for one change, such as `fix/bivouac-pings` or `docs/setup`.
2. Start from the branch your change targets. Stable fixes normally target `develop`;
   changes using the new feature framework target `next/feature-framework` while that work
   remains separate. Check the PR's **base branch** explicitly and avoid unrelated commits.
3. Make the change and update relevant documentation. Add regression coverage for behavior
   changes where practical; documentation-only changes do not require a game installation.
4. Run the applicable checks below. Record the commands and results, and explain anything
   you could not test. For gameplay changes, include host/client results from two-player
   testing when available. CI currently checks the portable suite, not the full mod or game.
5. Commit explicit paths, push your branch, then use **Compare & pull request** on GitHub.
   Fill in the PR template: problem, resulting behavior, related issue, verification and
   any compatibility impact. Use `Fixes #123` only if the PR fully resolves that issue;
   otherwise write `Related to #123`.
6. Open a **draft PR** if implementation or validation is unfinished. Check the diff for
   accidental files, address CI failures and respond to review comments. Further commits
   pushed to the same branch update the existing PR.

A good PR title describes the result: `Fix client pings after bivouac`, rather than
`Various fixes`. Screenshots help reviewers assess visible UI changes.

## Set up a development environment

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
