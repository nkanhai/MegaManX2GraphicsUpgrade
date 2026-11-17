# Rendering bridge proposal

Status: Sprint 0 design, now partially implemented by the
[Sprint 1 native recorder](../../bridge/README.md). The implemented profile is
native Mode 1 only; the runtime uses a bounded single binary container instead
of the proposed JSON-plus-blobs packaging. See the
[runtime v1 specification](../../schemas/RUNTIME_SCANOUT_V1.md) for the actual
contract and [Sprint 1 report](../roadmap/SPRINT_1_REPORT.md) for parity evidence
and remaining gaps. The proposals below remain the longer-term direction.
Source basis: [RENDERING_PIPELINE.md](RENDERING_PIPELINE.md).

Start with **recorded scanout snapshots**, captured inside the existing X2
presentation pass, then read offline. Preserve the SNES runtime as the only
simulation authority. Do not adopt a different beam driver, change NMI/IRQ,
reexecute HDMA, or extract semantic entities to make this work.

## Why this interface

| Approach | Benefit | Limitation / decision |
|---|---|---|
| Poll existing TCP commands | No engine changes, useful inspection now | Live reads can tear; historical memory is pre-raster; screenshot lacks layers; queries do not share one identity. Not the frame transport. |
| Extend debug TCP with capture request/status | Reuses discovery and controls | Useful later **only** to request a producer-owned snapshot and retrieve its ID/path. Network handlers must never assemble frames from live pointers. |
| Record inside X2's existing line walk | Captures what the renderer actually consumes; portable, repeatable, easy to diff | One small opt-in title hook; capture cost/storage must be bounded. Recommended first. |
| Native library embedded in Unity | Potentially low latency and unified input/audio lifecycle | Premature ABI, initialization, SDL/thread ownership, packaging and platform decisions. Defer until recorded fidelity works. |
| Reconstructed draw-command stream | Convenient Unity objects | Discards hardware information too early; transparency/priority/raster behavior becomes implicit. Derive later from preserved raw facts. |

## Capture contract proposed for Sprint 1

1. Opt-in adapter under `hd2d/bridge/`; disabled builds execute the original
   call sequence unchanged. At entry to `X2DrawPpuFrame`, start a bounded frame.
   At each existing line call, observe **before** `ppu_runLine(g_ppu, line)`.
   Include line 0, whose render step initializes field state. Do not move
   `SimpleHdma_DoLine`: its writes affect the following line.
2. Copy decoded, named display registers and necessary render latches. Copy
   effective `PpuRenderVram(ppu)`, CGRAM and both OAM regions into owned storage.
   Deduplicate equal memory images if needed, using byte comparison rather than
   assumed write-counter coverage. Associate every line with immutable memory
   image IDs. Native `Ppu` pointers/padding/caches must never cross the boundary.
3. At the end of the same draw pass, copy visible framebuffer rows with their
   source pitch. Encode an explicit byte layout (e.g. RGBA8 after conversion;
   never ambiguously call uint32 memory "ARGB"). Attach dimensions and hash.
4. Publish only a complete record. In the first implementation, record one
   explicitly requested frame on the owner thread and write after copies are
   complete. A file failure cancels the capture, never skips simulation work.
   Later use a bounded producer/consumer queue and background writer. Queue
   exhaustion drops a **whole capture**, increments a drop counter, and does
   not stall or modify the game's input/timing logic.
5. First runtime profile is native 256x224 with widescreen, run-ahead, rollback,
   host overlays, render-VRAM substitutions and other unsupported presentation
   policies excluded by explicit capture rejection, not silently disabled in
   the game. Record the chosen renderer and sprite-limit policy. Add support
   incrementally with fidelity tests.

A diagnostic opt-in copy can increase wall-clock cost. "Read-only" means no
writes to simulation state, not a claim of zero overhead. Measure enabled and
disabled cost before continuous capture; do not sell this as live integration.

### Minimum data for reconstruction

For the supported static subset: full VRAM (64 KiB), CGRAM (512 bytes), OAM
(544 bytes), BG mode/size/map/character bases, four scroll pairs, main/subscreen
masks, OBSEL and OBJ rotation/order state, transparency, priority and flips,
brightness/forced blank, output dimensions and chosen sprite-limit policy.
VRAM tile identity is content plus interpretation/context, not a persistent
asset ID: the game can overwrite the same address in the next frame.

For an arbitrary original frame, the static subset is insufficient. Preserve
all display registers (`inidisp..cgwsel`, including full `bgmode` and Mode-7
matrix/select), effective per-line changes and memory versions, relevant
latches (`scrollPrev`, `scrollPrev2`, `m7prev`, OAM port/rotation state), mosaic
origin, field parity/interlace/overscan, windows and main/subscreen color math,
and any host policies that affect the raster. Specify which are initial
conditions versus computed line state. Known cached values must be recomputed
by the consumer, not serialized as native memory. Unknown/unimplemented
features require an explicit unsupported result and a reference-pixel fallback.

A complete named-field list for the native capture profile must be verified
by differential replay tests against the pinned PPU. This sprint establishes
where it can be observed; it does not claim the minimal complete fidelity
contract is already proven. Subscanline fidelity beyond the upstream renderer
is a separate hardware-oracle problem.

## Runtime envelope and synchronization

Use a new runtime-format identifier (the synthetic v1 format deliberately
rejects runtime data). Proposed JSON manifest plus little-endian binary blobs:

- Schema version and feature/profile identifiers, producer code revision,
  framework SHA, ROM digest (never ROM bytes), configuration and render flags.
- Session ID and reset/load epoch; monotonically increasing capture sequence;
  guest counter observed at draw entry, master-cycle timestamp, phase
  `x2_scanout`, line convention and completeness/drop counters. Guest counters
  can rewind; sequence must not. Do not infer NTSC timing solely from host
  `simulation_hz=60.0`: record actual guest master clocks and declared pacing.
- Initial display/latch state, ordered lines 0..224, per-line register values
  or explicit deltas, and immutable VRAM/CGRAM/OAM image references.
- Reference image descriptor with width, height, stride-free payload format,
  color model, byte length and SHA-256. Match its capture ID to all state.

Use full named register records first; add deltas only after correctness.
Serialize uint16 words explicitly little endian, never `fwrite(Ppu)`.
Blob descriptors include relative filenames, exact lengths and hashes; a future
reader rejects traversal, unknown major versions, excessive allocations,
missing/reordered lines, missing blobs and incomplete records. Write blobs and
manifest in a temporary directory and rename the completed directory/manifest
last. No partly written capture is discoverable as valid. Keep all real-game
captures local and ignored.

Frame order describes simulated scanout, not each display refresh. Start with
run-ahead/rollback excluded, then extend the envelope to mark authoritative vs
speculative pictures. Pausing, reset and state load must not reuse a capture ID.
A renderer consuming recorded data cannot write back into guest memory.

## Cost and portability

One raw memory image is 66,592 bytes. At 60 frames/s a single image per frame
is about 4.0 MB/s; a naive 225-line full copy is about 15.0 MB/frame or
899 MB/s before register/reference data. These are arithmetic estimates,
**not measured throughput**. A 256x224 RGBA8 reference is 229,376 bytes/frame.
Repeated unchanged memory can share immutable storage; test ABA writes and
save-state restores. Budget and reject overflowing captures rather than
truncating them. Compress files offline before considering live deltas.

The Python spike validates bounded little-endian/base64/hash serialization;
production recording should avoid base64 expansion for large frame sequences.
A portable C implementation and file consumer isolate us from SDL/Unity
threading until a native library/IPC decision is justified by measurements.

## Unity reconstruction plan

First build a C# file reader/importer for a qualified recorded subset. Retain
RGB15/palette-index data and point sampling; reconstruct BG tilemaps and OBJ
pieces with correct address units, flip bits, sprite sizes, transparent index,
priority ties and wrapping. Use an orthographic, pixel-aligned diagnostic view.
Register scroll remains screen presentation data, not Unity gameplay physics.

Ordinary SpriteRenderer sorting and alpha blending do not implement SNES
main/subscreen color math, windows or every priority rule. A faithful comparison
path may need explicit compositing passes or pixel buffers. Keep reference
pixels available for unsupported features; report the feature gap instead of
substituting bloom. Palette changes are facts, not verified hit events.

Evaluate URP 2D Renderer first with synthetic normal maps and Light2D only
**after** reconstruction. Compare Universal Renderer if needed for 3D geometry,
particles or depth. No final choice, Unity project version or native ABI is
committed now. Semantic tags/material assignments remain a separately versioned
annotation layer with evidence/confidence, never changes to simulation.

## Compatibility and evidence

Maintain existing debug commands unchanged. Any future capture command returns
an immutable ID/status; it does not change `get_ppu_state` or `screenshot` output.
Keep the capture adapter outside shared framework code where possible; identify
small generally useful hooks for later upstream review. Do not re-pin the
framework merely to introduce this experiment.

Each fidelity claim must cite producer revision/configuration, capture ID,
reference hash and a pixel-diff report. Separate "matches this upstream
renderer" from "matches hardware". ROM-free tests can prove capture isolation,
serialization, line order, failure behavior and synthetic raster matching.
Real X2 scenes and Cx4 effects still require a user-supplied ROM and later playtests.
