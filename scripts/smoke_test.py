#!/usr/bin/env python3
"""Boot Volcanoids with a mod's developer capture, then report its log and screenshots.

Linux only. The game runs straight from its install (not through Steam), loads a save, lets the mod photograph the
requested times of day, and quits. On Hyprland it opens on the hidden special:agent workspace with its sound sent to
a null sink, so it neither takes over the screen nor makes noise.

The game never touches the player's own data folder. Unity keeps its data under $XDG_CONFIG_HOME on Linux, so each
run points that at a sandbox in dist/smoke/<time>/ holding copies of the player's settings, the save under test and
the mod. This matters beyond tidiness: Volcanoids syncs its data folder through Steam Cloud whenever a process runs
as the game, so a test that wrote there (even temporarily) could push its autosaves into the player's cloud.

    scripts/smoke_test.py day-night-cycle --save Slot02 --shots "15,29,31,40@0.5,40@0"
    scripts/smoke_test.py day-night-cycle --save-file spot.sav --shots "40@0.5"     # a save from anywhere
    scripts/smoke_test.py day-night-cycle --save Slot02 --shots "40" --workshop     # the subscribed Workshop copy
"""

from __future__ import annotations

import argparse
import hashlib
import json
import logging
import os
import re
import shlex
import shutil
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

logger = logging.getLogger(__name__)

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts"))

from mods import (  # noqa: E402
    assemble,
    is_game_running,
    load_manifest,
    read_settings,
    read_workshop_item_id,
    require_path,
    resolve_mods,
    shipped_name,
)

STEAM_APP_ID = "951440"
SILENT_SINK = "volcanoid_mods_smoke_test"
GAME_PROCESS_PATTERN = "[V]olcanoids.x86_64"
LOG_PREFIX = "[DayNightCycle]"
# The load-time scene report ("probe DC (0.186, ...") and every capture ("baseline ambient 0.186").
BASELINE_PATTERN = re.compile(r"(?:probe DC \(|baseline ambient )(-?[0-9.]+)")

# The player's settings, copied into the sandbox so the test renders with their graphics options.
SETTINGS_FILES = ("options.json", "prefs")


# ========================================================================================
# The sandbox
# ========================================================================================


def build_sandbox(run_dir: Path, data_dir: Path, mod: str, save: Path, use_workshop: bool) -> Path:
    """Lay out a private data folder for the test game and return its XDG_CONFIG_HOME.

    The game's data folder is <XDG_CONFIG_HOME>/unity3d/<company>/<product>; the same company/product path as the
    player's folder is kept, so the game finds it.
    """
    config_home = run_dir / "config"
    sandbox = config_home / data_dir.relative_to(data_dir.parents[2])
    (sandbox / "Saves").mkdir(parents=True)
    for name in SETTINGS_FILES:
        if (data_dir / name).is_file():
            shutil.copy2(data_dir / name, sandbox / name)
    shutil.copy2(save, sandbox / "Saves" / save.name)

    title = load_manifest(mod)["Title"]
    if use_workshop:
        item_id = read_workshop_item_id(mod)
        if not item_id:
            raise SystemExit(f"{mod} has no Workshop item to test.")
        entry = {"Id": int(item_id), "LocalDir": "", "Version": 0, "RequiredOnClients": False, "Title": title}
    else:
        shipped_dir = assemble(mod)
        shutil.copytree(shipped_dir, sandbox / "LocalMods" / shipped_dir.name)
        local_dir = f"/{shipped_name(mod)}"
        entry = {"Id": 0, "LocalDir": local_dir, "Version": 0, "RequiredOnClients": False, "Title": title}
    (sandbox / "mods.json").write_text(json.dumps({"m_mods": [entry]}, indent=4), encoding="utf-8")
    logger.info(f"[sandbox] {sandbox}: {'Workshop' if use_workshop else 'local'} copy of {mod}, save {save.name}")
    return config_home


def fingerprint(data_dir: Path) -> str:
    """A hash over every file's path, size and modification time in the player's data folder.

    Compared before and after a run, it proves the test left the player's files alone. Steam Cloud can still change
    them on its own (downloading saves made on another machine), so a change is reported, not undone.
    """
    digest = hashlib.sha256()
    for path in sorted(data_dir.rglob("*")):
        if path.is_file():
            stat = path.stat()
            digest.update(f"{path.relative_to(data_dir)}|{stat.st_size}|{stat.st_mtime_ns}\n".encode())
    return digest.hexdigest()


# ========================================================================================
# Running the game
# ========================================================================================


def open_silent_sink() -> str | None:
    """Add a PulseAudio null sink for the game's sound. Returns its module ID, or None without pactl.

    The mod mutes capture runs itself, but the game re-applies the player's volume during startup, so sound still
    reached the speakers. A null sink silences the process no matter what the game does.
    """
    if not shutil.which("pactl"):
        logger.warning("[audio] pactl is unavailable; the game's sound will play during the run")
        return None
    result = subprocess.run(
        ["pactl", "load-module", "module-null-sink", f"sink_name={SILENT_SINK}"],
        capture_output=True, text=True, check=True,
    )
    return result.stdout.strip()


def close_silent_sink(module_id: str | None) -> None:
    if module_id:
        subprocess.run(["pactl", "unload-module", module_id], check=False)
        logger.info("[audio] Removed the silent sink")


def launch(game_binary: Path, game_arguments: list[str], run_dir: Path, config_home: Path, is_silent: bool) -> None:
    """Start the game detached, on Hyprland's hidden special:agent workspace when available."""
    launcher = run_dir / "launch.sh"
    exports = {
        # Setting the app ID stops the Steam API relaunching the game through the Steam client.
        "SteamAppId": STEAM_APP_ID,
        "SteamGameId": STEAM_APP_ID,
        "XDG_CONFIG_HOME": str(config_home),
    }
    if is_silent:
        exports["PULSE_SINK"] = SILENT_SINK
    export_lines = "".join(f"export {name}={shlex.quote(value)}\n" for name, value in exports.items())
    launcher.write_text(
        "#!/usr/bin/env bash\n"
        f"cd {shlex.quote(str(game_binary.parent))}\n"
        + export_lines
        + f"exec {shlex.quote(str(game_binary))} {' '.join(shlex.quote(argument) for argument in game_arguments)}\n",
        encoding="utf-8",
    )
    launcher.chmod(0o755)

    if shutil.which("hyprctl"):
        hyprland_call = f"hl.exec_cmd('{launcher}', {{ workspace = 'special:agent silent' }})"
        subprocess.run(["hyprctl", "eval", hyprland_call], check=True, capture_output=True)
        logger.info("[launch] Started on the hidden special:agent workspace")
    else:
        subprocess.Popen([str(launcher)], start_new_session=True)
        logger.info("[launch] Started (no Hyprland, so the window is visible)")


def wait_for_exit(timeout_seconds: float) -> bool:
    """Wait for the game to start and then quit. Returns False if it was still running at the timeout."""
    deadline = time.monotonic() + timeout_seconds
    start_deadline = time.monotonic() + 30
    while not is_game_running():
        if time.monotonic() > start_deadline:
            logger.error("[launch] The game did not start within 30s")
            return True
        time.sleep(1)
    while is_game_running():
        if time.monotonic() > deadline:
            return False
        time.sleep(2)
    return True


def stop_game() -> None:
    """Stop the game, escalating to SIGKILL, and wait until it has fully exited."""
    for signal_name, grace_seconds in (("TERM", 30), ("KILL", 10)):
        subprocess.run(["pkill", f"-{signal_name}", "-f", GAME_PROCESS_PATTERN])
        deadline = time.monotonic() + grace_seconds
        while is_game_running() and time.monotonic() < deadline:
            time.sleep(1)
        if not is_game_running():
            return
        logger.warning(f"[launch] The game outlived SIG{signal_name} for {grace_seconds}s")
    logger.error("[launch] The game would not exit; stop it by hand")


# ========================================================================================
# Report
# ========================================================================================


def report(log_path: Path, capture_dir: Path) -> None:
    if not log_path.exists():
        logger.error(f"[report] No log at {log_path}; the game may not have started")
        return
    lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()
    interesting = [
        line for line in lines
        if LOG_PREFIX in line or "Exception" in line or "Mod loaded" in line or "Mod loading failed" in line
    ]
    print("\n".join(interesting[-120:]))
    shots = sorted(capture_dir.glob("*.png"))
    logger.info(f"[report] {len(shots)} screenshot(s) in {capture_dir}")
    check_lighting_baseline(lines)


def check_lighting_baseline(lines: list[str]) -> None:
    """Fail loudly if the game's own ambient light changed during the run.

    The night must only ever change lighting inside a camera's render. If the baseline the capture logs between
    renders moves, the night has leaked into the game's state: the bug where a lossy ambient-probe round trip drove
    the island's ambient light to black after the first night, and kept it there all day.
    """
    # The scene report's value comes first: it is read at load, before the night has run a single frame.
    baselines = [float(match.group(1)) for line in lines if (match := BASELINE_PATTERN.search(line))]
    if len(baselines) < 2:
        logger.warning("[report] Too few ambient baselines were logged; the leak check did not run")
        return
    if max(baselines) - min(baselines) > 0.01 * max(baselines):
        logger.error(
            f"[report] LEAK: the game's ambient light moved from {baselines[0]} at load to {baselines[-1]}"
        )
        raise SystemExit(1)
    logger.info(f"[report] The game's ambient light held at {baselines[0]} across {len(baselines)} captures")


# ####################### #
#           CLI           #
# ####################### #


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("mod", help="mod folder under mods/")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--save", help="one of the player's save slots to copy in, e.g. Slot02")
    source.add_argument("--save-file", type=Path, help="a .sav from anywhere, for shots from a fixed spot")
    parser.add_argument("--shots", required=True, help='minutes since sunrise, e.g. "15,30,40@0.5"')
    parser.add_argument("--workshop", action="store_true", help="test the subscribed Workshop copy, not a fresh build")
    parser.add_argument("--timeout", type=float, default=300, help="seconds before the game is killed")
    parser.add_argument(
        "--game-args",
        default="",
        help='extra arguments for the game, e.g. "-daynight-at 15" to hold noon from the first frame',
    )
    parsed = parser.parse_args()
    resolve_mods([parsed.mod], use_all=False)

    if is_game_running():
        raise SystemExit("Volcanoids is already running. Close it first.")
    settings = read_settings()
    game_dir = require_path(settings, "VOLCANOIDS_DIR")
    data_dir = require_path(settings, "VOLCANOIDS_DATA_DIR")
    save = parsed.save_file or data_dir / "Saves" / f"{parsed.save}.sav"
    if not save.is_file():
        raise SystemExit(f"No save at {save}")

    run_dir = REPO_ROOT / "dist" / "smoke" / datetime.now().strftime("%Y%m%d-%H%M%S")
    capture_dir = run_dir / "captures"
    run_dir.mkdir(parents=True)
    config_home = build_sandbox(run_dir, data_dir, parsed.mod, save, parsed.workshop)
    sandbox = config_home / data_dir.relative_to(data_dir.parents[2])
    player_files_before = fingerprint(data_dir)

    game_arguments = [
        # Steam starts the Linux build on OpenGL Core; match it so the test renders what players see.
        "-force-glcore",
        "+load", save.stem,
        # The save's own mod list may differ from the sandbox's; without this the game would restart to match it.
        "-mods_resolved",
        "-daynight-capture", str(capture_dir),
        "-daynight-shots", parsed.shots,
        *shlex.split(parsed.game_args),
    ]
    sink_module = open_silent_sink()
    try:
        launch(game_dir / "Volcanoids.x86_64", game_arguments, run_dir, config_home, sink_module is not None)
        if not wait_for_exit(parsed.timeout):
            logger.warning(f"[launch] Still running after {parsed.timeout:.0f}s; stopping it")
            stop_game()
    finally:
        close_silent_sink(sink_module)

    if fingerprint(data_dir) != player_files_before:
        logger.warning(f"[sandbox] Files in {data_dir} changed during the run. The test game writes only to the "
                       "sandbox, so this was most likely Steam Cloud syncing; check before trusting the folder.")
    else:
        logger.info(f"[sandbox] {data_dir} was untouched")
    report(sandbox / "Player.log", capture_dir)


if __name__ == "__main__":
    main()
