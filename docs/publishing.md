# Publishing and releasing

A release of a mod is three things, made together by `scripts/release.py`:

| Where | What |
| --- | --- |
| Steam Workshop | The item players subscribe to: the mod's files, its page, screenshots and a change note |
| Git tag | `<mod>-v<version>`, signed, on the released commit (for example `day-night-cycle-v2026.11.19`) |
| GitHub release | The tag's release, with the changelog line, install steps, links, and `<Folder>-<version>.zip` |

Releases are made from this Linux machine, not CI: CI cannot build the mods without the game's assemblies, and the
Workshop upload runs through the Steam client signed in here.

## A mod's Workshop page

Everything on the page comes from the mod's folder:

| Page | Source |
| --- | --- |
| Title | `Content/.manifest.json` `Title` |
| Tags | `Mod`, the multiplayer tag (`Client` for ClientOnly), then the manifest's `Tags`: what the game's own uploader sends |
| Preview image | `Content/.thumb.jpg` (or `.png`), at most 1 MB; the game's Mods menu shows it too |
| Description | `Workshop/description.txt`, Steam BBCode, at most 8,000 characters, ending with the GitHub and Patreon links |
| Screenshots | `Workshop/screenshots/*.jpg`, at most 1 MB each, shown in file-name order |
| Change note | The `Changelog.txt` line for the manifest's `Version` |
| Item ID | `Workshop/item-id`, written after the first upload |

`scripts/mods.py validate` checks the sizes and that the changelog has the version's line.

## How uploads work

`scripts/workshop.py` builds `tools/workshop-uploader` (a small C# tool on the Steamworks.NET package) and runs it on
the host with `SteamAppId=951440`. It loads the game's own `libsteam_api.so`, so Steam treats it as the game and
the upload belongs to the signed-in account. The account shows as playing Volcanoids while it runs.

It makes the same calls as the game's uploader (`WorkshopUpload` in `Volcanoids.dll`), and also sends what the
in-game button leaves out: the description, a real change note (the game always sends "Generic update"), and
screenshots, replacing the previous set on each update. An update keeps the item's visibility unless
`--visibility` is given, and refuses to touch an item another account owns.

Requirements: the Steam client running and signed in to the account that owns the item, a .NET runtime (9 or newer)
to run the tool, and Docker or the .NET SDK to build it. The game must be closed.

## The first release of a mod

1. Write `Workshop/description.txt`, add screenshots, set the manifest's `Version` and the matching `Changelog.txt`
   line, commit.
2. Create the Workshop item: `scripts/workshop.py publish <mod> --visibility public`. It writes `Workshop/item-id`.
3. Link the item from the mod's README and the root README's table, and commit them with `Workshop/item-id`. Push.
4. `scripts/release.py <mod>`.

## Later releases

1. Bump `Version` in the manifest and add the `Changelog.txt` line; update the description or screenshots if needed.
2. Commit and push.
3. `scripts/release.py <mod>`.

To change only the Workshop page (description, screenshots) without a new version, run
`scripts/workshop.py publish <mod>`: it uploads the current build with the current page.

## What `release.py` checks and does

1. Main is clean and matches `origin/main`, the mod has a Workshop item, and the changelog has the version's line.
2. Runs the mod's unit tests.
3. Builds the mod and updates its Workshop item (files, page, screenshots, change note).
4. Zips the shipped folder into `dist/<Folder>-<version>.zip`.
5. Creates and pushes the signed tag on HEAD.
6. Creates the GitHub release with notes and the zip.

It can be re-run after a failure: a tag already on HEAD is kept, an existing release just gets its zip replaced, and
the Workshop upload is repeated. A tag on a different commit stops it; bump the version instead.

## Checking a release

```shell
scripts/workshop.py info <mod>        # the live item: title, visibility, tags, size, screenshot count
scripts/workshop.py subscribe <mod>   # subscribe this account and wait for Steam to download it
```

A save that lists a hand-installed (`LocalMods`) mod cannot host multiplayer; one that lists the Workshop item can.
After subscribing, switch the game to the Workshop copy in the Mods menu to test hosting.
