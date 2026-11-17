# Sprint 1 report — native scanout capture

Date: 2026-11-17. Canonical repository:
[`nkanhai/MegaManX2GraphicsUpgrade`](https://github.com/nkanhai/MegaManX2GraphicsUpgrade).
Branch: `feature/hd2d-sprint-1`, based on merged master
`b96ac8f202cbe61432e0145196bd03911d17928a`.
Implementation commit: `16aa557`; test/CI commit: `4c4e57b`.
Sprint 0's upstream audit remains applicable. All five recursive dependency
pins in `upstream-lock.json` were verified unchanged; no submodule source,
generated game code, attribution, license, or gameplay logic was modified.

## Delivered

The title's existing `X2DrawPpuFrame` now has three optional observation hooks:
begin, before each of its 225 PPU calls, and after its complete line/HDMA walk.
The original DMA initialization, PPU calls and HDMA order are unchanged.
`HD2D_SCANOUT_CAPTURE` defaults OFF; preprocessing removes the hook calls and
the recorder is not linked. Both desktop host variants build without a ROM.
The default executable has no `hd2d_x2_*` symbols; the enabled one has all three.

An enabled build accepts one environment-selected draw request. The owner
thread captures 70 named register/latch fields per line, immutable independent
VRAM/CGRAM/OAM versions, session/epoch/sequence/draw identity, guest counter
and master cycles, source/engine revisions, optional observed-cart hash, and
the same pass's framebuffer. Capture does not write PPU, CPU or DMA state.
Run-ahead/netplay/rollback/rewind presentation and unsupported PPU/host policies
cause explicit rejection, not configuration changes. A failed attempt is
consumed and the normal draw still completes.

The new runtime format is `x2-hd2d.runtime-scanout` v1, profile
`native-mode1-v1`. It does not reuse the Sprint 0 synthetic JSON schema. A
single bounded little-endian container replaces the proposal's JSON manifest
and separate blobs: no external paths, one whole-record hash, and simpler
complete-file publication. This packaging change preserves the proposed
recorded-scanout architecture. The precise wire contract is in
[`RUNTIME_SCANOUT_V1.md`](../../schemas/RUNTIME_SCANOUT_V1.md).

Consecutive equal memory images share versions by byte comparison. The
synthetic A→B→A palette change creates three immutable images and is detected
without upstream write counters. All fields and memories are explicitly
encoded; no native PPU layout or pointer is serialized. The final reference
is RGBA8 with alpha 255, converted row-by-row from the pitched upstream surface.
Completed bytes remain immutable until the low-level caller explicitly starts
another record or frees it. The title adapter publishes once and frees them.

New directories are created exclusively; temporary output is closed and
renamed to `capture.x2scan` last. Existing directories are never overwritten.
Allocation, budget, profile, sequence-of-lines, surface-change, short-write and
file errors are handled without skipping simulation/render work. There is no
network reader of mutable memory, continuous queue, Unity renderer, or asset
catalogue/extraction stage.

## Validation results

Local environment: Debian 13 Linux x86_64, GCC/G++ 14.2.0, Python 3.12.14,
CMake 3.31.10, Ninja 1.13.0 package, SDL2 2.32.4 for setup-host builds only.
Commands and entry points are in [`bridge/README.md`](../../bridge/README.md).
Desktop builds use the same isolated SDL/GL prefix and setup-host options
documented in Sprint 0, plus the ON/OFF capture option.

| Check | Result and scope |
|---|---|
| Existing Python suite | **10/10 pass**, synthetic v1 unchanged |
| Native Release CTest | **3/3 pass**: armed/unarmed title loop, compiled-out loop, cross-process files/adapter checks |
| Native Debug ASan/UBSan CTest | **3/3 pass with the narrow upstream exception below**; leak detection enabled |
| Pinned upstream checks | **8/8 pass**: raster snapshots, render workers, and six PPU harnesses |
| Complete SDL2 setup host, OFF and ON | **Both build successfully**, no ROM/generated game-code requirement |
| Existing diagnostics CTest | **2/2 pass in each host build** |
| Dependency pins / diff hygiene | Unchanged gitlinks, clean submodules, `git diff --check` passes |
| Real X2 gameplay / hardware / Unity | **Not run; not qualified** |

The synthetic harness compiles the **actual title source**, with the pinned
PPU renderer and deterministic HDMA/host stand-ins. Its original graphics
contain two visibly contributing BG layers and three visibly contributing OBJ
pieces. Removing each changes the result. It exercises horizontal/vertical
scroll, flips/priorities, windowing/subscreen math, mid-frame palette A→B→A,
VRAM, low/high OAM changes, brightness and forced blank. It records lines
0..224 and checks that post-line HDMA changes first appear on the next line.

Offline replay decodes the exported representation into a fresh PPU, regenerates
derived caches, and calls the pinned `ppu_drawCapturedLine`. **All 57,344 RGBA
pixels match** for flags 0, 1, 8 and 9: legacy/optimized and each sprite-limit
policy. This compares each renderer against its own reference; it does not
assert that legacy and optimized renderers are mutually identical. Raw source
pixels including pitch padding, the entire initialized PPU struct, CPU state
and DMA state match between armed/unarmed runs. Cross-process compiled-out,
unarmed adapter, armed adapter, rejected requests and failed writes also have
identical surface hashes. Later writes to source memory cannot change the
owned record or its successful replay.

Failure tests cover allocator failures on either allocation, memory exhaustion
under per-line VRAM replacement, malformed line order/incomplete frames,
surface changes, unsupported modes/policies, and publication errors. Both
independent readers reject 22 structural/hash/size corruption cases; a 23rd
case has a valid container/hash but deliberately wrong reference RGB and is
rejected by the native pixel oracle. This distinction prevents an integrity
check from being reported as visual fidelity. Short writes are injected using
Linux `RLIMIT_FSIZE`; no partial final file or changed raster is produced.
The real adapter is tested for one attempt among three draws, target selection,
host policy rejection, session validation, generation metadata and hashing an
original synthetic byte string as cart identity. No game ROM is used.

**Known failing check:** a fully unsuppressed UBSan run fails in the unchanged
pinned `ppu.c:1300` because the optimized bitplane path left-shifts signed
`0x01010101` by 7. It reproduces with capture compiled out. The test build
disables only `shift-base` instrumentation for that upstream source file;
ASan, other UB checks, and full instrumentation of all new code remain active.
The dependency is not patched or re-pinned. Existing upstream compiler warnings
also remain. This is not a claim of a fully UB-clean engine.

GitHub Actions now runs the native Release and sanitizer matrix on pushes and
PRs, with pinned submodules and focused upstream tests. The existing desktop
job builds capture OFF and ON, and the existing Python job remains. All jobs
are ROM-independent. Their remote status is reported on the pull request;
the results above describe local verification.

## Reproducible parity artifact

Generate with `scanout_on hd2d/local/<new-directory>` and inspect/replay using
the commands in the bridge README. Generated files remain ignored and are
not committed. For the default optimized fixture:

| Evidence | Value |
|---|---|
| Capture ID | session `12121212121212121212121212121212`, epoch 7, sequence 1, draw 1 |
| Guest counter / clocks | 42 / 123456789 (synthetic) |
| Producer | zero SHA denotes standalone fixture; implementation `16aa557`, harness `4c4e57b` |
| PPU engine | `93fc677a2a9b688751d575ed1232343d83d3d2c0` |
| Versions | VRAM 2, CGRAM 3, OAM 3 |
| Canonical reference SHA-256 | `5e0218c16f560cac220b47e232a5e380caadacc3d9a74898a7a2cb8a2771d41f` |
| Container trailing digest | `87808896f1e284c3309def8e0533a0e890f56ee11fe403431fe7e5bcf52f28f8` |
| Pixel differences | 0 of 57,344 pixels / 229,376 RGBA bytes |

## Cost and bounds

The capture arena and maximum file size are **4,194,304 bytes**, plus fixed
recorder metadata under 1 KiB. The minimum successful one-version file is
362,876 bytes. The tested raster file is **430,584 bytes (~420.5 KiB)**. It copies
426,616 register/memory/reference payload bytes and offers 14,916,608 memory
bytes to comparison (actual memcmp traffic can be lower on early mismatch).
Buffers, descriptors and line counts cannot grow beyond the fixed budget.
Per-line full VRAM changes deliberately exceed it and reject the entire record.

Release benchmark, 50 draws each, optimized renderer, on this cloud host:

| Mode | Mean process CPU | Mean monotonic elapsed |
|---|---:|---:|
| Compiled-in, unarmed | 0.866 ms | 0.868 ms |
| Armed, copy + encode + reference + SHA | 3.272 ms | 3.275 ms |
| Added one-frame cost | 2.406 ms | 2.407 ms |

These are harness observations, not a real-game budget or a stable CI timing
threshold. Other local samples varied with host load (roughly 2.4–3.0 ms added
CPU time). Setup/reset is outside the timed region; draw validation is in both.
The preallocated-core benchmark excludes arena allocation, file publication,
per-hook runtime timing instrumentation and optional loaded-cart hashing.
The title adapter separately logs capture and publication process CPU time,
including allocation/cart hashing when applicable. File I/O can block for an
unbounded wall-clock interval; no asynchronous writer or 60-fps claim is made.
Memory/file limits are hard tests, not machine-dependent timing assertions.

## Risks, assumptions and remaining work

- **Qualified subset only.** Mode 1/native 256×224 is accepted, but one family
  of synthetic scenes does not exhaust big tiles, sprite overflow/rotation,
  all priority/window/math combinations, or every engine state. Further
  differential scenes should precede general Mode-1 fidelity claims. Modes
  0/2–7, mosaic, interlace/hires/overscan and host presentation extensions are
  explicitly rejected rather than approximated.
- **Canonical bytes, not source padding or hardware truth.** Legacy X bytes
  are unwritten; alpha is normalized to 255. Derived caches are assumed valid
  in the live pinned renderer and regenerated offline. Scanline boundaries
  reflect this engine, not subscanline SNES timing. PPU parity is not hardware
  parity, and no original-title/Cx4 scene was tested.
- **Host context and identity.** Runahead/netplay/rollback and open rewind are
  rejected. Epoch follows the existing upstream generation API; the adapter
  has only one request/sequence per process. Future re-arming must own sequence
  monotonicity, cancellation and resets. Session UUID reuse and dirty/stale
  CMake revision stamps can weaken provenance; use a new session and clean
  configured source. Cart digest refers to observed, possibly patched memory.
- **Synchronous diagnostic cost.** Whole-memory comparison and synchronous
  hashing/writing are intentionally simple and bounded in space. Broader
  profiles or continuous capture need a bounded immutable queue, cancellation
  and measured throughput before any live integration.
- **Portability and upstream UB.** Capture explicitly rejects big-endian
  hosts. Linux is verified; Windows/macOS/Apple Silicon and durable crash
  recovery remain work. The upstream signed-shift issue requires separate
  review, without silently changing this project's pin.

Next work is additional synthetic boundary coverage, then optional owner-run
native and raster/Cx4 captures using a legally supplied local ROM, with exact
pixel diffs and explicit unsupported results. Expand the profile only with
evidence. Unity reconstruction, semantic asset identity, real-game extraction
and live integration were not started. This sprint's PR is for review only;
it is not automatically merged.
