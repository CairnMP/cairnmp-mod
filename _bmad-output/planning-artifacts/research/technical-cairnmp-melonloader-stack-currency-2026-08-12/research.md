---
title: 'Technical research: CairnMP MelonLoader stack currency'
type: 'technical'
topic: 'CairnMP MelonLoader stack currency'
decision: 'Which CairnMP stack changes are mandatory vs merely possible'
source: 'native run (harness web search + fetch)'
status: complete
preset: 'standard'
validation: 'normal'
red_team: 'off (not run)'
citation_check: 'mechanical passed; semantic verified 2026-08-12 by 3 independent-context verifiers (22/28 clean, 6 corrected)'
claims: { verified: 9, unverified: 1, disputed: 1, total: 11 }
dimensions_completed: 5
created: '2026-08-12'
updated: '2026-08-12'
---

# Technical research: CairnMP MelonLoader stack currency

**Decision this research serves:** which CairnMP stack changes are mandatory vs merely possible — so "change only what is mandatory" resolves to a defensible list rather than a judgment call.

## Executive summary

**The mandatory list is very short, and almost nothing on it is a technology upgrade.**

Of the five areas examined, four turn out to be pinned, moot, or not yours to change. `net6.0` is not technical debt: MelonLoader v0.7.3 — released 2026-05-14, three months before this research and actively maintained — still requires the .NET 6 Desktop Runtime for IL2CPP games [3][4]. Il2CppInterop and HarmonyX are current and healthy, and are **supplied by the loader**, so their versions were never yours to pick [2][15][16]. xUnit v3 requires net8.0 as a floor [19][20], which the net6 pin puts out of reach, confining the project to the deprecated xunit v2 line through no fault of its own [21]. Even .NET 8 was never a viable target: its support ends November 2026, about three months from now [13].

**Three findings drive the answer:**

1. **The runtime is a pin, not a choice.** Every "upgrade the framework" instinct dies here, and correctly so. Changing `<TargetFramework>` would break the mod, not modernise it.
2. **The highest-value work requires no new technology at all.** `CairnMultiplayerShared.Tests` depends only on the protocol project — no game assemblies anywhere in its graph. A CI job running just that test project works today on a stock runner with no secrets and no proprietary files, and it covers exactly the untrusted-input surface that matters most.
3. **That surface matters more than it looks.** The mod parses packets from remote peers on a runtime that stopped receiving security patches on 2024-11-12 [8]. You cannot patch the runtime — it is the player's, and the loader's — so hardening the packet boundary is the available mitigation rather than a nice-to-have.

**Biggest caveat:** the ecosystem's standard answer to CI-without-game-assemblies is publishing *stripped* reference assemblies to a NuGet feed [24][25]. The claim that this is legally acceptable turned out, on verification, to rest on a single informal aside on a Risk of Rain 2 wiki — "Yay! No copyright issues!" — with no citation, no legal reasoning and no rightsholder permission [26]. It is not legal advice, not a publisher statement, and not transferable to Cairn, whose README asserts these assemblies cannot be redistributed. Treat that route as a licensing decision first. The recommendations below deliberately route around it.

**Also surfaced, incidentally:** four places where the repository instructs something that contradicts its own reality. Three were found before this research and two already fixed; the fourth — `generate-il2cpp-refs.ps1:97` telling you to commit `game-refs/` — was found while verifying D5 and is still open.

---

## D1 — What runtime does MelonLoader pin IL2CPP mods to?

**Answer: `net6.0` is a pin, not a choice — and it is current, not stale.**

MelonLoader's newest release is **v0.7.3, published 2026-05-14** [1], corroborated by the NuGet package `LavaGang.MelonLoader` 0.7.3 carrying the same publication date [2]. That is roughly three months before this research, from a project whose recent cadence runs 0.6.6 (2024-11), 0.7.0 (2025-02), 0.7.1 (2025-06), 0.7.2 (2026-03), 0.7.3 (2026-05) [1]. This is an actively maintained loader, not an abandoned one.

That current release still requires the **.NET 6.0 Desktop Runtime** for IL2CPP games [3]. The NuGet package declares **three** dependency groups — `.NETFramework 3.5`, `.NETFramework 4.7.2` and `net6.0` — and it is specifically the `net6.0` group that carries the Il2CppInterop packages (Common, Generator, HarmonySupport, Runtime) [2]; both .NET Framework groups are the Mono-backend surface and carry none of them. IL2CPP modding *is* the net6.0 surface.

Because the loader's README and its NuGet metadata are both published by LavaGang, they are one publisher rather than two. Independent corroboration comes from a community modding tutorial that names the exact runtime build — `runtime-6.0.12-windows-x64-installer` — and the failure mode without it, an `il2cpp_init detour failed` error [4]; and from the BTD-Mod-Helper wiki, which instructs mod authors to "switch your .csproj to target net6.0 instead of net48" for newer MelonLoader versions [5]. The pack's two-source bar for compatibility claims is met.

**What this means for CairnMP.** The `net6.0` target in all four project files is not technical debt. It is the loader's host runtime, and a mod assembly targeting net8.0 could not be loaded by a .NET 6 CoreCLR host. Current third-party templates reinforce this by declining to pin a framework themselves, stating their requirement as ".NET SDK (as per MelonLoader requirements)" [6] — though that is ecosystem convention rather than direct evidence.

**Confidence and caveats.** The core claim is high-confidence. Two lower-confidence notes: no official MelonLoader roadmap toward .NET 8+ surfaced in this run — that is an absence of evidence, not evidence of absence; and v0.7.3's changelog adds "support for .NET Portable directories under `<GAME>/dotnet` and `<GAME>/MelonLoader/Dependencies/dotnet`" plus a "Portable .NET Runtime Fallback" [7], which suggests a game may ship its own runtime. Whether that could ever be a *newer* runtime is unresolved and carried into D2.

**A methodological warning for anyone refreshing this research:** GitHub's HTML release pages were misread by the fetch tooling in this run, reporting every release exactly two years early. The machine-readable `releases.atom` feed is authoritative and was used to settle it [1]. Do not trust HTML release-page dates here.

_Dimension stopped on **coverage** after round 2 — plan questions answered with the two-source bar met._

### Sources

| # | Source | Publisher | Date |
|---|---|---|---|
| 1 | https://github.com/LavaGang/MelonLoader/releases.atom | LavaGang | 2026-05-14 |
| 2 | https://www.nuget.org/packages/LavaGang.MelonLoader/ | NuGet / LavaGang | 2026-05-14 |
| 3 | https://github.com/LavaGang/MelonLoader (README, Requirements) | LavaGang | accessed 2026-08-12 |
| 4 | https://hemisemidemipresent.github.io/btd6-modding-tutorial/ | hemisemidemipresent | accessed 2026-08-12 |
| 5 | https://github.com/gurrenm3/BTD-Mod-Helper/wiki/Switching-to-MelonLoader-0.6.0 | gurrenm3 | accessed 2026-08-12 |
| 6 | https://github.com/k073l/S1MelonModTemplate/blob/master/README.md | k073l | accessed 2026-08-12 |
| 7 | https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3 | LavaGang | 2026-05-14 |

---

## D2 — Is .NET 6 being out of support a real risk here?

**Answer: the risk is real, inherited, and not fixable at the mod level. Two smaller things are actionable.**

.NET 6 reached end of support on **2024-11-12** [8], twenty-one months before this research, confirmed independently by Microsoft's own end-of-support announcement [9]. Out-of-support versions "no longer receive security updates that protect your applications and data" [10]. There is no ambiguity about the status.

The question is who carries that risk. CairnMP does not ship a runtime — the player installs the .NET 6 Desktop Runtime because MelonLoader requires it (D1 [3][4]). So the unpatched component sits on the player's machine, under the game, and neither the mod's target framework nor its build configuration can change which runtime the loader hosts.

**Implication for CairnMP — analysis, not a sourced claim.** The exposure is not merely theoretical for this particular mod, because CairnMP parses untrusted input from remote peers over a Steam relay. Networked packet handling on a runtime that stopped receiving security patches twenty-one months ago is a genuine attack surface. But the mitigation available to you is defensive parsing and validation at the packet boundary, not a framework bump — the framework is not yours to move. This raises the value of hardening `CairnMultiplayerShared`'s codecs and of the tests that cover them; it does not justify touching `<TargetFramework>`.

**The portable-runtime feature does not help.** The question carried out of D1 resolves negatively: MelonLoader v0.7.3's `<GAME>/dotnet` portable directories exist so the loader can run on machines without admin rights — locked-down corporate PCs, cybercafés — and the documented fallback is explicitly "download and execute the **.NET 6** runtime installer as before" [11][12]. The implementation looks for a folder containing "dotnet" and checks for `hostfxr.dll`, without restricting or endorsing a newer runtime, so newer-runtime behaviour is undefined rather than supported. Reported as unverified; do not build a plan on it.

**A migration target would be a moving one anyway.** .NET 8 (LTS) support ends **November 2026** — about three months after this research — while .NET 9 (STS) ends the same month and .NET 10 (LTS) runs to November 2028 [13]. Had net8 been reachable, it would have been near-EOL on arrival. This reinforces the D1 conclusion rather than softening it.

**Actionable outcomes from this dimension:**
1. Set the MSBuild property `CheckSdkVulnerabilities=true`, which emits warning NETSDK1239 when the resolved .NET **SDK** is end of life [14]. Note the distinction: it flags your build SDK, not your target framework, so it will not fire on the net6 target — it guards a different exposure.
2. Treat packet-boundary validation in `CairnMultiplayerShared` as security-relevant work rather than tidiness.

_Dimension stopped on **coverage** after round 1 — the question resolved cleanly and the D1 carry-over closed._

### Sources

| # | Source | Publisher | Date |
|---|---|---|---|
| 8 | https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core | Microsoft | accessed 2026-08-12 |
| 9 | https://devblogs.microsoft.com/dotnet/dotnet-6-end-of-support/ | Microsoft .NET Blog | 2024 |
| 10 | https://learn.microsoft.com/en-us/dotnet/core/releases-and-support | Microsoft Learn | doc updated 2026-06-02 |
| 11 | https://github.com/LavaGang/MelonLoader/pull/1062 | LavaGang / JoShMiQueL | accessed 2026-08-12 |
| 12 | https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3 | LavaGang | 2026-05-14 |
| 13 | https://learn.microsoft.com/en-us/dotnet/core/releases-and-support | Microsoft Learn | doc updated 2026-06-02 |
| 14 | https://learn.microsoft.com/en-us/dotnet/core/releases-and-support | Microsoft Learn | doc updated 2026-06-02 |

---

## D3 — Are Il2CppInterop and HarmonyX current, and are they yours to choose?

**Answer: both are healthy and current, and neither is yours to choose. This dimension yields no work.**

Il2CppInterop.Runtime is at **1.5.3** (2026-06-20) [15], HarmonyX at **2.16.1** (2026-03-30) [16]. Both ship steadily — Il2CppInterop 1.5.0 → 1.5.1 → 1.5.3 across 2025–2026 [15], HarmonyX 2.13 through 2.16.1 over the same span [16]. Both are maintained under the **BepInEx** organisation [16][17], which is worth noticing: the loader is LavaGang's and the interop and patching layers are BepInEx's, so the chain you depend on has two independent maintainers rather than one.

The structural point matters more than the version numbers. MelonLoader's NuGet package declares Il2CppInterop (Common, Generator, HarmonySupport, Runtime) and HarmonyX as dependencies of its **net6.0 group** [2], and v0.7.3 bundles Il2CppInterop at `1.5.1-ci.845` [18] — the release notes state the version but do not themselves describe it as a CI build; that reading is inferred from the `-ci.845` suffix and from NuGet listing 1.5.1 with no `-ci.845` entry. A mod that references `0Harmony.dll` and `Il2CppInterop.Runtime.dll` out of the loader's folder therefore consumes whatever the installed MelonLoader ships. **"Upgrade Il2CppInterop" and "upgrade HarmonyX" are not actions a mod project can take.** The only lever is which MelonLoader the player has.

Il2CppInterop.Runtime itself targets **.NET 6.0** as its primary framework [15], independently corroborating D1's conclusion from a different publisher.

There is a five-week lag between MelonLoader v0.7.3 (2026-05-14) and Il2CppInterop 1.5.3 (2026-06-20), so the current loader necessarily ships slightly-behind interop [15][18]. That is ordinary release lag in a two-maintainer chain, not neglect, and nothing you can or should act on.

**What this means for CairnMP:** nothing to do. The three `<Reference Include>` entries pointing at `$(MelonLoaderNet6Dir)` are correct as written.

_Dimension stopped on **coverage** after round 1. Breaking-change lists between interop versions were deliberately not pursued: the structural finding makes them moot for this decision._

### Sources

| # | Source | Publisher | Date |
|---|---|---|---|
| 15 | https://www.nuget.org/packages/Il2CppInterop.Runtime | NuGet / BepInEx | 2026-06-20 |
| 16 | https://www.nuget.org/packages/HarmonyX | NuGet / BepInEx | 2026-03-30 |
| 17 | https://github.com/BepInEx/Il2CppInterop | BepInEx | accessed 2026-08-12 |
| 18 | https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3 | LavaGang | 2026-05-14 |

---

## D4 — Is xunit v3 reachable, and how far can the test tooling move?

**Answer: xunit v3 is unreachable. The test tooling has a low, precise ceiling — and CairnMP is below it, so two small bumps are available.**

xUnit.net v3 targets ".NET 8 (or later) and/or .NET Framework 4.7.2 (or later)", with `net8.0` documented as "xUnit.net's lowest supported version of .NET" [19]. The NuGet package confirms it from a second angle: xunit.v3 3.2.2 (2026-01-14) "does not support .NET 6.0" [20]. D1 established that MelonLoader pins IL2CPP mods to net6.0. **The two are incompatible — this dimension collapses into D1, exactly as the plan gate anticipated, and is now evidenced rather than guessed.**

The uncomfortable consequence: CairnMP is confined to the xunit **v2** line, and every v2 version is marked **deprecated** on NuGet, with "all future feature work moved onto v3" and only security updates planned for v2 [21]. That is not a consequence of any decision made in this repo. It is inherited from the loader's runtime pin, and it cannot be engineered around while the mod must load into a .NET 6 host.

**What movement actually remains:**

| Package | CairnMP has | Ceiling on net6.0 | Confidence |
|---|---|---|---|
| `xunit` | 2.9.2 (2024-09-27) | **2.9.3** (2025-01-08) [21] | high |
| `Microsoft.NET.Test.Sdk` | 17.11.1 | **17.13.0** [22] | high — maintainer-confirmed |
| `xunit.runner.visualstudio` | 2.8.2 | not established | — |

The Test.Sdk ceiling was confirmed on verification, and the mechanism matters more than the version number. A vstest maintainer states it directly: "NET6 tfm is end-of-life for a while, you should be able to stick with microsoft.net.test.sdk 17.13.0 for a (long) while" [22]. The underlying cause is a TFM bump in the package's shipped assets — 17.13.0 ships `netcoreapp3.1` + `net462`, while 17.14.0 ships `net8.0` + `net462` [22].

**The failure mode is the dangerous part.** 17.14.0 does **not** hard-fail restore on a net6.0 project. It silently falls back to the `net462` asset via `AssetTargetFallback` and then fails at *runtime* with "Could not find testhost" — an error that looks like a broken test setup rather than an unsupported package. A later 17.14.1 added an explicit warning. If you bump past 17.13.0 by accident, expect a confusing runtime failure, not a clear restore error.

One precision note: pinning to 17.13.0 is one of *two* remedies the maintainer offers — the other is moving the project to net8+, which D1 rules out here. So for CairnMP specifically, pinning is the only remedy, though the source itself is less absolute than that.

Separately, Microsoft.NET.Test.Sdk is now at 18.8.1 (2026-07-14). net6.0 does still appear on that package's NuGet page, but only as a *computed* compatibility entry — it is not among the explicitly included target frameworks, which are .NET 8.0, .NET Core 2.0, .NET Standard 2.0 and .NET Framework 4.6.2 [23]. The computed entry is exactly the `AssetTargetFallback` trap described above.

`xunit.runner.visualstudio`'s own net6.0 ceiling was not established in this run and must be checked before any bump, since a mismatched runner is a common cause of silent test-discovery failure.

**What this means for CairnMP.** The available work is two version bumps totalling a handful of characters, bounded above by a ceiling you cannot raise. Whether they are worth doing at all is genuinely marginal: if the v2 line is dormant rather than actively patched, 2.9.2 and 2.9.3 differ by little. This is a case where "change only what is mandatory" plausibly resolves to **change nothing here**.

_Dimension stopped on **coverage** after round 1, with two items carried out as open questions: the runner ceiling, and the exact Test.Sdk boundary version._

### Sources

| # | Source | Publisher | Date |
|---|---|---|---|
| 19 | https://xunit.net/docs/getting-started/v3/getting-started | xUnit.net | doc dated 2026-05-02 |
| 20 | https://www.nuget.org/packages/xunit.v3 | NuGet / xUnit.net | 2026-01-14 |
| 21 | https://www.nuget.org/packages/xunit | NuGet / xUnit.net | 2025-01-08 |
| 22 | harness web search (aggregated; no primary release note located) | — | accessed 2026-08-12 |
| 23 | https://www.nuget.org/packages/Microsoft.NET.Test.Sdk | NuGet / Microsoft | 2026-07-14 |

---

## D5 — How do IL2CPP mod repos run CI without redistributable game assemblies?

**Answer: the ecosystem's answer is a stripped-assembly NuGet feed, which may not be legally available to you. The move that is available today needs no game assemblies at all.**

The BepInEx ecosystem solved this with infrastructure: a web service for "uploading game assemblies to BepInEx NuGet", explicitly "intended for modding communities for uploading publicized and stripped game assemblies" [24]. Mod projects then reference game types from that feed, so developers "only have to bump the version number of the assembly reference when a new game update comes" and "don't have to handle all the assembly stripping / publicizing themselves" [25]. The stated benefit is developer convenience; the source does not frame it as a way to keep DLLs out of source control, and that motivation should not be attributed to it.

The distinction that makes this work is **stripping**. A stripped assembly carries "only the method signatures, class definitions… no method bodies" [26]; JetBrains' **Refasmer** is the standard tool, stripping "method bodies, private class fields etc." and installable as `dotnet tool install -g JetBrains.Refasmer.CliTool` [27]. Community guidance draws the line sharply for the un-stripped case: "If you want to keep method bodies, add -n, but don't upload that dll on github or somewhere else if you do that" [28].

**A hard warning about the legality claim — strengthened after verification.** The wiki's entire treatment of legality is one informal aside: *"how do we publish the game dlls on NuGet, aren't those copyrighted, well, yes! They are. But we are publishing a version of them where only the method signatures, class definitions are in it, no method bodies! Yay! No copyright issues!"* [26]. That is a casual, celebratory assertion — no citation, no legal reasoning, no reference to any licence, and no permission from any publisher or rightsholder. It appears on a Risk of Rain 2 modding wiki, about Risk of Rain 2, and is not offered as a general principle. It is **weaker than even a cautious reading would assume**, and it is emphatically not clearance for Cairn. CairnMP's own README asserts these assemblies cannot be redistributed; that position is yours to hold or revisit on a proper basis. Treat any move in this direction as a licensing decision first and a technical one second.

**Why full-solution CI is genuinely blocked.** Your own `scripts/generate-il2cpp-refs.ps1` settles it: generating the Il2Cpp assemblies requires a local Cairn install, a built CairnLoader from the sibling `cairnmp-launcher` repo, and **launching the actual game** and waiting up to five minutes for it to dump the assemblies. That is not automatable on a CI runner. Without a stripped-assembly feed, `CairnMultiplayerMod` and `CairnMultiplayerMod.Tests` cannot build in CI at all.

**The move that is available now, at zero legal risk.** `CairnMultiplayerShared.Tests` references only `CairnMultiplayerShared` — no game assemblies anywhere in that graph. A CI workflow doing nothing but

```
dotnet test CairnMultiplayerShared.Tests -c Release
```

runs today, on a stock runner, with no secrets and no proprietary files. It covers the packet codecs, command parser, quaternion codec, rope-clip logic, server teleport and extension negotiator — which is precisely the untrusted-input surface that D2 identified as security-relevant on an unpatched runtime. This is the highest value-per-effort item the entire research surfaced, and it requires changing no technology whatsoever.

**A fourth contradiction found while verifying this.** `generate-il2cpp-refs.ps1:97` prints "You can now commit these files and run package-mod.ps1", instructing the user to commit `game-refs/` — which `.gitignore:2` excludes and which the README says cannot be redistributed. An agent or contributor following the script would attempt exactly the thing the project forbids. This should be corrected in the script.

_Dimension stopped on **coverage** after round 1, with one acknowledged gap below._

**Honest gap:** no concrete MelonLoader-specific CI workflow was located to cite as an exemplar — searches returned GitHub topic listings, not workflow files. The recommendation above rests on the structural fact that the Shared test project has no game dependencies (verifiable in your own repo) plus the BepInEx pattern, not on an observed MelonLoader precedent. Reported as a gap rather than papered over.

### Sources

| # | Source | Publisher | Date |
|---|---|---|---|
| 24 | https://github.com/BepInEx/BepInEx.NuGetUpload.Service | BepInEx | accessed 2026-08-12 |
| 25 | https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/ | Risk of Rain 2 modding wiki | accessed 2026-08-12 |
| 26 | https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/ | Risk of Rain 2 modding wiki | accessed 2026-08-12 |
| 27 | https://github.com/JetBrains/Refasmer · https://www.nuget.org/packages/JetBrains.Refasmer/ | JetBrains | accessed 2026-08-12 |
| 28 | https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/ | Risk of Rain 2 modding wiki | accessed 2026-08-12 |

---

## Cross-dimension insights

What only the combination shows:

**The pin cascades.** Each dimension was framed as independent, but D1's runtime pin turned out to be the upstream cause of D3's and D4's answers. Il2CppInterop and HarmonyX are not choices because they are net6.0 dependencies of the loader [2]; xunit v3 is unreachable because it floors at net8.0 [19]. A reader taking the dimensions separately would see three unrelated "no" answers. They are one "no", propagating.

**"Up to date" and "stable" point in opposite directions here.** The most up-to-date test framework (xunit v3) is unreachable, and the most current .NET LTS (10) is unreachable. What remains reachable is a deprecated v2 line on an EOL runtime. This project cannot be simultaneously current and functional, and functional wins — which reframes the original goal: the way to "improve with more stable technologies" is to stop treating version currency as the objective and treat **verification** as the objective instead.

**The security exposure and the CI opportunity are the same surface.** D2 identified packet parsing on an unpatched runtime as the real risk. D5 independently identified the protocol project as the one thing that builds without game assemblies. These are the same code. The single cheapest action available — CI on `CairnMultiplayerShared.Tests` — is also the one that guards the highest-risk surface. That coincidence is the strongest signal in this research.

**Two maintainers, not one.** The dependency chain runs LavaGang (MelonLoader) → BepInEx (Il2CppInterop, HarmonyX). Both are active [1][15][16], but a stall at either end pins you, and you have no lever on either.

## Contrary evidence

**Not gathered.** `red_team` was `off` for this run (config default, confirmed at the plan gate), so no adversarial pass was made against these conclusions. The load-bearing claims were instead held to the pack's two-source bar, and the places where that bar was *not* met are named explicitly in Open questions below.

## Verification pass

A semantic citation check ran on 2026-08-12 across three independent-context verifiers, each given only a claim and its URL — no project context and no access to the reasoning that produced the claim. The mechanical check (`recon_kit.py citations`) had already passed with 0 dangling markers and 0 orphaned rows.

**22 of 28 citations verified clean. Six required correction, and all six have been applied to the text above:**

| [n] | Problem found | Resolution |
|---|---|---|
| 2 | Claimed two declared target frameworks; the package declares **three** dependency groups (netfx3.5, netfx4.7.2, net6.0) | Text corrected. The Il2CppInterop-only-under-net6.0 finding was confirmed exactly |
| 7 | A "verbatim" changelog quote was silently truncated, omitting `<GAME>/MelonLoader/Dependencies/dotnet` | Full quote restored |
| 18 | "a CI build rather than the tagged release" was **inference**, not something the release notes say | Relabelled as inference with its basis shown |
| 22 | Rated medium on a single aggregated search result | **Upgraded to high** — verifier located a vstest maintainer statement and the underlying TFM asset change, plus the silent-fallback failure mode. Open question 2 closed |
| 23 | Claimed net6.0 was "absent from the compatibility list"; it is present as a *computed* entry | Corrected to "not an explicitly included target framework" — which turns out to be the same `AssetTargetFallback` mechanism behind [22] |
| 25, 26 | [25] attributed a source-control motive the page never states. [26] overstated an already-weak legality claim | Both corrected; [26] downgraded to **low** with the actual wording quoted |

**Two findings the verifiers added that no round had surfaced:**

1. The Test.Sdk failure mode — 17.14.0 restores successfully on net6.0 and fails later at runtime with "Could not find testhost" — which is materially more useful than the version number alone, because it names the symptom you would otherwise misdiagnose.
2. Independent confirmation of this run's date hazard: a verifier's fetch of the v0.7.3 tag page rendered "May 14, **2025**", a third distinct wrong year for the same release. The GitHub API (`published_at: 2026-05-14T20:20:01Z`) settles it. **Do not read release dates from GitHub HTML in this ecosystem** — use the API or the atom feed.

**One caveat on the atom feed itself:** it exposes `<updated>`, not original publication time. Versions 0.6.4, 0.6.5 and 0.6.6 carry near-identical 2024-11-24 stamps minutes apart, which indicates a bulk edit rather than three releases in seven minutes. The v0.7.3 date is API-confirmed and unaffected; older cadence entries are approximate.

No finding was reversed by verification. Two were strengthened, four were tightened, and one open question closed.

## Recommendations

Each is bound to the decision — mandatory vs merely possible — and names its confidence basis.

**Do now (high confidence, no technology change):**

1. **Add CI running `dotnet test CairnMultiplayerShared.Tests -c Release`.** Works today on a stock runner with no game assemblies and no secrets — verifiable in your own repo, and consistent with the BepInEx-ecosystem pattern for projects that cannot ship game refs [24][25]. Guards the packet-codec surface that D2 flags as security-relevant. *Confidence: high on feasibility; the absence of a MelonLoader CI exemplar (Open question 3) does not weaken it, since the claim rests on your own project graph.*
2. **Fix `generate-il2cpp-refs.ps1:97`,** which instructs committing `game-refs/` against both `.gitignore:2` and the README. *Confidence: high — verified directly in the repo.*
3. **Leave `<TargetFramework>net6.0</TargetFramework>` alone,** in all four projects. *Confidence: high — two independent publishers [3][4], corroborated by Il2CppInterop's own net6.0 targeting [15].*

**Consider (medium confidence, marginal value):**

4. **`xunit` 2.9.2 → 2.9.3** [21] and **`Microsoft.NET.Test.Sdk` 17.11.1 → 17.13.0** [22]. Both are ceilings imposed by the net6 pin, not steps toward currency. *Confidence: high for both — the Test.Sdk ceiling was upgraded from medium during the verification pass, which located a vstest maintainer statement and the underlying TFM change.* **Do not exceed 17.13.0:** 17.14.0 will restore without error and then fail at runtime with "Could not find testhost", because net6.0 survives only as a computed `AssetTargetFallback` entry [22][23]. Establish `xunit.runner.visualstudio`'s own ceiling first (Open question 1).
5. **Set `CheckSdkVulnerabilities=true`** to get NETSDK1239 when the build **SDK** goes end-of-life [14] — note this guards your SDK, not your target framework, so it will not fire on net6.

**Do not do:**

6. **Do not attempt xunit v3.** Floored at net8.0 [19][20]; unreachable while the loader pins net6.
7. **Do not publish stripped Cairn assemblies to a public feed on the strength of this research.** The supporting claim is a community wiki about a different game [26], explicitly recorded as disputed. That is a licensing decision requiring its own basis.

**Feeds (per the technical pack):** architecture spine — the net6 pin and loader-supplied dependency chain are hard operational constraints, not preferences. Roadmap risk — the EOL runtime is an accepted, unmitigable risk to be recorded rather than scheduled.

## Open questions

| # | Question | What it would take |
|---|---|---|
| 1 | `xunit.runner.visualstudio`'s net6.0 ceiling | One NuGet compatibility check; blocks recommendation 4 |
| 2 | ~~Exact Microsoft.NET.Test.Sdk version that dropped net6.0~~ | **Resolved during verification** — 17.14.0 bumped shipped assets from `netcoreapp3.1`+`net462` to `net8.0`+`net462`; maintainer confirms 17.13.0 as the stopping point [22]. No official release note exists; cite the issue |
| 3 | A real MelonLoader IL2CPP CI workflow to use as an exemplar | Searching mod repositories directly rather than via topic listings |
| 4 | Whether a net8 `hostfxr.dll` in a portable `<GAME>/dotnet` folder actually loads | Reading MelonLoader's runtime-resolution source, or testing it |
| 5 | Whether the xunit v2 line has had any security release since 2.9.3 (2025-01-08) | Checking the v2 branch's release history; decides if recommendation 4 is worth doing at all |
| 6 | Whether any post-EOL .NET 6 CVE realistically affects a client game process | A CVE sweep against .NET 6 since 2024-11-12 |

## Source appendix

| [n] | Supports | Publisher | Pub date | Accessed | Confidence |
|---|---|---|---|---|---|
| 1 | MelonLoader v0.7.3 release date and cadence | [LavaGang (atom feed)](https://github.com/LavaGang/MelonLoader/releases.atom) | 2026-05-14 | 2026-08-12 | high |
| 2 | Package targets net6.0/netfx3.5; Il2CppInterop + HarmonyX are net6.0 dependencies | [NuGet / LavaGang](https://www.nuget.org/packages/LavaGang.MelonLoader/) | 2026-05-14 | 2026-08-12 | high |
| 3 | IL2CPP requires .NET 6.0 Desktop Runtime | [LavaGang README](https://github.com/LavaGang/MelonLoader) | — | 2026-08-12 | high |
| 4 | Independent confirmation of the .NET 6 requirement and its failure mode | [hemisemidemipresent](https://hemisemidemipresent.github.io/btd6-modding-tutorial/) | — | 2026-08-12 | high |
| 5 | csproj should target net6.0 for newer MelonLoader | [gurrenm3](https://github.com/gurrenm3/BTD-Mod-Helper/wiki/Switching-to-MelonLoader-0.6.0) | — | 2026-08-12 | medium |
| 6 | Templates defer to "MelonLoader requirements" | [k073l](https://github.com/k073l/S1MelonModTemplate/blob/master/README.md) | — | 2026-08-12 | medium |
| 7 | v0.7.3 changelog: Il2CppInterop 1.5.1-ci.845, portable runtime | [LavaGang](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3) | 2026-05-14 | 2026-08-12 | high |
| 8 | .NET 6 end of support 2024-11-12 | [Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | — | 2026-08-12 | high |
| 9 | Independent confirmation of .NET 6 EOL | [Microsoft .NET Blog](https://devblogs.microsoft.com/dotnet/dotnet-6-end-of-support/) | 2024 | 2026-08-12 | high |
| 10 | Out-of-support versions receive no security updates | [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) | 2026-06-02 | 2026-08-12 | high |
| 11 | Portable runtime targets .NET 6; use case is admin-less machines | [LavaGang / JoShMiQueL](https://github.com/LavaGang/MelonLoader/pull/1062) | — | 2026-08-12 | high |
| 12 | v0.7.3 portable-directory changelog entries | [LavaGang](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3) | 2026-05-14 | 2026-08-12 | high |
| 13 | .NET 10 LTS to Nov 2028; 9 and 8 to Nov 2026 | [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) | 2026-06-02 | 2026-08-12 | high |
| 14 | `CheckSdkVulnerabilities` emits NETSDK1239 | [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) | 2026-06-02 | 2026-08-12 | high |
| 15 | Il2CppInterop.Runtime 1.5.3, targets net6.0 | [NuGet / BepInEx](https://www.nuget.org/packages/Il2CppInterop.Runtime) | 2026-06-20 | 2026-08-12 | high |
| 16 | HarmonyX 2.16.1, BepInEx-maintained, cadence | [NuGet / BepInEx](https://www.nuget.org/packages/HarmonyX) | 2026-03-30 | 2026-08-12 | high |
| 17 | Il2CppInterop maintained under BepInEx | [BepInEx](https://github.com/BepInEx/Il2CppInterop) | — | 2026-08-12 | high |
| 18 | v0.7.3 bundles Il2CppInterop 1.5.1-ci.845 | [LavaGang](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3) | 2026-05-14 | 2026-08-12 | high |
| 19 | xUnit v3 floors at net8.0 | [xUnit.net](https://xunit.net/docs/getting-started/v3/getting-started) | 2026-05-02 | 2026-08-12 | high |
| 20 | xunit.v3 3.2.2 does not support net6.0 | [NuGet / xUnit.net](https://www.nuget.org/packages/xunit.v3) | 2026-01-14 | 2026-08-12 | high |
| 21 | xunit v2 latest 2.9.3; v2 line deprecated, security-only | [NuGet / xUnit.net](https://www.nuget.org/packages/xunit) | 2025-01-08 | 2026-08-12 | high |
| 22 | Test.Sdk 17.14.0 dropped net6.0; 17.13.0 the ceiling; silent AssetTargetFallback then runtime failure | [microsoft/vstest issue #15069](https://github.com/microsoft/vstest/issues/15069) (maintainer `nohwnd`) + NuGet asset metadata | 2025-05-20 | 2026-08-12 | high |
| 23 | Test.Sdk 18.8.1; net6.0 present only as a *computed* entry, not an explicitly included target framework | [NuGet / Microsoft](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | 2026-07-14 | 2026-08-12 | high |
| 24 | BepInEx stripped/publicized assembly upload service | [BepInEx](https://github.com/BepInEx/BepInEx.NuGetUpload.Service) | — | 2026-08-12 | high |
| 25 | Mods consume stripped/publicized assemblies from the BepInEx NuGet feed, bumping a version on game updates. *Source motive is developer convenience; it says nothing about source control* | [Risk of Rain 2 modding wiki](https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/) | — | 2026-08-12 | medium |
| 26 | Stripped = signatures and class definitions, no method bodies (**backed**). That this is *legal* rests on one informal aside — "Yay! No copyright issues!" — with no citation, reasoning, licence reference or rightsholder permission, about one game | [Risk of Rain 2 modding wiki](https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/) | — | 2026-08-12 | **low, disputed** |
| 27 | Refasmer strips method bodies and private fields | [JetBrains](https://github.com/JetBrains/Refasmer) | — | 2026-08-12 | high |
| 28 | Warning against uploading un-stripped DLLs | [Risk of Rain 2 modding wiki](https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/) | — | 2026-08-12 | medium |

## Staleness map

Computed via `recon_kit.py staleness` against the technical pack's freshness bars (version and compatibility 1 month, ecosystem 6 months, patterns 24 months). Re-check dates, soonest first:

| Re-check | Claim | Class | Status |
|---|---|---|---|
| 2026-04-30 | HarmonyX 2.16.1 is latest | version | **overdue** |
| 2026-06-02 | xunit v3 floors at net8.0 | compatibility | **overdue** |
| 2026-06-14 | MelonLoader v0.7.3 is latest | version | **overdue** |
| 2026-06-14 | Interop/Harmony are loader-supplied | compatibility | **overdue** |
| 2026-07-02 | Current supported .NET versions | version | **overdue** |
| 2026-07-20 | Il2CppInterop 1.5.3 is latest | version | **overdue** |
| 2026-09-12 | .NET 6 requirement for IL2CPP | compatibility | fresh |
| 2026-09-12 | Portable runtime does not document net8 | compatibility | fresh |
| 2026-09-12 | Test.Sdk net6 ceiling | compatibility | fresh |
| 2026-09-30 | BepInEx cadence healthy | ecosystem | fresh |
| 2028-08-12 | Stripped-assembly NuGet pattern | pattern | fresh |

**Reading this honestly:** the "overdue" flags are computed from each artifact's *publication* date against a one-month window, not from when it was checked — every one of these was verified directly on 2026-08-12. What the flags actually mean is that "X is the latest version" claims decay fast and a newer release could appear at any time. They are a re-check cadence, not errors.

**One mechanical artifact to ignore:** the tool computes an earliest re-check of 2024-12-12 for the .NET 6 end-of-support date. An EOL date that has already passed is a historical fact and will not change. That row is an artifact of classing it as `version`, not a work item.

**Earliest genuine re-check: 2026-04-30** (HarmonyX), though in practice a single pass over the five package pages refreshes every version claim at once — a Refresh on this run folder is a ten-minute job.
