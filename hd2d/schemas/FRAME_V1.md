# Synthetic PPU frame, version 1

Status: implemented experimental serialization contract. Not the production
runtime bridge or a claim to represent every SNES frame. Normative validation
is `tools/frame_format.py:validate`; the tests cover the supported subset.

UTF-8 JSON, at most 262,144 bytes. The writer sorts keys, uses compact JSON
and appends LF. Readers accept whitespace/key order changes, but reject duplicate,
missing or unknown keys. Booleans/floats are not integers. Unknown versions or
features fail explicitly. Base64 is canonical RFC 4648, with padding and no
whitespace. Hashes cover decoded bytes, not JSON text; they detect corruption,
not malicious edits or asset identity.

| Top-level field | Contract |
|---|---|
| `format` | `x2-hd2d.synthetic-ppu` |
| `version` | integer 1 |
| `source` | `synthetic` only; cannot mislabel a runtime dump as this format |
| `sequence` | integer 0..2^53-1, ordering within an external fixture sequence |
| `profile` | `static-mode1-native` |
| `viewport` | exactly `width: 256, height: 224`; top-left origin, y increases down |
| `ppu` | explicit decoded register values below; not a register-write stream |
| `memory` | exactly `vram`, `cgram`, `oam` blocks |

Each memory block has exactly `encoding: "base64"`, integer `byte_length`,
64-character lowercase `sha256`, and string `data`. The length is checked
before decoding; decoded length and checksum must match.

| Block | Bytes | Layout |
|---|---:|---|
| VRAM | 65,536 | 32,768 uint16 words, explicitly little endian; addresses in this byte stream are byte offsets |
| CGRAM | 512 | 256 little-endian RGB15 words; bits 0..4 red, 5..9 green, 10..14 blue, bit 15 zero |
| OAM | 544 | first 512 bytes: 128 pieces x `[xLow,y,tile,attributes]`; final 32 bytes: four 2-bit `(xHigh,size)` pairs per byte |

`ppu` scalar integers: `inidisp`, `obsel`, `bgmode`, `mosaic`, `setini`,
`window1left`, `window1right`, `window2left`, `window2right`, `cgadsub`, `cgwsel`
are 0..255; `bgTileAdr` and `wbgobjlog` are 0..65535; `windowsel` is 0..0xffffff;
`fixedColor` is RGB15. Arrays: `bgXsc` has 4 bytes, `hScroll`/`vScroll` have
4 uint16 values, `screenEnabled`/`screenWindowed` each have 2 values in 0..31
(main then sub). Names track the upstream `Ppu` fields without inheriting its
C layout, padding or pointers.

Additional profile restrictions: `bgmode=1` (8x8 BG tiles); `mosaic`, `setini`,
`windowsel`, `wbgobjlog`, `cgadsub`, `cgwsel` are zero. No subscreen or windowed
layers. Only main-screen BG1, BG2 and OBJ can be enabled (mask 0x13).
Registers and memory are constant throughout the field. `inidisp` preserves
brightness/forced blank; `obsel` preserves OBJ size/base configuration. There
is no implied world-space camera or entity identity. Fixed/window values
remain explicit even though their corresponding features are disabled.

BG tilemap entries are SNES words: tile bits 0..9, palette 10..12, priority 13,
horizontal flip 14, vertical flip 15. BG map base in words is
`(bgXsc[layer] & 0xfc) << 8`; character base in words is
`((bgTileAdr >> (4*layer)) & 15) << 12`. Convert words to bytes before indexing.
Mode 1 BG1/2 and OBJ use 4bpp planar tiles, 32 bytes per 8x8 tile. Index zero
is transparent for a layer/OBJ, but CGRAM[0] also supplies the backdrop.
Palette bank, priority and OAM order must survive import. Signed OBJ X uses
9-bit wrap; Y wrapping and large sprite splitting remain renderer duties.

## Example

```sh
python3 hd2d/tools/frame_format.py demo hd2d/local/demo.json
python3 hd2d/tools/frame_format.py validate hd2d/local/demo.json
```

A compact, committed example is
[`expected-summary.json`](../fixtures/synthetic/expected-summary.json). The full
89 KiB-class JSON is generated locally to avoid committing repeated zero data.
The fixture recipe and independent layout expectations are documented in
[`fixtures/synthetic/README.md`](../fixtures/synthetic/README.md).

## Deliberately absent

No field timing, session/reset epoch, scanline deltas, windows/color math,
Mode 7, hires/interlace, sprite-limit policy, widescreen policies, semantic
events, native ABI or Unity importer is implemented. The next **runtime**
format must use a distinct identifier/version and record capture phase,
provenance, configuration, line-state/memory associations, completeness and
reference pixels as specified in the bridge proposal. Do not silently extend
this static format and claim fidelity. The Python prototype establishes only
portable encoding, bounded validation, layout preservation and error handling.
