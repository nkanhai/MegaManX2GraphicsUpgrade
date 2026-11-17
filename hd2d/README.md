# X2 HD-2D

Unofficial, noncommercial HD-2D presentation experiment for *Mega Man X2*.
Preserve the original simulation; enhance its presentation. A Unity 6 / URP
offline reconstruction prototype is available; live integration remains deferred.

Canonical repository: [nkanhai/MegaManX2GraphicsUpgrade](https://github.com/nkanhai/MegaManX2GraphicsUpgrade).
Our additions live in `hd2d/`. The original runtime is Matthew Stanley's
[MegaManX2Recomp](https://github.com/mstan/MegaManX2Recomp), powered by
[snesrecomp](https://github.com/RetroPortingToolKit/snesrecomp). Capcom owns the
original game and art. This directory contains no game assets or ROM data.
Existing [LICENSE](../LICENSE), [PERMISSIONS.md](../PERMISSIONS.md), and
[contribution terms](../CONTRIBUTING.md) apply; no replacement license is
introduced. Dependency notices and history remain intact.

## Start here

- [Unity component inspector, private imports and Mac qualification](unity/README.md)
- [Sprint 4 reconstruction report and fidelity limits](docs/roadmap/SPRINT_4_REPORT.md)
- [Interactive gameplay capture on macOS](docs/guides/INTERACTIVE_CAPTURE.md)
- [Sprint 3 report and owner-observed Mac validation](docs/roadmap/SPRINT_3_REPORT.md)
- [Sprint 2 report and real-game validation blockers](docs/roadmap/SPRINT_2_REPORT.md)
- [Private ROM-backed build, intro capture and image validation](docs/guides/PRIVATE_CAPTURE_WORKFLOW.md)
- [Apple Silicon investigation](docs/architecture/MACOS_NATIVE_AUDIT.md)
- [Sprint 1 implementation, tests and limitations](docs/roadmap/SPRINT_1_REPORT.md)
- [Native recorder usage and ROM-independent replay](bridge/README.md)
- [Versioned runtime scanout format](schemas/RUNTIME_SCANOUT_V1.md)
- [Sprint 0 report and reproducible checks](docs/roadmap/SPRINT_0_REPORT.md)
- [Upstream audit and pinned revisions](docs/architecture/UPSTREAM_AUDIT.md)
- [Rendering pipeline and exact observation points](docs/architecture/RENDERING_PIPELINE.md)
- [Bridge proposal](docs/architecture/BRIDGE_PROPOSAL.md)
- [Milestones, risk register and Sprint 1 acceptance](docs/roadmap/ROADMAP.md)
- [Recorded-capture decision](docs/decisions/0001-recorded-scanout.md)
- [Experimental synthetic format](schemas/FRAME_V1.md)

## ROM-free quick start

From the repository root, Python 3.10+ with only the standard library:

```sh
python3 -m unittest discover -s hd2d/tests -v
python3 hd2d/tools/frame_format.py demo hd2d/local/demo.json
python3 hd2d/tools/frame_format.py validate hd2d/local/demo.json
```

`demo` refuses to overwrite a file; use a new filename on a second run. The
fixture has two scrolling background layers and three hardware sprite pieces,
constructed from original stripe/checkerboard patterns. The output contains
64 KiB VRAM, 512-byte CGRAM and 544-byte OAM, with explicit little-endian
encoding and hashes. This tests an interchange contract, not rendered fidelity.

For the focused upstream C tests, initialize submodules, install CMake >=3.20,
Ninja and a C11 compiler, then run:

```sh
git submodule update --init --recursive
python3 hd2d/tools/check_upstream.py
```

Generated artifacts and logs belong in ignored `hd2d/local/`; synthetic source
and its expected summary live in `fixtures/synthetic/`. Ignore rules reduce
accidental staging but are not a substitute for reviewing `git diff --cached`.
Never force-add a ROM, firmware, save, screenshot, or extracted game asset.

## Current boundary

The owner reports a successful native ARM64 ROM-backed build, keyboard gameplay
and a real opening-narrative capture with zero pixel differences. This is local
owner evidence, separate from cloud CI. Sprint 3 adds an opt-in keyboard trigger
for sequential captures during normal play, private session directories and
automated SDL/native isolation tests. Two interactive gameplay captures still
need owner qualification on the Mac. Sprint 4 adds an offline Unity component
inspector, a strict C# runtime-v1 reader, independently decoded BG/OAM graphics
and an isolated normal-map experiment. Managed reconstruction matches eighteen
original synthetic frames exactly; Unity Editor/GPU and private gameplay import
still need owner qualification. Live integration and asset cataloguing remain
deferred. Runtime v1 and Sprint 0 synthetic JSON stay separate.
