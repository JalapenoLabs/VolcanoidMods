# Building, testing and installing

All tooling is one stdlib Python script, `scripts/mods.py`, plus `scripts/smoke_test.py` for in-game checks on Linux.

## Requirements

- Python 3.10 or newer.
- Docker, or the .NET 9 SDK. When no SDK is installed, every `dotnet` command runs in
  `mcr.microsoft.com/dotnet/sdk:9.0.300`, the same version CI pins.
- Volcanoids installed. Mods compile against the game's own assemblies, read from the install; they are never
  copied into the repo, and the game loads its own copies at runtime.
- A `.env` copied from `.env.example`, pointing at the install and the game's data folder.

## Commands

```shell
scripts/mods.py list                      # every mod, its shipped folder, version and multiplayer mode
scripts/mods.py validate --all            # required files and manifest checks (CI runs this)
scripts/mods.py test --all                # unit tests, no game needed (CI runs this)
scripts/mods.py build day-night-cycle     # compile against the installed game
scripts/mods.py install day-night-cycle   # build, then copy into the game's LocalMods/
scripts/mods.py enable day-night-cycle    # add it to mods.json (or tick it in the in-game Mods menu)
scripts/mods.py package day-night-cycle   # build and zip the shipped folder into dist/
```

`install` replaces the mod's `LocalMods/` folder but keeps the `.workshop` ID file the game writes there after an
upload. `enable` refuses to run while the game is open, because the game rewrites `mods.json` itself.

## How a mod is built

`build/VolcanoidsMod.props` holds everything shared: .NET Standard 2.1 (what Unity 6 Mono loads), C# `latest`, and a
reference to every assembly in `Volcanoids_Data/Managed`, including the game's own `mscorlib` and `netstandard`
(implicit framework references are off). A mod's `mod.csproj` imports it, names its assembly, and compiles
`Source/**/*.cs` into `bin/`.

A mod ships as its `Content/` folder plus the compiled assembly, laid out in `dist/<Folder>/`.

## Unit tests

A mod's `Tests/` project targets .NET 9 and compiles only the mod's Unity-free sources (for Day/Night Cycle,
`Source/Cycle/`). Keep logic worth testing in such files so CI can check it without the game.

## Smoke tests (Linux)

```shell
scripts/smoke_test.py day-night-cycle --save Slot02 --shots "15,30.5,40@0.5"
```

It boots the game straight from its install with `+load`, lets the mod's developer capture
(`-daynight-capture`) photograph each moment, and reports the log lines and screenshots in `dist/smoke/<time>/`.

- On Hyprland the game opens on the hidden `special:agent` workspace, and its sound goes to a temporary PulseAudio
  null sink, so a run neither takes over the screen nor makes noise.
- `Saves/`, `options.json`, `prefs`, `Player.log` and `Player-prev.log` are backed up first and put back after, so a
  test autosave never replaces a real one.
- Capture mode turns vsync off: a hidden window gets almost no presents, and with vsync on the loading screen
  crawls (six minutes instead of twenty seconds).
- The game's own `NullReferenceException`s from `AgentSpawner.OnDisable` and `StandaloneCrew` at quit are normal.
