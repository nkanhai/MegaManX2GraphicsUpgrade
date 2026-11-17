# Native scanout recorder

Sprint 1 implements a diagnostic, owner-thread recorder at `X2DrawPpuFrame`.
It does not render in Unity, extract game assets, or alter simulation. The
standalone suite needs CMake 3.20+, Python 3.10+, a C11 compiler and SDL2
development files (Sprint 3 keyboard tests). Linux is
verified; a Darwin dead-strip link path and native Apple Silicon CI are added
in Sprint 2, with qualification status in the [platform audit](../docs/architecture/MACOS_NATIVE_AUDIT.md).
The unmodified pinned PPU is the offline replay oracle.

## ROM-independent build and checks

From the repository root:

```sh
git submodule update --init --recursive
cmake -S hd2d/tests/native -B hd2d/local/native -DCMAKE_BUILD_TYPE=Release
cmake --build hd2d/local/native --parallel 2
ctest --test-dir hd2d/local/native --output-on-failure
python3 -m unittest discover -s hd2d/tests -v
python3 hd2d/tools/check_upstream.py
```

The native tests link the actual title loop with synthetic HDMA inputs, the
pinned PPU, and small standalone host stubs. Both the recorder and the real
one-shot adapter are tested; unused CPU/ROM code is discarded by Linux section
GC (Darwin uses dead stripping). No ROM, generated game code, proprietary
library, or extracted art is needed. SDL tests use its dummy video driver.
Source patterns are original, generated in `scanout_test.c`.

Inspect an original synthetic capture (choose a new directory each time):

```sh
hd2d/local/native/scanout_on hd2d/local/example-scanout
python3 hd2d/tools/scanout_format.py hd2d/local/example-scanout/capture.x2scan
hd2d/local/native/scanout_replay hd2d/local/example-scanout/capture.x2scan
```

The Python reader checks structure/integrity only. Native replay decodes the
file into a fresh PPU and compares all canonical RGBA bytes with the reference.
It is an offline diagnostic executable, not a function to call alongside a
live game. It refuses unsupported profiles or an unexpected engine revision.

Sprint 2 adds image/report export, including valid reconstructions that differ:

```sh
python3 hd2d/tools/validate_capture.py hd2d/local/example-scanout/capture.x2scan \
  --replay hd2d/local/native/scanout_replay --output hd2d/local/example-validation
```

The new directory receives reference/reconstruction/difference PNGs and a
JSON/text report. Exit 0 means exact equality, 1 means differing pixels, and
2 means an input/tool error without a fidelity verdict. Profile failures now
identify the first failed field/policy, its observed value, and draw-entry or
line boundary. The wire format and default-off integration are unchanged.
See the [private real-game workflow](../docs/guides/PRIVATE_CAPTURE_WORKFLOW.md)
for ROM-backed builds and intro-stage calibration.

Sanitizer check:

```sh
cmake -S hd2d/tests/native -B hd2d/local/sanitize \
  -DCMAKE_BUILD_TYPE=Debug -DHD2D_SANITIZE=ON
cmake --build hd2d/local/sanitize --parallel 2
ASAN_OPTIONS=detect_leaks=1:halt_on_error=1 \
UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1 \
  ctest --test-dir hd2d/local/sanitize --output-on-failure
```

The pinned optimized PPU has signed-shift UB in packed bitplane expansion,
reproduced with capture compiled out at `ppu.c:1300`. The harness disables
**only `shift-base` instrumentation for upstream `ppu.c`**. All HD-2D code
retains full ASan/UBSan instrumentation; the other upstream checks remain on.
No dependency source or pin is changed. A completely unsuppressed sanitizer
pass is not claimed.

## Opt-in title adapter

The root build defaults to `HD2D_SCANOUT_CAPTURE=OFF`: hooks preprocess to
no-ops and no recorder objects are linked. Add `-DHD2D_SCANOUT_CAPTURE=ON` to
the existing host build command to compile the adapter. An enabled build
without `HD2D_CAPTURE_DIR` performs no recording or file I/O.

For later owner-supplied gameplay testing, launch an enabled host with:

```sh
HD2D_CAPTURE_DIR=hd2d/local/my-new-capture \
HD2D_CAPTURE_SESSION=$(python3 -c 'import uuid; print(uuid.uuid4().hex)') \
HD2D_CAPTURE_DRAW=600 ./build/MegaManX2SNESRecomp
```

This command is documentation, not a ROM requirement for this sprint. The
parent directory must exist; the requested directory must not. DRAW selects
the 600th call to the title draw loop, not a guest frame or wall-clock time;
default is the first call. It may select an unsupported boot/menu frame, which
is an explicit failed attempt. Use native presentation with run-ahead/netplay
off; the adapter will reject unsupported settings without changing them.
SESSION must be a fresh nonzero 32-digit lowercase hex identifier. The single
attempt is consumed even when allocation, profile checks, or writing fails.
This one-shot mode remains unchanged. Sprint 3 additionally supports
`HD2D_CAPTURE_INTERACTIVE=1` with no DRAW selector: Left Control + Left Shift +
F10 requests the next draw, with a distinct directory/sequence per attempt.
Use the [interactive launcher and guide](../docs/guides/INTERACTIVE_CAPTURE.md)
to create unique private sessions automatically. No runtime debug/TCP command
is added. The [trigger audit](../docs/architecture/INTERACTIVE_TRIGGER.md)
explains callback ownership, conflict detection and the 64-attempt session cap.

The header records the observed guest counter and master cycles, source Git
revisions, engine pin, session/draw/sequence, and the upstream reset/load
generation. Guest counters may rewind; they are not capture IDs. An optional
digest identifies already-loaded cart memory; no ROM file is opened/exported.
Reconfigure from a clean committed tree for trustworthy producer attribution.

## Boundaries

See [wire format](../schemas/RUNTIME_SCANOUT_V1.md) for exact field order,
versioning, rejection rules, file publication, and the RGBA padding convention.
The 4 MiB arena includes the final file; metadata is fixed and below 1 KiB.
Unchanged arrays are compared each line, so roughly 14.9 MB are offered to
comparison even for a small capture. Whole-image VRAM changes on enough lines
exhaust the budget and drop the complete capture. This is intentional.

File output, hashing and capture are synchronous. CPU timing printed by the
adapter separates copying/hashing from publication but is not wall time or a
gameplay latency guarantee; cart hashing can add work. The owner reports a
native ARM64 real-game build and exact reconstruction of an opening-narrative
frame; two interactive gameplay captures remain a separate local acceptance
gate. Hardware accuracy, broader X2/Cx4 scenes, arbitrary Mode-1 coverage,
Windows/SDL3 and broader presentation profiles remain unqualified. Reference pixels are present only
in successful captures; unsupported frames do not produce a fallback file.
