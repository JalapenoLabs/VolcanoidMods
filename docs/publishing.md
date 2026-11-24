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
| Cover | `Content/.thumb.jpg` (or `.png`): square, at least 512 x 512 (1024 x 1024 is crisp), at most 1 MB. The page shows it at 268 x 268 and the game's Mods menu shows it too, so the title must read at that size |
| Description | `Workshop/description.txt`, Steam BBCode, at most 8,000 characters, ending with the GitHub and Patreon links and the Patreon banner |
| Screenshots | `Workshop/screenshots/*.jpg`, at most 1 MB each, shown in file-name order |
| Change note | The `Changelog.txt` line for the manifest's `Version` |
| Item ID | `Workshop/item-id`, written after the first upload |

`scripts/mods.py validate` checks the sizes, that the cover is square, and that the changelog has the version's
line. Day/Night Cycle's cover is a showcase capture (`docs/building.md`) of a fully built drillship under the full
moon, cropped square, with the title centred in the brand's Noto Sans Black (150 pt at 1024 px, white, soft shadow)
under a letter-spaced Noto Sans Bold eyebrow (40 pt, `#9BCB3C`). Rain & Thunderstorms' cover matches it: a showcase
capture of a lightning bolt striking a fully built drillship, with the title across the sky at the top (120 and 104 pt,
eyebrow 36 pt) so the drillship and the bolt stay clear.

## The Patreon banner

Every Workshop description ends with a clickable Patreon banner, `branding/patreon-banner.png`, linked to the
Patreon page:

```
[url=https://www.jalapenolabs.io/patreon][img]https://raw.githubusercontent.com/JalapenoLabs/VolcanoidMods/main/branding/patreon-banner.png[/img][/url]
```

Steam keeps no copy of a description's images, so the banner is served from the repository's raw GitHub URL on
`main`: moving or renaming the file breaks it on every page. It is 1280 x 320 and readable at the 636 px Steam
shows it at: the Jalapeno Labs circle mark (160 px, with the brand's clear space of half its height) on an
Ink-to-Surface-Navy gradient with a faint Primary green glow, a letter-spaced Secondary green eyebrow
("JALAPENO LABS ON PATREON"), a white Noto Sans Bold headline, and a Primary green "Join on Patreon" button.
The mark and colours come from the brand guide in the jalapenolabs-io repository (`apps/web/brand/`).

## How uploads work

`scripts/workshop.py` builds `tools/workshop-uploader` (a small C# tool on the Steamworks.NET package) and runs it on
the host with `SteamAppId=951440`. It loads the game's own `libsteam_api.so`, so Steam treats it as the game and
the upload belongs to the signed-in account. The account shows as playing Volcanoids while it runs.

It makes the same calls as the game's uploader (`WorkshopUpload` in `Volcanoids.dll`), and also sends what the
in-game button leaves out: the description, a real change note (the game always sends "Generic update"), and
screenshots. An update keeps the item's visibility unless `--visibility` is given, and refuses to touch an item
another account owns.

**Screenshots and Steam's image store.** Steam files every image under its content's SHA-1, and removing an image
deletes that file. Removing the gallery and adding byte-identical images in one update therefore leaves the page
pointing at deleted files (every image 404s, while Steam reports success). So the uploader compares the gallery's
hashes with the local screenshots: an unchanged gallery is left alone, and a changed one is cleared in an update of
its own before the main update adds the new set. `--force-screenshots` re-uploads an unchanged gallery the same way,
which repairs one whose images Steam lost.

**Every publish ends by fetching the cover and each gallery image** from Steam's image server, retrying for a
minute while new images are processed. It fails if any image is still broken, because Steam's own success result
does not mean the page works.

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

To change only the Workshop page (description, cover, screenshots) without a new version, run
`scripts/workshop.py publish <mod> --change-note "What changed"`: it uploads the current build with the current
page. Every upload adds an entry to the item's public Change Notes tab, so a page-only update must say what it
changed instead of repeating the release's changelog line; the script refuses to update an existing item without
one. Releases (`release.py`) use the changelog line.

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
scripts/workshop.py info <mod>        # the live item: title, visibility, tags, size, screenshots, broken images
scripts/workshop.py subscribe <mod>   # subscribe this account and wait for Steam to download it
```

A save that lists a hand-installed (`LocalMods`) mod cannot host multiplayer; one that lists the Workshop item can.
After subscribing, switch the game to the Workshop copy in the Mods menu to test hosting.
