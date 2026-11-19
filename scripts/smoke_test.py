#!/usr/bin/env python3
"""Boot Volcanoids with a mod's developer capture, then report its log and screenshots.

Linux only. The game runs straight from its install (not through Steam), loads a save, lets the mod photograph the
requested times of day, and quits. On Hyprland it opens on the hidden special:agent workspace, so it never takes
over the screen.

The user's saves and settings are backed up before the run and put back after it, so an autosave during the test
can never replace a real one. Usage:

    scripts/smoke_test.py day-night-cycle --save Slot02 --shots "15,29,31,40@0.5,40@0"
"""

from __future__ import annotations

import argparse
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

from mods import is_game_running, read_settings, require_path  # noqa: E402

STEAM_APP_ID = "951440"
SILENT_SINK = "volcanoid_mods_smoke_test"
GAME_PROCESS_PATTERN = "[V]olcanoids.x86_64"
LOG_PREFIX = "[DayNightCycle]"
# The load-time scene report ("probe DC (0.186, ...") and every capture ("baseline ambient 0.186").
BASELINE_PATTERN = re.compile(r"(?:probe DC \(|baseline ambient )(-?[0-9.]+)")

# Everything the game might write during a session that belongs to the player. Unity rotates Player.log into
# Player-prev.log at startup even when -logFile sends this run's log elsewhere, so both logs are kept too.
PROTECTED_ENTRIES = ("Saves", "options.json", "prefs", "Player.log", "Player-prev.log")


def back_up(data_dir: Path, backup_dir: Path) -> None:
    backup_dir.mkdir(parents=True)
    for name in PROTECTED_ENTRIES:
        source = data_dir / name
        if source.is_dir():
            shutil.copytree(source, backup_dir / name)
        elif source.is_file():
            shutil.copy2(source, backup_dir / name)
    logger.info(f"[backup] Saved {', '.join(PROTECTED_ENTRIES)} to {backup_dir}")


def restore(data_dir: Path, backup_dir: Path) -> None:
    for name in PROTECTED_ENTRIES:
        saved = backup_dir / name
        target = data_dir / name
        if not saved.exists():
            continue
        if target.is_dir():
            shutil.rmtree(target)
        elif target.exists():
            target.unlink()
        if saved.is_dir():
            shutil.copytree(saved, target)
        else:
            shutil.copy2(saved, target)
    logger.info(f"[backup] Restored {', '.join(PROTECTED_ENTRIES)} from {backup_dir}")


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


def launch(game_binary: Path, game_arguments: list[str], run_dir: Path, is_silent: bool) -> None:
    """Start the game detached, on Hyprland's hidden special:agent workspace when available."""
    launcher = run_dir / "launch.sh"
    launcher.write_text(
        "#!/usr/bin/env bash\n"
        f"cd {shlex.quote(str(game_binary.parent))}\n"
        # Setting the app ID stops the Steam API relaunching the game through the Steam client.
        f"export SteamAppId={STEAM_APP_ID} SteamGameId={STEAM_APP_ID}\n"
        + (f"export PULSE_SINK={SILENT_SINK}\n" if is_silent else "")
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
    """Stop the game and wait until it has fully exited.

    Unity writes prefs while shutting down, so restoring the player's files before the process is gone lets the
    dying game overwrite them.
    """
    for signal_name, grace_seconds in (("TERM", 30), ("KILL", 10)):
        subprocess.run(["pkill", f"-{signal_name}", "-f", GAME_PROCESS_PATTERN])
        deadline = time.monotonic() + grace_seconds
        while is_game_running() and time.monotonic() < deadline:
            time.sleep(1)
        if not is_game_running():
            return
        logger.warning(f"[launch] The game outlived SIG{signal_name} for {grace_seconds}s")
    logger.error("[launch] The game would not exit; if it writes on its way out, compare the files with the backup")


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


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("mod", help="mod folder under mods/, already installed and enabled")
    parser.add_argument("--save", required=True, help="save slot to load, e.g. Slot02")
    parser.add_argument(
        "--save-file",
        type=Path,
        help="load this .sav from elsewhere, copied in as --save for the run only (a new slot name)",
    )
    parser.add_argument("--shots", required=True, help='minutes since sunrise, e.g. "15,30,40@0.5"')
    parser.add_argument("--timeout", type=float, default=300, help="seconds before the game is killed")
    parser.add_argument(
        "--game-args",
        default="",
        help='extra arguments for the game, e.g. "-daynight-at 15" to hold noon from the first frame',
    )
    parsed = parser.parse_args()

    if is_game_running():
        raise SystemExit("Volcanoids is already running. Close it first.")
    settings = read_settings()
    game_dir = require_path(settings, "VOLCANOIDS_DIR")
    data_dir = require_path(settings, "VOLCANOIDS_DATA_DIR")
    slot_path = data_dir / "Saves" / f"{parsed.save}.sav"
    if parsed.save_file:
        if not parsed.save_file.is_file():
            raise SystemExit(f"--save-file {parsed.save_file} does not exist")
        if slot_path.exists():
            raise SystemExit(f"{parsed.save} is a real save; give --save-file a slot name that is not taken")
    elif not slot_path.is_file():
        raise SystemExit(f"No save named {parsed.save} in {data_dir / 'Saves'}")

    run_dir = REPO_ROOT / "dist" / "smoke" / datetime.now().strftime("%Y%m%d-%H%M%S")
    capture_dir = run_dir / "captures"
    log_path = run_dir / "Player.log"
    run_dir.mkdir(parents=True)
    back_up(data_dir, run_dir / "backup")
    if parsed.save_file:
        # Copied in after the backup, so restoring the Saves folder afterwards removes it again.
        shutil.copy2(parsed.save_file, slot_path)

    game_arguments = [
        # Steam starts the Linux build on OpenGL Core; match it so the test renders what players see.
        "-force-glcore",
        "+load", parsed.save,
        # The save's mod list does not include the mod under test; without this the game would restart to match it.
        "-mods_resolved",
        "-logFile", str(log_path),
        "-daynight-capture", str(capture_dir),
        "-daynight-shots", parsed.shots,
        *shlex.split(parsed.game_args),
    ]
    sink_module = open_silent_sink()
    try:
        launch(game_dir / "Volcanoids.x86_64", game_arguments, run_dir, is_silent=sink_module is not None)
        if not wait_for_exit(parsed.timeout):
            logger.warning(f"[launch] Still running after {parsed.timeout:.0f}s; stopping it")
            stop_game()
    finally:
        close_silent_sink(sink_module)
        restore(data_dir, run_dir / "backup")
    report(log_path, capture_dir)


if __name__ == "__main__":
    main()
