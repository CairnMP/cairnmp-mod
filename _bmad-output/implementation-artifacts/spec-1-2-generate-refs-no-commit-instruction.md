---
title: 'Story 1.2: Stop generate-il2cpp-refs.ps1 telling contributors to commit game-refs'
type: 'bugfix'
created: '2026-08-14'
status: 'done'
route: 'one-shot'
---

# Story 1.2: Stop generate-il2cpp-refs.ps1 telling contributors to commit game-refs

## Intent

**Problem:** After a five-minute game launch, `generate-il2cpp-refs.ps1` ended by telling the contributor "You can now commit these files" — the files being proprietary Cairn, Unity and MelonLoader assemblies. That instructed the one action `.gitignore:2`, the README's non-redistribution statement and the AGENTS.md policy all forbid, and it was the last thing on screen at the exact moment someone would act on it.

**Approach:** Replace the instruction with the opposite one, stating why the constraint exists rather than merely asserting it, and put the same constraint in the file header so it is visible to anyone reading the script before running it. Audit confirmed line 97 was the only contradicting line in this script; the equivalent claims found in sibling files are recorded as deferred work rather than fixed in passing.

## Suggested Review Order

- The instruction that was inverted: success, then the constraint and why, then the next step.
  [`generate-il2cpp-refs.ps1:99`](../../scripts/generate-il2cpp-refs.ps1#L99)

- The constraint moved to where it is read first — before a contributor spends five minutes running the script.
  [`generate-il2cpp-refs.ps1:6`](../../scripts/generate-il2cpp-refs.ps1#L6)

- Comments translated to English because `AGENTS.md:30` requires it of any French file you touch.
  [`generate-il2cpp-refs.ps1:2`](../../scripts/generate-il2cpp-refs.ps1#L2)
