# Digest — D4 xunit v3 and test-tooling currency — round 1

Question owned: is xunit v3 reachable from a net6.0 target, and how far can the test tooling move?
Accessed: 2026-08-12

## Claims

| # | Claim | Source | Publisher | Pub date | Confidence | Class |
|---|---|---|---|---|---|---|
| 27 | xUnit.net v3 targets ".NET 8 (or later) and/or .NET Framework 4.7.2 (or later)"; `net8.0` is "xUnit.net's lowest supported version of .NET" | https://xunit.net/docs/getting-started/v3/getting-started | xUnit.net | doc dated 2026-05-02 | high | compatibility |
| 28 | xunit.v3 latest stable is **3.2.2** (2026-01-14); package "does not support .NET 6.0"; minimum is net8.0 / net472. Prereleases up to 4.0.0-pre.154 exist | https://www.nuget.org/packages/xunit.v3 | NuGet / xUnit.net | 2026-01-14 | high | compatibility |
| 29 | xunit (v2 line) latest is **2.9.3**, published 2025-01-08 | https://www.nuget.org/packages/xunit | NuGet / xUnit.net | 2025-01-08 | high | version |
| 30 | **All xunit v2 versions are marked deprecated on NuGet**; "all future feature work has moved onto v3"; only security updates are planned for the v2 line | https://www.nuget.org/packages/xunit | NuGet / xUnit.net | accessed 2026-08-12 | high | ecosystem |
| 31 | Microsoft.NET.Test.Sdk latest is **18.8.1** (2026-07-14); net6.0 is not in its finalised compatibility list | https://www.nuget.org/packages/Microsoft.NET.Test.Sdk | NuGet / Microsoft | 2026-07-14 | high | version |
| 32 | Microsoft.NET.Test.Sdk **17.14.0 dropped net6.0 support**; projects targeting net6.0 must pin to **17.13.0** or move to net8+ | harness web search result set (aggregated; see caveat) | — | accessed 2026-08-12 | medium | compatibility |

## The decisive finding

xUnit v3 requires net8.0 as a floor [27][28]. D1 established that MelonLoader pins IL2CPP mods to net6.0. **These are incompatible: xunit v3 is not reachable for CairnMP.** The plan gate anticipated this collapse — D4 folds into D1 — and it is now evidenced rather than assumed.

The consequence is uncomfortable but real: the project is confined to a **deprecated, security-updates-only** test framework line [30], not by its own choices but by the loader's runtime pin.

## What movement remains (the small opening)

| Package | CairnMP has | Ceiling on net6.0 | Note |
|---|---|---|---|
| `xunit` | 2.9.2 (2024-09-27) | **2.9.3** (2025-01-08) [29] | Last v2 release; deprecated line |
| `xunit.runner.visualstudio` | 2.8.2 | not established this round | Needs its own check |
| `Microsoft.NET.Test.Sdk` | 17.11.1 | **17.13.0** [32] | 17.14.0+ drops net6.0; 18.x definitively out [31] |

## Caveat on claim #32

The specific "17.13.0" ceiling comes from a single aggregated search result, not a primary Microsoft release note. It is corroborated in direction by the NuGet compatibility list for 18.8.1 [31], which omits net6.0, but the exact boundary version is **medium confidence**. Verify empirically by attempting the bump before relying on the number.

## Leads worth chasing

- `xunit.runner.visualstudio` net6.0 ceiling not established — needed before any bump is attempted.
- Whether xunit v2's "security updates only" posture has produced any actual security release since 2.9.3 (2025-01-08) — if the line is genuinely dormant, staying on 2.9.2 vs 2.9.3 is nearly immaterial.

## Looked for and could not find

- A primary Microsoft changelog entry stating the 17.14.0 net6.0 drop.
