# Contributing to CairnMP

Bug reports, documentation fixes, tests, and focused code changes are welcome.
Follow the [code of conduct](CODE_OF_CONDUCT.md). Report vulnerabilities privately
using [SECURITY.md](SECURITY.md), never as a public bug report.

## Start here

- Search existing issues and pull requests before opening a new one.
- Discuss large features, protocol changes, or architectural changes in an issue first.
- Fork the repository and create a focused branch from `develop`.
- Keep unrelated refactors and formatting changes out of the same pull request.
- Submit your pull request against `develop`; explain the problem, behavior,
  compatibility impact, and what you actually tested.

## Build and test

Use a .NET 10 SDK (which understands the `.slnx` solution) and the .NET 6 runtime
required by the existing test projects. The mod still targets `net6.0` for its
loader; installing a newer SDK does not migrate that runtime target.

Checks that do not require Cairn reference assemblies:

```bash
dotnet test CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj -c Release
dotnet build CairnMultiplayerMod.Generators/CairnMultiplayerMod.Generators.csproj -c Release
```

For the full solution, provide your own reference assemblies as described in the
[README](README.md#reference-assemblies), then run:

```bash
dotnet build CairnMultiplayer.slnx -c Release
dotnet test CairnMultiplayer.slnx -c Release
```

Builds do not deploy to your game by default. To deliberately install a local
build, set your game path and opt in:

```bash
dotnet build CairnMultiplayer.slnx -c Release -p:DeployMod=true -p:CairnDir="D:/Games/Cairn"
```

Do not commit or upload game/Unity reference DLLs. Public CI runs shared protocol
tests and builds the generator; it cannot validate the complete mod or gameplay.
For runtime changes, test with a host and a second client, including joining late,
disconnection/reconnection, and relevant bivouac/save transitions. State clearly
when a scenario has not been tested. Back up saves first.

## Engineering expectations

Follow the surrounding C# style. Add regression tests for behavior changes where
possible, especially malformed packets, authorization checks, and cleanup paths.
Validate remote data before allocating memory or changing game state; preserve
host authority and avoid blocking the Unity update loop. Keep logs useful without
recording credentials or unnecessary player data.

Use `versions.json` as the version source. For incompatible wire changes, update
the protocol version and run `node scripts/sync-versions.js`; describe the
compatibility break in the PR and changelog. Read the feature example in README
and [extension API documentation](docs/multiplayer-api.md) before extending them.

Dependencies, workflows, transport, and release scripts deserve explicit security
review. Workflows must use pinned action commit SHAs, minimum token permissions,
and GitHub-hosted runners for public contributions. Never execute PR code with
repository secrets or a privileged `pull_request_target` workflow.

## License and review

Submit only work you have the right to contribute under this repository's
existing AGPL-3.0 license. Preserve attribution and third-party notices. Do not
include proprietary game content, extracted assets, or credentials.

Maintainers decide scope and merge readiness. A green CI run is necessary but does
not replace review or in-game testing. Do not merge your own changes around a
required independent review. There is no guaranteed review time.
