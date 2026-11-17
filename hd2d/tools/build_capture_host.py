#!/usr/bin/env python3
"""Check a private ROM and optionally build in a new, detached private worktree.

Never downloads a ROM or writes generated data into the original source tree.
Without --build, only checks inputs and prints a command plan. No network I/O.
"""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import zlib

ROOT = Path(__file__).resolve().parents[2]


def identity(root=ROOT):
    return {key.strip(): value.strip()
            for line in (root / "rom_identity.txt").read_text().splitlines()
            if "=" in line and not line.lstrip().startswith("#")
            for key, value in [line.split("=", 1)]}


def check_rom(path, expected):
    path = Path(path).expanduser().resolve()
    if not path.is_file():
        raise ValueError("private ROM is not accessible on this machine; run this tool where the file resides")
    size = int(expected["rom_size"])
    actual = path.stat().st_size
    if actual == size + 512:
        raise ValueError("512-byte copier header detected: supply a private headerless copy; the tool does not alter the original")
    if actual != size:
        raise ValueError(f"ROM size mismatch: requires {size} bytes, observed {actual}")
    with path.open("rb") as stream:
        data = stream.read(size + 1)
    if len(data) != size:
        raise ValueError("ROM changed while being read")
    sha = hashlib.sha256(data).hexdigest()
    crc = f"{zlib.crc32(data):08x}"
    if sha != expected["expected_sha256"].lower() or crc != expected["expected_crc32"].lower():
        raise ValueError("ROM digest mismatch: requires the unmodified USA revision in rom_identity.txt")
    return {"path": str(path), "bytes": size, "sha256": sha, "crc32": crc}


def git(*args, root=ROOT):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def check_pins(root=ROOT):
    lock = json.loads((root / "hd2d/docs/architecture/upstream-lock.json").read_text())
    for dep in lock["dependencies"]:
        path = root / dep["path"]
        if git("rev-parse", "HEAD", root=path) != dep["commit"]:
            raise ValueError(f"dependency pin mismatch: {dep['path']}; initialize the pinned submodules")
        if git("status", "--porcelain", "--untracked-files=no", root=path):
            raise ValueError(f"modified pinned dependency: {dep['path']}")
    return {dep["path"]: dep["commit"] for dep in lock["dependencies"]}


def commands(rom, workspace, system, root=ROOT, prefix=None):
    source, build = workspace / "source", workspace / "build"
    configure = ["cmake", "-S", str(source), "-B", str(build), "-G", "Ninja",
                 "-DCMAKE_BUILD_TYPE=Release", "-DSNESRECOMP_SDL_BACKEND=SDL2",
                 "-DSNESRECOMP_SETUP_HOST=OFF", "-DHD2D_SCANOUT_CAPTURE=ON",
                 "-DSNESRECOMP_ENABLE_TRACE=OFF"]
    if system == "Darwin":
        configure.append("-DCMAKE_OSX_ARCHITECTURES=arm64")
    if prefix:
        configure.append(f"-DCMAKE_PREFIX_PATH={prefix}")
    return [
        ["git", "-C", str(root), "worktree", "add", "--detach", str(source), "HEAD"],
        ["git", "-C", str(source), "submodule", "update", "--init", "--recursive"],
        [sys.executable, str(source / "snesrecomp/tools/regen_project.py"),
         "--project-root", str(source), "--rom", str(rom), "--no-tests"],
        configure,
        ["cmake", "--build", str(build), "--target", "MegaManX2SNESRecomp", "--parallel", "3"],
    ]


def preflight(rom, workspace, prefix=None):
    report = {"format": "x2-hd2d.private-build", "version": 1, "status": "blocked",
              "system": platform.system(), "machine": platform.machine(),
              "python": platform.python_version(), "blockers": []}
    expected = identity()
    report["required_rom"] = {key: expected[key] for key in ("display_name", "rom_size", "expected_sha256", "expected_crc32", "region")}
    try:
        report["rom"] = check_rom(rom, expected)
    except (ValueError, OSError) as error:
        report["blockers"].append(str(error))
    try:
        report["pins"] = check_pins()
        report["producer_revision"] = git("rev-parse", "HEAD")
        if git("status", "--porcelain", "--untracked-files=no"):
            report["blockers"].append("source checkout has tracked modifications; commit/review them before making the private worktree")
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        report["blockers"].append(str(error))
    for tool in ("git", "cmake", "ninja"):
        if not shutil.which(tool):
            report["blockers"].append(f"missing build tool: {tool}")
    if report["system"] not in ("Darwin", "Linux"):
        report["blockers"].append("this workflow supports macOS and Linux only")
    if report["system"] == "Darwin" and report["machine"] != "arm64":
        report["blockers"].append("Apple Silicon qualification requires native arm64 Python/shell; do not use Rosetta")
    workspace = Path(workspace).expanduser().resolve()
    if not workspace.is_relative_to((ROOT / "hd2d/local").resolve()):
        report["blockers"].append("workspace must be a new directory under ignored hd2d/local/")
    if workspace.exists():
        report["blockers"].append("workspace already exists; choose a new private build directory")
    report["commands"] = commands(Path(rom).expanduser().resolve(), workspace, report["system"], prefix=prefix)
    report["executable"] = str(workspace / "build/MegaManX2SNESRecomp")
    if not report["blockers"]:
        report["status"] = "ready-to-build"
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rom", type=Path, required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--cmake-prefix")
    parser.add_argument("--report", type=Path, help="new private JSON file; never commit it")
    parser.add_argument("--build", action="store_true", help="execute the plan after all preflight checks pass")
    args = parser.parse_args()
    report = preflight(args.rom, args.workspace, args.cmake_prefix)
    if args.build and not report["blockers"]:
        workspace = args.workspace.expanduser().resolve()
        workspace.parent.mkdir(parents=True, exist_ok=True)
        workspace.mkdir(mode=0o700)
        report["status"] = "building"
        try:
            with (workspace / "build.log").open("w", encoding="utf-8") as log:
                for command in report["commands"]:
                    log.write(json.dumps(command) + "\n"); log.flush()
                    subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
            report["status"] = "built-not-gameplay-validated"
        except (OSError, subprocess.SubprocessError) as error:
            report["status"] = "build-failed"
            report["blockers"].append(str(error) + "; inspect the private build.log")
        (workspace / "build-report.json").write_text(json.dumps(report, indent=2) + "\n")
    text = json.dumps(report, indent=2) + "\n"
    if args.report:
        with args.report.open("x", encoding="utf-8") as stream:
            stream.write(text)
    print(text, end="")
    return 2 if report["blockers"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
