# Original synthetic fixture

No Capcom graphics, ROM bytes, firmware or extracted assets are used here.
`tools/frame_format.py:synthetic_frame` is the executable recipe. The full
snapshot is generated under ignored `hd2d/local/`, and `expected-summary.json`
is its human-readable size/hash/scroll report.

- Native 256x224 static Mode 1, 8x8 BG tiles, BG1/BG2/OBJ on the main screen.
- BG1 tile 1 at byte 32: alternating color indices 1,2 across each row.
  Its 32x32 map begins at byte 0x4000 and alternates horizontal flip flags.
- BG2 tile 1 at byte 0x2020: transparent/blue checkerboard. Its map begins
  at byte 0x4800, uses palette 1, and has the high priority bit set.
- BG scroll `(x,y)` is `(3,5)` and `(11,7)`, deliberately not tile-aligned.
- CGRAM includes red 0x001f, green 0x03e0, blue 0x7c00, white 0x7fff and
  yellow 0x03ff. This makes channel order and endianness testable.
- OBJ slots 0 and 1 are adjacent 8x8 pieces at (24,32) and (32,32), using
  tile 1 and palette 0 in the OBJ palette range. Slot 1 is flipped horizontally.
  Slot 2 is vertically flipped at signed x=-4, y=48; its x-high bit lives in
  OAM byte 512, bit 4. Remaining pieces have y=240.

These are hardware graphics pieces, not characters or semantic objects.
Tests decode selected planar pixels and raw map/palette/OAM bytes independently
of serialization. No screenshot or Unity rendering is claimed by this fixture.
