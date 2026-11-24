# VolcanoidMods

Monorepo for [JalapenoLabs](https://www.jalapenolabs.io/) Volcanoids mods. Every mod lives in `mods/<name>/`; the
build tooling, CI and docs are shared at the root so each mod stays small.

## Mods

| Mod | Description | Multiplayer | Steam |
| --- | --- | --- | --- |
| [day-night-cycle](mods/day-night-cycle) | A 30-minute day and 20-minute night: sunsets, stars, the Milky Way and a phasing moon | Client-side only | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3842322377) |
| [weather](mods/weather) | Rain & Thunderstorms: a storm about once an hour, with lightning, puddles, rough seas and steaming lava | Client-side only | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3845333600) |

## Prerequisites

| Tool | Version | Purpose |
| --- | --- | --- |
| [Volcanoids](https://store.steampowered.com/app/951440/Volcanoids/) | 1.33 | Mods compile against its assemblies |
| [Python](https://www.python.org/) | 3.10+ | `scripts/mods.py` (standard library only) |
| [Docker](https://www.docker.com/) or the [.NET SDK](https://dotnet.microsoft.com/download) | SDK 9.0.300 | Compiling and testing; Docker runs the pinned SDK image when no SDK is installed |

## Getting started

```shell
git clone git@github.com:JalapenoLabs/VolcanoidMods.git
cd VolcanoidMods
cp .env.example .env   # then set VOLCANOIDS_DIR and VOLCANOIDS_DATA_DIR for your machine
```

## Building and testing

```shell
scripts/mods.py list                      # the mods, their versions and multiplayer modes
scripts/mods.py validate --all            # required files and manifests
scripts/mods.py test --all                # unit tests (no game needed)
scripts/mods.py install day-night-cycle   # build and copy into the game's LocalMods/
scripts/mods.py enable day-night-cycle    # turn it on (or tick it in the in-game Mods menu)
```

Details, including in-game smoke tests: [docs/building.md](docs/building.md).

## Layout

```
VolcanoidMods/
├── mods/<mod>/
│   ├── Content/              # shipped as-is: .manifest.json, .thumb.jpg
│   ├── Source/               # C# compiled into the mod's assembly
│   ├── Tests/                # optional .NET unit tests for the mod's Unity-free code
│   ├── mod.csproj
│   ├── Workshop/             # the Steam Workshop page: description.txt, screenshots/, item-id
│   └── README.md, Changelog.txt, LICENSE
├── build/VolcanoidsMod.props # shared MSBuild settings and game references
├── scripts/
│   ├── mods.py               # list, validate, build, test, install, package, enable
│   ├── workshop.py           # publish, inspect and subscribe to a mod's Workshop item
│   ├── release.py            # Workshop update, signed tag and GitHub release in one go
│   └── smoke_test.py         # boot the game, capture screenshots, restore saves (Linux)
├── tools/workshop-uploader/  # the Steamworks uploader workshop.py runs
├── docs/                     # how the game loads mods, toolchain, publishing, per-mod design
└── .github/workflows/main.yml
```

## CI

`main.yml` runs on every push to `main` and every pull request: it validates every mod's files and manifest and runs
every mod's unit tests. Volcanoids' assemblies cannot be redistributed, so CI does not compile the mods themselves;
compile locally with `scripts/mods.py build --all` before pushing.

## Releasing

`scripts/release.py <mod>` updates the mod's Steam Workshop item, tags the commit, and publishes a GitHub release
with the mod's zip. See [docs/publishing.md](docs/publishing.md).

## Support

If you enjoy these mods, you can [support Jalapeno Labs on Patreon](https://www.jalapenolabs.io/patreon).

## License

Each mod is licensed individually; see its `LICENSE`. The tooling and CI in this repo are MIT licensed.
