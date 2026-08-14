# Digest — D1 MelonLoader runtime pin — round 2 (independent corroboration)

Purpose of round: the net6 claim rested on GitHub README + NuGet metadata, both published by LavaGang — one publisher. Round 2 sought genuinely independent confirmation to satisfy the pack's two-source rule for compatibility claims.
Accessed: 2026-08-12

## Claims

| # | Claim | Source | Publisher | Pub date | Confidence | Class |
|---|---|---|---|---|---|---|
| 9 | "To use Melonloader you now need to download and install .NET 6.0"; installer referenced is `runtime-6.0.12-windows-x64-installer`; without it users hit an `il2cpp_init detour failed` error | https://hemisemidemipresent.github.io/btd6-modding-tutorial/ | hemisemidemipresent (community tutorial) | accessed 2026-08-12 | high | compatibility |
| 10 | "Switching your .csproj to target net6.0 instead of net48 is recommended for newer MelonLoader versions" | https://github.com/gurrenm3/BTD-Mod-Helper/wiki/Switching-to-MelonLoader-0.6.0 | gurrenm3 (BTD-Mod-Helper) | accessed 2026-08-12 | medium | compatibility |
| 11 | Current third-party mod templates state their requirement only as ".NET SDK (as per MelonLoader requirements)" — they defer to MelonLoader rather than pinning a framework themselves | https://github.com/k073l/S1MelonModTemplate/blob/master/README.md | k073l | accessed 2026-08-12 | medium | compatibility |

## Two-source status for the load-bearing claim

**"IL2CPP MelonLoader mods target net6.0, and this is dictated by the loader's host runtime"** — now carried by two genuinely independent publishers:
- LavaGang (README requirements + NuGet net6.0 dependency group carrying Il2CppInterop)
- hemisemidemipresent (independent tutorial, names the exact 6.0.12 runtime installer and the failure mode without it)
- gurrenm3 wiki adds a third, weaker voice on the csproj target specifically

Satisfied.

## Leads worth chasing

- The `il2cpp_init detour failed` error is the observable symptom of a runtime mismatch — useful for D2 (what actually breaks if the runtime is wrong).
- Templates deferring to "as per MelonLoader requirements" implies the ecosystem treats the framework as loader-dictated, not per-mod — supports the "pin, not choice" reading but is inference, not evidence.

## Looked for and could not find

- A current mod template with a directly quotable `<TargetFramework>` line. The S1MelonModTemplate README does not quote its csproj, and the fetcher could not read the csproj file itself. The net6.0 target claim therefore rests on the gurrenm3 wiki wording plus the loader requirement, not on a template's literal csproj.
