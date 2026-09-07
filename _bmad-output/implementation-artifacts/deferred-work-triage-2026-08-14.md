---
title: 'Deferred-work ledger triage'
date: '2026-08-14'
action_item: 'epic-1-retro-item-6-triage-the-20-entry-deferred-work-ledger'
issue: 17
ledger: '_bmad-output/implementation-artifacts/deferred-work.md'
entries_triaged: 31
verified_against: '1f5e30c + PR #19 (chore/refresh-epic-1-records)'
---

# Deferred-work ledger triage

Every one of the 31 entries was re-checked against the tree, the GitHub API and the
run history before being routed. The ledger is append-only, so nothing below edits an
entry; this file is the partition, and the ledger stays the raw record.

Entries are referenced by their position in `deferred-work.md` (E1 = first entry).

## New finding, raised by the triage itself

**`next/feature-framework` is a CI trigger branch with no branch protection.**

```
develop                 checks=["Protocol test suite"]  enforce_admins=true
production              checks=["Protocol test suite"]  enforce_admins=true
next/feature-framework  404 Branch not protected
```

E1 claimed no branch was protected; the correction appended after it (E24) said both
long-lived branches are. Both statements missed the third trigger branch. `ci.yml:13,15`
list `next/feature-framework` under both `push` and `pull_request`, and `decision-q2`
deliberately kept it and gave it the workflow — so it runs CI, and a red run there stops
nothing. This is the residue of E1 that survives the correction.

## Bundles — work worth doing

### B1 — Stop the repo calling `game-refs/` versioned *(closes E7, E8, E20, E23)*

Four live lines, all verified present: `scripts/package-mod.ps1:27,56` ("versioned") and
`Directory.Build.props:16,21` ("versionné"), against `.gitignore:2` which excludes the
directory.

This is the highest-value bundle because it is the **only thing standing between the repo
and a stated SPEC bar**. The amended Success signal binds files that instruct, and these
four are the reason it reads unmet today.

The three entries are one job, not three: E20 (the file ships half-translated) cannot be
resolved without restating the header's "versionné" claim, which is E8. `AGENTS.md:30`
asks that French files be translated when touched, so B1 either translates
`Directory.Build.props` wholesale or documents why it stays bilingual. **Needs a human
decision — see D6.**

### B2 — CI guards that fit the existing constraint *(closes E9, E12, E13, E16; E18 rides along)*

Every one of these is roughly a one-line addition to the job that already exists, needs no
game assemblies and no secrets, and answers the epic's own premise that verification is
the reachable mitigation. The retrospective named three of them as the ledger's sharpest.

| Entry | Guard | Why it cannot be caught today |
|---|---|---|
| E13 | Floor on the reported `Passed:` count | `ci.yml:51` reads only the exit code, so *partial* test-discovery collapse is green |
| E16 | `dotnet msbuild -getProperty:CheckSdkVulnerabilities` probe | The property emits nothing on any shipping SDK, so a typo is byte-identical and green |
| E9 | Grep that no *instructing* file tells anyone to commit `game-refs/` | Nothing guards CAP-2; the corrected message is protected only by convention |
| E12 | Byte-compare the two test csprojs' version blocks | Parity is preserved only by whoever edits next remembering the other file |

Two constraints carried in from the ledger. E9's grep **must scope itself to instructing
files** or it fires on the retrospective and `spec-1-2`, which quote the old message in
order to document it — that is E21, and it is why the SPEC bar was worded that way. E12
is preventive, not corrective: both csprojs currently carry identical pins
(`[17.13.0]`, `[2.9.3]`, `[3.0.2]`), verified.

E18 (an upstream `NoWarn` could silently cancel the CS8600-CS8604 promotion) has zero
impact today — no such upstream setter exists — but an assertion for it is the same
mechanism as E16's probe, so it is nearly free if B2 lands. Not worth its own work.

### B3 — CI observability and supply chain *(closes E4, E5, E6, E22)*

- **E6** — no `.trx` logger, no uploaded artifact, no job summary. Confirmed: zero matches
  for any of them in `ci.yml`. A test-host launch failure leaves nothing durable once logs age out.
- **E4** — `actions/checkout@v7` and `actions/setup-dotnet@v6` are mutable major tags, and
  `.github/` holds only `FUNDING.yml` and `workflows/` — no `dependabot.yml`. For an epic
  premised on security posture, an unpinned third-party action sits in the verification path.
- **E5 + E22 are one defect.** E22 corrects E22's own subject: the precondition in E5 (a push
  *while* a promotion PR is open) is not needed. Any SHA already pushed to a trigger branch
  collects a second run the moment a PR is opened from it — observed on PR #13
  (`31792852280` push, `31792960792` pull_request, same SHA). Cost is duplicate runner
  minutes, not correctness. Fold at implementation; do not fix twice.

### B4 — Contributor documentation *(closes E3, E10, E11)*

All three verified live. `README.md` mentions `generate-il2cpp-refs.ps1` **zero** times and
has no CI badge or Actions mention; `AGENTS.md:26` still presents `bash scripts/check.sh`
as the pre-push gate without noting that CI now exists at a deliberately narrower scope;
`package-mod.ps1` throws when the MelonLoader DLLs are absent, which
`generate-il2cpp-refs.ps1` never produces — so its "You can now run package-mod.ps1"
handoff can dead-end a contributor after a five-minute game launch. (Partially softened by
a `MELON_LOADER_NET6_DIR` fallback, which the handoff message also does not mention.)

One coherent pass over `README.md` and `AGENTS.md`. Touching `AGENTS.md` is Ask-First territory.

### B5 — Records format, before epic 2 accumulates another twenty *(closes E29, E30; feeds #18)*

- **E30** — ledger entries carry no `id`, so refinements restate their subject instead of
  linking to it. Visible three times already (E22→E5, E23→E7/E8, E24→E1). Adding `id:` to
  *new* entries costs nothing and leaves the append-only rule intact.
- **E29** — `resolved_by` is a scalar, but action item 1 was resolved by two decisions
  (`decision-q1` and `decision-q4`); the board silently records one.
- **E31's lesson** belongs here rather than as work: records meant to outlive the files they
  cite should cite by heading or quoted text, not by line number. The retrospective's
  `spec-1-1:92` citation broke the moment PR #19 inserted lines above it — the same failure
  mode that retro raised as finding S4. Fold into the convention work in **#18**.

### B6 — Finish the branch-naming pass in `SPEC.md` *(closes E26)*

CAP-1's success clause is still branch-unqualified while CAP-2 and the Success signal now
name their three branches. Retro finding S1's lesson was general, not CAP-2-specific.
One line.

## Already resolved — close without work

| Entry | Why |
|---|---|
| **E1** | Superseded. Both long-lived branches are protected with the required check and `enforce_admins`. The surviving residue is `next/feature-framework` (see above), not the original claim. |
| **E15** | Obsolete. `sprint-status.yaml` conflicts arose from four stories on parallel branches; epic 1 is closed and no such branches remain. The underlying policy question survives — see **D5**. |
| **E21** | Resolved by PR #19. The Success signal now excludes historical records explicitly, so the bar is no longer satisfiable by its own evidence. Its second half (the guard must scope to instructing files) is carried into **B2**. |
| **E24** | Resolved by this triage, which verified the protection state directly. |
| **E23** | Not separate work — it is the bar that **B1** clears. Closes with B1. |
| **E22** | Not separate work — folds into **B3**'s E5. |

## Blocked

- **E14 — `CairnMultiplayerMod.Tests` pins ship unexecuted.** Blocked by the epic's own
  binding constraint: that project needs `game-refs/`, which cannot reach a runner.
  Confirmed nothing under `.github/workflows/` or `scripts/` references it.
  **Partial option worth weighing:** `dotnet restore CairnMultiplayerMod.Tests` needs no game
  assemblies and would catch an unresolvable typo like `[2.9.13]` — but *not* a ceiling breach
  like `[17.14.0]`, which restores cleanly and fails only at runtime. That is the more dangerous
  of the two failure modes, so a restore-only check buys less than it appears to. See **D7**.
- **E19 — re-check `CheckSdkVulnerabilities` at .NET 11 GA.** Waiting on an external event.
  The property, its task and NETSDK1238/1239/1240 exist only in dotnet/sdk `main`; if any is
  renamed or dropped before GA, `Directory.Build.props:74` becomes dead text that still reads
  as an armed tripwire. Needs a scheduled reminder, not code.

## Skip

- **E18** — zero impact today and a free rider on B2's E16 guard. Not worth its own work.
- **E31** — the specific fix means editing a dated retrospective; the general lesson is
  already captured in B5 and #18. Skip the edit, keep the lesson.

## Human decisions

These cannot be resolved from the codebase. Each blocks or reshapes a bundle above.

- **D1 (E2)** — `epic-1-context.md` and the spec Intent both say "every push", but `push` is
  filtered to three branches so a topic branch with no PR runs nothing. Which gives way, the
  wording or the triggers? In practice every change reaches `develop` via PR, so coverage is
  complete along the normal path.
- **D2 (new)** — Protect `next/feature-framework`, or drop it from the trigger lists? Leaving
  it as-is means a trigger branch where a red run blocks nothing. `decision-q2` deliberately
  kept the branch, so dropping the triggers would partly undo that.
- **D3 (E17)** — `WarningsAsErrors` at `Directory.Build.props:44` is still the overwrite form
  that story 1.3 fixed one line below in `NoWarn`. A curated error-promotion list is more
  plausibly deliberate than a curated suppression list, which is why this was never a reflex fix.
- **D4 (E25)** — `SPEC.md` now annotates one unmet bar and silently asserts two others: CAP-3
  is unmeetable by any shipping SDK, and CAP-1's red proof is superseded evidence (#16).
  Annotate all three consistently, or move capability status out of the contract into the
  retrospective's verdict table, which already carries it.
- **D5 (E15 residue)** — Should the board be updated only on `develop` after merge, or should
  parallel-branch conflicts be resolved by hand each time? Will recur the moment epic 2 runs
  stories in parallel.
- **D6 (B1)** — Translate `Directory.Build.props` wholesale per `AGENTS.md:30`, or fix only the
  two "versionné" claims and record why the file stays bilingual?
- **D7 (E14)** — Accept a restore-only check on `CairnMultiplayerMod.Tests` knowing it misses
  the ceiling-breach case, or leave the project unverified and rely on the recorded ceilings?
- **D8 (E27)** — The branch list is hardcoded in three places (CAP-2, the Success signal,
  `ci.yml:13,15`) with no rule keeping them in sync. Bind the spec clauses to
  `ci.yml`'s `push.branches` instead, or keep the explicit enumeration `decision-q3` chose?
- **D9 (E28)** — The Success signal excludes all of `_bmad-output/`, but that tree holds specs
  and epic-context files that agents read as instructions, not only history. Tighten the
  exclusion to records for closed work?

## Suggested order

**B1** first — it is the only bundle clearing a stated SPEC bar, and D6 is a small decision.
**B2** next: highest value per line of change, and it converts three of the retrospective's
sharpest findings into machine checks. Both are natural epic 2 material.

B3, B4, B5, B6 are independent and can land in any order. B5 is worth doing before epic 2
starts rather than after, because its whole value is preventing the next twenty entries from
repeating this one's problems.
