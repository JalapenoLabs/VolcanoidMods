**Source-of-truth policy.** The USER is the primary source of truth. This `CLAUDE.md` is the SECOND source of truth.
The codebase is **not** a source of truth (it can drift). Whenever a design decision is made or changed, update this
file in the same change, and keep `docs/` current.

# VolcanoidMods

A monorepo for JalapenoLabs' Volcanoids mods. Every mod lives in-tree under `mods/<mod-name>/`; the root holds the
shared build settings, tooling, CI and docs. There are no submodules.

## Where to read first

| Topic | Doc |
| --- | --- |
| How the game loads mods, manifests, multiplayer rules, launch arguments | `docs/modding.md` |
| Toolchain, commands, unit tests, smoke tests | `docs/building.md` |
| Releases: Workshop page, tags, GitHub releases | `docs/publishing.md` |
| Decompiling the game and reading its bundles; the island's lighting, scenery and drillships as found | `docs/reverse-engineering.md` |
| Day/Night Cycle's requirements and design | `docs/day-night-cycle.md` |
| Rain & Thunderstorms' requirements and design | `docs/weather.md` |

## Repository structure

```
mods/<mod>/            Content/ (shipped verbatim), Source/, Tests/, Workshop/ (description, screenshots,
                       item-id), mod.csproj, README.md, Changelog.txt, LICENSE
build/VolcanoidsMod.props   shared MSBuild: netstandard2.1, game references from VOLCANOIDS_DIR
shared/                code several mods compile into their own assemblies (developer capture tooling)
scripts/mods.py        list | validate | build | test | install | package | enable | disable
scripts/workshop.py    publish | info | subscribe a mod's Workshop item (Linux, through the local Steam client)
scripts/release.py     Workshop update + signed tag + GitHub release with the mod's zip
scripts/smoke_test.py  boots the game with a mod's developer capture (Linux; Hyprland-aware)
tools/workshop-uploader/  C# Steamworks.NET tool that workshop.py builds and runs
branding/              art every mod's public pages share (the Patreon banner the Workshop descriptions embed)
docs/                  one document per area of decisions
.github/workflows/     main.yml: validate + unit tests
```

## Mods

| Mod | Shipped folder | Multiplayer mode | Notes |
| --- | --- | --- | --- |
| `day-night-cycle` | `DayNightCycle` | ClientOnly (2) | 30 min day / 20 min night, hardcoded; synced by UTC + Steam time. Workshop item in `Workshop/item-id` |
| `weather` | `Weather` | ClientOnly (2) | Rain & Thunderstorms: a storm an hour (light, heavy or thunder), synced by UTC + Steam time; lightning damages on the host only. Workshop item in `Workshop/item-id` |

## Key conventions and constraints

- **Game files never enter the repo.** Mods reference `Volcanoids_Data/Managed/*.dll` from the local install
  (`VOLCANOIDS_DIR` in `.env`). Decompiled source stays in a scratch folder. CI therefore cannot compile mods; it
  validates and runs unit tests. Compile locally before pushing.
- **A mod is one assembly plus `Content/`.** Prefer generating art at runtime over asset bundles: bundles must be
  rebuilt in a matching Unity editor, once per platform (shaders differ between Windows D3D and Linux OpenGL), after
  every game update. Use a bundle only when runtime generation genuinely cannot do the job.
- **Manifest enums are integers** (`JsonUtility`). `MultiplayerMode`: 0 ClientServer, 1 ServerOnly, 2 ClientOnly,
  3 SinglePlayer. `UniqueModId` is `JalapenoLabs.<ModName>`; `Assemblies` names exactly `<AssemblyName>.dll`.
  `scripts/mods.py validate` enforces these.
- **Versions are dates** (`2026.11.19`), in the manifest's `Version` and the matching `Changelog.txt` line. Tags are
  `<mod>-v<version>`, signed.
- **Releases go through `scripts/release.py`**, never the in-game upload button: the button drops the description
  and change note. Commit `Workshop/item-id` after a mod's first upload, or the next upload creates a new item.
- **Every public page links Patreon** (`https://www.jalapenolabs.io/patreon`) and the GitHub source: the Workshop
  description, the mod's README and the GitHub release notes. A Workshop description ends with the clickable
  Patreon banner (`branding/patreon-banner.png`).
- **Keep testable logic Unity-free** (like `Source/Cycle/`), so `Tests/` can compile it as plain .NET in CI.
- **Never fail silently**: every early return on unexpected state logs, with the `[ModName]` prefix.
- **C# style**: K&R braces, 4 spaces, file-scoped namespaces, 120-column lines, `// Copyright © 2026 Jalapeno Labs`
  headers, comments that explain why.

## Testing in the game

- The user plays on this machine. Never launch the game, or write into `mods.json` or the game's data folder, while it
  is running. `scripts/mods.py enable` and `smoke_test.py` refuse to.
- `scripts/smoke_test.py` is the way to check a change in-game: it runs the game in a sandboxed data folder
  (`XDG_CONFIG_HOME`), launches on Hyprland's hidden `special:agent` workspace with sound sent to a null sink, and
  collects the mod's log lines and screenshots in `dist/smoke/`. Visual judgement and gameplay feel are the user's
  playtest.
- **Never let a test game or tool write into the player's data folder.** Volcanoids syncs it through Steam Cloud
  whenever any process runs as the game (the test game, and the Workshop uploader too), so even a temporary file can
  land in the player's cloud and come back later.
- A save that lists a local mod cannot be hosted, so multiplayer hosting is tested with an unlisted Workshop upload.

## Adding a new mod

1. Create `mods/<mod-name>/` with `mod.csproj` (import `../../build/VolcanoidsMod.props`, set `AssemblyName` to
   `JalapenoLabs.<ModName>`, compile `../../shared/**/*.cs` too), `Source/` (with a `ModIdentity.cs`),
   `Content/.manifest.json`, `Content/.thumb.jpg`, `README.md`, `Changelog.txt`, `LICENSE`,
   `Workshop/description.txt` and `Workshop/screenshots/`.
2. Entry point: a `GameMod` subclass (`docs/modding.md`). Pick the narrowest multiplayer mode that works.
3. `scripts/mods.py validate <mod>`, `build`, `install`, then smoke-test.
4. Add the mod to the tables in this file and `README.md`, and a design doc in `docs/` if it makes decisions.
5. Release it: `docs/publishing.md#the-first-release-of-a-mod`.
