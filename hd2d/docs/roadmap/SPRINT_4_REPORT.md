# Sprint 4 — Unity graphics reconstruction

Date: 2026-11-17. Canonical fork: `nkanhai/MegaManX2GraphicsUpgrade`.
Branch: `feature/hd2d-sprint-4`, based on merged `master`
`c464b91b85daf435405d0123519a2e9aa7f7aa7c` (Sprint 3 PR #4).

**Implemented, pending Unity/Mac qualification.** The independent managed
decoder reconstructs all 57,344 pixels exactly in eighteen original synthetic
static captures. The Unity project contains the importer, diagnostic scene,
component quads, source inspection, GPU comparison export and isolated lighting
experiment. This cloud environment has no Unity Editor, so Editor compilation,
Package Manager resolution, actual URP/Metal output and owner private-gameplay
acceptance have not been verified. Native owner-observed success from prior
sprints is separate evidence and is not being extended to Unity by inference.

## Implementation

- `unity/Assets/X2HD2D/Core`: engine-independent C# reader for the existing
  runtime-v1 format, a bounded/strict validator and managed graphics decoder.
  Imported memory/register/reference state is immutable to consumers. The
  4 MiB file limit is checked before reading/allocating from file lengths;
  fixed dimensions, 225 ordered lines, all 70 field widths, memory references,
  SHA-256, profile and pinned PPU revision are validated. No native struct,
  executable payload, external resource path or ROM is loaded.
- Backgrounds decode original 2bpp/4bpp planar tiles, RGB15 palettes, cell
  attributes, 8/16-pixel tile sizes, 32/64-cell tilemaps, scroll/wrap and Mode-1
  priority. Three BG roots contain two priority quads each. This uses sampled
  tilemap textures, not per-tile meshes or the reference screenshot.
- All 128 OAM slots retain independent roots and original graphics metadata.
  Decoding includes high OAM, signed X, Y wrap, size, bank, flips, palette,
  evaluation rotation and BG priority. Per-piece visibility masks resolve
  sprite ownership and 32-object/34-sliver limits before BG composition. Raw
  piece pixels remain available independently of baseline masking.
- `Runtime`: reusable component construction, metadata, point textures,
  unlit baseline and separate lit experimental camera/layer. `Editor`: private
  file picker, scanline inspection, layer toggles, tile-cell/source preview,
  OAM selection, comparison export and repeatable project setup. Imported
  textures/objects/materials are transient `DontSave` data.
- Comparison exports actual Unity GPU readback, managed component composite,
  reference and pixel-difference images plus identity/support/count reports.
  `verified-this-frame` requires strict support, no layer filters and zero
  differences in **both** GPU and managed comparisons. Rendering failure
  prevents evidence export; incomplete support never becomes a parity claim.
- An isolated OAM copy receives a procedural normal map, URP Sprite-Lit
  material and moving normal-aware Light2D. A capture-local ScriptableObject
  can supply slot, tint, scale, strength and material. It neither changes
  baseline components nor assumes a slot is a semantic game entity.

The project targets Unity **6000.0.62f1**, URP **17.0.4** and Test Framework
**1.4.6**. Version/API selection was checked against Unity release information,
UnityCsReference 6000.0 and the Graphics repository's 6000.0/staging sources.
An actual Unity resolution/build was not available. Generated pipeline assets,
machine settings and the unqualified transitive lock remain local/ignored;
capture the resolved lock in a follow-up after owner qualification. This does
not change the existing native dependency pins.

## Executed validation

Environment: Linux x86_64, Python 3.12, GCC 14.2, local CMake/Ninja tools,
SDL2 development files, .NET SDK **8.0.428**. All inputs were original synthetic
graphics; no ROM or game-derived capture was accessed or downloaded.

| Check | Result |
|---|---|
| Managed core compiled with C# 9, warnings as errors, .NET 8 reference assemblies | PASS |
| Same core compiled against .NET Standard 2.1 reference assemblies | PASS; API compatibility only, not Unity compilation |
| Managed console suite | PASS, **104 assertions** |
| Managed/native pixel comparison | **18/18 frames, 0/57,344 differing pixels each** |
| Existing raster-changing capture | Imported; explicitly incomplete; strict compositing rejects it |
| Python unittest discovery | **23 tests PASS** |
| Existing native CTest suite | **6/6 groups PASS**, including on/off isolation, file validation and interactive requests |
| Focused pinned upstream checks | Raster snapshot, render workers and all six PPU test executables PASS |
| Recursive dependency verification | All five revisions match `upstream-lock.json`; sources unchanged |
| Unity EditMode tests (4), URP rendering, normal-map appearance | **NOT RUN**: no Unity Editor/graphics qualification environment |
| Real gameplay capture inside Unity on Apple Silicon | **NOT RUN**: owner-private capture/Unity environment unavailable here |

The native oracle generates reference pixels; the C# algorithm does not call
the native renderer and never consumes reference pixels during reconstruction.
Cases include static 8/16-pixel BGs, BG3 priority, 64×64 maps with scroll/flips,
RGB brightness and blanking, legacy output, OAM rotation, wrapping, overflow,
unlimited policy, lower-OAM ownership versus higher BG priority, OBJ bank and
tile-row/column wrapping, and all six square OBJ size modes. The original
raster fixture tests honest handling of mid-frame register/memory changes.
Malformed length/version/profile/order/register/memory/checksum cases and
input/reference copy isolation are included. Python checks guard the exact
native-to-C# field order and ignored/private fixture output policy.

The local `dotnet run` CLI initially failed during NuGet initialization with
an environment mutex error (`NuGet-Migrations`, “cannot open device or file
specified”), before compilation. The SDK's Roslyn compiler was invoked directly
with its installed reference assemblies and the resulting executable run with
`dotnet`; both compilation targets and all 104 assertions passed. No NuGet
packages are needed by the managed test project. GitHub Actions uses the normal
`dotnet run --project hd2d/tests/unity/CoreTests.csproj` path on its runner.
This local CLI failure is tooling evidence, not a passing Unity test.

Reproduce fixtures/tests and Unity qualification using
[`unity/README.md`](../../unity/README.md). New GitHub Actions workflow
`hd2d-unity-core-tests.yml` builds the synthetic recorder/pinned PPU, derives
the fixtures, runs the managed suite and Python checks on pushes and PRs.
Existing native and platform workflows are unchanged. No Unity licence,
private input or uploaded imagery is required by CI.

## Limitations and remaining acceptance work

The reader's runtime profile is broader than the component renderer's faithful
subset. Color math, color clipping, layer windows and raster-varying state are
identified explicitly. Such captures can be inspected using a chosen scanline's
state; this is a static interpretation, not full-frame replay. Unsupported OBJ
size modes 6/7 use the pinned square approximation for inspection only. Other
display modes/mosaic/SETINI features are rejected entirely. Detection is
conservative and can flag changes to unused memory as incomplete.

The owner must open the project in the pinned Apple Silicon Editor, resolve
packages and pass all four EditMode tests with a real graphics device. Then
privately import a gameplay capture, toggle BG/OAM groups, inspect an individual
piece, apply its isolated treatment and export comparisons. Record the Editor,
package resolution, Metal device and GPU/managed counts. Keep the capture,
screenshots and comparison directory private. If the frame has unsupported
effects, record its exact diagnostics and selected line rather than claiming
full-frame parity. Qualifying a supported real frame is still required before
calling Unity fidelity verified.

Unity setup/API compatibility, pixel alignment/color handling on Metal and the
normal-map experiment remain risks until those tests run. The prototype is an
Editor tool, reserves layers 8/9, decodes one viewport at a time and has no
performance qualification for continuous playback. Manually hiding an OAM slot
does not recompute ownership masks. Generated resources are discarded on reload;
private gameplay textures are never serialized as a reusable asset library.

No original game logic, native capture/replay tooling, ROM patches, dependency
gitlinks, licences or upstream attribution changed. No game assets or capture
binaries are included. Live Unity integration, asset cataloguing and additional
SNES compositing effects remain future work. The Sprint 4 PR is for review and
owner qualification; it must not be merged automatically.
