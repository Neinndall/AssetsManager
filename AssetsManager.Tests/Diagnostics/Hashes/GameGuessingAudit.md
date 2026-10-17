# GAME Hash Guessing Lab audit — 2026-10-17

Input: 2,324,825 GAME catalog paths and 606 persisted unknown hashes. The older 631 figure predates the local snapshot used here. Of these 606, 497 occur in installed WADs and 345 have BIN references. Another 109 are absent from both current WAD chunks and atlas sprite tables. No user catalog or unknown inventory was changed by the diagnostics.

## Implemented changes

- GrepWad accepts modern 64-bit material names and falls back to the resolved identity of the material BIN entry. The previous material-name branch accepted only strings. Against the same pending inventory, the BIN cohort changed from 0 to 19 exact path matches.
- A shared material-texture vocabulary projects catalog filenames and their character/container-relative forms into the owning skin or companion theme. Sampler roles select MatCap, FlowMap, masks, noise, scroll, distortion and iridescence families. Every candidate must match the exact pending WAD link.
- GrepWad invokes this vocabulary after its material-context pass. Custom Textures invokes it before the broad filename/build-list passes. The index is cached by corpus revision; completed target/directory/role scopes are deduplicated per engine. Interrupted or budget-limited scopes remain eligible for retry.
- Existing candidate generation remains available. No discovered path or current champion name is hardcoded into production.

The material-name fix yields 19 matches; the role experiment yields 10, with 6 overlapping. Their measured union is 23 distinct pending hashes. This supports 606 -> 583, not the requested reduction of 200. The combined full-install GrepWad run confirmed all 23 matches in 7:26. It is recorded separately in `game-grep-after.txt` beside the project directory.

## GrepWad coverage inspected

| Branch | Responsibility |
|---|---|
| Lua manifest | Character/shared script names, directory mapping and compiled/preload variants |
| Binary strings | Length-delimited references, root aliases, shader permutations, map geometry and image variants |
| Structured BIN | Animation graphs, regalia/loadouts, submesh roles, material samplers, sibling references and learned class/field templates |
| Dotted BINs | Object-key paths for loadouts and companion resources |
| Shader includes | Relative and rooted include references |
| Preload | Lua, particles, shader and dependent preload references |
| Atlas | Metadata sprite hashes, texture/directory context and catalog filename patterns |
| Generic content | Embedded asset paths and supported extension variants |

BIN cohort baseline: 6,611,899 instrumented engine checks, 0 matches, 3:31. Material-name correction alone: 6,493,550 engine checks, 19 matches, 5:12. Some older targeted branches compute hashes outside the engine, so these counters are not a complete measure of all hashing work. Runs shared the machine with other diagnostic work; elapsed times are observations, not controlled performance benchmarks.

## Basic Suite

All ten submethods were inspected. All configured passes completed across the initial Basic run and a separate numeric run; none found a new path in this snapshot.

| Submethod | Responsibility | Engine checks |
|---|---|---:|
| Cross domain | LCU game-data paths mapped into GAME roots | 197,995 |
| Characters | Champion/Jade templates, skins, icons, loadscreens, recalls, pet themes/tiers | 89,996,402 |
| Regalia | Ranked and loadout asset matrices | 647,324 |
| Shaders | Known families, executable references, stages, platforms and permutations | 4,530,918 |
| Identical copies | Checksum twins and owner/WAD directory evidence; path hash still confirms identity | 366,993 |
| Locales | Language/region variants | 159,776 |
| Extensions | Extension substitution over known paths | 156,313,914 |
| Prefixes | Standard basename prefixes | 23,248,250 |
| Token removal | One-token deletion and unnumbered aliases | 8,071,427 |
| Numbers | Numeric substitution, followed by two-digit padding with duplicate work excluded | 654,926,400 + 71,934,800 |

Total across those passes: 1,010,394,199 checks. Numeric range preserves production behavior: values 0 through 199 for a maximum of 200.

## Custom and independent experiments

| Pass | Outcome before changes | Completion |
|---|---|---|
| Custom Shaders | 0 matches / 79,468,571 checks / 9.37 s | Complete |
| BIN-referenced animations | 0 / 54,310,717 / 12.82 s | Complete |
| Animation Build List | 0 / 562,361,933 / 120 s | Partial; time limit |
| Texture Build List | 0 / 911,590,750 / 120 s | Partial; time limit |
| New sampler-role vocabulary | 10 matches / 20,861,338 checks | Complete experiment |
| Role vocabulary extended to diffuse textures | Same 10 matches / 29,100,538 checks | Complete experiment; diffuse expansion rejected |
| Single-character edits, insertions, deletions and adjacent transpositions | 0 / 632,566,695 | Complete experiment; kept out of production |
| Esports banners | 0 / 5,300,630 | Complete configured attack |

After integration, production Custom Textures reports the same 10 role-based matches within its first 20-second diagnostic window. Its later broad search remains partial in that measurement.

The final standalone production role pass completed with 10 matches, 21,145,138 checks, 4.50 s and 781 MB of cumulative managed allocations. A raw pending-reference prefilter avoided building irrelevant BIN object graphs; before that filter the same production pass reported 10 matches, the same check count, 7.54 s and 4,915 MB allocated. These are cumulative allocations, not retained memory. The full application build succeeded and all 633 hash tests passed after the filter.

Additional completed experiments yielded 0 matches: augment icon conventions (6,048), icon relocation (160,940), map-theme substitutions (154,638), shader directory/stage combinations (673,992), image extensions (6,671,980), cross-catalog paths (223,901), and animation suffix recombination in 22 BIN containers (19,339,166).

The installed Map12 WAD accounts for 118 pending chunks: 117 textures and one empty BIN. Only 33 have direct BIN references in the forensic dump. General local spelling changes and the tested theme/icon conventions do not explain this family. Missing names in those fields and absent historical chunks remain separate research problems; zero-hit or partial searches do not prove their paths cannot be recovered.

## Validation and reproduction

The hash test suite passed 633 tests, including the corrupt-BIN regression added during final review.

Run from the solution directory:

```powershell
dotnet test AssetsManager.Tests/AssetsManager.Tests.csproj --no-restore --filter FullyQualifiedName~Services.Hashes
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj --no-build -- game-evidence-patterns --stage=roles --seconds=120
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj --no-build -- game-evidence-patterns --stage=basic --seconds=300
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj --no-build -- game-evidence-patterns --stage=numbers --seconds=300
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj --no-build -- game-evidence-patterns --stage=custom --method=game-custom-textures --seconds=120
dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj --no-build -- game-grep-dryrun
```

`game-evidence-patterns` accepts `--root`, `--wad` for WAD-scoped experiments, and `--out` for an exact-match export. Its engines are in memory and it never calls hash-store persistence. Build before launching diagnostics; on Windows a running diagnostic can lock the assemblies needed by another build.
