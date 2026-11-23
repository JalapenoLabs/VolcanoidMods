#!/usr/bin/env python3
"""Render Rain & Thunderstorms' rain sounds from the CC0 recordings beside this script.

The mod plays four loops: light and heavy rain out in the open, and the same two heard from inside a drillship or a
shelter. The open-air loops are the recordings themselves, brought to a common loudness. The indoor loops are the
same recordings as a steel hull would let them through: the hiss of the drops filtered away, a low drumming boosted,
and a few short reflections off the cabin's walls. Baking the muffling into its own loop lets the game crossfade
between open and sheltered rain instead of sweeping a live filter, which warbled.

Each loop is rendered twice through and only its second pass kept, so the filters' start-up transient never lands on
the loop's seam and the result loops as cleanly as the recording did.

Needs ffmpeg with libvorbis on PATH (rendered with ffmpeg 9.0.2):

    python3 mods/weather/Recordings/render.py
"""

from __future__ import annotations

import logging
import re
import subprocess
import sys
from pathlib import Path

logger = logging.getLogger(__name__)

RECORDINGS_DIR = Path(__file__).resolve().parent
SOUNDS_DIR = RECORDINGS_DIR.parent / "Content" / "Sounds"

# Through a steel hull: rumble and drumming survive, the hiss of the drops does not.
HULL_MUFFLING = ",".join((
    "highpass=f=35",
    "lowpass=f=750",
    "lowpass=f=750",
    "equalizer=f=170:width_type=o:width=1.2:g=5",
    "aecho=0.85:0.6:19|37:0.22|0.12",
))

# (recording, shipped loop, filter chain or "" for none, target loudness in LUFS). The mod sets the playing volumes;
# these targets only put the loops on a common footing, quieter indoors where the hull swallows the top end.
RENDERS = (
    ("ylmir-rain-light.ogg", "RainLight.ogg", "", -20.0),
    ("ylmir-rain-heavy.ogg", "RainHeavy.ogg", "", -16.0),
    ("ylmir-rain-light.ogg", "RainLightIndoor.ogg", HULL_MUFFLING, -24.0),
    ("ylmir-rain-heavy.ogg", "RainHeavyIndoor.ogg", HULL_MUFFLING, -20.0),
)


def render(recording: Path, target: Path, chain: str, loudness: float) -> None:
    """Render one loop: filter it over a second pass of itself, then bring it to the target loudness."""
    # The loop's exact length in samples, decoded: a container's duration is rounded, and a loop cut even a few samples
    # long or short no longer meets itself at the seam.
    decoded = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", str(recording), "-ac", "1", "-ar", "44100", "-f", "s16le", "-"],
        check=True,
        capture_output=True,
    )
    length = len(decoded.stdout) // 2
    # The input is looped once; keep exactly the second pass, so every filter has already warmed up on the loop.
    second_pass = f"atrim=start_sample={length}:end_sample={2 * length},asetpts=PTS-STARTPTS"
    filters = f"{chain},{second_pass}" if chain else second_pass

    measure = subprocess.run(
        [
            "ffmpeg", "-v", "info",
            "-stream_loop", "1", "-i", str(recording),
            "-af", f"aresample=44100,{filters},ebur128",
            "-f", "null", "-",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    integrated = re.findall(r"I:\s+(-?[\d.]+) LUFS", measure.stderr)
    if not integrated:
        raise RuntimeError(f"ffmpeg reported no loudness for {recording.name}")
    gain = loudness - float(integrated[-1])

    subprocess.run(
        [
            "ffmpeg", "-v", "error", "-y",
            "-stream_loop", "1", "-i", str(recording),
            "-af", f"aresample=44100,{filters},volume={gain:.2f}dB",
            "-ac", "2",
            "-c:a", "libvorbis", "-q:a", "5",
            str(target),
        ],
        check=True,
    )
    logger.info(f"[render] {target.name}: {length} samples from {recording.name}, gain {gain:+.1f} dB")


def main() -> int:
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    SOUNDS_DIR.mkdir(parents=True, exist_ok=True)
    for recording_name, target_name, chain, loudness in RENDERS:
        render(RECORDINGS_DIR / recording_name, SOUNDS_DIR / target_name, chain, loudness)
    return 0


if __name__ == "__main__":
    sys.exit(main())
