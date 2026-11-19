#!/usr/bin/env python3
"""Publish a mod to the Volcanoids Steam Workshop, or inspect and subscribe to its Workshop item.

Uploads go through tools/workshop-uploader, which makes the same Steam calls as the game's own uploader, through the
Steam client signed in on this machine. Unlike the in-game button it also sends the description, a real change note
and screenshots. Linux only: the uploader loads the game's own libsteam_api.so.

    scripts/workshop.py publish day-night-cycle --visibility public   # first upload: creates the item
    scripts/workshop.py publish day-night-cycle                       # later uploads: update it, visibility kept
    scripts/workshop.py info day-night-cycle
    scripts/workshop.py subscribe day-night-cycle

A mod's page lives in its folder: the manifest's Title and Tags, Content/.thumb.jpg as the preview,
Workshop/description.txt (Steam BBCode), Workshop/screenshots/, and the Changelog.txt line for the manifest's Version as
the change note. The item ID is recorded in Workshop/item-id after the first upload; commit it.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from mods import (  # noqa: E402
    MODS_DIR,
    REPO_ROOT,
    SCREENSHOT_SUFFIXES,
    THUMBNAIL_NAMES,
    WORKSHOP_ITEM_ID_PATH,
    assemble,
    changelog_entry,
    is_game_running,
    load_manifest,
    read_settings,
    read_workshop_item_id,
    require_path,
    resolve_mods,
    run_dotnet,
    validate_mod,
)

logger = logging.getLogger(__name__)

UPLOADER_PROJECT = "tools/workshop-uploader/WorkshopUploader.csproj"
UPLOADER_ASSEMBLY = REPO_ROOT / "tools" / "workshop-uploader" / "bin" / "WorkshopUploader.dll"
STEAM_APP_ID = "951440"

# The tag the game puts on a mod's Workshop item for its multiplayer mode (ModUtils.GetMultiplayerModeTag), keyed by
# the manifest's MultiplayerMode. The in-game Mods browser lists items tagged "Mod".
MULTIPLAYER_TAGS = {
    0: "ClientServer",
    1: "Server",
    2: "Client",
    3: "SinglePlayer",
}
CATEGORY_TAG = "Mod"

# Freshly uploaded images can take a moment to appear on Steam's image server.
IMAGE_CHECK_ATTEMPTS = 6
IMAGE_CHECK_INTERVAL_SECONDS = 10


# ========================================================================================
# The uploader
# ========================================================================================


def run_uploader(command: str, arguments: list[str]) -> dict:
    """Build the uploader if needed, run one of its commands, and return the JSON it prints."""
    if sys.platform != "linux":
        raise SystemExit("The Workshop uploader runs on Linux only (it loads the game's libsteam_api.so).")
    if not shutil.which("dotnet"):
        raise SystemExit("The uploader needs a .NET runtime (9 or newer) to run. Install the dotnet package.")
    if not is_steam_running():
        raise SystemExit("The Steam client is not running. Start it and sign in to the account that owns the mod.")

    run_dotnet(["build", UPLOADER_PROJECT, "--configuration", "Release"], None)

    game_dir = require_path(read_settings(), "VOLCANOIDS_DIR")
    steam_api = game_dir / "Volcanoids_Data" / "Plugins" / "libsteam_api.so"
    # The native Steam API takes the app ID from the process environment.
    environment = {**os.environ, "SteamAppId": STEAM_APP_ID, "SteamGameId": STEAM_APP_ID}
    completed = subprocess.run(
        ["dotnet", str(UPLOADER_ASSEMBLY), command, "--steam-api", str(steam_api), *arguments],
        env=environment,
        stdout=subprocess.PIPE,
        text=True,
    )
    if completed.returncode != 0:
        raise SystemExit(f"The Workshop uploader's '{command}' failed (exit code {completed.returncode}); see above.")
    return json.loads(completed.stdout.strip().splitlines()[-1])


def is_steam_running() -> bool:
    # The bracket keeps pgrep from matching its own command line.
    return subprocess.run(["pgrep", "-f", "[u]buntu12_32/steam"], capture_output=True).returncode == 0


# ========================================================================================
# Commands
# ========================================================================================


def workshop_tags(manifest: dict) -> list[str]:
    """The tags the game's own uploader would give the mod: "Mod", its multiplayer tag, then the manifest's Tags."""
    tags = [CATEGORY_TAG, MULTIPLAYER_TAGS[manifest["MultiplayerMode"]]]
    tags += [tag for tag in manifest.get("Tags", []) if tag not in tags]
    return tags


def publish(mod: str, visibility: str | None, force_screenshots: bool = False) -> dict:
    """Build the mod and upload it with its page, then check every image on the live page loads.

    Creates the Workshop item on the first upload. The gallery is re-uploaded only when its screenshots changed,
    unless force_screenshots is set (to repair a gallery whose images Steam lost).
    """
    problems = validate_mod(mod)
    if problems:
        raise SystemExit(f"mods/{mod} is not ready to publish: {'; '.join(problems)}")

    manifest = load_manifest(mod)
    version = manifest["Version"]
    item_id = read_workshop_item_id(mod)
    if item_id is None and visibility is None:
        raise SystemExit("This is the mod's first upload: choose its visibility with --visibility.")

    mod_dir = MODS_DIR / mod
    preview = next(mod_dir / "Content" / name for name in THUMBNAIL_NAMES if (mod_dir / "Content" / name).is_file())
    screenshots = sorted(
        path for path in (mod_dir / "Workshop" / "screenshots").glob("*") if path.suffix in SCREENSHOT_SUFFIXES
    )
    shipped_dir = assemble(mod)

    with tempfile.TemporaryDirectory() as scratch:
        change_note_path = Path(scratch) / "change-note.txt"
        change_note_path.write_text(f"{version}: {changelog_entry(mod, version)}", encoding="utf-8")

        arguments = [
            "--content", str(shipped_dir),
            "--preview", str(preview),
            "--title", manifest["Title"],
            "--description-file", str(mod_dir / "Workshop" / "description.txt"),
            "--change-note-file", str(change_note_path),
            "--tags", ",".join(workshop_tags(manifest)),
        ]
        if item_id is not None:
            arguments += ["--item", item_id]
        if visibility is not None:
            arguments += ["--visibility", visibility]
        for screenshot in screenshots:
            arguments += ["--screenshot", str(screenshot)]
        if force_screenshots:
            arguments += ["--force-screenshots", "true"]

        logger.info(f"[publish] {manifest['Title']} {version}: {len(screenshots)} screenshot(s), "
                    f"tags {workshop_tags(manifest)}")
        result = run_uploader("publish", arguments)

    if result["created"]:
        (mod_dir / WORKSHOP_ITEM_ID_PATH).write_text(result["itemId"] + "\n", encoding="utf-8")
        logger.info(f"[publish] Recorded the new item in mods/{mod}/{WORKSHOP_ITEM_ID_PATH}; commit it.")
    if result["needsLegalAgreement"]:
        logger.warning("[publish] Accept the Workshop legal agreement or the item stays hidden: "
                       "https://steamcommunity.com/sharedfiles/workshoplegalagreement")
    require_working_images(result["itemId"])
    logger.info(f"[publish] Live at {result['url']}")
    return result


def require_working_images(item_id: str) -> None:
    """Fail unless the live page's cover and every gallery image load from Steam's image server.

    Steam reports an update as successful even when it leaves the page pointing at deleted images, so the only proof
    that the page works is fetching them.
    """
    for attempt in range(1, IMAGE_CHECK_ATTEMPTS + 1):
        details = run_uploader("info", ["--item", item_id])
        broken = details["brokenImages"]
        if not broken:
            logger.info(f"[publish] The cover and all {details['screenshots']} screenshot(s) load")
            return
        logger.info(f"[publish] {len(broken)} image(s) not loading yet (check {attempt} of {IMAGE_CHECK_ATTEMPTS})")
        time.sleep(IMAGE_CHECK_INTERVAL_SECONDS)
    raise SystemExit(
        f"Workshop item {item_id} still has broken images: {'; '.join(broken)}. "
        "Re-upload the gallery with: scripts/workshop.py publish <mod> --force-screenshots"
    )


def require_item_id(mod: str) -> str:
    item_id = read_workshop_item_id(mod)
    if item_id is None:
        raise SystemExit(f"{mod} has no Workshop item yet. Publish it first: scripts/workshop.py publish {mod} "
                         "--visibility public")
    return item_id


def command_publish(arguments: argparse.Namespace) -> None:
    if is_game_running():
        raise SystemExit("Close Volcanoids first: Steam would see two copies of the game running.")
    publish(arguments.mod, arguments.visibility, arguments.force_screenshots)


def command_info(arguments: argparse.Namespace) -> None:
    item_id = require_item_id(arguments.mod)
    print(json.dumps(run_uploader("info", ["--item", item_id]), indent=2))


def command_subscribe(arguments: argparse.Namespace) -> None:
    item_id = require_item_id(arguments.mod)
    print(json.dumps(run_uploader("subscribe", ["--item", item_id]), indent=2))


# ####################### #
#           CLI           #
# ####################### #


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)

    publish_parser = commands.add_parser("publish", help="build and upload a mod with its Workshop page")
    publish_parser.add_argument("mod")
    publish_parser.add_argument(
        "--visibility",
        choices=["public", "unlisted", "friends", "private"],
        help="required on the first upload; leave out to keep an existing item's visibility",
    )
    publish_parser.add_argument(
        "--force-screenshots",
        action="store_true",
        help="re-upload the gallery even if unchanged (repairs images Steam lost)",
    )
    publish_parser.set_defaults(handler=command_publish)

    for name, handler, summary in (
        ("info", command_info, "show the live Workshop item's details"),
        ("subscribe", command_subscribe, "subscribe this Steam account and wait for the download"),
    ):
        subcommand = commands.add_parser(name, help=summary)
        subcommand.add_argument("mod")
        subcommand.set_defaults(handler=handler)

    parsed = parser.parse_args()
    resolve_mods([parsed.mod], use_all=False)
    try:
        parsed.handler(parsed)
    except subprocess.CalledProcessError as error:
        raise SystemExit(f"Command failed with exit code {error.returncode}: {' '.join(map(str, error.cmd))}")


if __name__ == "__main__":
    main()
