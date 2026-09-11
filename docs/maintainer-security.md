# Maintainer Security Setup

> [!IMPORTANT]
> Repository files cannot activate GitHub administrative settings. A repository
> administrator must apply and verify the controls below in GitHub Settings.

## Contents

- [Current observed state](#current-observed-state)
- [Protect branches](#protect-branches)
- [Harden GitHub Actions](#harden-github-actions)
- [Enable security features](#enable-security-features)
- [Secure releases and access](#secure-releases-and-access)
- [Final verification checklist](#final-verification-checklist)

## Current observed state

The following state was observed on **2026-09-06**:

| Control | Observed state |
| --- | --- |
| Default branch | `develop` |
| Default branch protected | ❌ No |
| Ruleset | `Protect` (ID `22360860`) |
| Ruleset enforcement | Disabled |
| Ruleset targets | Default branch and `production` |
| Existing restrictions | Branch deletion and force push only |

The integration used during the inspection could not modify administrative
settings. Re-check the live settings before acting; the table is an observation,
not a guarantee of current state.

## Protect branches

1. Merge the baseline pull request after reviewing its workflows and diff.
2. Let CI run on `develop` and record the exact check names.
3. Open **Settings → Rules → Rulesets → Protect**.
4. Keep the existing targets and set enforcement to **Active**.
5. Require all of the following:
   - a pull request;
   - one independent approval;
   - dismissal of stale approvals;
   - approval after the latest push;
   - resolution of review conversations;
   - the branch to be up to date before merging;
   - **Shared protocol tests** and **Source generator build**.
6. Keep branch deletion and force-push restrictions enabled.

> [!WARNING]
> Do not require the full solution in public CI: it needs proprietary game DLLs.
> Require CodeQL only after its first successful run.

Keep routine bypass access empty. If a solo-maintainer emergency path is needed,
configure a narrowly scoped administrator bypass usable only through pull
requests, and document every use. Never silently bypass failed checks.

One approval requires a second person; authors cannot approve their own pull
requests. `CODEOWNERS` currently identifies `@yutho-o`, whose administrator access
was verified. Add a second verified maintainer with write access before requiring
code-owner approval on every pull request.

### Ruleset proposal

[`branch-protection.ruleset.json`](branch-protection.ruleset.json) contains a
reviewable active configuration proposal that can be imported into GitHub.

> [!CAUTION]
> Importing creates another ruleset; it does not update `Protect`. Prefer editing
> the existing ruleset. The JSON does not prove that protection is enabled, and
> intentionally includes neither code-owner approval nor bypass actors.

## Harden GitHub Actions

Under **Settings → Actions → General**:

- set default workflow permissions to **Read repository contents and packages**;
- disable **Allow GitHub Actions to create and approve pull requests**;
- require approval for workflows from **all outside collaborators**;
- use GitHub-hosted runners for public pull requests;
- keep write tokens and secrets unavailable to fork pull requests;
- restrict actions to reviewed sources and pin each action to a full commit SHA.

Never use `pull_request_target` to check out or execute contributor-controlled
code. Review Dependabot’s pin updates before merging them.

The CodeQL job requests only `security-events: write`, which it needs to publish
results. Do not grant broad repository write access to CI, and do not publish or
reuse executable pull-request artifacts in trusted release jobs.

## Enable security features

Verify these controls in the repository security settings:

- [ ] Dependency graph enabled.
- [ ] Dependabot alerts enabled.
- [ ] Dependabot security updates enabled.
- [ ] Secret scanning enabled where available.
- [ ] Push protection enabled where available.
- [ ] Private vulnerability reporting enabled.
- [ ] **Report a vulnerability** visible under **Security**.
- [ ] CodeQL advanced setup enabled through the committed workflow.

If default CodeQL setup is already enabled, resolve the conflict before enabling
the workflow. The workflow uses C# `build-mode: none` because public builds lack
game reference DLLs.

> [!NOTE]
> Missing external assemblies and generated sources can reduce CodeQL accuracy.
> A successful scan does not prove the absence of vulnerabilities. The full mod,
> framework tests, Steam transport, and Unity behavior still require local
> validation with legitimately obtained references.

If secret scanning finds a credential, revoke or rotate it first. Deleting the
file alone is not remediation.

References: [CodeQL build modes](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages) ·
[Dependabot options](https://docs.github.com/code-security/reference/supply-chain-security/dependabot-options-reference)

## Secure releases and access

- Require 2FA for organization members.
- Review write and administrator access periodically.
- Keep at least one documented account-recovery path.
- Use narrowly scoped, short-lived credentials for release automation.
- Build only from reviewed commits on protected branches and in a trusted
  workspace.
- Run the full local test suite and a two-client smoke test.
- Verify `versions.json`, protocol compatibility, and package contents.
- Exclude game references, credentials, and private logs.
- Publish SHA-256 checksums with release archives and record the source commit.
- Protect release tags with a dedicated tag ruleset after confirming the project’s
  tag convention and release operators.

The current .NET 6 target is a loader compatibility constraint. Track a tested
upgrade path; changing the SDK in CI does not change the runtime used by players.

## Final verification checklist

- [ ] `Protect` is active on every intended branch.
- [ ] Required checks match the exact successful workflow check names.
- [ ] Pull requests require an independent, current approval.
- [ ] Force push and deletion restrictions are active.
- [ ] Default workflow permissions are read-only.
- [ ] Fork pull requests receive neither secrets nor write tokens.
- [ ] Security features and private reporting are visible and working.
- [ ] Release access, checksums, and recovery procedures are documented.
