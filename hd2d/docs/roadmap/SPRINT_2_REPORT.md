# Sprint 2 report — real-game validation readiness

Date: 2026-11-17. Repository: `nkanhai/MegaManX2GraphicsUpgrade`.
Branch: `feature/hd2d-sprint-2`, based on merged master
`3ecebf8bb2378b29d101a01fdcc4b75256185247`.

**Real-game reconstruction is not yet verified.** The owner supplied a private
Mac ROM path, but that path is inaccessible from the active Linux workspace.
Preflight found exactly that remaining input blocker. No ROM was downloaded,
transferred to GitHub, generated from synthetic data, or substituted with a
different game. The ARM64 macOS CI attempt also encountered a confirmed hosted
runner capacity failure before any build step ran. These are explicit
validation dependencies, not evidence of a broken PPU capture or Mac compiler.

## Implementation

- Added native macOS qualification CI (`macos-15`, explicit arm64 assertion,
  native harness, full setup host, architecture check, `--help`, diagnostics).
  The test harness selects Darwin dead stripping instead of ELF section GC.
  The [platform audit](../architecture/MACOS_NATIVE_AUDIT.md) maps the existing
  portable C, SDL2, executable-path and fiber implementations and their limits.
- Profile diagnostics identify the first failed requirement, observed value
  and observation boundary, e.g. `line 64: mosaic: requires 0; observed 17`.
  Host context rejection names run-ahead/netplay/rollback/rewind individually.
  Checks preserve the Sprint 1 supported profile and fail without changing it.
- Native replay separates invalid input, exact reconstruction and pixel
  mismatch. Its optional raw RGBA output is published only for a fully decoded
  image and never overwrites an existing file. The old boolean API is retained.
- `validate_capture.py` uses the native replay plus an independent Python pixel
  comparison. It exports `reference.png`, `reconstruction.png`, `difference.png`,
  `report.json` and `report.txt`. Reports contain exact RGBA counts, errors,
  per-row differences, a bounding box, capture identity and hashes. Invalid
  input/tool errors produce an error report without images or a fidelity verdict.
  The original capture is snapshotted privately before replay to prevent two
  file versions from being mixed. PNG encoding uses only the standard library.
- `build_capture_host.py` verifies the existing ROM size/SHA-256/CRC32, clean
  source, pins and tools. On explicit `--build`, it creates a detached private
  worktree, runs the unchanged generation recipe and builds with setup-host
  OFF and capture ON. Upstream generation's annotation updates stay in that
  private worktree; the review checkout is untouched.
- `capture_frame.py` plans or runs one frame-selected capture in a fresh private
  directory. It records input/config/executable/ROM hashes, uses controller-only
  bounded input scripts, removes ambient engine/replay overrides, and retains
  logs on errors/timeouts. The proposed intro script is explicitly an
  **unverified calibration candidate**, not a claimed gameplay checkpoint.

Runtime wire format v1, title gameplay source, dependency pins and attribution
are unchanged. There is no Unity work, asset catalogue, real-game extraction,
new gameplay hook or silent expansion of unsupported PPU modes.

## Verified results

Linux Debian 13 x86_64, GCC/G++ 14.2.0, Python 3.12.14, CMake 3.31.10 and Ninja
1.13.0 package. SDL2 2.32.4 is used only for the desktop setup host.

| Check | Result |
|---|---|
| Python suite | **16/16 pass**, including private input/preflight, command planning and process failure/timeout checks |
| Native Release CTest | **4/4 pass**: armed/off loops, process/file/adapter checks, PNG/report validation |
| Native ASan/UBSan CTest | **4/4 pass** with the existing narrowly scoped upstream shift-base exception |
| Added priority/rotation/overflow cases | **40 pass per compiled-on/off harness**; armed cases additionally replay every complete frame |
| Pinned upstream focused checks | **8/8 pass** |
| Full Linux capture-enabled setup host | **Build and `--help` execution pass** |
| Setup-host diagnostic tests | **2/2 pass** |
| Gitlinks / diff | All five recursive pins unchanged; no dependency edits; diff whitespace checks pass |
| Actual ROM-backed generation/execution | **Blocked: owner's private ROM is inaccessible here** |
| Native Apple Silicon build/execution | **Blocked: no hosted runner acquired the initial job** |

The added sprite suite tests all four OBJ priorities against both BG1 priority
levels, lower-OAM-index ordering with conflicting OBJ priorities, mid-raster
priority rotation, and both sprite range/time overflow behaviors. These run
under legacy/optimized renderers with strict/unlimited sprite policies. The
tests assert expected visible colors/overflow flags as well as exact replay.
Existing palette A→B→A, scroll, VRAM/OAM, brightness/blanking and isolation tests
remain. Unsupported-at-entry and mid-frame rejection logs are checked, with
the same original drawing result after rejection.

The image workflow was exercised on the retained original synthetic capture:
**0 of 57,344 pixels differ**, reference and reconstruction SHA-256 both
`5e0218c16f560cac220b47e232a5e380caadacc3d9a74898a7a2cb8a2771d41f`.
Artifacts remain ignored in `hd2d/local/sprint2-validation/`. A deliberately
modified, re-hashed reference gives exactly one changed pixel at (17,100),
with the correct magenta difference mask and mismatch exit code. Truncated
input and a missing replay executable produce no images. PNG chunk CRCs,
decoded pixels and no-overwrite behavior are tested. Native rejection tests
require the intended error exit code, so a crash/sanitizer abort cannot count
as a successful rejection.

The sanitizer limitation is inherited and explicit: optimized upstream
`ppu.c:1300` uses signed packed-bitplane shifts. Only `shift-base` instrumentation
for that upstream file is disabled; all new code remains instrumented. No
fully unsuppressed sanitizer pass or hardware accuracy is claimed.

## Exact blockers and corrective actions

1. **Private ROM visibility.** A local preflight on the supplied path returned
   `private ROM is not accessible on this machine; run this tool where the file
   resides`. The current host is Linux x86_64; the file is on the owner's Mac.
   Its contents/digest have not been verified here. Run the supplied workflow
   locally on that Mac, or use a private accessible ROM path on an owned Linux
   machine. No upload to GitHub/Actions is needed or recommended.
2. **Apple Silicon hosted capacity.** [Initial run 44140389161](https://github.com/nkanhai/MegaManX2GraphicsUpgrade/actions/runs/44140389161)
   spent 15 minutes queued, then ended with no runner and zero steps. GitHub's
   annotation says: `The job was not acquired by Runner of type hosted even
   after multiple attempts`, alongside macOS ARM64 capacity notices. This is
   an infrastructure failure, not a compiler result. The PR's current check
   provides a fresh attempt; if capacity is still unavailable, run the same
   arm64 commands on the owner's Mac or rerun when capacity returns. Do not
   re-pin dependencies or substitute Rosetta to make the result look green.

Source inspection finds native macOS plausible, but cannot establish that
Apple's SDK, fiber switching, dynamic libraries, video/audio and ROM-backed
execution all work. Linux builds and synthetic replay demonstrate the fallback
toolchain, not a ROM-backed executable. No intro-stage capture exists yet.

## Reproduction and next acceptance gate

Full commands and private handling instructions are in
[`PRIVATE_CAPTURE_WORKFLOW.md`](../guides/PRIVATE_CAPTURE_WORKFLOW.md).

1. On the ROM's machine, run preflight, then the isolated ROM-backed build.
   Preserve its source/pin/toolchain identity and private build log.
2. Calibrate the controller script and draw ordinal against the actual intro
   stage in a window. A default draw number is not proof of scene identity.
3. Capture one supported native frame and run `validate_capture.py`. Confirm
   the intro visually and require zero RGBA differences against its original
   pinned-renderer reference. Keep the images and capture private.
4. Repeat the exact script/config/ROM/executable in a new session/output
   directory. Compare image hashes and guest-counter/master-cycle metadata.
   Record a precise profile failure if the target frame is unsupported.
5. Extend to another raster/Cx4 scene only after the first reproducible frame.

Remaining risks: arbitrary Mode-1 combinations are not exhaustively qualified;
other modes/mosaic/interlace/host extensions remain rejected; timing remains
scanline-level; the original renderer is the oracle, not physical hardware;
the candidate intro timing and private build pipeline need an actual ROM run.
Native macOS remains unqualified until the platform gate executes. The Sprint 2
PR records these blockers and is not automatically merged.
