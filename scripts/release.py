#!/usr/bin/env python3
"""Release a mod: Workshop update, signed git tag, and a GitHub release with the mod's zip attached.

    scripts/release.py day-night-cycle

It releases the version in the mod's manifest, which needs a matching Changelog.txt line, from a clean, pushed main
branch. The Workshop item must exist already (create it with `scripts/workshop.py publish <mod> --visibility public`,
then link it from the READMEs). Re-running after a failure picks up where it stopped: a tag already on HEAD and a
release already on GitHub are kept, and the Workshop upload is simply repeated.

Linux only (the Workshop uploader loads the game's libsteam_api.so). Needs the GitHub CLI signed in, git commit
signing set up, and the Steam client running.
"""

from __future__ import annotations

import argparse
import logging
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from mods import (  # noqa: E402
    DIST_DIR,
    MODS_DIR,
    REPO_ROOT,
    changelog_entry,
    load_manifest,
    resolve_mods,
    run_dotnet,
    shipped_name,
    zip_shipped_folder,
)
from workshop import publish, require_item_id  # noqa: E402

logger = logging.getLogger(__name__)

GITHUB_REPOSITORY = "JalapenoLabs/VolcanoidMods"
PATREON_URL = "https://www.jalapenolabs.io/patreon"
WORKSHOP_URL = "https://steamcommunity.com/sharedfiles/filedetails/?id={item_id}"


def git(*arguments: str) -> str:
    return subprocess.run(["git", *arguments], cwd=REPO_ROOT, check=True, capture_output=True, text=True).stdout.strip()


def require_releasable_tree() -> None:
    """Releases come from a clean main branch that matches origin, so the tag points at code everyone can see."""
    branch = git("rev-parse", "--abbrev-ref", "HEAD")
    if branch != "main":
        raise SystemExit(f"Release from main, not '{branch}'.")
    if git("status", "--porcelain"):
        raise SystemExit("The working tree has uncommitted changes. Commit or stash them first.")
    git("fetch", "--quiet", "origin", "main")
    if git("rev-parse", "HEAD") != git("rev-parse", "origin/main"):
        raise SystemExit("main and origin/main differ. Pull or push first, so the release is built from shared code.")


def tag_release(tag: str, message: str) -> None:
    """Create and push a signed tag on HEAD; keep it if a previous run already made it there."""
    existing = git("tag", "--list", tag)
    if existing:
        tagged_commit = git("rev-list", "-n", "1", tag)
        if tagged_commit != git("rev-parse", "HEAD"):
            raise SystemExit(f"Tag {tag} already exists on another commit. Bump the version to release new code.")
        logger.info(f"[tag] {tag} already exists on HEAD; keeping it")
    else:
        git("tag", "--sign", tag, "--message", message)
        logger.info(f"[tag] Created signed tag {tag}")
    git("push", "--quiet", "origin", tag)


def release_notes(mod: str, manifest: dict, item_id: str) -> str:
    """The GitHub release body. One line per paragraph: GitHub renders a single line break in release notes as a
    break, so hard-wrapped prose would split mid-sentence."""
    version = manifest["Version"]
    folder = shipped_name(mod)
    workshop_url = WORKSHOP_URL.format(item_id=item_id)
    paragraphs = [
        changelog_entry(mod, version),
        "## Install",
        f"**Steam Workshop (recommended):** [subscribe to {manifest['Title']}]({workshop_url}), then turn it on in the "
        "game's Mods menu. Steam keeps it up to date.",
        f"**By hand:** download `{folder}-{version}.zip` below and unzip it into the game's `LocalMods` folder, so you "
        f"get `LocalMods/{folder}/`:",
        "- Windows: `%USERPROFILE%\\AppData\\LocalLow\\Volcanoid\\Volcanoids\\LocalMods`\n"
        "- Linux: `~/.config/unity3d/Volcanoid/Volcanoids/LocalMods`",
        "Then turn it on in the game's Mods menu. A save that lists a hand-installed mod cannot host multiplayer; the "
        "Workshop copy can.",
        f"More about the mod: [README](https://github.com/{GITHUB_REPOSITORY}/tree/main/mods/{mod}#readme)",
        f"Enjoying it? [Support Jalapeno Labs on Patreon]({PATREON_URL}).",
    ]
    return "\n\n".join(paragraphs) + "\n"


def create_github_release(tag: str, title: str, notes: str, archive_path: Path) -> None:
    """Create the GitHub release, or refresh its zip if a previous run already created it."""
    exists = subprocess.run(
        ["gh", "release", "view", tag, "--repo", GITHUB_REPOSITORY],
        cwd=REPO_ROOT,
        capture_output=True,
    ).returncode == 0
    if exists:
        subprocess.run(
            ["gh", "release", "upload", tag, str(archive_path), "--clobber", "--repo", GITHUB_REPOSITORY],
            cwd=REPO_ROOT,
            check=True,
        )
        logger.info(f"[github] {tag} already existed; replaced its zip")
        return

    with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False, encoding="utf-8") as notes_file:
        notes_file.write(notes)
    try:
        subprocess.run(
            [
                "gh", "release", "create", tag, str(archive_path),
                "--repo", GITHUB_REPOSITORY,
                "--title", title,
                "--notes-file", notes_file.name,
                "--verify-tag",
            ],
            cwd=REPO_ROOT,
            check=True,
        )
    finally:
        Path(notes_file.name).unlink()
    logger.info(f"[github] Created release {title}")


def release(mod: str) -> None:
    manifest = load_manifest(mod)
    version = manifest["Version"]
    title = f"{manifest['Title']} {version}"
    tag = f"{mod}-v{version}"

    require_releasable_tree()
    item_id = require_item_id(mod)
    if changelog_entry(mod, version) is None:
        raise SystemExit(f"Changelog.txt has no line for {version}.")

    logger.info(f"[release] Releasing {title} as {tag}")
    if (MODS_DIR / mod / "Tests").is_dir():
        run_dotnet(["test", f"mods/{mod}/Tests"], None)
    else:
        logger.info(f"[release] {mod} has no unit tests to run")

    # The Workshop first: it builds the mod, and a failed upload is the step most likely to need a retry.
    publish(mod, visibility=None)
    archive_path = zip_shipped_folder(DIST_DIR / shipped_name(mod), version)
    tag_release(tag, title)
    create_github_release(tag, title, release_notes(mod, manifest, item_id), archive_path)
    logger.info(f"[release] Done: https://github.com/{GITHUB_REPOSITORY}/releases/tag/{tag}")


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("mod")
    parsed = parser.parse_args()
    resolve_mods([parsed.mod], use_all=False)
    try:
        release(parsed.mod)
    except subprocess.CalledProcessError as error:
        details = error.stderr.strip() if isinstance(error.stderr, str) else ""
        command = " ".join(map(str, error.cmd))
        raise SystemExit(f"Command failed with exit code {error.returncode}: {command}\n{details}")


if __name__ == "__main__":
    main()
