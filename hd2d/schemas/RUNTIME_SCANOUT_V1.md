# Runtime scanout v1

Identifier: `x2-hd2d.runtime-scanout`, version **1**, profile **1**
(`native-mode1-v1`). This binary format is distinct from Sprint 0's
`x2-hd2d.synthetic-ppu` JSON. A synthetic native test may produce runtime v1;
the `source` field identifies its origin. Neither format accepts the other.

## Container

A single `capture.x2scan` contains everything. All integers are explicitly
little endian. There is no native struct serialization, compression, external
blob path, or executable content. Reject unknown versions/profiles, inconsistent
lengths, unknown engine revisions, and files over **4,194,304 bytes** before
allocating from file data. The last 32 bytes are SHA-256 of every preceding
byte. This detects damage; it is not an authenticity signature.

| Header offset | Bytes | Meaning |
|---|---:|---|
| 0 | 8 | ASCII `X2SCAN01` |
| 8, 12 | 4 each | Version 1, profile 1 |
| 16 | 4 | Complete file length, including digest |
| 20, 24, 28, 32 | 4 each | 225 lines, width 256, height 224, 70 fields |
| 36 | 4 | Source: 0 original synthetic graphics, 1 title runtime |
| 40 | 16 | Caller-generated nonzero session ID |
| 56 | 8 | Reset/load epoch from `RtlStateGeneration()` |
| 64 | 8 | Nonzero capture sequence within session (one-shot adapter: 1) |
| 72 | 8 | Nonzero draw ordinal since process start with capture configured |
| 80 | 8 | Observed guest counter; signed value represented as uint64 bits |
| 88 | 8 | Observed `g_cpu.master_cycles` at draw entry |
| 96, 136 | 40 each | Lowercase ASCII producer and engine Git SHAs |
| 176 | 4 | Renderer flags: 0 legacy; bit 0 optimized; bit 3 no sprite limits |
| 180, 184, 188 | 4 each | VRAM, CGRAM, OAM version counts (each 1..225) |
| 192 | 8 | Register/memory/reference payload bytes copied |
| 200 | 8 | Memory bytes offered to comparison (memcmp may exit early) |
| 208 | 32 | SHA-256 of observed loaded cart, or all zero if unavailable |

Producer SHA is stamped at CMake configuration; reconfigure a clean checkout
after committing. The standalone synthetic harness uses a zero producer SHA
and the actual pinned engine SHA. The cart digest describes the in-memory
cart at capture time, potentially modified by upstream runtime patches; it
is **not** a claim about an original ROM's distribution identity. The adapter
hashes an already-loaded cart only, capped at 16 MiB, and never opens a ROM
file. Absent/oversized carts have an explicitly unknown digest. No cart bytes
are exported. A fresh UUID per process is required to avoid session reuse.

## Ordered state and pixels

At byte 240 are exactly 225 records of 296 bytes. Each begins with four uint32
values: line number, VRAM version ID, CGRAM version ID, OAM version ID. The
remaining 70 uint32 values are the named fields, in the exact order specified
by [`fields.inc`](../bridge/fields.inc). Array elements have explicit indices.
Types `u8`, `u16`, `bit`, `u32`, `s16`, and `s32` constrain values; signed
fields are sign-extended to 32 bits, booleans are 0 or 1. No padding is present.

The list includes all decoded display registers from `inidisp` through
`cgwsel`, VRAM/CGRAM/OAM port state, scroll/Mode-7 latches, mosaic origin,
sprite overflow flags, field parity/interlace/overscan state, and counters.
Some are inactive under this profile; preserving them does not qualify the
unsupported feature. Host caches, scratch buffers, pointers and write counters
are excluded. Replay regenerates brightness/mosaic caches and sprite buffers.

Phase is always **before the existing `ppu_runLine(line)`**. Line 0 initializes
field state; lines 1..224 produce rows 0..223. Existing HDMA runs **after** each
line and affects the following record. The last HDMA writes after line 224
are not inputs to the captured visible image. No subscanline events are implied.

At byte **66,840** are **229,376** reference bytes: 256×224 tightly packed
RGBA8, top-to-bottom, left-to-right, alpha 255. RGB is the pinned PPU's expanded
8-bit channel output (no new gamma/color transform). Source pitch is honored
while copying and not retained as pixel padding. Upstream's optimized renderer
writes BGRX on supported little-endian hosts; the legacy renderer leaves X
unwritten. Consequently parity means **byte-identical canonical RGBA**, not
identity of source X padding. Tests separately compare the complete source
framebuffers, including their padding, with capture on/off.

## Memory versions

At byte **296,216**, until the final digest, are memory chunks. Each has three
uint32 values (`kind`, `id`, `length`) followed by exactly `length` bytes:

| Kind | Length | Encoding |
|---:|---:|---|
| 0 VRAM | 65,536 | 32,768 little-endian uint16 words |
| 1 CGRAM | 512 | 256 little-endian uint16 words, raw bits retained |
| 2 OAM | 544 | 256 little-endian uint16 words followed by 32 high-OAM bytes |

IDs start at zero and increase consecutively within each kind. Chunks of
different kinds may interleave. All line references must exist, start at 0,
and either repeat or advance by one; every version must be used. Equal
consecutive images reuse an ID by byte comparison, independently for each
memory. An A→B→A change receives IDs 0→1→2: older owned images never change.
Direct writes and restores are detected without trusting upstream counters.

## Profile and publication

Version 1 qualifies only the pinned engine
`93fc677a2a9b688751d575ed1232343d83d3d2c0`. Capture and replay reject other
display modes, any mosaic, SETINI features, overscan/interlace, widened
surfaces, render-VRAM substitution, extra host objects, overlay outputs,
Mode-7 HD output, active shadow providers, debug layer masks, and relevant
widescreen policies. Run-ahead, active netplay/rollback and an open rewind UI
are rejected by the title adapter. Nothing is disabled in the game to make a
capture succeed. Supported settings are checked at every line; changing the
surface, pitch, or renderer flags aborts the record. Only little-endian hosts
are currently qualified. Caller-owned surfaces must have 224 valid rows.

An explicit request causes one attempt, including failure. The owner thread
allocates one bounded arena, observes lines, then copies the completed pixels
and hashes the immutable record. Allocation, capacity, order, unsupported
profile, and publication failures never skip the original draw/HDMA work.
The writer creates a **new** output directory, writes `capture.tmp`, closes
it successfully, and renames it to `capture.x2scan` last. Existing directories
are refused. Readers only accept the final filename with valid size/hash.
Write/close failures remove the temporary file and owned empty directory.
This provides complete-file publication, not power-loss durability: no fsync
guarantee, and a crash may leave an incomplete directory to remove manually.

The one-shot adapter logs one outcome and frees its arena. It has no re-arm,
network queue, continuous capture, or capture-drop history. A multi-capture
caller of the low-level API owns sequence monotonicity and session identity;
starting another record is an explicit invalidation of the previous view.
