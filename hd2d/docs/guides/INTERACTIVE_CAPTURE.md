# Interactive gameplay capture (Sprint 3)

Play normally, then press **Left Control + Left Shift + F10** to capture the
next draw. On a Mac keyboard, also hold **Fn/Globe** if F10 normally controls
volume. Use Control, not Command, and the **left** Shift key: the stock right
Shift key is SNES Select. Release F10 between requests. Continue playing and
press the shortcut again whenever another frame is useful.

The shortcut operates only inside the game's SDL process. It does not install
a global keyboard listener or change controller input. Capture results and
exact rejection reasons appear in the terminal. There is no in-game overlay.

## Build on your Apple Silicon Mac

The owner has reported a successful native ARM64 ROM-backed build, keyboard
gameplay and zero pixel differences for the opening narrative at draw 1800.
That validates the earlier recorder locally, not this new interactive shortcut
or a playable-stage capture. No ROM or private image is included in this PR.

Use a native arm64 terminal and Python (not Rosetta):

```sh
brew install cmake ninja sdl2 python@3.12
uname -m
git fetch origin
git switch feature/hd2d-sprint-3
git submodule update --init --recursive
```

`uname -m` and the Python process architecture should both report `arm64`.
Use `/opt/homebrew/opt/python@3.12/bin/python3.12` below if `python3` is not your
native Homebrew Python. Xcode Command Line Tools must already be installed;
use `xcode-select --install` if they are missing.

Set the existing ROM's private path, then use a **new** private build workspace:

```sh
ROM="/absolute/private/path/Mega Man X2.sfc"
python3 hd2d/tools/build_capture_host.py --rom "$ROM" \
  --workspace hd2d/local/sprint3-build --cmake-prefix "$(brew --prefix)"

python3 hd2d/tools/build_capture_host.py --rom "$ROM" \
  --workspace hd2d/local/sprint3-build --cmake-prefix "$(brew --prefix)" --build
```

The first command checks/plans; the second generates and builds in a detached
private worktree. Keep the source checkout clean and committed. The unchanged
helper enforces the exact upstream USA ROM identity and dependency pins, sets
`HD2D_SCANOUT_CAPTURE=ON`, `SNESRECOMP_SETUP_HOST=OFF`, SDL2 and ARM64, and never
downloads a ROM. Choose another workspace name if one already exists. Keep
the previous working build until this one is qualified. Details and the Linux
fallback are in [PRIVATE_CAPTURE_WORKFLOW.md](PRIVATE_CAPTURE_WORKFLOW.md).

A Sprint 2 executable must be rebuilt: the new launcher refuses binaries
without the Sprint 3 capability marker. The marker is a compatibility check,
not a signature or proof that generated game code is present. CI's ROM-free
setup host still cannot execute the game.

## Launch, play, capture twice

```sh
python3 hd2d/tools/interactive_capture.py \
  --exe hd2d/local/sprint3-build/build/MegaManX2SNESRecomp \
  --rom "$ROM" --run
```

Omit `--run` to inspect a plan without creating a session or starting the game.
Every actual launch creates a fresh directory such as:

```text
hd2d/local/interactive/20261117-190000-<unique-session-id>/
  config.ini
  session.json
  captures/
    capture-000001-draw-00000000000000012345/capture.x2scan
    capture-000002-draw-00000000000000016789/capture.x2scan
```

The numbers above are examples, not frame targets. The terminal prints the
actual private session path, then `HD2D_INTERACTIVE_CAPTURE_V1 ready` once the
first draw installs capture handling. Play through the opening narrative
using your normal keyboard controls. When X is in the playable introduction,
press the shortcut. Look for `complete` and the actual `path=.../capture.x2scan`
line. Continue playing, release F10, then request another capture later. Close
the game window normally when finished. No controller script, predicted draw
number, or capture-driven restart is required.

The launcher supplies a fresh native-width config with audio, VSync, sprite
limits and the optimized PPU enabled; run-ahead, blending and autosave are off.
It uses software SDL presentation and the existing keyboard controls. Existing
per-executable `keybinds.ini` still applies. It does not load saved states or
copy mod/save data into the new session. Engine/replay environment overrides
are cleared so an inherited draw number, replay or finite-frame run cannot
silently change this interactive launch.

## Validate both files

Build the unchanged runtime-v1 reader and run the existing image workflow:

```sh
cmake -S hd2d/tests/native -B hd2d/local/native -DCMAKE_BUILD_TYPE=Release
cmake --build hd2d/local/native --parallel 3
ctest --test-dir hd2d/local/native --output-on-failure

python3 hd2d/tools/validate_capture.py "/actual/first/capture.x2scan" \
  --replay hd2d/local/native/scanout_replay --output hd2d/local/validation-first
python3 hd2d/tools/validate_capture.py "/actual/second/capture.x2scan" \
  --replay hd2d/local/native/scanout_replay --output hd2d/local/validation-second
```

Choose new output directory names for each validation. Each produces reference,
reconstruction and difference PNGs plus JSON/text reports. Acceptance requires
both reports to show **0/57,344 pixels differ**, and owner inspection that the
reference images show the intended gameplay moments. A capture `complete` log
only means recording/publication succeeded; it is not a fidelity verdict.
Retain `session.json` and the individual reports privately. Do not attach game
images, generated code, ROMs or captures to GitHub.

## Limits and troubleshooting

- One pending request is retained. Key repeat, holding F10 and multiple presses
  before the next draw cannot create a backlog. Requests during a capture can
  queue one later capture. Release F10 before requesting again.
- Capture happens at the next existing draw boundary, not necessarily the
  already displayed frame. A paused game must resume before it can fulfill a
  queued request. Use the shortcut during active gameplay for predictable
  scene selection. Rewind, rollback, netplay and run-ahead remain rejected.
- Both the raw controller bindings and `[KeyMap]` are checked. A binding to
  F10/left modifiers in `keybinds.ini`, or to the chord/modifiers in `[KeyMap]`,
  disables the capture shortcut with a warning. Existing controls keep their
  behavior. Review those bindings in your private setup before using the chord;
  the tool does not rewrite them. OS-level shortcuts may also intercept F10.
- Unsupported profiles keep their exact diagnostics. Failure consumes that
  request/sequence number; later requests can succeed. No partial capture is
  published and no prior directory is overwritten.
- At most **64 attempts per session** (including failures), each bounded to
  **4 MiB**: at most 256 MiB of capture containers. Further requests are ignored
  with a limit notice; gameplay continues. Logs and upstream saves are outside
  that container budget. Start a new private session only after exhausting it.
- Recording, hashing and publication remain synchronous on the draw thread.
  A request can cause a visible one-frame hitch, especially on slow storage.
  This is a diagnostic tool, not continuous capture or a zero-overhead promise.
- Current automated keyboard tests use SDL2's dummy driver. The owner's actual
  macOS keyboard/Fn behavior and two interactive gameplay captures remain the
  final local acceptance check. Windows/SDL3 are not qualified by this sprint.
