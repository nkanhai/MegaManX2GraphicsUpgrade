# Upstream audit — 2026-11-17

## Provenance and scope

Audited MegaManX2Recomp commit
`622aa882881a07c8238ce17a14381c7d75a979d7` from
<https://github.com/mstan/MegaManX2Recomp>, initially branch `master`.
Full history was cloned into a previously empty cloud workspace. The owner
confirmed <https://github.com/nkanhai/MegaManX2GraphicsUpgrade> as the canonical
fork. It is configured as `origin`; `upstream` remains the original author's
repository. `origin/master` is exactly the audited commit (zero commits ahead
or behind), so the initial audit remains applicable without rework.
Work branch: `feature/hd2d-sprint-0`; PR base: `origin/master`.

Publication is verified in [PR #1](https://github.com/nkanhai/MegaManX2GraphicsUpgrade/pull/1)
against the canonical fork's `master`. Initial Git push and connector branch
creation returned HTTP 403 (`Resource not accessible by integration`). After
the owner reviewed connection access, retrying the existing branch push and
creating the PR through the connector both succeeded on 2026-11-17. The prior
permission blocker is resolved; no changes target the original author's repository.

Exact URLs and revisions are also in [upstream-lock.json](upstream-lock.json).
`git submodule update --init --recursive` and `bash tools/bootstrap.sh` succeeded.
All gitlinks remain unchanged and match `framework_pins.txt` where listed.

| Dependency | Pinned SHA | Role |
|---|---|---|
| snesrecomp | `93fc677a2a9b688751d575ed1232343d83d3d2c0` | Hardware runtime, interpreter/AOT framework, shared host, PPU, debug server |
| recomp-ui | `0546fbcb0126bb14bf88d2128b4fe8a4229570a3` | Preboot launcher / shared UI |
| snesrecomp/lib/recomp-net | `8c76fb643339144a3a84d5ba8f5defe6250dcd26` | Framework networking library |
| snesrecomp/lib/retcomm-rbengine | `2a03e73693acee0fb78076ea058642c931bef12e` | Framework rollback library |
| third_party/dizProjects | `c16f01134f29e4a61b41a3d8ddf5121b556cbdc7` | Disassembly annotation reference |

## Verified repository organization

`src/` is a small X2 identity/adaptation layer; it is not a decompiled gameplay
engine. `src/main.c` defines title/ROM identity/debug port and includes
`snesrecomp/runner/src/desktop/mmx23_host_main.inc`, which builds a
`SnesDesktopHostGame` and delegates to `snesrecomp_desktop_main`.
`src/x2_cpu_infra.c:kX2GameInfo` binds `RunOneFrameOfGame`, `X2DrawPpuFrame`,
save-state extras and diagnostic replay hooks into the shared runtime.
`src/x2_display.c` handles presentation sizing and title-specific scroll expansion.
`src/mods/` and `mods/preloaded/` implement/package optional widescreen.

`recomp/` holds generation policy and CPU coverage configurations.
`tools/regen.sh` delegates to the framework; `recomp/generate.toml` applies
Diztinguish annotations and game-specific overrides. ROM-derived `src/gen/*.c`
and `recomp/funcs.h` are intentionally absent/ignored. CMake collects framework
sources and links the title adapters plus launcher; it does not embed Unity.

### Execution and Cx4

`src/x2_rtl.c:RunOneFrameOfGame` boots RESET through `interp_bridge`, gives
execution a 1364*262 master-clock budget per host frame, gates NMI on NMITIMEN,
and services IRQ only when the CPU interrupt-disable flag is clear. Selective
AOT coverage is an optimization over the LLE floor, not a high-level entity API.

`snesrecomp/runner/src/snes/cx4.c` is an ares-derived HG51B instruction-level
core. `cx4_load_firmware` calls `cx4_synthesize_data_rom` before optional
user-file verification; an external Cx4 firmware dump is **not required by this
revision**. The Cx4 section of `THIRD_PARTY_ATTRIBUTION.md` still says a dump is
required, contradicting the implementation and current game README. Preserve
its legal notices; use source for runtime behavior. No firmware was fetched,
created as an artifact, or tested against a physical dump in this sprint.

### Published support versus verified here

The upstream README describes a playable Windows v0.1.5 release and owner
playtests. Those are upstream claims, not tests performed here. The repository
also has `tools/build-linux.sh` for AppImage builds, and a Linux GitHub Actions
job that compiles a **ROM-free setup host** with SDL2. The framework supports
SDL3 by default, including a pinned source fallback, and explicit SDL2.
Windows and macOS conditionals do not establish Apple Silicon qualification.
See the sprint report for actual commands and results in Linux x86_64.

## Licensing and attribution reviewed

Read root `LICENSE` (PolyForm Noncommercial 1.0.0), `PERMISSIONS.md` (Matthew
Stanley's additional donation/sponsorship/ad permissions), and `CONTRIBUTING.md`
(existing file terms plus the additional-permissions contribution requirement).
This sprint introduces no separate project license or commercial permissions.
New additions follow the repository's existing terms; third-party terms stay
separate. Do not imply any rights to Capcom's game, art or trademarks.

Reviewed dependency texts/notices relevant to this work:

- `snesrecomp/LICENSE`: PolyForm Noncommercial; its own contributions must not
  be presented as wholly MIT merely because they build on permissive code.
- `snesrecomp/THIRD_PARTY_ATTRIBUTION.md`: snesrev and LakeSnes lineage (MIT),
  ares Cx4/DSP cores (ISC), GL loader and other components. Preserve and ship
  required notices with future runtime distributions.
- `recomp-ui/LICENSE`, `recomp-ui/src/third_party/imgui/LICENSE.txt`,
  `snesrecomp/lib/recomp-net/LICENSE` and
  `snesrecomp/lib/retcomm-rbengine/LICENSE`: MIT notices retained.
- No license-named file was found in the pinned `dizProjects` checkout.
  Preserve its existing pin/reference and credits; do not assume permission
  to copy its contents into a new HD-2D asset catalogue.

SDL/OpenGL development packages used locally are build tools/dependencies,
not new vendored project files. A future redistributable runtime/native plugin
and Unity distribution need a targeted dependency/license review; none is
produced in Sprint 0.

## Confirmed, inferred, unresolved

**Confirmed:** raw graphics state is directly accessible in `Ppu`; graphics
are rendered per line; the game draw hook and framebuffer have identifiable
observation boundaries; existing probes are not a complete atomic frame export.
The exact map is in [RENDERING_PIPELINE.md](RENDERING_PIPELINE.md).

**Proposed:** record the state actually consumed by each X2 render line, then
compare replay pixels with the completed source framebuffer. No modification
to timing, raster-journal adoption, or gameplay is necessary to begin.

**Unresolved:** which per-line features real X2 scenes use; whether upstream
pixels fully match hardware; semantic identity of ordinary OAM pieces; practical
capture cost; Apple Silicon portability; Unity renderer choice. None blocks a
synthetic capture adapter, but all constrain claims about a real remaster.
