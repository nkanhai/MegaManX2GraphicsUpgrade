#!/usr/bin/env python3
"""Launch normal keyboard gameplay with opt-in Ctrl+Shift+F10 captures.

Default: print a plan. --run creates a new ignored private session and launches.
No controller script, predicted frame, ROM download, or automatic fidelity claim.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import uuid

from build_capture_host import ROOT, check_rom, identity
from capture_frame import CONFIG as CAPTURE_CONFIG

MARKER = b"HD2D_INTERACTIVE_CAPTURE_V1"
CONFIG = CAPTURE_CONFIG.replace("VSync = 0", "VSync = 1").replace("EnableAudio = 0", "EnableAudio = 1")


def plan(executable, rom):
    rom_info = check_rom(rom, identity())
    executable = Path(executable).expanduser().resolve()
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise ValueError("executable is missing or not executable; build the Sprint 3 ROM-backed host first")
    binary = executable.read_bytes()
    if MARKER not in binary:
        raise ValueError("executable lacks the Sprint 3 interactive capture marker; rebuild with HD2D_SCANOUT_CAPTURE=ON")
    session = uuid.uuid4().hex
    output = ROOT / "hd2d/local/interactive" / (datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S-") + session)
    environment = {"HD2D_CAPTURE_DIR": str(output / "captures"),
                   "HD2D_CAPTURE_SESSION": session, "HD2D_CAPTURE_INTERACTIVE": "1",
                   "SNESRECOMP_RUNAHEAD": "0", "MMX_REPLAY_RECORD": "0", "MMX_LOG_CONSOLE": "1"}
    return {
        "format": "x2-hd2d.interactive-session", "version": 1, "status": "planned",
        "platform": {"system": platform.system(), "machine": platform.machine()},
        "session": session, "rom": rom_info,
        "executable_sha256": hashlib.sha256(binary).hexdigest(),
        "config_sha256": hashlib.sha256(CONFIG.encode()).hexdigest(),
        "shortcut": "Left Control + Left Shift + F10 (Fn may be needed on macOS)",
        "scene": "owner-selected interactive gameplay; not automatically classified",
        "command": [str(executable), "--no-launcher", "--rom", rom_info["path"],
                    "--config", str(output / "config.ini")],
        "environment": environment, "working_directory": str(output),
    }


def execute(report):
    output = Path(report["working_directory"])
    # Resolve again at execution; no caller-specified destination can escape the
    # ignored root, and an existing session is never reused or overwritten.
    if not output.resolve().is_relative_to((ROOT / "hd2d/local").resolve()):
        raise ValueError("session must be under ignored hd2d/local/")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.mkdir(mode=0o700)
    (output / "config.ini").write_text(CONFIG)
    report_path = output / "session.json"
    report["status"] = "running"
    report_path.write_text(json.dumps(report, indent=2) + "\n")
    environment = {k: v for k, v in os.environ.items()
                   if not k.startswith(("HD2D_CAPTURE_", "SNESRECOMP_", "MMX_REPLAY_"))}
    environment.update(report["environment"])
    print(f"Private session: {output}\nCapture: {report['shortcut']}\n"
          "Play normally. Capture results appear in this terminal. Close the game window to finish.", flush=True)
    code = 2
    try:
        # Inherit the terminal so capture success/rejection messages are visible.
        # No script, timeout, input injection, or global keyboard listener.
        result = subprocess.run(report["command"], cwd=output, env=environment)
        report["exit_code"] = result.returncode
        report["captures"] = [str(p.relative_to(output)) for p in sorted((output / "captures").glob("*/capture.x2scan"))]
        report["status"] = ("closed-captures-not-yet-validated" if report["captures"] else "closed-no-captures") if result.returncode == 0 else "host-failed"
        code = 0 if result.returncode == 0 else 2
    except KeyboardInterrupt:
        report["status"] = "interrupted"
        code = 130
    except OSError as error:
        report["status"] = "launch-failed"
        report["error"] = str(error)
    finally:
        report_path.write_text(json.dumps(report, indent=2) + "\n")
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--rom", type=Path, required=True)
    parser.add_argument("--run", action="store_true", help="launch instead of printing the plan")
    args = parser.parse_args()
    try:
        report = plan(args.exe, args.rom)
        code = execute(report) if args.run else 0
    except (OSError, ValueError) as error:
        parser.exit(2, f"Interactive capture blocked: {error}\n")
    print(json.dumps(report, indent=2))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
