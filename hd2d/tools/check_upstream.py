#!/usr/bin/env python3
"""Run focused ROM-free upstream C tests without changing dependency sources.

Requires CMake >=3.20, Ninja and a C11 compiler. Saves commands, output and
exit status under hd2d/local/checks. Does not run the game or fetch dependencies.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / "snesrecomp"
OUT = ROOT / "hd2d/local/checks"


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    records = []

    def run(label, argv, cwd=ROOT):
        result = subprocess.run([str(arg) for arg in argv], cwd=cwd,
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (OUT / (label + ".log")).write_text(result.stdout, encoding="utf-8")
        records.append({"check": label, "argv": [str(arg) for arg in argv],
                        "cwd": str(cwd), "exit_code": result.returncode})
        print(f"{label}: {'PASS' if result.returncode == 0 else 'FAIL'}", flush=True)
        if result.returncode:
            print(result.stdout[-2500:])
        return result.returncode == 0

    cc = shutil.which(os.environ.get("CC", "cc"))
    for name in ("cmake", "ctest", "ninja"):
        if not shutil.which(name):
            sys.exit(f"Missing {name}; see docs/roadmap/SPRINT_0_REPORT.md")
    if not cc:
        sys.exit("Missing C11 compiler (CC or cc)")
    if not (ENGINE / "runner/src/snes/ppu.c").is_file():
        sys.exit("Initialize pinned submodules first")
    for kind in ("raster_snapshot", "render_workers"):
        build = OUT / kind
        if run(kind + "-configure", ["cmake", "-S", ENGINE / "runner/tests" / kind,
                                     "-B", build, "-G", "Ninja"]):
            if run(kind + "-build", ["cmake", "--build", build]):
                run(kind + "-test", ["ctest", "--test-dir", build, "--output-on-failure"])
    # Same six tests and compile flags as upstream tests/ppu/run.ps1, plus libm
    # for Unix. No copied or modified upstream test implementations.
    for name in ("ppu_sprite_limit_test", "ppu_world_mirror_test", "ppu_elastic_band_test",
                 "ppu_mode7_hd_test", "ppu_raster_journal_test", "ppu_hires_mode_test"):
        exe = OUT / name
        if run(name + "-build", [cc, "-std=c11", "-Wall", "-Wextra", "-O1",
                "-DSNESRECOMP_REVERSE_DEBUG=0", "-Irunner/src", "-Irunner/src/snes",
                f"tests/ppu/{name}.c", "runner/src/snes/ppu.c",
                "runner/src/snes/ppu_legacy.c", "-lm", "-o", exe], ENGINE):
            run(name + "-test", [exe])
    (OUT / "results.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
    return int(any(record["exit_code"] for record in records))


if __name__ == "__main__":
    sys.exit(main())
