#!/usr/bin/env python3
"""Build, test, validate, install and package the Volcanoids mods in this repository.

Each mod lives in mods/<name>/: a mod.csproj, a Content/ folder whose files ship exactly as they are (the game's
.manifest.json and the Workshop thumbnail), and optionally a Tests/ project of plain .NET unit tests. A mod ships
as a folder named after it in PascalCase (day-night-cycle ships as DayNightCycle), which is also its folder under
the game's LocalMods/.

Machine paths come from .env (see .env.example). The .NET SDK is used when installed; otherwise every dotnet
command runs in the pinned SDK container, so Docker is the only hard requirement.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ElementTree
import zipfile
from pathlib import Path

logger = logging.getLogger(__name__)

REPO_ROOT = Path(__file__).resolve().parent.parent
MODS_DIR = REPO_ROOT / "mods"
DIST_DIR = REPO_ROOT / "dist"

# Pinned to match CI's setup-dotnet version.
DOTNET_IMAGE = "mcr.microsoft.com/dotnet/sdk:9.0.300"

# ModMultiplayerMode in the game's Volcanoids.dll; the manifest stores it as its integer value.
MULTIPLAYER_MODES = {
    0: "ClientServer",
    1: "ServerOnly",
    2: "ClientOnly",
    3: "SinglePlayer",
}

REQUIRED_MOD_FILES = (
    "mod.csproj",
    "README.md",
    "Changelog.txt",
    "LICENSE",
    "Content/.manifest.json",
)

# The game and the Workshop accept either; the Workshop caps previews at 1 MB.
THUMBNAIL_NAMES = (".thumb.png", ".thumb.jpg")
THUMBNAIL_MAX_BYTES = 1024 * 1024

# The game writes this next to a local mod after its first Workshop upload; it holds the Workshop item ID.
WORKSHOP_ID_FILE = ".workshop"


# ========================================================================================
# Configuration
# ========================================================================================


def read_settings() -> dict[str, str]:
    """Read .env, letting real environment variables override it."""
    settings: dict[str, str] = {}
    env_path = REPO_ROOT / ".env"
    if env_path.exists():
        for line in env_path.read_text(encoding="utf-8").splitlines():
            stripped = line.strip()
            if not stripped or stripped.startswith("#") or "=" not in stripped:
                continue
            key, value = stripped.split("=", 1)
            settings[key.strip()] = value.strip()
    else:
        logger.debug(f"[settings] No .env at {env_path}; using environment variables only")

    for key in ("VOLCANOIDS_DIR", "VOLCANOIDS_DATA_DIR"):
        if key in os.environ:
            settings[key] = os.environ[key]
    return settings


def require_path(settings: dict[str, str], key: str) -> Path:
    """Return a configured folder, failing with a pointer to .env.example when it is unset or missing."""
    value = settings.get(key)
    if not value:
        raise SystemExit(f"{key} is not set. Copy .env.example to .env and fill it in.")
    path = Path(value).expanduser().resolve()
    if not path.is_dir():
        raise SystemExit(f"{key} points at {path}, which is not a folder.")
    return path


# ========================================================================================
# Mods
# ========================================================================================


def discover_mods() -> list[str]:
    """Every folder under mods/ holding a mod.csproj."""
    return sorted(path.parent.name for path in MODS_DIR.glob("*/mod.csproj"))


def resolve_mods(names: list[str], use_all: bool) -> list[str]:
    known = discover_mods()
    if use_all:
        return known
    if not names:
        raise SystemExit(f"Name a mod or pass --all. Known mods: {', '.join(known)}")
    unknown = [name for name in names if name not in known]
    if unknown:
        raise SystemExit(f"Unknown mod(s): {', '.join(unknown)}. Known mods: {', '.join(known)}")
    return names


def shipped_name(mod: str) -> str:
    """The folder a mod ships as: day-night-cycle becomes DayNightCycle."""
    return "".join(part.capitalize() for part in mod.split("-"))


def load_manifest(mod: str) -> dict:
    manifest_path = MODS_DIR / mod / "Content" / ".manifest.json"
    return json.loads(manifest_path.read_text(encoding="utf-8"))


def assembly_name(mod: str) -> str:
    project = ElementTree.parse(MODS_DIR / mod / "mod.csproj")
    name = project.findtext(".//AssemblyName")
    if not name:
        raise SystemExit(f"mods/{mod}/mod.csproj has no <AssemblyName>.")
    return name


# ========================================================================================
# dotnet
# ========================================================================================


def has_local_sdk() -> bool:
    if not shutil.which("dotnet"):
        return False
    result = subprocess.run(["dotnet", "--list-sdks"], capture_output=True, text=True)
    return bool(result.stdout.strip())


def run_dotnet(arguments: list[str], volcanoids_dir: Path | None) -> None:
    """Run a dotnet command from the repo root, locally or in the pinned SDK container.

    Paths in arguments must be relative to the repo root, so they mean the same thing in both places.
    """
    if has_local_sdk():
        command = ["dotnet", *arguments]
        if volcanoids_dir:
            command.append(f"-p:VOLCANOIDS_DIR={volcanoids_dir}")
        logger.info(f"[dotnet] {' '.join(command)}")
        subprocess.run(command, cwd=REPO_ROOT, check=True)
        return

    if sys.platform == "win32":
        raise SystemExit("No .NET SDK found. Install the .NET 9 SDK, or run this from WSL with Docker.")

    # Share the host's NuGet cache so the container does not download packages on every run. It is mounted
    # outside HOME: a mount inside it would make Docker create HOME's .nuget folder as root.
    nuget_cache = Path.home() / ".nuget" / "packages"
    nuget_cache.mkdir(parents=True, exist_ok=True)
    command = [
        "docker", "run", "--rm",
        "--user", f"{os.getuid()}:{os.getgid()}",
        "--env", "HOME=/tmp",
        "--env", "NUGET_PACKAGES=/nuget-packages",
        "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
        "--env", "DOTNET_NOLOGO=1",
        "--volume", f"{REPO_ROOT}:/repo",
        "--volume", f"{nuget_cache}:/nuget-packages",
        "--workdir", "/repo",
    ]
    dotnet_arguments = list(arguments)
    if volcanoids_dir:
        command += ["--volume", f"{volcanoids_dir}:/volcanoids:ro"]
        dotnet_arguments.append("-p:VOLCANOIDS_DIR=/volcanoids")
    command += [DOTNET_IMAGE, "dotnet", *dotnet_arguments]
    logger.info(f"[dotnet] (in {DOTNET_IMAGE}) dotnet {' '.join(dotnet_arguments)}")
    subprocess.run(command, check=True)


# ========================================================================================
# Commands
# ========================================================================================


def command_list(arguments: argparse.Namespace) -> None:
    for mod in discover_mods():
        manifest = load_manifest(mod)
        mode = MULTIPLAYER_MODES.get(manifest.get("MultiplayerMode"), "?")
        print(f"{mod:24} {shipped_name(mod):24} {manifest.get('Version', '?'):12} {mode}")


def command_validate(arguments: argparse.Namespace) -> None:
    failures: list[str] = []
    for mod in resolve_mods(arguments.mods, arguments.all):
        failures += [f"{mod}: {problem}" for problem in validate_mod(mod)]

    if failures:
        for failure in failures:
            logger.error(f"[validate] {failure}")
        raise SystemExit(f"{len(failures)} problem(s) found.")
    logger.info("[validate] All checks passed.")


def validate_mod(mod: str) -> list[str]:
    """Check a mod has every file the build and the Workshop need, and a manifest the game will accept."""
    mod_dir = MODS_DIR / mod
    problems = [f"missing {relative}" for relative in REQUIRED_MOD_FILES if not (mod_dir / relative).is_file()]

    thumbnails = [mod_dir / "Content" / name for name in THUMBNAIL_NAMES if (mod_dir / "Content" / name).is_file()]
    if not thumbnails:
        problems.append(f"missing a Workshop thumbnail: Content/{' or Content/'.join(THUMBNAIL_NAMES)}")
    for thumbnail in thumbnails:
        if thumbnail.stat().st_size > THUMBNAIL_MAX_BYTES:
            problems.append(f"{thumbnail.name} is over the Workshop's 1 MB preview limit")

    if (mod_dir / "Content" / ".manifest.json").is_file():
        problems += validate_manifest(mod)
    return problems


def validate_manifest(mod: str) -> list[str]:
    try:
        manifest = load_manifest(mod)
    except json.JSONDecodeError as error:
        return [f".manifest.json is not valid JSON: {error}"]

    problems = []
    unique_id = manifest.get("UniqueModId", "")
    if not unique_id.startswith("JalapenoLabs.") or unique_id.count(".") != 1:
        problems.append(f"UniqueModId '{unique_id}' should be 'JalapenoLabs.<ModName>'")
    if not manifest.get("Title"):
        problems.append("manifest has no Title")
    if not manifest.get("Version"):
        problems.append("manifest has no Version")
    if manifest.get("MultiplayerMode") not in MULTIPLAYER_MODES:
        problems.append(f"MultiplayerMode must be one of {MULTIPLAYER_MODES} (the game reads it as an integer)")

    expected_assembly = f"{assembly_name(mod)}.dll"
    if manifest.get("Assemblies") != [expected_assembly]:
        problems.append(f"Assemblies should be ['{expected_assembly}'], matching mod.csproj's AssemblyName")
    return problems


def command_build(arguments: argparse.Namespace) -> None:
    volcanoids_dir = require_path(read_settings(), "VOLCANOIDS_DIR")
    for mod in resolve_mods(arguments.mods, arguments.all):
        run_dotnet(["build", f"mods/{mod}/mod.csproj", "--configuration", "Release"], volcanoids_dir)


def command_test(arguments: argparse.Namespace) -> None:
    for mod in resolve_mods(arguments.mods, arguments.all):
        tests_dir = MODS_DIR / mod / "Tests"
        if not tests_dir.is_dir():
            logger.info(f"[test] {mod} has no Tests/ project; skipping")
            continue
        run_dotnet(["test", f"mods/{mod}/Tests"], None)


def assemble(mod: str) -> Path:
    """Build a mod and lay out its shipped folder in dist/: the Content/ files plus the compiled assembly."""
    volcanoids_dir = require_path(read_settings(), "VOLCANOIDS_DIR")
    run_dotnet(["build", f"mods/{mod}/mod.csproj", "--configuration", "Release"], volcanoids_dir)

    shipped_dir = DIST_DIR / shipped_name(mod)
    if shipped_dir.exists():
        shutil.rmtree(shipped_dir)
    shutil.copytree(MODS_DIR / mod / "Content", shipped_dir)
    assembly = f"{assembly_name(mod)}.dll"
    shutil.copy2(MODS_DIR / mod / "bin" / assembly, shipped_dir / assembly)
    shipped_files = sorted(path.name for path in shipped_dir.iterdir())
    logger.info(f"[assemble] {shipped_dir.relative_to(REPO_ROOT)}: {shipped_files}")
    return shipped_dir


def command_install(arguments: argparse.Namespace) -> None:
    """Copy a freshly built mod into the game's LocalMods/, keeping the Workshop ID the game wrote there."""
    data_dir = require_path(read_settings(), "VOLCANOIDS_DATA_DIR")
    shipped_dir = assemble(arguments.mod)
    target = data_dir / "LocalMods" / shipped_dir.name

    workshop_id = None
    existing_id_file = target / WORKSHOP_ID_FILE
    if existing_id_file.is_file():
        workshop_id = existing_id_file.read_text(encoding="utf-8").strip()
        if not (shipped_dir / WORKSHOP_ID_FILE).is_file():
            logger.warning(
                f"[install] {existing_id_file} holds Workshop ID {workshop_id}, but Content/ has no "
                f"{WORKSHOP_ID_FILE}. Copy it into mods/{arguments.mod}/Content/ and commit it, or a fresh "
                "install elsewhere will upload the mod as a new Workshop item."
            )

    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(shipped_dir, target)
    if workshop_id and not (target / WORKSHOP_ID_FILE).is_file():
        (target / WORKSHOP_ID_FILE).write_text(workshop_id, encoding="utf-8")

    if is_game_running():
        logger.warning("[install] Volcanoids is running; it loads the new build after a restart.")
    logger.info(f"[install] Installed {target}")


def command_package(arguments: argparse.Namespace) -> None:
    shipped_dir = assemble(arguments.mod)
    version = load_manifest(arguments.mod)["Version"]
    archive_path = DIST_DIR / f"{shipped_dir.name}-{version}.zip"
    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(shipped_dir.rglob("*")):
            archive.write(path, path.relative_to(DIST_DIR))
    logger.info(f"[package] Wrote {archive_path.relative_to(REPO_ROOT)}")


def command_enable(arguments: argparse.Namespace, enabled: bool) -> None:
    """Add a local mod to, or remove it from, the game's active mod list (mods.json).

    The in-game Mods menu does the same thing; this exists for unattended test runs.
    """
    if is_game_running():
        raise SystemExit("Volcanoids is running and rewrites mods.json when it applies mods. Close it first.")

    data_dir = require_path(read_settings(), "VOLCANOIDS_DATA_DIR")
    config_path = data_dir / "mods.json"
    config = json.loads(config_path.read_text(encoding="utf-8")) if config_path.exists() else {"m_mods": []}
    local_dir = f"/{shipped_name(arguments.mod)}"
    others = [entry for entry in config.get("m_mods", []) if entry.get("LocalDir") != local_dir]

    if enabled:
        if not (data_dir / "LocalMods" / shipped_name(arguments.mod)).is_dir():
            raise SystemExit(f"{arguments.mod} is not installed. Run: scripts/mods.py install {arguments.mod}")
        others.append({
            "Id": 0,
            "LocalDir": local_dir,
            "Version": 0,
            "RequiredOnClients": False,
            "Title": load_manifest(arguments.mod)["Title"],
        })
    config["m_mods"] = others
    config_path.write_text(json.dumps(config, indent=4), encoding="utf-8")
    logger.info(f"[{'enable' if enabled else 'disable'}] {config_path}: {[entry['LocalDir'] for entry in others]}")


def is_game_running() -> bool:
    if not shutil.which("pgrep"):
        logger.debug("[game] pgrep is unavailable; assuming Volcanoids is not running")
        return False
    # The bracket keeps pgrep from matching its own command line.
    return subprocess.run(["pgrep", "-f", "[V]olcanoids.x86_64|[V]olcanoids.exe"], capture_output=True).returncode == 0


# ####################### #
#           CLI           #
# ####################### #


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)

    commands.add_parser("list", help="list the mods").set_defaults(handler=command_list)
    for name, handler, summary in (
        ("validate", command_validate, "check required files and manifests"),
        ("build", command_build, "compile mods against the installed game"),
        ("test", command_test, "run mods' unit tests (no game needed)"),
    ):
        subcommand = commands.add_parser(name, help=summary)
        subcommand.add_argument("mods", nargs="*")
        subcommand.add_argument("--all", action="store_true", help="every mod")
        subcommand.set_defaults(handler=handler)

    for name, handler, summary in (
        ("install", command_install, "build and copy a mod into the game's LocalMods/"),
        ("package", command_package, "build and zip a mod's shipped folder into dist/"),
        ("enable", lambda parsed: command_enable(parsed, enabled=True), "add an installed mod to mods.json"),
        ("disable", lambda parsed: command_enable(parsed, enabled=False), "remove a mod from mods.json"),
    ):
        subcommand = commands.add_parser(name, help=summary)
        subcommand.add_argument("mod")
        subcommand.set_defaults(handler=handler)

    parsed = parser.parse_args()
    try:
        parsed.handler(parsed)
    except subprocess.CalledProcessError as error:
        raise SystemExit(f"Command failed with exit code {error.returncode}: {' '.join(map(str, error.cmd))}")


if __name__ == "__main__":
    main()
