# Rendering pipeline at the audited revision

All framework paths below are relative to `snesrecomp/runner/src/`; title paths
start with `src/`. Pin references are in [UPSTREAM_AUDIT.md](UPSTREAM_AUDIT.md).
Line numbers below refer to those immutable upstream revisions.

## Actual X2 call chain

```text
src/main.c -> desktop/mmx23_host_main.inc -> desktop/host_main.c
  RtlRunFrame(inputs) [common_rtl.c:795]
    kX2GameInfo.run_frame -> src/x2_rtl.c:RunOneFrameOfGame [95]
      interp_bridge -> CPU bus / DMA / PPU writes / Cx4
    debug_server_record_frame; ppudma_frame_snapshot [before increment]
    snes_frame_counter++
  CaptureSimulationFrame(number) [desktop/host_main.c:1280]
    PreparePpuFrame -> PpuBeginDrawing(g_ppu, g_my_pixels, pitch, flags)
    MmxBeginFrame -> X2 widescreen/HUD/background presentation policies
    kX2GameInfo.draw_ppu_frame -> X2DrawPpuFrame [src/x2_rtl.c:749]
      dma_startDma(..., true); SimpleHdma_Init x8
      for line=0..224:
        ppu_runLine(g_ppu, line)
        SimpleHdma_DoLine x8
    optional end_sim_frame / publish_native_field callbacks
  DrawPpuFrameWithPerf -> RtlDrawPpuFrame -> RtlWidescreenPresent
    SDL/OpenGL surface upload, viewport/scaling, presentation
```

Generic host comments say a game's raster hook can execute guest IRQs. X2's
specific hook explicitly does **not**: its IRQ delivery is in
`RunOneFrameOfGame`. Do not infer X2 behavior from that generic comment.
The draw pass still mutates PPU/HDMA state; calling it a second time to capture
is not a passive observation. The exporter must attach to the existing pass.

The host has run-ahead and rollback paths. A displayed field can be speculative;
a simulated frame can be skipped for display. `snes_frame_counter` is incremented
before X2 rasterization, while existing frame-ring snapshots happen before that
increment. Pairing separate debug queries by the same integer is insufficient.
Record a dedicated capture sequence and exact phase/guest counter together.

## State and extraction map

| Concern | Verified source / symbol | Capture meaning |
|---|---|---|
| Execution adapter | `src/x2_cpu_infra.c:7`, `kX2GameInfo`; `src/x2_rtl.c:95`, `RunOneFrameOfGame` | Guest execution and IRQ/NMI ownership |
| PPU registers | `snes/ppu.h:179`, `struct Ppu` | `inidisp..cgwsel` contains the 0x40-byte snapshot register region; export fields explicitly |
| Background maps/tiles | `snes/ppu.h:438`, `PPU_bgTilemapAdr`, `PPU_bgTileAdr`; `snes/ppu.c:1500`, `PpuTilemapWordAddress`; `:2453`, `PpuDrawBackgrounds` | Word-addressed map/tile bases, mode, tile size, priority, flips, palette selection |
| BG rasterization | `snes/ppu.c:1218`, `PpuDrawBackground_4bpp`; `:1728`, `_2bpp`; `:2242`, `_mode7`; `snes/ppu_legacy.c` | Optimized and reference paths; do not assume all modes use one renderer |
| Sprites | `snes/ppu.c:3033`, `ppu_evaluateSprites`; `:2313`, `PpuDrawSprites`; `snes/ppu.h:385` | 256 OAM words + 32 high bytes, OBSEL, OAM address/rotation state, flips/sizes/order/limits |
| Palette/color | `snes/ppu.h:385`, `cgram[256]`; `snes/ppu.c:3614`, `ppu_write`; `PpuRunLineJob`, `PpuDrawWholeLine` | RGB15 palette, brightness, transparent index zero, main/subscreen color math and windows |
| Scroll/camera | `snes/ppu.h:190`, `hScroll[4]`, `vScroll[4]`; `src/x2_display.c:91`, `X2Display_ExpandStageScroll` | Register scroll is verified; world camera semantics require title-specific evidence |
| Video memory | `snes/ppu.h:388`, `vram[0x8000]`; `PpuRenderVram` at :413 | 64 KiB guest VRAM; renderer may use a host override `renderVram`, capture effective memory or reject overrides |
| Line input boundary | `snes/ppu.c:831`, `PpuRunLineJob`; `src/x2_rtl.c:758` | Observe immediately before existing `ppu_runLine`; after prior line's HDMA |
| HDMA | `common_rtl.c:2821`, `SimpleHdma_DoLine` | Table-driven writes applied after each X2 line; must be reflected in the next line snapshot |
| Framebuffer | `snes/ppu.c:182`, `PpuBeginDrawing`; `:2557`, `PpuDrawWholeLine`; `snes/ppu.h:355` | Buffer + byte pitch; line 1 maps to output y=0; visible rows 1..224 |
| Platform presentation | `desktop/host_main.c:1341`, `RtlDrawPpuFrame`; `:1425`, `DrawPpuFrameWithPerf`; `desktop/opengl.c` | Native raster is input to scaled presentation; use pre-OSD reference pixels |
| Debug server | `debug_server.c:8823`, `debug_server_init`; `:9087`, network thread; `debug_server.h` | Trace build, background command processing; no blanket atomicity for live PPU queries |
| Existing observations | `debug_server.c:1621`, `debug_server_record_frame`; `ppu_dma_trace.c`; `ppu_lines`, `ppu_window`, OAM/VRAM rings | Useful diagnostics, with distinct phases and bounded/truncated histories |

`Ppu` also contains host pointers, cached brightness/mosaic arrays, sprite
history, windows, render flags, optional extra objects, widescreen policy and
write counters. Its full binary layout is not an interchange format.
`ppu_runLine(0)` resets line-dependent state/toggles field parity; 1..224 generate
pixels. A future replay needs the line-zero prestate and each actual line's
inputs, not just the final register file.

### Available framework facilities are not automatically active in X2

`ppu_rasterBegin`, `ppu_rasterRenderBegin`, `ppu_rasterApplyLine`, and
`ppu_rasterRenderEnd` in `snes/ppu.c:3319+` implement a double-buffered per-field
register journal. `beam_frame_driver.c` uses it. **X2DrawPpuFrame does not call
that driver or these journal APIs.** Simply querying `raster_journal` does not
prove it contains X2's scanout state.

Even where armed, the journal excludes VRAM/CGRAM/OAM data uploads, is bounded
at 4096 entries, and has a dropped counter. It is not a complete visual history.
`raster_snapshot.h:SnesRasterSnapshotCapture` provides immutable, deduplicated
byte images and has standalone ROM-free tests. Reuse is a candidate for a later
adapter, not evidence that X2 already publishes frames through it.

## What the two README probes really expose

`tools/dbgprobe.py` imports `snesrecomp/tools/sneslib/client.py:DebugClient` and
connects to loopback, port 4383 by default (`DBG_PORT` changes the client port).
Commands/replies are newline-delimited text, normally one JSON object per line.
Server port can be overridden separately with `SNESRECOMP_DEBUG_PORT`.
`SNESRECOMP_ENABLE_TRACE=ON` builds the server; release headers use no-op stubs.

- `ppu` sends `get_ppu_state`. `cmd_get_ppu_state` at `debug_server.c:5304`
  reads **live** `g_ppu`. It returns INIDISP, `bgmode & 7` (losing upper mode
  bits!), MOSAIC/OBSEL/SETINI, BG bases, four scroll pairs, main/subscreen masks,
  windows/color math/fixed color, VRAM/CGRAM port pointers, selected widescreen
  policy and field parity. It does **not** return tile bytes, palettes, full
  OAM, all latches/Mode-7 matrix, an immutable frame ID or a scanline history.
- `shot x.bmp` sends `screenshot <absolute-path>`. `cmd_screenshot` at :5067
  copies the most recently composed surface when valid, otherwise PPU
  `renderBuffer` (224 rows, width 256 + 2*extraLeftRight), honoring pitch.
  It writes a top-down **24-bit BMP on the server's filesystem**, not image
  bytes over TCP, then replies with path/dimensions/source/frame. Its BGR bytes
  omit alpha and layer/priority/asset identity. It does not rerender a frame.
  X2 supplies no `draw_frame` compositor callback in its identity adapter, so
  the normal source is PPU. This is not necessarily a window/OSD screenshot.

These live handlers do not form a producer-published immutable transaction;
the network thread can sample while the runtime progresses. Treat results as
diagnostic evidence, not synchronized reconstruction inputs.

### Richer existing probes

`dump_vram`, `dump_cgram`, `dump_oam` expose live byte arrays as hex JSON
(:4760, :4869, :4880). OAM is 512 main bytes followed by 32 high bytes.
`dump_frame_vram` and `dump_frame_cgram` read a specific `FrameRecord` under the
ring mutex, copy out, then send. These are **pre-raster CPU-frame** snapshots
in X2, not automatically matched to the last displayed picture. Raw uint16
memory dumps also inherit host endianness.

`ppu_lines` records selected scroll/window/math/screen values per line;
`ppu_window` records effective spans; `oam_render_get` records OAM at the first
rendered line; VRAM/OAM write rings record history. None replaces full
per-line graphics memory with explicit completeness. `dump_frame_raw` and
`dump_frame_range` are armed pixel captures despite the README's broad claim
that every probe merely queries existing history. Inspect their actual phase
before relying on them. No live probe was executed against a game in Sprint 0.
