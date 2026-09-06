# Maintainer security setup

## Observed state and activation

At the initial inspection on 2026-09-06, the default branch was `develop` and its
branch metadata reported `protected: false`. The existing ruleset **Protect**
(ID `22360860`) was disabled; it targeted the default branch and `production`,
with deletion and force-push restrictions only.

Repository files do not activate administrative settings. Apply the following
through GitHub Settings with an administrator account. The connected integration
used to prepare these changes could not write those settings.

1. Merge the baseline PR after checking its workflows and diff. Let CI run on
   `develop` and confirm the exact check names before requiring them.
2. Edit **Settings → Rules → Rulesets → Protect**, keep the current targets, and
   set enforcement to **Active**. Require a pull request, one independent approval,
   dismissal of stale approvals, approval after the latest push, and resolution
   of review conversations. Keep deletion and force-push restrictions enabled.
3. Require **Shared protocol tests** and **Source generator build**, with the branch
   up to date. Do not require the full solution: it needs private/local game DLLs.
   Add CodeQL as a required check only after its first successful execution.
4. Keep routine bypass access empty. If a solo-maintainer workflow needs emergency
   recovery, deliberately configure a tightly scoped administrator bypass usable
   only through PRs, and document each use. Never silently bypass failed checks.
   One approval requires a second person: an author cannot approve their own PR.
5. CODEOWNERS currently identifies `@yutho-o` (admin access verified). Before
   requiring code-owner approval for every PR, add a second verified write-access
   maintainer so the owner's own changes can be independently approved.

A ruleset definition is provided in
[branch-protection.ruleset.json](branch-protection.ruleset.json) for review or
GitHub's ruleset import. Prefer editing the existing **Protect** ruleset; importing
creates another ruleset and does not update the existing one. The JSON is an
**active configuration proposal**, not evidence that protections are enabled.
It intentionally does not mandate code-owner approval or add bypass actors.

## Actions

In **Settings → Actions → General**:

- Set default workflow token permissions to **Read repository contents and packages**.
- Disable **Allow GitHub Actions to create and approve pull requests**.
- Require approval for workflows from **all outside collaborators**; inspect their
  changes before approving. Use GitHub-hosted runners for public PRs.
- Keep write tokens and secrets unavailable to fork PRs. Never use
  `pull_request_target` to check out or execute contributor-controlled code.
- Limit allowed actions to reviewed sources used by the workflows. Pin every action
  to a full commit SHA and review Dependabot's pin updates.

The CodeQL job requests only the extra `security-events: write` permission needed
for results. Do not grant broad repository write access to CI. Do not publish or
reuse executable PR artifacts in trusted release jobs.

## Security features

In the repository security settings, enable/check:

- Dependency graph, Dependabot alerts, and Dependabot security updates.
- Secret scanning and push protection where available. Resolve any historical
  finding by revoking/rotating the credential first; deleting a file is insufficient.
- Private vulnerability reporting. Verify **Report a vulnerability** is visible
  under Security; SECURITY.md also provides a private email fallback.
- Code scanning using the committed CodeQL workflow (advanced setup). If default
  setup is already enabled, resolve that conflict before enabling the workflow.

CodeQL uses C# `build-mode: none` because public builds lack game reference DLLs.
Missing external assemblies and generated source can reduce analysis accuracy;
a successful scan does not prove complete coverage or absence of vulnerabilities.
The full mod, framework tests, Steam transport, and Unity behavior still need
local validation with legitimately obtained references.

References: [CodeQL build modes](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages),
[Dependabot configuration](https://docs.github.com/code-security/reference/supply-chain-security/dependabot-options-reference).

## Releases and access

Require 2FA for organization members through organization settings; review write
and admin access periodically. Keep at least a documented account recovery path.
Use narrowly scoped, short-lived credentials when release automation is added.

Build releases only from reviewed commits on protected branches, in a trusted
workspace. Run full tests locally, perform a two-client smoke test, confirm
`versions.json` and protocol compatibility, and inspect the package contents.
Never include game reference assemblies, credentials, or private logs. Publish
SHA-256 checksums alongside release archives and record the source commit.
Restrict release tag updates/deletion with a separate tag ruleset once the
project's actual tag naming convention and release operators are confirmed.

The current .NET 6 target is a loader compatibility constraint. Track a tested
upgrade path; changing the SDK in CI does not upgrade the runtime used by players.
