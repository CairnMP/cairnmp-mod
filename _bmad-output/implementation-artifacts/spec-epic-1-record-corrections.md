---
title: 'Epic 1 record corrections: make the artifacts match the tree that shipped'
type: 'chore'
created: '2026-08-14'
status: 'done'
baseline_commit: '1f5e30cec084c4d624fbf67e113b7128e05e77d1'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 1 closed with records that no longer describe the tree. `spec-1-3`'s frontmatter still says `review` while the board says `done`; four references in `spec-1-1` were invalidated by stories 1.3 and 1.4 or by events that happened after it closed; and the governing `SPEC.md` states its CAP-2 bar in words that are ambiguous across branches and, read literally, are now satisfiable by historical quotations of the very defect they describe. Nothing here is a product defect — it is the epic's evidence trail disagreeing with reality, which is what makes the next epic's retrospective unreliable.

**Approach:** Correct the four records in place against anchors re-verified at HEAD, and split the CAP-2 wording by role — CAP-2 keeps its script scope and gains the branches it binds; the repo-wide Success signal is restated to bind files that *instruct* a contributor or agent, excluding historical records. State the amended bar honestly even though it is currently unmet, and append one ledger entry recording that.

## Boundaries & Constraints

**Always:** Re-verify every line anchor against HEAD before writing it — the stale anchors being fixed are themselves cited by line number in the retrospective, and those citations have already drifted. Record each `spec-1-1` edit in its Spec Change Log with the story that invalidated the reference. Keep `sprint-status.yaml`'s existing `action_items` text verbatim: it is the retrospective's frozen record, and only `status` changes.

**Ask First:** Any change to `scripts/package-mod.ps1`, `Directory.Build.props`, `README.md`, or `AGENTS.md`. Any edit inside a `<frozen-after-approval>` block. Adding a CI check or grep guard for the amended CAP-2 bar.

**Never:** Touch product code, `ci.yml`, or any test. Rewrite the retrospective's findings, verdicts, or action-item text — it is a dated record of what was true on 2026-08-14. De-duplicate or edit existing `deferred-work.md` entries; the ledger is append-only. Mark action items 2, 6 or 7 as done — they are genuinely outstanding.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Status correction | `spec-1-3` frontmatter reads `status: 'review'` | Reads `status: 'done'`, matching the board | Verify the post-state by reading the file back; the original failure was a pattern substitution that matched nothing and was never checked |
| Anchor drift | `spec-1-1` cites `Directory.Build.props:46` and csproj `:17` | Cites `:85` and `:28`, with the changed `NoWarn` form described | If an anchor does not match at HEAD, stop and re-derive it rather than trusting this spec's numbers |
| Amended bar is false | Success signal binds instructing files; four such lines still say "versioned" | Bar is written as stated and left failing; one new ledger entry records the gap | Do not soften the wording to make it pass, and do not fix the four lines here |
| Already-satisfied items | Action items 1 and 5 are `open` but done on disk | Marked `done` with the evidence that satisfies them | If `origin/production` or `origin/next/feature-framework` lacks `ci.yml`, leave the item open and report |

</frozen-after-approval>

## Code Map

All anchors below were verified against HEAD (`1f5e30c`) during planning; the line numbers are current, not inherited from the retrospective.

- `_bmad-output/implementation-artifacts/spec-1-3-check-sdk-vulnerabilities.md:5` -- `status: 'review'`. Siblings 1.1, 1.2, 1.4 all read `'done'`. The only edit needed in this file.
- `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md:42` -- Code Map entry citing the test csproj `ProjectReference` at line 17. **Now line 28** — story 1.4's ceiling-comment block occupies 11-18, so line 17 is inside a comment.
- `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md:46` -- cites `Directory.Build.props:46` as `NoWarn=NETSDK1138`. **Now line 85**, and story 1.3 changed the form to `$(NoWarn);NETSDK1138` (append, not overwrite) — so both the anchor and the described shape are wrong.
- `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md:54` -- task text: triggers are `push` on `develop`/`production`. `ci.yml:13,15` now also list `next/feature-framework`, added by `41607e3` and ratified by `decision-q2`. Outside the frozen block, so editable.
- `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md:92` -- "**Not yet exercised:** the `push` trigger. Only `pull_request` has fired." False: **12 push runs, all green** — `develop` 10, `production` 1 (`31793019109`), `next/feature-framework` 1 (`31793458048`); first was `31780031548` on the merge of PR #5. The retrospective's own "8 push-event runs" has itself gone stale.
- `_bmad-output/specs/spec-cairnmp-melonloader-stack-currency/SPEC.md:27` -- CAP-2 success clause. Script-scoped ("no remaining line **of it**") and true today; gains only the branches it binds.
- `_bmad-output/specs/spec-cairnmp-melonloader-stack-currency/SPEC.md:57` -- Success signal, second sentence. This is the actually-ambiguous repo-wide bar that `decision-q3` meant.
- `_bmad-output/implementation-artifacts/sprint-status.yaml:49-107` -- `action_items`. Items keyed `...item-1-...` through `...item-8-...`; each has a `status` field to flip. Items 1 and 5 carry a `resolved_by` decision id already.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- append-only, 22 entries. One entry to append; the last existing entry is the source of the instructing-files caveat.

**Read-only evidence, do not edit:**
- `scripts/package-mod.ps1:27,56` and `Directory.Build.props:16,21` -- the four live lines calling `game-refs/` "versioned"/"versionné", which make the amended Success signal false. Already covered by two existing ledger entries.
- `.github/workflows/ci.yml:13,15` -- the trigger lists that make `spec-1-1:54` stale.
- `_bmad-output/implementation-artifacts/epic-1-retro-2026-08-14.md` -- the source of every item here; a dated record, never rewritten.

## Tasks & Acceptance

**Execution:**
- [x] `spec-1-3-check-sdk-vulnerabilities.md` -- set frontmatter `status` to `'done'`, then read the file back and confirm the new value. Closes action item 3; the point of the item is the verification, not the substitution.
- [x] `spec-1-1-protocol-test-suite-ci.md` -- correct the four references: csproj `ProjectReference` 17→28; `Directory.Build.props` 46→85 with the append form described; the task-text trigger list to include `next/feature-framework`; and replace the "Not yet exercised" paragraph with the observed push-run evidence. Leave the rest of the Code Map alone — `props:44`, `props:17-37`, `PacketCodecTests.cs:108`, `Mod.csproj:171`, `check.sh:21` and every `ci.yml` anchor in Suggested Review Order all verify exact at HEAD.
- [x] `spec-1-1-protocol-test-suite-ci.md` -- add a Spec Change Log entry naming each correction, the story or event that invalidated it, and that the frozen intent block was not touched.
- [x] `SPEC.md` -- amend CAP-2's success clause to name the branches it binds (`develop`, `production`, `next/feature-framework`) while keeping its script scope; restate the Success signal's second sentence to bind files that instruct a contributor or agent, on those three branches, excluding historical records under `_bmad-output/`.
- [x] `deferred-work.md` -- append one entry recording that the amended Success signal is unmet today, naming the four live lines and the two existing entries that cover them, so ledger triage sees the bar and its gap together.
- [x] `sprint-status.yaml` -- set `status: done` on action items 1, 3, 4, 5 and 8. Leave 2, 6 and 7 `open`. Do not alter any item's `action` text.

**Acceptance Criteria:**
- Given the four corrected `spec-1-1` references, when each cited line is read at HEAD, then the file and line contain what the reference claims.
- Given `spec-1-3` and `sprint-status.yaml`, when both are read, then the story's frontmatter status and its `development_status` entry agree.
- Given the amended `SPEC.md`, when CAP-2 and the Success signal are read together, then CAP-2 constrains `generate-il2cpp-refs.ps1` and the Success signal constrains instructing files repo-wide, with no overlap in what each binds and both naming their branches.
- Given the amended Success signal, when the four "versioned" lines are checked, then the bar reads as unmet and the ledger carries an entry saying so — the wording is not weakened to make it pass.
- Given `sprint-status.yaml` after the edit, when action items are listed, then exactly 1, 3, 4, 5 and 8 are `done`, items 2, 6 and 7 are `open`, and every `action` string is byte-identical to before.

## Spec Change Log

**2026-08-14 -- execution notes.** No intent was renegotiated; the frozen block is untouched. Two things worth recording:

- Every anchor this spec predicted verified exact at HEAD (`1f5e30c`), so none had to be re-derived: csproj `:28` is the `ProjectReference`, `Directory.Build.props:85` is the `NoWarn` append line, and the push-run count re-derived from `gh run list` is 12 (`develop` 10, `production` 1, `next/feature-framework` 1, all green) — the number this spec carried, not a stale copy of it.
- `sprint-status.yaml`'s `last_updated` was initially left at `08-14-2026 12:41`, reading the Verification section's "only `status:` lines change" as binding over the usual board-metadata convention. **Review reversed this** (patch P6): the board was reporting content newer than its own timestamp, which every prior board edit had avoided. It now reads `08-14-2026 13:17`, items 3 and 4 carry `resolved_by: "spec-epic-1-record-corrections"` to match the evidence pointers on items 1, 5 and 8, and the header legend documents the `issue:` field. The Verification check was restated accordingly — the invariant is that no `action:` text appears in the diff, not that only `status:` lines do.

**2026-08-14 -- review patches P1-P8 applied; no loopback, intent unchanged.** The substantive find was P1: `spec-1-1`'s "Exactly one run per SHA" evidence bullet asserted repo-wide what only held inside PR #5's window, and after the push-trigger paragraph three lines below it was corrected, the file contradicted itself. Counter-example `7bcf667` (push `31792852280` + pull_request `31792960792`) re-verified at HEAD before writing. P7 also removed a "Met on all three branches as of 2026-08-14" assertion this execution had added to CAP-2 — a success clause states the bar, not its own satisfaction, which is precisely the rot this spec exists to repair. `deferred-work.md` was left untouched at the coordinator's instruction to avoid a concurrent-write race.

## Design Notes

**Why CAP-2 and the Success signal split rather than merge.** `decision-q3` says "amend CAP-2's success clause", but CAP-2 (`SPEC.md:27`) is scoped to one script and is already true — the sentence that proved ambiguous across branches is the Success signal at `:57`. Amending only what q3 literally names would leave the actual defect in place. The two are therefore given distinct jobs: CAP-2 stays the narrow, met, script-level clause; the Success signal carries the durable repo-wide property. The divergence from q3's literal wording is deliberate and recorded here.

**Why the bar is left failing.** `git grep -i "commit these files" origin/production` returns four hits, all inside `_bmad-output/` — the retrospective, `spec-1-2`, and the research report, each quoting the defect they document. A bar phrased as "no file contains the string" is therefore satisfied by its own evidence, which is the flaw the last ledger entry warns about. Binding *instructing* files fixes that, and makes the bar honestly false: `package-mod.ps1:27,56` and `Directory.Build.props:16,21` still describe `game-refs/` as versioned. Fixing those four lines is real work with a French-translation question attached (`AGENTS.md:30`), and it belongs to ledger triage — action item 6 — not to a records correction.

## Verification

**Commands:**
- `grep -n "^status" _bmad-output/implementation-artifacts/spec-1-3-check-sdk-vulnerabilities.md` -- expected: `5:status: 'done'`.
- `sed -n '85p' Directory.Build.props` and `sed -n '28p' CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj` -- expected: the `NoWarn` append line and the `ProjectReference` line, matching what the corrected `spec-1-1` claims.
- `gh run list --workflow ci.yml -R yerayalfageme-glitch/cairnmp-mod --limit 100 --json event,conclusion -q '.[] | "\(.event) \(.conclusion)"' | sort | uniq -c` -- expected: the push-run count in the corrected `spec-1-1` matches the `push success` line; re-derive rather than copying 12 if it has moved.
- `grep -c "^- source_spec" _bmad-output/implementation-artifacts/deferred-work.md` -- expected: **31**, and `git diff --numstat` on that file showing **zero deletions**. Implementation appended 1 entry (22 → 23, the unmet-bar record this spec asked for); step-04 review appended 8 more as `defer` findings (23 → 31). The deletion count is the real check — the ledger is append-only, so a nonzero deletion means an existing entry was edited.
- `git diff -- _bmad-output/implementation-artifacts/sprint-status.yaml` -- expected: **no `action:` text appears in the diff.** That is the invariant that matters: the action-item text is the retrospective's frozen record. The original phrasing of this check ("only `status:` lines change") has been superseded by two authorized additions that also touch the file — `issue: 16/17/18` on items 2, 6 and 7, added on a later instruction to sync the board with GitHub, and the `last_updated` bump plus `resolved_by` on items 3 and 4, added by review patch P6. Expect those; expect nothing else.

**Manual checks:**
- Read `SPEC.md:29` (CAP-2) and `:57-61` (Success signal) together and confirm they do not now assert the same thing at two scopes, and that neither can be satisfied by a file that merely quotes the old script message. Post-patch line numbers; the provenance line added by P7 sits at `SPEC.md:13`.
- Confirm `spec-1-1`'s `<frozen-after-approval>` block (lines 11-37) is unchanged in the diff — `git diff -U0` should show no hunk starting below line 41.

## Suggested Review Order

**The governing contract — read this first, it is the only change with downstream consequence**

- The bar `decision-q3` actually meant: instructing files, three branches, history excluded.
  [`SPEC.md:57`](../specs/spec-cairnmp-melonloader-stack-currency/SPEC.md#L57)

- Shipped knowingly false. The four live lines are named; softening it was refused.
  [`SPEC.md:61`](../specs/spec-cairnmp-melonloader-stack-currency/SPEC.md#L61)

- CAP-2 kept narrow and script-scoped, so the two clauses bind disjoint things.
  [`SPEC.md:29`](../specs/spec-cairnmp-melonloader-stack-currency/SPEC.md#L29)

- Provenance: a contract amended after its epic closed should say so.
  [`SPEC.md:13`](../specs/spec-cairnmp-melonloader-stack-currency/SPEC.md#L13)

**The correction review caught — a record that had begun contradicting itself**

- Was repo-wide and false; now scoped to where it held, with the counter-example.
  [`spec-1-1:100`](spec-1-1-protocol-test-suite-ci.md#L100)

- The paragraph three lines below, whose correction exposed the one above.
  [`spec-1-1:103`](spec-1-1-protocol-test-suite-ci.md#L103)

**Anchors that drifted when later stories moved the lines they cited**

- `NoWarn` moved to :85 and changed from overwrite to append form (story 1.3).
  [`spec-1-1:48`](spec-1-1-protocol-test-suite-ci.md#L48)

- `ProjectReference` moved to :28; the old anchor now lands inside a comment (story 1.4).
  [`spec-1-1:44`](spec-1-1-protocol-test-suite-ci.md#L44)

- `41607e3` changed three things in this task, not just the trigger list.
  [`spec-1-1:56`](spec-1-1-protocol-test-suite-ci.md#L56)

- Why the section mixes a pre-story record with current-at-HEAD anchors.
  [`spec-1-1:41`](spec-1-1-protocol-test-suite-ci.md#L41)

**What each correction claims, and what invalidated it**

- Five corrections, each traced to the story or event that broke it.
  [`spec-1-1:67`](spec-1-1-protocol-test-suite-ci.md#L67)

- The status flip plus the post-state verification that was item 3's actual substance.
  [`spec-1-3:52`](spec-1-3-check-sdk-vulnerabilities.md#L52)

**Deferred findings — the input to action item 6, not fixes**

- The unmet bar recorded where triage will see it alongside the work it implies.
  [`deferred-work.md:94`](deferred-work.md#L94)

- Ledger entry 1 is now known false: `production` is protected, per `decision-q1`.
  [`deferred-work.md:98`](deferred-work.md#L98)

- `SPEC.md` flags one unmet bar and silently asserts two others.
  [`deferred-work.md:102`](deferred-work.md#L102)

**Board — status only, no frozen text touched**

- Items 3 and 4 gained an evidence pointer; 1, 5, 8 already had decision ids.
  [`sprint-status.yaml:69`](sprint-status.yaml#L69)

- The three genuinely-open items, now carrying their GitHub issue numbers.
  [`sprint-status.yaml:61`](sprint-status.yaml#L61)

- New field documented, because the legend covered statuses only.
  [`sprint-status.yaml:24`](sprint-status.yaml#L24)
