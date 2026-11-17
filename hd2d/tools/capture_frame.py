#!/usr/bin/env python3
"""Plan/run a private, controller-scripted one-shot capture. No ROM downloads.

Requires a ROM-backed executable (not the ROM-free setup host). The default
intro input sequence is an unqualified candidate; inspect the scene yourself.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import time
import uuid

from build_capture_host import ROOT, check_rom, identity
import scanout_format

CONFIG = """[Graphics]
OutputMethod = SDL-Software
NewRenderer = 1
Widescreen = 0
NoSpriteLimits = 0
FrameBlend = 0
LinearFiltering = 0
Fullscreen = 0
VSync = 0
[General]
RunAhead = 0
Autosave = 0
SkipLauncher = 1
[Sound]
EnableAudio = 0
"""


def check_script(inputs):
    if len(inputs) > 65536:
        raise ValueError("input script exceeds the 64 KiB workflow limit")
    buttons = {"a", "b", "x", "y", "l", "r", "start", "select", "up", "down", "left", "right"}
    ended = False
    for raw in inputs.decode("utf-8").splitlines():
        words = raw.partition("#")[0].split()
        if not words:
            continue
        if ended:
            raise ValueError("commands after quit are unreachable")
        if words == ["quit"]:
            ended = True
        elif words[0] == "wait" and len(words) == 2 and words[1].isdigit() and 0 <= int(words[1]) <= 36000:
            continue
        elif (words[0] == "press" and len(words) == 3 and words[2].isdigit() and
              1 <= int(words[2]) <= 36000 and set(words[1].split("+")) <= buttons):
            continue
        else:
            raise ValueError("script requires bounded wait N / press buttons N / quit; no memory pokes or state changes")
    if not ended:
        raise ValueError("input script must end with quit")


def plan(executable, rom, output, script, draw, headless=False):
    rom_info = check_rom(rom, identity())
    executable, script, output = (Path(p).expanduser().resolve() for p in (executable, script, output))
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise ValueError("executable is missing or not executable; complete the ROM-backed build first")
    if not output.is_relative_to((ROOT / "hd2d/local").resolve()) or output.exists():
        raise ValueError("output must be a NEW directory under ignored hd2d/local/")
    if draw < 1:
        raise ValueError("draw must be positive")
    inputs = script.read_bytes()
    check_script(inputs)
    environment = {"HD2D_CAPTURE_DIR": str(output / "capture"),
                   "HD2D_CAPTURE_SESSION": uuid.uuid4().hex, "HD2D_CAPTURE_DRAW": str(draw),
                   "SNESRECOMP_RUNAHEAD": "0", "MMX_REPLAY_RECORD": "0", "MMX_LOG_CONSOLE": "1"}
    if headless:
        environment.update(SDL_VIDEODRIVER="dummy", SDL_AUDIODRIVER="dummy")
    return {
        "format": "x2-hd2d.capture-run", "version": 1, "status": "planned",
        "platform": {"system": platform.system(), "machine": platform.machine()},
        "rom": rom_info, "executable_sha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
        "input_script_sha256": hashlib.sha256(inputs).hexdigest(),
        "config_sha256": hashlib.sha256(CONFIG.encode()).hexdigest(),
        "scene": "unverified; owner must confirm intro stage visually",
        "command": [str(executable), "--no-launcher", "--rom", rom_info["path"],
                    "--config", str(output / "config.ini"), "--script", str(output / "inputs.txt")],
        "environment": environment, "working_directory": str(output),
    }, inputs


def execute(report, inputs, timeout):
    output = Path(report["working_directory"])
    output.parent.mkdir(parents=True, exist_ok=True)
    output.mkdir(mode=0o700)
    (output / "config.ini").write_text(CONFIG)
    (output / "inputs.txt").write_bytes(inputs)
    # A fresh cwd has no config.local.ini, saves or active mod state. Prevent
    # inherited engine/replay overrides from silently changing this run.
    environment = {k: v for k, v in os.environ.items()
                   if not k.startswith(("HD2D_CAPTURE_", "SNESRECOMP_", "MMX_REPLAY_"))}
    environment.update(report["environment"])
    start = time.monotonic()
    try:
        with (output / "capture.log").open("w", encoding="utf-8") as log:
            result = subprocess.run(report["command"], cwd=output, env=environment,
                                    stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        report["exit_code"] = result.returncode
        capture = output / "capture/capture.x2scan"
        if result.returncode != 0:
            raise ValueError(f"host exited with code {result.returncode}; inspect capture.log")
        if not capture.is_file():
            raise ValueError("no complete capture: inspect [hd2d] diagnostics; draw may be unreachable or unsupported")
        metadata = scanout_format.read(capture)
        metadata.pop("lines")
        report["capture"] = metadata
        report["status"] = "captured-not-yet-replayed"
        code = 0
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        report["status"] = "blocked-or-failed"
        report["error"] = str(error)
        code = 2
    report["elapsed_seconds"] = time.monotonic() - start
    (output / "run-report.json").write_text(json.dumps(report, indent=2) + "\n")
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--rom", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--input-script", type=Path, default=ROOT / "hd2d/fixtures/input/intro-stage-candidate.txt")
    parser.add_argument("--draw", type=int, default=1800)
    parser.add_argument("--timeout", type=float, default=180)
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--run", action="store_true", help="launch; default prints the plan only")
    args = parser.parse_args()
    if not 0 < args.timeout <= 3600:
        parser.error("timeout must be in (0, 3600] seconds")
    try:
        report, inputs = plan(args.exe, args.rom, args.output, args.input_script, args.draw, args.headless)
        code = execute(report, inputs, args.timeout) if args.run else 0
    except (OSError, ValueError) as error:
        parser.exit(2, f"Capture blocked: {error}\n")
    print(json.dumps(report, indent=2))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
