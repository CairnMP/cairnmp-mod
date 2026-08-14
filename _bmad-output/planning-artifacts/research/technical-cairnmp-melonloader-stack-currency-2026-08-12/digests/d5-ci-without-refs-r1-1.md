# Digest — D5 CI without redistributable game assemblies — round 1

Question owned: how do IL2CPP/MelonLoader mod repos run CI when the reference assemblies cannot be redistributed?
Accessed: 2026-08-12

## Claims

| # | Claim | Source | Publisher | Pub date | Confidence | Class |
|---|---|---|---|---|---|---|
| 33 | BepInEx runs a "Web service for uploading game assemblies to BepInEx NuGet", "intended for modding communities for uploading publicized and stripped game assemblies" | https://github.com/BepInEx/BepInEx.NuGetUpload.Service | BepInEx | accessed 2026-08-12 | high | pattern |
| 34 | Mod developers reference game assemblies "through NuGet packages rather than committing DLLs to source control"; the BepInEx feed hosts stripped and publicized game libraries, so developers "only need to update version numbers when the game updates" | https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/ | Risk of Rain 2 modding wiki | accessed 2026-08-12 | medium | pattern |
| 35 | Stripped assemblies contain "only method signatures and class definitions without method bodies" and the wiki states these can be legally published on NuGet | https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/ | Risk of Rain 2 modding wiki | accessed 2026-08-12 | low-medium | legal-adjacent |
| 36 | Explicit community warning for full assemblies: "If you want to keep method bodies, add -n, but **don't upload that dll on github or somewhere else** if you do that" | https://risk-of-thunder.github.io/R2Wiki/Mod-Creation/C%23-Programming/Assembly-References/ | Risk of Rain 2 modding wiki | accessed 2026-08-12 | medium | pattern |
| 37 | JetBrains **Refasmer** creates a reference assembly from a normal assembly, "strips method bodies, private class fields etc."; installable as `dotnet tool install -g JetBrains.Refasmer.CliTool`; latest CliTool 2.0.0 / library 2.0.2 | https://github.com/JetBrains/Refasmer + https://www.nuget.org/packages/JetBrains.Refasmer/ | JetBrains | accessed 2026-08-12 | high | tooling |
| 38 | Refasmer can omit private and internal types from the reference assembly since they are not part of the public API | https://blog.jetbrains.com/dotnet/2020/08/05/generate-reference-assemblies-with-refasmer/ | JetBrains Blog | 2020-08-05 | medium | tooling |

## Caveat on the legal-adjacent claim

Claim #35 is a **community wiki's assertion about one specific game**, not legal advice and not a statement from any publisher. It is recorded because it documents an ecosystem norm, not because it establishes what is permissible for Cairn. Confidence deliberately low-medium. Do not treat it as clearance.

## Freshness caveat

The Refasmer blog post [38] is from 2020 — outside the pack's 2-year bar for patterns. It is used only for a capability description that the current NuGet listing corroborates [37], not for any currency claim.

## Leads worth chasing (not pursued — see stopping note)

- Whether BepInEx's NuGet feed has an acceptance policy naming which games/publishers are permitted (the service README points to a wiki not fetched this round).
- Whether any MelonLoader-specific (as opposed to BepInEx-specific) CI convention exists; the MelonLoader-side search returned only topic listings and no concrete workflow examples.

## Looked for and could not find

- A concrete, citable GitHub Actions workflow from a MelonLoader IL2CPP mod repository demonstrating either approach. The searches returned GitHub topic pages and the loader repo, not workflow files. **This is a genuine gap**: the recommendation below rests on structural reasoning plus the BepInEx pattern, not on an observed MelonLoader exemplar.
