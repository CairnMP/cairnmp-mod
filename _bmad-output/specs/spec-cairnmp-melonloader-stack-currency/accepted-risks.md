# Accepted risks and unresolved decisions

Companion to `SPEC.md`. These are recorded rather than scheduled — each is either outside this project's control or blocked on a decision that is not technical.

## Accepted: the mod runs on an unpatched runtime

.NET 6 reached end of support on 2024-11-12. Out-of-support versions receive no security updates.

CairnMP does not ship a runtime — the player installs the .NET 6 Desktop Runtime because MelonLoader requires it. The unpatched component therefore sits on the player's machine, under the game, and neither the mod's target framework nor its build configuration can change which runtime the loader hosts.

**Why it matters more here than for a typical mod:** CairnMP parses untrusted input from remote peers over a Steam relay. Networked packet handling on a runtime that stopped receiving security patches is a genuine attack surface, not a theoretical one.

**The available mitigation is defensive parsing and validation at the packet boundary, not a framework bump.** This is what raises `CairnMultiplayerShared`'s codecs and their tests from tidiness to security-relevant work, and it is the reasoning behind CAP-1. It does not justify touching `<TargetFramework>`.

Status: **accepted, unmitigable at this level.** Re-opens only if MelonLoader itself moves to a supported runtime.

MelonLoader v0.7.3's portable-runtime feature (`<GAME>/dotnet` directories) does not change this. It exists so the loader can run on machines without admin rights, and its documented fallback is to download and execute the .NET 6 runtime installer as before. Whether a newer runtime placed there would load is undefined rather than supported — do not build a plan on it.

## Unresolved: whether stripped Cairn assemblies may be published

The BepInEx ecosystem's answer to CI-without-game-assemblies is a NuGet feed of stripped, publicized game assemblies — a stripped assembly carrying only method signatures and class definitions, no method bodies, with JetBrains Refasmer as the standard tool.

**The legality of that pattern is not established for Cairn.** The claim rests on a single informal aside on a Risk of Rain 2 modding wiki — "Yay! No copyright issues!" — with no citation, no legal reasoning, no licence reference, and no rightsholder permission. It is about a different game, is not offered as a general principle, and is not clearance for Cairn. CairnMP's own README asserts these assemblies cannot be redistributed.

**Treat any move in this direction as a licensing decision first and a technical one second.** SPEC.md routes around it: CAP-1 needs no game assemblies at all.

Status: **open, and deliberately out of scope.** While it stays open, CI for `CairnMultiplayerMod` remains a non-goal.

## Noted: .NET 8 was never a viable migration target

.NET 8 (LTS) support ends November 2026; .NET 9 (STS) ends the same month; .NET 10 (LTS) runs to November 2028. Even if the loader had permitted net8, it would have been near-EOL on arrival. This reinforces the pin rather than softening it, and forecloses "migrate to the current LTS" as a future answer without a loader change first.
