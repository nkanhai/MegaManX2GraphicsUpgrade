# Apple Silicon / native macOS investigation

Sprint 2, 2026-11-17. Source baseline: fork master `3ecebf8` (merged Sprint 1).
All dependency pins in `upstream-lock.json` remain unchanged. This is a source
audit plus explicit qualification gates, not a claim of tested gameplay.

**Subsequent owner evidence (Sprint 3 handover, 2026-11-17):** the owner reports
exact USA ROM verification, a native ARM64 ROM-backed build, working keyboard
gameplay, and a draw-1800 opening-narrative capture with 0/57,344 different
pixels. This resolves the earlier local feasibility question for that setup;
it is not a cloud CI result or verification of Sprint 3's new shortcut.
The historical investigation/blockers below remain as recorded. See the
[Sprint 3 report](../roadmap/SPRINT_3_REPORT.md) for current scope.

## What the pinned code supports

| Observed source | Implication / limit |
|---|---|
| `snesrecomp/runner/runner.cmake` supplies CMake/SDL2 desktop targets and explicitly mentions macOS/Clang | A native build is plausible without replacing the simulation or using Wine/Rosetta |
| Non-Windows defaults retain reference LLE frame/APU paths and byte CRC | Do not force the Windows-only optimized defaults on Apple Silicon |
| `desktop/fiber_compat.c` has a macOS/glibc `ucontext` backend, `_XOPEN_SOURCE=600`, and a no-argument trampoline | Portability path exists; stack switching still needs real ARM64 execution coverage |
| `host_paths.c` uses `_NSGetExecutablePath` under `__APPLE__` | No Linux `/proc/self/exe` assumption on the Mac path |
| PPU priority buffers have explicit 8-byte alignment; renderer is scalar C with optional backends | Native ARM64 replay is a meaningful test; host architecture is little endian, matching capture v1 |
| CMake guards include paths against case-insensitive `<version>`/`VERSION` collisions | Preserve existing include layout; do not add the repository root to compiler include paths |
| `SNESRECOMP_SETUP_HOST=ON` supplies setup dispatch stubs when generated C is absent | A successful setup-host build/`--help` is **not** proof that guest code executes |
| `tools/regen.sh` delegates to the pinned generator recipe, including title annotation/override hooks | A real executable needs the correct private ROM and generated C, then `SETUP_HOST=OFF` |
| `snes/cx4.c` synthesizes the Cx4 internal tables from closed-form definitions | No extra firmware download is required; an optional dump only cross-checks synthesis |

The generator also updates `recomp/bank00.cfg`'s existing annotation overlay.
The new build helper therefore runs it in a **detached private worktree**,
leaving the review checkout unchanged. It does not bypass the title's recipe,
change generated gameplay semantics, or update gitlinks.

## Qualification workflow

`.github/workflows/hd2d-macos-tests.yml` requests `macos-15`. GitHub's
[runner-image inventory](https://github.com/actions/runner-images/blob/main/README.md)
lists that label as ARM64 (the Intel label is `macos-15-intel`). The job asserts
`uname -m = arm64`, records OS/compiler versions, then:

1. Builds/runs the native scanout tests with `CMAKE_OSX_ARCHITECTURES=arm64`.
2. Installs SDL2/Ninja, builds the complete capture-enabled ROM-free host.
3. Inspects the Mach-O architecture, executes `--help`, and runs diagnostics tests.

Sprint 1's Linux-only test link flag becomes Darwin `-dead_strip`; Linux keeps
`--gc-sections`. No dependency source is patched. The workflow never receives
a ROM, save, capture, generated C, or secret containing game data, and uploads
no game artifacts.

At the first attempt, [run 44140389161](https://github.com/nkanhai/MegaManX2GraphicsUpgrade/actions/runs/44140389161)
waited 15 minutes and ended before any steps ran: the job was cancelled, with
no runner assigned. Thus it supplies **no macOS compiler or execution result**.
GitHub's check annotation states: **"The job was not acquired by Runner of type
hosted even after multiple attempts"**, with notices: **"Due to capacity
constraints, jobs targeting macOS arm64 runners may experience longer queue
times."** This is a confirmed hosted-runner capacity blocker.
The current PR carries a fresh attempt; consult its checks for newer evidence.
Do not diagnose this queue/runner failure as a source-code failure or silently
substitute an Intel run for native ARM64 qualification.

## Current conclusion and fallback

Native Apple Silicon is **source-level plausible but not yet qualified by this
environment**. macOS compilation, dynamic-library loading, fibers, SDL video,
audio and real gameplay are separate gates. A Linux x86_64 setup-host build,
`--help` execution, diagnostics and synthetic PPU replay have passed. Linux is
the reproducible fallback for the same source/pins; ROM-backed generation and
execution remain dependent on access to the owner's private ROM.

The active workspace is Linux. The owner's `/Users/...` ROM path is not mounted
here. Checking that exact path and running the preflight produced the specific
blocker: `private ROM is not accessible on this machine; run this tool where
the file resides`. No ROM was searched for online or downloaded. Follow
[`PRIVATE_CAPTURE_WORKFLOW.md`](../guides/PRIVATE_CAPTURE_WORKFLOW.md) locally
on the Mac, or supply a private local path on a Linux machine you control.

If a local macOS build fails, retain `build.log`, the source/pin SHAs, `sw_vers`,
`uname -m`, compiler/CMake versions and the first compiler/linker error. Do not
change the pins or run an x86_64/Rosetta build and call it ARM64 validation.
Resolve a build-system portability issue separately from gameplay changes.
