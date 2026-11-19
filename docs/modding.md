# How Volcanoids loads mods

Volcanoids (Unity 6000.0, URP, Mono) has an official mod loader built into `Volcanoids.dll`. These are the facts the
mods in this repo rely on, read from the decompiled game (see [reverse-engineering.md](reverse-engineering.md)).
They were checked against game version 1.33.506.0 (Steam build 23320293).

## Where mods live

| Kind | Folder | Mod ID |
| --- | --- | --- |
| Local | `<persistentDataPath>/LocalMods/<Folder>/` | `0` plus `LocalDir` `/<Folder>` |
| Workshop | Steam's workshop content folder, found by item ID | the Workshop item ID |

`persistentDataPath` is `~/.config/unity3d/Volcanoid/Volcanoids` on Linux and
`%USERPROFILE%\AppData\LocalLow\Volcanoid\Volcanoids` on Windows.

The active mod list is `<persistentDataPath>/mods.json`, a `ModConfig` written by `JsonUtility`:

```json
{ "m_mods": [ { "Id": 0, "LocalDir": "/DayNightCycle", "Version": 0, "RequiredOnClients": false, "Title": "..." } ] }
```

The in-game Mods menu edits it and restarts the game; `scripts/mods.py enable` does the same for test runs.

## What a mod folder holds

- `.manifest.json`: a `ModManifest`, parsed with `JsonUtility`, so enums are **integers**.
- The assemblies named in `Assemblies` (or every `*.dll` if the list is absent), loaded with `Assembly.LoadFrom`.
- Optional asset bundles (`DataBundles`, `MenuBundles`) and `*.patch.json` content patches.
- `.thumb.png` or `.thumb.jpg`: the preview shown in the Mods menu and on the Workshop.
- `.workshop`: written by the game after the first upload; it holds the Workshop item ID that later uploads update.

Manifest fields that matter:

| Field | Meaning |
| --- | --- |
| `UniqueModId` | Stable ID, `JalapenoLabs.<ModName>` in this repo; also names the mod's persistent data folder |
| `Title`, `Version`, `Tags` | Shown in the Mods menu; tags become Workshop tags (the game adds `Mod` and the multiplayer tag) |
| `MultiplayerMode` | `0` ClientServer, `1` ServerOnly, `2` ClientOnly, `3` SinglePlayer |
| `LoadGroup`, `LoadOrder` | `-200` Library, `-100` Framework, `0` Default, `100` Late; order within a group |
| `SkipContentPatches` | Skip `*.patch.json` files |

## Code entry points

Every non-abstract `GameMod` subclass with a parameterless constructor is created once at startup. It gets
`Load()`, then `OnMainMenuLoaded`, `OnGameLoading`, `OnGameLoaded(Scene)` and `OnInitData()` as the game reaches each
point. `OnGameLoaded` fires for the host and for clients joining a game. `GameMod.Owner` is the `Mod`, with its
manifest and folder.

Mods can also name static methods in `RuntimeInit` to run at a `RuntimeInitializeLoadType`.

## Multiplayer rules

- `ModResolver` marks a mod `RequiredOnClients` only when its mode is **ClientServer**.
- When a client joins, `ModNetworkInfo.IsConfigurationCompatible` requires the host's required mods, and lets the
  client keep any extra mod whose mode is **ClientOnly**. A ClientOnly mod never blocks a join either way.
- The host advertises only Workshop mods (ID not 0) in its lobby data.
- **Saves record every active mod**: every save and autosave calls `Settings.Save`, which copies
  `ModManager.Configuration`.
- Loading a save whose list differs from the active mods (`SavedGame.ApplyModsAndLoad`) opens `ModLoadDialogUi`,
  listing both. **Continue** loads with the active mods; switching mods resolves the save's list and restarts the game
  with it (`+load <Save> -mods_resolved`). Passing `-mods_resolved` yourself skips the dialog.
- The save settings screen **disables multiplayer for any save that lists a local mod**. A ClientOnly mod tested
  from `LocalMods/` can join other players' games, but a save it was active in cannot be hosted until the mod is
  installed from the Workshop instead. Test hosting with an unlisted Workshop upload.

## Useful command-line arguments

| Argument | Effect |
| --- | --- |
| `+load <SaveName>` | Load `Saves/<SaveName>.sav` straight from launch |
| `-mods_resolved` | Do not restart to match the save's mod list |
| `-force-glcore` | OpenGL Core, which the Steam launch uses on Linux |
| `-logFile <path>` | Write this run's log elsewhere (Unity still rotates `Player.log`) |

When run outside Steam, set `SteamAppId=951440` so the Steam API does not relaunch the game through the client.
