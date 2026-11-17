# Roadmap and risk register

Full-game production is not scheduled. Each milestone ends in evidence, not a
large speculative implementation.

| Milestone | Exit evidence |
|---|---|
| 0: engineering baseline | Pinned source map, honest build results, bridge decision, synthetic format tests; confirmed owner fork and reviewable feature commit/PR |
| 1: graphics extraction | Immutable ordered scanout captures, reference pixels, no guest mutation, local-only artifacts |
| 2: Unity reconstruction | Synthetic then representative recorded scenes; exact pixel/priority/scroll comparison before visual enhancement |
| 3: live integration | Measured latency and synchronization, reset/load/input/audio lifecycle, platform build validation |
| 4: HD-2D demonstration | One playable section, normals/lights/emission/particles, charged projectile presentation; original simulation authoritative |
| 5: reusable content pipeline | Stable context-aware catalogue and import/material mappings applicable to a second stage |

## Sprint 1: native synthetic scanout recorder

Implemented for a restricted native Mode-1 profile; see
[Sprint 1 report](SPRINT_1_REPORT.md). The acceptance criteria below are retained
as the original gate. Verified pixel parity is canonical RGBA (source X padding
is undefined in the legacy renderer), with a single bounded binary container
instead of separately named blobs. Real-game qualification remains open.

Implement `hd2d/bridge/` as an opt-in **single-frame native-profile capture
adapter** at `X2DrawPpuFrame`, plus a ROM-independent native harness that drives
it with synthetic PPU memory and per-line scroll/palette changes. Export named
fields, little-endian memory, ordered lines and a same-capture reference image.
Keep runtime recording bounded and one-shot. Do not add Unity, semantic event
recognition, continuous TCP streaming or change the simulation/render order.

Acceptance criteria:

1. Default build has capture disabled; source/call order and output remain
   unchanged. Enabled capture reads but does not write guest PPU/CPU/DMA state.
2. A synthetic field contains lines 0..224, two BGs, several OBJ pieces and at
   least one mid-field scroll and palette change. IDs associate the correct
   memory version with each line; later source writes cannot change a capture.
3. Reader rejects wrong versions, missing/reordered lines, broken hashes,
   invalid lengths, unsafe paths, incomplete records and unsupported host
   settings. Capacity/allocation/file errors abort the capture, not simulation.
4. Independent replay of the supported captured state matches the pinned
   upstream PPU framebuffer byte-for-byte for the synthetic harness. Document
   the full named-field/latch contract needed to obtain that match.
5. Run capture on/off with identical synthetic inputs; compare serialized guest
   state and reference pixels. Test reset/new epoch, sequence monotonicity and
   failed publication. No file/queue work on a network reader of mutable PPU.
6. Record bytes copied and elapsed capture cost, without claiming gameplay
   performance. Keep artifacts ignored. Re-run focused upstream tests.
7. Optional follow-up, **not** a ROM-free success claim: owner supplies a legally
   obtained ROM locally, captures a native gameplay frame and a raster/Cx4
   scene, and compares them. Report any unsupported feature explicitly.

A small hook to the title draw loop and an isolated recorder are appropriate;
if linking needs framework edits, keep them separately reviewable and do not
silently move a gitlink to an unpublished submodule commit.

## Risk register

Sprint 3 adds interactive keyboard capture before Unity work. The owner reports
a real opening-narrative frame reconstructed exactly on Apple Silicon; the next
gate is two owner-selected gameplay captures in one session. See
[Sprint 3 report](SPRINT_3_REPORT.md) and the [interactive guide](../guides/INTERACTIVE_CAPTURE.md).
This is owner-observed local validation, not a cloud CI claim.

Sprint 2 follows up graphics extraction with real-game qualification before
starting the Unity milestone. It supplies diagnostic reasons, stronger
synthetic coverage, private ROM build/capture tooling and image-diff reports.
See [Sprint 2 report](SPRINT_2_REPORT.md) for verified Linux results and the
specific ROM-access/Apple-Silicon-runner blockers. The long-term milestones
above are not sprint numbers; Unity and the asset catalogue remain deferred.

| ID / severity | Risk and evidence | Mitigation / validation gate | Owner |
|---|---|---|---|
| R1 high | Incomplete scanout state: final PPU values lose HDMA changes; X2 does not use shared journal replay; VRAM uploads are excluded from that journal | Observe each actual line and effective memory; differential synthetic replay then real captures; distinguish upstream from hardware fidelity | Engineering + owner scene review |
| R2 high | Frame mismatch/torn reads: TCP thread reads live state; frame ring precedes raster; run-ahead/rollback complicate identity | Producer-owned immutable capture, explicit phase/session/epoch, paired pixels; first profile excludes speculative modes | Engineering |
| R3 high | Unity fidelity and portability unproven: sprite sorting/alpha do not encode SNES math; Apple Silicon native host untested | Recorded importer and pixel comparisons before lighting; separate macOS build spike; defer final ABI/URP decision | Owner + engineering |
| R4 medium | Copy/serialization cost: up to ~15 MB per naive scanout before reference pixels | One-shot bounded captures, immutable dedup, measure copy/compare/write cost, whole-record rejection on overflow | Engineering |
| R5 medium | Semantic misclassification and unstable asset addresses | Content/context hashes and evidence-tagged annotations; never label OAM slots as persistent entities | Owner creative direction + engineering |
| R6 medium | Widescreen/host overlays diverge from native graphics | Native profile first, explicit render configuration, reject unsupported settings, later separate presentation-policy support | Engineering |
| R7 medium | Licensing/asset provenance; no clear dizProjects license file found | Preserve notices/pins, do not copy annotation corpus or game assets, review runtime distribution before release | Project owner |
| R8 resolved | Initial GitHub connection writes returned HTTP 403 | After owner access review, existing branch push and connector PR creation succeeded; [PR #1](https://github.com/nkanhai/MegaManX2GraphicsUpgrade/pull/1) targets the fork's master | Project owner / GitHub connection |

Material decisions still open: embedding vs IPC, authoritative/speculative
frame selection for live playback, native API lifecycle, 2D vs Universal URP,
and how to handle unsupported SNES compositing. The current recorded-file
proposal is reversible and does not precommit those choices.
