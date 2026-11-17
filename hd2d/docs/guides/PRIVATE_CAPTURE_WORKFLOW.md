# Private ROM-backed build, intro capture, and fidelity validation

These commands run **on the machine holding your legally obtained ROM**.
The cloud Linux workspace cannot open a Mac's `/Users/...` path. No ROM is
included or fetched by these tools. All generated code, captures, images,
logs, input/calibration copies and reports stay under ignored `hd2d/local/`.
Do not attach them to a public PR or upload them to Actions. Source scripts,
documentation and original synthetic inputs are the only committed material.

## 1. Native toolchain and source

On Apple Silicon, use a native arm64 terminal/Python and Homebrew at
`/opt/homebrew`, not Rosetta. Install Xcode Command Line Tools and then:

```sh
xcode-select --install  # only if the tools are not already installed
brew install cmake ninja sdl2 python@3.12
uname -m
/opt/homebrew/opt/python@3.12/bin/python3.12 -c 'import platform; print(platform.machine())'
```

Both architecture outputs should say `arm64`. On Linux (Ubuntu/Debian), use
Python 3.10+ (3.12 tested), a C/C++ compiler, CMake 3.20+, Ninja, SDL2 and GL
development headers, for example:

```sh
sudo apt-get update
sudo apt-get install build-essential cmake ninja-build python3 libsdl2-dev libgl1-mesa-dev
```

Check out the Sprint 2 branch, or approved merged master after review:

```sh
git clone --recurse-submodules --branch feature/hd2d-sprint-2 \
  https://github.com/nkanhai/MegaManX2GraphicsUpgrade.git
cd MegaManX2GraphicsUpgrade
mkdir -p hd2d/local
git status --short
git submodule status --recursive
```

Use the same native Python for every command below (`python3` stands for it).
Do not run regeneration in your review checkout. The helper creates its own
private worktree because upstream generation writes annotation overlays as
well as ignored `src/gen/` files.

## 2. Verify the private ROM and build

Set `ROM` to your existing private file. Quoting preserves spaces in its name:

```sh
ROM="/absolute/private/path/Mega Man X2.sfc"
python3 hd2d/tools/build_capture_host.py --rom "$ROM" \
  --workspace hd2d/local/intro-build
```

The first command is a preflight/plan, with **no generation, networking or
build**. It requires the exact headerless USA image already specified upstream:

| Property | Expected |
|---|---|
| Size | 1,572,864 bytes |
| SHA-256 | `f3246755f608a1e1dc9c848b61da3b824c7853b29b3be40df6fc7f2793a887ed` |
| CRC32 | `947B0355` |

A 512-byte copier header is reported separately; provide a private headerless
copy without altering your original dump. Wrong region, patched images and
wrong digests are rejected. No optional Cx4 firmware dump is needed: the
pinned core synthesizes its internal table.

When preflight says `ready-to-build`, run:

```sh
python3 hd2d/tools/build_capture_host.py --rom "$ROM" \
  --workspace hd2d/local/intro-build --build
```

On macOS, add `--cmake-prefix "$(brew --prefix)"` if dependency discovery needs
it. On the supplied Linux cloud image, the isolated SDL/GL prefix from Sprint 0
can be passed with `--cmake-prefix`; its include flags must also be present in
`CFLAGS`/`CXXFLAGS`. Normal distro development packages need neither override.

The helper requires clean committed source and the existing dependency pins.
It creates a detached worktree at `hd2d/local/intro-build/source`, initializes
its pinned submodules (public source downloads only), invokes the unmodified
generator recipe with your ROM, and builds with `SNESRECOMP_SETUP_HOST=OFF`,
`HD2D_SCANOUT_CAPTURE=ON`, SDL2 and trace OFF. Darwin additionally requests
arm64. It preserves a private `build.log` and `build-report.json`.

The expected executable is:

```text
hd2d/local/intro-build/build/MegaManX2SNESRecomp
```

`built-not-gameplay-validated` means linking completed, not that a game frame
was reconstructed. A setup-host binary from CI cannot substitute for this
ROM-backed executable. On failure, keep the worktree/log for diagnosis; the
tool does not delete or overwrite prior builds. Choose a new workspace for a
new attempt. Never commit from the private worktree or publish its build output.

## 3. Calibrate and capture the intro stage

The committed `hd2d/fixtures/input/intro-stage-candidate.txt` is an **unverified
candidate**, derived from the existing `tools/navigate.py` controller sequences.
It waits, presses START to skip text/title screens, waits again and quits.
It contains no RAM pokes, save-state loads, reset or gameplay patches. Upstream
adds an idle frame after each hold entry; draw ordinal is not wall-clock time.
The proposed draw 1800 is a calibration starting point, not a claimed intro
stage checkpoint. It must be confirmed by the owner on the supported ROM.

First print a capture plan, then add `--run` to launch it in a window:

```sh
python3 hd2d/tools/capture_frame.py \
  --exe hd2d/local/intro-build/build/MegaManX2SNESRecomp \
  --rom "$ROM" --output hd2d/local/intro-attempt-01 --draw 1800

python3 hd2d/tools/capture_frame.py \
  --exe hd2d/local/intro-build/build/MegaManX2SNESRecomp \
  --rom "$ROM" --output hd2d/local/intro-attempt-01 --draw 1800 --run
```

The runner uses a new private working directory and config: native width,
sprite limits enabled, optimized PPU, no run-ahead/frame blending/autosave,
software SDL presentation and muted output. This is a graphics check, not an
audio qualification. Existing mod/save/config state is not copied. Ambient
engine and replay overrides are removed, and full-session replay recording
is explicitly disabled. The run records executable/ROM/input/config hashes,
the exact command/environment, capture ID and log. Default timeout is 180 s.
Only one capture is attempted. A rejected frame does not change host settings.

Inspect the window and reference image to confirm the intro stage. If it is
still a logo, title screen or menu, copy the input file into `hd2d/local/`, adjust
controller waits/presses, and retry with `--input-script` and a **new** output
directory. Do not use memory edits to force a stage. Preserve the successful
script, its recorded hash, and exact draw ordinal as the calibrated recipe.
After calibration, repeat the identical recipe in another new directory.
Compare reference/reconstruction hashes and reported guest counter/master
cycles; session IDs deliberately differ. Unexpected differences require
investigation before calling the capture repeatable.

Linux fallback accepts `--headless` to request SDL dummy video/audio after the
scene recipe is calibrated; the PPU still renders and the capture supplies
reference pixels. Do not infer the scene from the selected draw number alone.

## 4. Reconstruct and inspect evidence

Build the ROM-independent reader on the same machine:

```sh
cmake -S hd2d/tests/native -B hd2d/local/native -DCMAKE_BUILD_TYPE=Release
cmake --build hd2d/local/native --parallel 3
ctest --test-dir hd2d/local/native --output-on-failure

python3 hd2d/tools/validate_capture.py \
  hd2d/local/intro-attempt-01/capture/capture.x2scan \
  --replay hd2d/local/native/scanout_replay \
  --output hd2d/local/intro-validation-01
```

Successful reconstruction (including a mismatch) produces:

- `reference.png`: same-frame original renderer pixels from the capture.
- `reconstruction.png`: fresh pinned PPU replay from captured state/memories.
- `difference.png`: magenta for any changed RGBA pixel; identical pixels black.
- `report.json` and `report.txt`: capture identity/provenance, hashes, changed
  pixel count, maximum/mean channel errors, per-row counts and bounding box.

Exit **0** means exact canonical RGBA equality. Exit **1** means a valid
reconstruction with differing pixels; the images/report remain for analysis.
Exit **2** means invalid input/tool failure; no fidelity verdict or images are
published. Existing output directories are never overwritten. The validator
replays a private snapshot of the exact bytes it inspected to avoid a changing
input file mixing two frames. It requires no third-party Python image package.

This compares against the original **pinned upstream PPU renderer**, not SNES
hardware or a second emulator. A zero-difference synthetic frame does not prove
real gameplay, and a zero-difference real frame does not prove the entire game.
Legacy BGRX padding is undefined; the v1 contract compares RGBA with alpha 255.

## 5. Resolve failures precisely

An unsupported record logs the first failed requirement and the boundary, for
example `line 64: mosaic: requires 0; observed 17` or `draw entry: bgmode:
requires Mode 1; observed Mode 7`. Host policy errors name run-ahead, netplay,
rollback or rewind individually. Do not force values to zero to bypass the
check: that would change the scene. Pick a supported frame, or extend that
feature with independent synthetic parity tests in a subsequent increment.

No capture file can also mean the script quit before the requested draw,
capacity was exhausted, or the destination was not new/writable. Inspect
`capture.log` and `run-report.json`. If native replay rejects an unknown version
or engine pin, use matching source/tooling rather than relaxing validation.

Real-game acceptance requires a confirmed intro-stage image, a complete runtime
capture, zero differences, retained private provenance and a repeatable second
run. Until those exist, report the specific ROM/platform/profile blocker.
Only share non-game diagnostic text deliberately reviewed for private paths;
never force-add a ROM, generated code, saves, captures or rendered game art.
