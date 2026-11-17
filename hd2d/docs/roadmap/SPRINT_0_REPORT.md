# Sprint 0 report — 2026-11-17

## Outcome

The central source questions are answered: X2 exposes PPU memory/registers,
an existing per-line render loop, and a completed framebuffer. A read-only
scanout recorder is a defensible next step. The existing TCP probes alone are
not an atomic, faithful frame-export interface.

Sprint 0 includes the project structure, pinned audit, rendering map, bridge
proposal, decision/risk records, and a tested synthetic format. It does not
include a runtime recorder, Unity integration, gameplay testing or a proven
real-game reconstruction. Native graphics tests and a ROM-free setup-host
build provide an engineering baseline without a game ROM.

Canonical project: <https://github.com/nkanhai/MegaManX2GraphicsUpgrade>.
Feature branch: `feature/hd2d-sprint-0`; PR target: the fork's `master` branch.
`upstream` remains <https://github.com/mstan/MegaManX2Recomp>.
The initial workspace was empty; the audit began from an upstream clone while
the owner supplied the renamed fork URL. Full upstream history is retained. The fork's `master` matches the audited
upstream SHA exactly (zero commits ahead/behind).
No gameplay, engine source, upstream license or dependency pin is changed.

## Confirmed architecture and decisions

- The title adapts the shared desktop host using `SnesDesktopHostGame` and
  `RtlGameInfo`. `RunOneFrameOfGame` runs original SNES execution through the
  interpreter/AOT framework; Cx4 is emulated at instruction level.
- `Ppu` holds 64 KiB VRAM, 512-byte CGRAM and 544-byte OAM plus display registers.
  Source entry points and units are listed in
  [RENDERING_PIPELINE.md](../architecture/RENDERING_PIPELINE.md).
- `X2DrawPpuFrame` calls `ppu_runLine` for 0..224, then HDMA after each line.
  Capture before each existing call, and copy reference pixels after the pass.
  No extra draw call or simulation modification is required for observation.
- X2 does not use the shared framework's raster-journal replay path. Existing
  `FrameRecord` snapshots happen before rendering; live debug reads and the
  last screenshot can represent different phases.
- Start with recorded immutable frames; keep TCP as a possible request/status
  layer. Defer live IPC/native-library and final URP renderer choices. See
  [ADR 0001](../decisions/0001-recorded-scanout.md).

## Changes delivered

| Files | Purpose |
|---|---|
| Root `.gitignore`; `hd2d/.gitignore` | Exclude local ROM/firmware/state/export/build/Unity cache artifacts |
| `hd2d/README.md`; `hd2d/AGENTS.md` | Onboarding, attribution, durable engineering instructions |
| `hd2d/docs/architecture/UPSTREAM_AUDIT.md`; `upstream-lock.json` | Verified source provenance, pins, licensing and platform findings |
| `hd2d/docs/architecture/RENDERING_PIPELINE.md` | Execution-to-presentation map, exact graphics observation points and probe limits |
| `hd2d/docs/architecture/BRIDGE_PROPOSAL.md` | Capture contract, synchronization, encoding, performance and Unity plan |
| `hd2d/docs/decisions/0001-recorded-scanout.md` | Scoped architectural decision and deferred choices |
| `hd2d/docs/roadmap/ROADMAP.md`; this report | Milestones, risk register, next-task acceptance and evidence |
| `hd2d/schemas/FRAME_V1.md` | Explicit experimental synthetic snapshot contract |
| `hd2d/fixtures/synthetic/README.md`; `expected-summary.json` | Original fixture recipe and small sample output |
| `hd2d/tools/frame_format.py`; `hd2d/tests/test_frame_format.py` | Serializer, bounded reader/validator, generator, ten tests |
| `hd2d/tools/check_upstream.py` | Repeatable focused native test runner, local logs/results |
| `hd2d/tools/prepare_cloud_deps.sh` | Reproduce workspace-local Debian build dependencies without system changes |

Empty bridge/Unity directories are deferred until implementation exists.
The generated synthetic document is 89,677 bytes and stays in
`hd2d/local/demo.json`; its decoded memories total 66,592 bytes. It represents
two BG maps, different scroll positions, palettes and three OBJ pieces.
It deliberately rejects runtime input and unsupported raster features.

## Build environment and reproducibility

Linux Debian 13 (trixie), x86_64. Git 2.52.0; Python 3.12.14;
GCC/G++ 14.2.0; CMake 3.31.10; Ninja package 1.13.0
(binary reports `1.13.0.git.kitware.jobserver-pipe-1`); pkg-config 1.8.1.
The initial environment lacked CMake, Ninja, SDL and GL development headers.
No Windows hardware, Unity Editor or game ROM was used.

`prepare_cloud_deps.sh` was executed successfully. It installs Python build
tools into `/workspace/hd2d-venv`, maintains an isolated apt package index, and
extracts pinned Debian development packages into `/workspace/hd2d-deps/prefix`.
It does not install or edit system packages. Packages used: SDL2
`2.32.4+dfsg-1`; GL/EGL/GLES/OpenGL development/runtime components
`1.7.0-1+b2`; X11 headers `2:1.8.12-1`; X11 protocol headers `2024.1-1`.
The initial manual attempt also downloaded the Mesa development metapackage
`25.0.7-2+deb13u1`; it is not needed by the final script. Existing host runtime
libraries supply the SDL/X11 dependencies. This helper is specific to this
Debian x86_64 image; it is not a universal macOS/Windows provisioning script.
Pinned package availability on the live Debian mirror may change later.

From the repository root in this cloud environment:

```sh
git submodule update --init --recursive
bash tools/bootstrap.sh
bash hd2d/tools/prepare_cloud_deps.sh
export PATH=/workspace/hd2d-venv/bin:$PATH
python3 -m unittest discover -s hd2d/tests -v
python3 hd2d/tools/check_upstream.py

cmake -S . -B build-hd2d-setup -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DSNESRECOMP_SDL_BACKEND=SDL2 \
  -DSNESRECOMP_SETUP_HOST=ON \
  -DCMAKE_PREFIX_PATH=/workspace/hd2d-deps/prefix/usr \
  -DCMAKE_C_FLAGS='-isystem /workspace/hd2d-deps/prefix/usr/include/x86_64-linux-gnu' \
  -DCMAKE_CXX_FLAGS='-isystem /workspace/hd2d-deps/prefix/usr/include/x86_64-linux-gnu'
cmake --build build-hd2d-setup --parallel 4
ctest --test-dir build-hd2d-setup --output-on-failure

# Also compile the inspected TCP/debug path in the ROM-free host.
cmake -S . -B build-hd2d-setup -DSNESRECOMP_ENABLE_TRACE=ON
cmake --build build-hd2d-setup --parallel 4
```

On a provisioned Linux host, use the repository CI's SDL2/OpenGL development
packages and omit the local prefix/compiler flags. The ROM-free setup host
can prepare a generated build later; it cannot execute guest gameplay.
`tools/build-linux.sh` additionally packages an AppImage; packaging was not run.

## Executed checks and limits

| Check | Observed result |
|---|---|
| Recursive submodule initialization and bootstrap | PASS, all five gitlinks match pins |
| Python format suite | PASS, 10 tests: canonical round trip, exact bytes/registers, independent planar/map/palette/OAM expectations, corruption/size/version/profile/type/JSON rejection, CLI/no overwrite |
| Demo and validator CLI | PASS, same summary and memory hashes after reading output |
| Native `snes_raster_snapshot` CTest | PASS, immutable memory/dedup, changed/ABA data and allocation/capacity failures |
| Native `render_workers` CTest | PASS |
| Six native PPU harnesses | PASS: sprite limits, world mirror, elastic bands, Mode-7 HD, raster journal, hires mode |
| ROM-free SDL2 setup-host configure/build | PASS, executable linked; no engine source edits needed |
| Setup-host diagnostic CTest | PASS, 2/2: trace writer and replay writer |
| Trace-enabled setup-host compile | PASS, TCP/debug server compiled and executable linked |
| Ignore-path and diff whitespace checks | PASS for ROM, firmware, local export/capture, Python cache and Unity Library paths |

The focused harness runner reproduces the six upstream PowerShell PPU tests
using Unix C11 compiler flags plus `-lm`, without rewriting their sources.
It saves every command, cwd, exit code and logs under
`hd2d/local/checks/results.json`. Setup build logs are in `hd2d/local/`.
These C tests exercise synthetic graphics, not original game scenes.

### Failures encountered, retained as evidence

1. Initial `cmake --version`: command unavailable. Resolved with local venv.
2. Initial SDL2 setup configure: `SDL2Config.cmake` missing. A direct apt download
   initially had no package index; resolved with workspace-local apt state and
   extracted packages.
3. Initial local-prefix compile: missing `SDL2/_real_SDL_config.h`; resolved by
   adding the extracted Debian multiarch include path in CMake configuration.
4. Next compile: missing `X11/Xlib.h`; resolved by extracting X11/protocol
   development packages. No repository source workaround was introduced.
5. `bash tools/regen.sh --no-tests`: exit 1, `ROM not found: .../mmx2.sfc`.
   Expected and unresolved without an owner-supplied ROM; no substitute created.

Not run: ROM-backed generated game build, gameplay, live `dbgprobe ppu/shot`,
real-frame fidelity comparison, real Cx4 scene validation, save-state/rewind
integration (`MMX_STATE_TESTS`/`tests/test_diagnostic_replay.py`), Windows builds,
Apple Silicon builds, Unity tests or AppImage packaging. The full framework
regression suite was not run; only the named focused checks are qualified.
No test failure is reclassified as a gameplay success.

## Main risks and next step

The top three risks are **per-line fidelity**, **consistent frame identity and
thread ownership**, and **Unity compositing/platform portability**. Source
examples and mitigations are in the [risk register](ROADMAP.md).

Recommended Sprint 1 is the native synthetic scanout recorder described there.
Its decisive acceptance gate is byte-for-byte reconstruction against the
pinned upstream PPU for a synthetic raster-changing field, with capture-on/off
guest state unchanged and invalid/incomplete captures rejected. Only after that
should a user-owned ROM supply local real-game captures or Unity importing begin.

## Publication status

Published on 2026-11-17 in
[PR #1](https://github.com/nkanhai/MegaManX2GraphicsUpgrade/pull/1), from
`feature/hd2d-sprint-0` to the canonical fork's `master`. The original baseline
commit `3a2888486227b7d49136269ff8c203499f71ff70` and publication-blocker record
`734623f5e68d94d9ce8d91948e0ddfd48d7e74f3` remain intact; this status correction
is an additional commit. No work was recreated or discarded.

The initial Git push returned HTTP 403 (`Permission ... denied`), and connector
branch creation returned `Resource not accessible by integration`. After the
owner reviewed repository/connection access, the same authenticated Git push
succeeded, followed by connector PR creation. Both publication paths now work;
the earlier response alone does not identify which permission setting changed.

The original local review patch and bundle remain at
`/workspace/hd2d-sprint-0.patch` and `/workspace/hd2d-sprint-0.bundle`; they retain
the two pre-publication commits. The PR branch is the current review source,
including this publication-status update. The prepared PR body is also retained
in ignored `hd2d/local/PR_BODY.md`.
