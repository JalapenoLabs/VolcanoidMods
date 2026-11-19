# Publishing to the Steam Workshop

Mods are uploaded with Volcanoids' own Workshop uploader, from the in-game Mods menu. It works on Linux and needs no
SDK project or Unity editor.

## First upload

1. Bump `Version` in the mod's `Content/.manifest.json` and add a line to its `Changelog.txt`.
2. `scripts/mods.py validate <mod>` and `scripts/mods.py install <mod>`.
3. In the game, open the Mods menu, show local mods, and publish the mod from its entry (the button calls
   `ModManagerUi.PublishMod`). Steam asks you to accept the Workshop agreement the first time.
4. The game writes `LocalMods/<Folder>/.workshop` holding the new item ID. **Copy it into the mod's `Content/` and
   commit it**: every later upload reads it to update the same item, and without it the next upload creates a new one.
5. On the item's Steam page, paste the description from the mod's `Workshop.txt` (Steam BBCode, one paragraph per
   line) and set visibility. Keep it unlisted or hidden until the release is tested.

## What the uploader sends

From `ModManagerUi.PublishMod` and `WorkshopUpload`:

| Sent | From |
| --- | --- |
| Content | the whole `LocalMods/<Folder>/` |
| Title | the manifest's `Title` |
| Tags | `Mod`, the multiplayer tag (`Client` for ClientOnly), then the manifest's `Tags` |
| Preview | `.thumb.png` or `.thumb.jpg` (1 MB at most) |
| Change note | always "Generic update" |
| Description | **never sent**: edit it on the Steam page; uploads leave it alone |

## Updates

Bump the version, add the changelog line, `install`, then publish again from the Mods menu. Players subscribed to the
item get the new version the next time Steam updates it.

## Testing multiplayer before release

A save that lists a local mod cannot be hosted (see [modding.md](modding.md#multiplayer-rules)). Upload as
unlisted, subscribe from a second account or ask a friend to, and test hosting and joining with the Workshop copy.
