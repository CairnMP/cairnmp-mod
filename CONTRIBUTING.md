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
| Native gameplay validation | Two clients and distinct Steam identities |

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

---

Questions are welcome through the repository’s
[issue templates](https://github.com/CairnMP/cairnmp-mod/issues/new/choose).
